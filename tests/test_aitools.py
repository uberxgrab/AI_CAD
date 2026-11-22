import asyncio
import json
import os
import shutil
import sys
import unittest
import uuid
from pathlib import Path

import ezdxf

from aitools import config, drawings, load, overview, privacy, profile, render, rooms
from tests.test_arch_rooms import build_plan

REPO = Path(__file__).resolve().parents[1]
WALLS = ["A-Tuong", "cua-doors"]
REGION = [-500, -500, 10500, 6500]


class Base(unittest.TestCase):
    def setUp(self):
        root = REPO / ".tmp_test_runs"
        root.mkdir(exist_ok=True)
        self.tmp = root / f"ai_{uuid.uuid4().hex}"
        (self.tmp / "CAD").mkdir(parents=True)
        self.dxf = self.tmp / "plan.dxf"
        build_plan(self.dxf)
        self.write_config()

    def write_config(self, **extra):
        cfg = {"work_dir": str(self.tmp / "work"), "drawing_dirs": [str(self.tmp / "CAD")], **extra}
        path = self.tmp / "cfg.json"
        path.write_text(json.dumps(cfg), encoding="utf-8")
        self._old = os.environ.get("AITOOLS_CONFIG")
        os.environ["AITOOLS_CONFIG"] = str(path)
        self.addCleanup(self._restore)

    def _restore(self):
        if self._old is None:
            os.environ.pop("AITOOLS_CONFIG", None)
        else:
            os.environ["AITOOLS_CONFIG"] = self._old
        drawings._DOC_CACHE.clear()


class ProfileTests(Base):
    def test_wall_layer_has_spacing_signature_and_door_arc_counted(self):
        result = profile.layer_profile(self.dxf)
        rows = {r["layer"]: r for r in result["layers"]}
        wall = rows["A-Tuong"]
        self.assertEqual(wall["suggested"], "wall")
        self.assertIn(200, [p["mm"] for p in wall["parallel_mm"]])
        self.assertEqual(rows["cua-doors"]["door_arcs"], 1)
        self.assertEqual(rows["cua-doors"]["suggested"], "opening")
        self.assertGreater(wall["len_m"], 50)
        self.assertEqual(wall["continuous"], 1.0)
        self.assertEqual(rows["A-Text"]["text_count"], 2)
        self.assertIn("PHÒNG KHÁCH", rows["A-Text"]["sample_texts"])

    def test_region_limits_the_scan(self):
        inside = profile.layer_profile(self.dxf, [-500, -500, 10500, 6500])
        whole = profile.layer_profile(self.dxf)
        w_in = {r["layer"]: r for r in inside["layers"]}["A-Tuong"]["len_m"]
        w_all = {r["layer"]: r for r in whole["layers"]}["A-Tuong"]["len_m"]
        self.assertLess(w_in, w_all)

    def test_xref_prefix_is_cleaned_and_full_name_kept(self):
        doc = ezdxf.readfile(self.dxf)
        doc.modelspace().add_line((0, 0), (5000, 0), dxfattribs={"layer": "XR plan$0$A-Tuong"})
        doc.saveas(self.dxf)
        row = {r["layer"]: r for r in profile.layer_profile(self.dxf)["layers"]}["A-Tuong"]
        self.assertEqual(row["full_names"], ["XR plan$0$A-Tuong"])

    def test_max_layers_truncates(self):
        result = profile.layer_profile(self.dxf, max_layers=2)
        self.assertEqual(result["layers_shown"], 2)
        self.assertGreater(result["layers_total"], 2)


class PrivacyTests(Base):
    def test_redact_masks_client_names_but_keeps_labels_and_codes(self):
        self.assertEqual(privacy.mask_text("CÔNG TY ACME VIỆT NAM", "redact"), "[text]")
        self.assertEqual(privacy.mask_text("DT. 26,2M2", "redact"), "DT. 26,2M2")
        self.assertEqual(privacy.mask_text("SUP.04", "redact"), "SUP.04")
        self.assertEqual(privacy.mask_text("PHÒNG KHÁCH", "redact"), "PHÒNG KHÁCH")
        self.assertIsNone(privacy.mask_text("anything", "numbers_only"))

    def test_profile_redact_and_numbers_only_texts(self):
        doc = ezdxf.readfile(self.dxf)
        doc.modelspace().add_text("CONG TY ACME CORPORATION", dxfattribs={"layer": "A-Text", "insert": (0, 0)})
        doc.saveas(self.dxf)
        normal = {r["layer"]: r for r in profile.layer_profile(self.dxf)["layers"]}["A-Text"]
        self.assertIn("CONG TY ACME CORPORATION", normal["sample_texts"])
        red = {r["layer"]: r for r in profile.layer_profile(self.dxf, mode="redact")["layers"]}["A-Text"]
        self.assertNotIn("CONG TY ACME CORPORATION", json.dumps(red))
        self.assertIn("DT. 25,4M2", red["sample_texts"])
        num = {r["layer"]: r for r in profile.layer_profile(self.dxf, mode="numbers_only")["layers"]}["A-Text"]
        self.assertNotIn("sample_texts", num)
        self.assertEqual(num["text_count"], 3)

    def test_render_refused_in_numbers_only(self):
        with self.assertRaises(PermissionError):
            render.render_region(self.dxf, REGION, WALLS, mode="numbers_only")

    def test_configured_privacy_is_the_minimum(self):
        self.write_config(privacy="redact")
        self.assertEqual(privacy.effective_mode(), "redact")
        self.assertEqual(privacy.effective_mode("normal"), "redact")
        self.assertEqual(privacy.effective_mode("numbers_only"), "numbers_only")
        with self.assertRaises(ValueError):
            privacy.effective_mode("bogus")
        self.write_config(privacy="numbers_only")
        with self.assertRaises(PermissionError):
            render.render_region(self.dxf, REGION, WALLS)

    def test_file_names_only(self):
        self.assertEqual(privacy.safe_name(r"C:\Clients\Secret\a.dxf"), "a.dxf")
        self.assertEqual(overview_name(self.dxf), "plan.dxf")


def overview_name(dxf):
    return overview.drawing_overview(dxf)["drawing"]


class RenderRoomTests(Base):
    def test_find_rooms_then_render_and_load(self):
        found = rooms.find_rooms(self.dxf, REGION, WALLS)
        self.assertEqual(found["status"], "ok")
        self.assertEqual(sum(1 for r in found["rooms"] if r["kind"] == "room"), 3)
        self.assertEqual(sum(1 for r in found["rooms"] if r["kind"] == "shaft"), 1)
        self.assertNotIn("polygon", found["rooms"][0])
        self.assertTrue(Path(found["rooms_json"]).is_file())
        self.assertTrue((Path(found["rooms_json"]).parent / "request.json").is_file())
        with_poly = rooms.find_rooms(self.dxf, REGION, WALLS, include_polygons=True)
        self.assertIn("polygon", with_poly["rooms"][0])

        img = render.render_region(self.dxf, REGION, WALLS, found["rooms_json"], max_px=800)
        png = Path(img["path"])
        self.assertTrue(png.is_file())
        self.assertEqual(png.read_bytes()[:8], b"\x89PNG\r\n\x1a\n")
        self.assertEqual(img["rooms_drawn"], 4)
        self.assertLessEqual(max(img["width_px"], img["height_px"]), 800)

        res = load.cooling_load(found["rooms_json"])
        table = json.loads((REPO / "heat_load_table.json").read_text(encoding="utf-8"))
        living = table["types"]["living"]["w_per_m2"]
        big = max((r for r in res["rooms"] if r["type"] == "living"), key=lambda r: r["area_m2"])
        self.assertAlmostEqual(big["load_kw"], big["area_m2"] * living * res["safety_factor"] / 1000, places=3)
        self.assertEqual(sum(1 for r in res["rooms"] if not r["cooled"]), 1)   # the shaft
        self.assertAlmostEqual(res["total_kw"], sum(r["load_kw"] for r in res["rooms"]), places=2)
        self.assertTrue(res["table_is_placeholder"])

        ids = [r["id"] for r in res["rooms"] if r["type"] != "living" and r["cooled"]]
        over = load.cooling_load(found["rooms_json"], {ids[0]: "meeting"}, safety_factor=1.0)
        self.assertEqual({r["id"]: r for r in over["rooms"]}[ids[0]]["type"], "meeting")
        self.assertEqual(over["safety_factor"], 1.0)

    def test_redact_hides_room_name_in_find_rooms(self):
        doc = ezdxf.readfile(self.dxf)
        doc.modelspace().add_text("PHONG HOP CONG TY ACME CORPORATION", dxfattribs={"layer": "A-Text", "insert": (7500, 1500)})
        doc.saveas(self.dxf)
        found = rooms.find_rooms(self.dxf, REGION, WALLS, mode="redact")
        self.assertNotIn("ACME", json.dumps(found))

    def test_find_rooms_requires_layers(self):
        with self.assertRaises(ValueError):
            rooms.find_rooms(self.dxf, REGION, [])


class DrawingTests(Base):
    def test_list_and_convert_cache_with_fake_converter(self):
        dwg = self.tmp / "CAD" / "Plan A.dwg"
        dwg.write_bytes(b"fake dwg")
        calls = []

        def fake(src, out):
            calls.append(src)
            shutil.copyfile(self.dxf, out)

        listed = drawings.list_drawings()
        self.assertEqual(listed, [{"name": "Plan A.dwg", "type": "dwg", "size_mb": 0.0, "converted": False}])
        first = drawings.convert_dwg("Plan A.dwg", fake)
        self.assertFalse(first["cached"])
        self.assertTrue(Path(first["dxf"]).is_file())
        second = drawings.convert_dwg("plan a.dwg", fake)
        self.assertTrue(second["cached"])
        self.assertEqual(len(calls), 1)
        self.assertTrue(drawings.list_drawings()[0]["converted"])
        # source changed -> new cache key -> converts again
        dwg.write_bytes(b"fake dwg, edited")
        third = drawings.convert_dwg("Plan A.dwg", fake)
        self.assertFalse(third["cached"])
        self.assertEqual(len(calls), 2)
        self.assertEqual(drawings.resolve_dxf("Plan A.dwg", fake), Path(third["dxf"]))

    def test_incomplete_dxf_is_rejected(self):
        dwg = self.tmp / "CAD" / "x.dwg"
        dwg.write_bytes(b"x")

        def truncated(src, out):
            out.write_text("0\nSECTION\n", encoding="ascii")

        with self.assertRaises(RuntimeError):
            drawings.convert_dwg("x.dwg", truncated)

    def test_unknown_drawing(self):
        with self.assertRaises(FileNotFoundError):
            drawings.convert_dwg("nope.dwg")

    def test_overview_is_cached_and_never_writes_next_to_the_source(self):
        before = sorted(p.name for p in self.tmp.iterdir() if p.name != "work")
        first = overview.drawing_overview(self.dxf)
        second = overview.drawing_overview(self.dxf)
        self.assertFalse(first["cached"])
        self.assertTrue(second["cached"])
        self.assertEqual(first["views"], second["views"])
        self.assertEqual(before, sorted(p.name for p in self.tmp.iterdir() if p.name != "work"))


class McpEndToEndTests(Base):
    def test_stdio_server_lists_tools_and_profiles(self):
        from mcp import Client
        from mcp.client.stdio import StdioServerParameters

        params = StdioServerParameters(
            command=sys.executable, args=[str(REPO / "mcp_server.py")], cwd=str(REPO),
            env={"AITOOLS_CONFIG": os.environ["AITOOLS_CONFIG"]})

        async def go():
            async with Client(params) as client:
                tools = {t.name for t in (await client.list_tools()).tools}
                prof = await client.call_tool("layer_profile", {"drawing": str(self.dxf)})
                bad = await client.call_tool("layer_profile", {"drawing": "missing.dxf"})
                img = await client.call_tool("render_region", {"drawing": str(self.dxf), "region": REGION,
                                                               "wall_layers": WALLS})
                return tools, prof, bad, img

        tools, prof, bad, img = asyncio.run(go())
        self.assertEqual(tools, {"list_drawings", "convert_dwg", "drawing_overview", "layer_profile",
                                 "render_region", "find_rooms", "cooling_load", "get_privacy_mode"})
        self.assertFalse(prof.is_error)
        data = json.loads(prof.content[0].text)
        self.assertEqual(data["layers"][0]["layer"], "A-Tuong")
        self.assertTrue(bad.is_error)
        self.assertIn("failed", bad.content[0].text)
        self.assertFalse(img.is_error)
        self.assertEqual({c.type for c in img.content}, {"image", "text"})
        self.assertTrue((self.tmp / "work" / "mcp_server.log").is_file())


if __name__ == "__main__":
    unittest.main()
