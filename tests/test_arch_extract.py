import json
import subprocess
import sys
import unittest
import uuid
from pathlib import Path

import ezdxf

from arch_extract import clean_layer, parse_area_label, run
from tcvn3 import decode_cad_text, normalize


def rect(x1, y1, x2, y2):
    return [(x1, y1), (x2, y1), (x2, y2), (x1, y2)]


def build_sample(path: Path) -> None:
    """Plan at the origin (two rooms, a door, a shaft) and a bound-xref section far away."""
    doc = ezdxf.new("R2018")
    msp = doc.modelspace()
    wall = {"layer": "A-Tuong"}
    # outer wall 200 thick around 8000 x 4000
    msp.add_lwpolyline(rect(0, 0, 8000, 4000), close=True, dxfattribs=wall)
    msp.add_lwpolyline(rect(200, 200, 7800, 3800), close=True, dxfattribs=wall)
    # partition x = 4000..4200 with a 900 door opening at y = 1500..2400
    for x in (4000, 4200):
        msp.add_line((x, 200), (x, 1500), dxfattribs=wall)
        msp.add_line((x, 2400), (x, 3800), dxfattribs=wall)
    # shaft 1200 x 1200 in the right room's top-right corner
    msp.add_line((6600, 2600), (6600, 3800), dxfattribs=wall)
    msp.add_line((6600, 2600), (7800, 2600), dxfattribs=wall)
    # an unlabelled shaft 1200 x 1200 in the left room, marked only by an X drawn on
    # the wall layer (the X must not cut it into triangles)
    msp.add_line((1400, 200), (1400, 1400), dxfattribs=wall)
    msp.add_line((200, 1400), (1400, 1400), dxfattribs=wall)
    msp.add_line((200, 200), (1400, 1400), dxfattribs=wall)
    msp.add_line((200, 1400), (1400, 200), dxfattribs=wall)
    # a sheet frame around the plan must not swallow it into one view
    msp.add_lwpolyline(rect(-5000, -5000, 20000, 10000), close=True, dxfattribs=wall)
    # furniture must be ignored
    msp.add_line((500, 500), (3500, 3500), dxfattribs={"layer": "NOI THAT"})
    # texts: TCVN3 room name + area label, Unicode name, shaft tag, TCVN3 title
    msp.add_text("phßng kh¸ch", dxfattribs={"layer": "A-Text", "insert": (1500, 2000)})
    msp.add_text("DT. 12,2M2", dxfattribs={"layer": "A-Text", "insert": (1500, 1500)})
    msp.add_text("PHÒNG NGỦ", dxfattribs={"layer": "A-Text", "insert": (5000, 1500)})
    msp.add_text("HKT", dxfattribs={"layer": "A-Text", "insert": (7000, 3000)})
    msp.add_text("MÆT B»NG TÇNG 1", dxfattribs={"layer": "A-Text", "insert": (3000, -1500)})

    # nested blocks: ROOMBOX (layer 0 walls) inside a bound xref block
    roombox = doc.blocks.new("XR-A$0$ROOMBOX")
    roombox.add_lwpolyline(rect(0, 0, 3000, 3000), close=True)
    roombox.add_lwpolyline(rect(200, 200, 2800, 2800), close=True)
    plan = doc.blocks.new("XR-A$0$PLAN")
    plan.add_blockref("XR-A$0$ROOMBOX", (0, 0), dxfattribs={"layer": "XR-A$0$A-Tuong"})
    msp.add_blockref("XR-A$0$PLAN", (50000, 0))
    msp.add_text("MẶT CẮT A-A", dxfattribs={"layer": "A-Text", "insert": (51000, -1500)})

    # an obsolete sheet crossed out with an X (one arm on a wall layer)
    msp.add_lwpolyline(rect(100000, 0, 108000, 6000), close=True, dxfattribs=wall)
    msp.add_lwpolyline(rect(100200, 200, 107800, 5800), close=True, dxfattribs=wall)
    msp.add_text("MẶT BẰNG TẦNG 2", dxfattribs={"layer": "A-Text", "insert": (103000, -1500)})
    msp.add_line((95000, -10000), (113000, 12000), dxfattribs=wall)
    msp.add_line((95000, 12000), (113000, -10000), dxfattribs={"layer": "Net cat"})
    doc.saveas(path)


class Tcvn3Tests(unittest.TestCase):
    def test_decodes_uppercase_title(self):
        self.assertEqual(decode_cad_text("MÆT B»NG Bè TRÝ NéI THÊT TÇNG HÇM"),
                         "MẶT BẰNG BỐ TRÍ NỘI THẤT TẦNG HẦM")

    def test_decodes_lowercase_and_strips_underline(self):
        self.assertEqual(decode_cad_text("phßng thê"), "phòng thờ")
        self.assertEqual(decode_cad_text("%%UmÆt c¾t"), "mặt cắt")
        self.assertEqual(decode_cad_text("vØa hÌ"), "vỉa hè")
        self.assertEqual(decode_cad_text("tÇng HÇM"), "tầng HẦM")

    def test_leaves_unicode_and_symbols_alone(self):
        for text in ("PHÒNG HỌP- 80 NGƯỜI", "PHÒNG KHÁCH", "Phòng ăn", "Ø250", "600×300", "会议室", "DN350"):
            self.assertEqual(decode_cad_text(text), text)

    def test_vn_font_forces_decoding(self):
        self.assertEqual(decode_cad_text("®æ", font=".VnTime.ttf"), "đổ")
        self.assertEqual(decode_cad_text("®æ", font=".VnTimeH.ttf"), "ĐỔ")

    def test_normalize(self):
        self.assertEqual(normalize("MẶT BẰNG  Đường"), "mat bang duong")


class ArchHelperTests(unittest.TestCase):
    def test_clean_layer_strips_bound_xref_prefix(self):
        self.assertEqual(clean_layer("XR nhà$0$Xr_ MB$0$CJSC.2-Tuong"), "CJSC.2-Tuong")
        self.assertEqual(clean_layer(None), "0")

    def test_parse_area_label(self):
        self.assertEqual(parse_area_label("DT. 26,2M2"), 26.2)
        self.assertEqual(parse_area_label("DT. 30 M2"), 30.0)
        self.assertEqual(parse_area_label("30m²"), 30.0)
        self.assertIsNone(parse_area_label("DN350"))
        self.assertIsNone(parse_area_label("2950"))


class ArchExtractTests(unittest.TestCase):
    def workspace_tmpdir(self):
        root = Path.cwd() / ".tmp_test_runs"
        root.mkdir(exist_ok=True)
        run_dir = root / f"arch_{uuid.uuid4().hex}"
        run_dir.mkdir()
        return run_dir

    def run_sample(self):
        tmp = self.workspace_tmpdir()
        build_sample(tmp / "sample.dxf")
        result = run(tmp / "sample.dxf", tmp / "out", {"min_plan_area_m2": 20})
        payload = json.loads((tmp / "out" / "arch.json").read_text(encoding="utf-8"))
        return tmp, result, payload

    def test_finds_rooms_shaft_and_scores_area_label(self):
        _tmp, result, payload = self.run_sample()
        self.assertEqual(result["views"], 3)
        self.assertEqual(result["plan_views"], 1)
        self.assertEqual(result["crossed_out_views"], 1)
        self.assertEqual(result["rooms_in_plans"], 2)
        self.assertEqual(result["shafts_in_plans"], 2)
        self.assertEqual(result["labelled_within_5pct"], 1)

        plan = next(v for v in payload["views"] if v["kind"] == "plan")
        self.assertEqual(plan["title"], "MẶT BẰNG TẦNG 1")
        self.assertEqual(plan["floor"], "1")
        rooms = {r["name"]: r for r in plan["rooms"]}
        self.assertAlmostEqual(rooms["phòng khách"]["area_m2"], 13.68 - 1.44, delta=0.1)
        self.assertAlmostEqual(rooms["PHÒNG NGỦ"]["area_m2"], 12.96 - 1.44, delta=0.1)
        shafts = sorted(plan["shafts"], key=lambda s: s["name"] or "")
        self.assertEqual([s["name"] for s in shafts], [None, "HKT"])  # X-marked, then labelled
        for shaft in shafts:
            self.assertAlmostEqual(shaft["area_m2"], 1.44, delta=0.05)
        self.assertEqual(len(plan["envelope"]), 1)

    def test_reads_walls_inside_nested_bound_xref_blocks(self):
        _tmp, _result, payload = self.run_sample()
        other = next(v for v in payload["views"] if v["kind"] == "other")
        self.assertEqual(other["title"], "MẶT CẮT A-A")
        self.assertEqual(len(other["rooms"]), 1)
        self.assertAlmostEqual(other["rooms"][0]["area_m2"], 6.76, delta=0.1)

    def test_crossed_out_sheet_is_not_a_plan(self):
        _tmp, _result, payload = self.run_sample()
        crossed = next(v for v in payload["views"] if v["kind"] == "crossed_out")
        self.assertEqual(crossed["title"], "MẶT BẰNG TẦNG 2")
        self.assertEqual(len(crossed["rooms"]), 1)  # the X did not split the room

    def test_cli_writes_json_and_overlay(self):
        tmp = self.workspace_tmpdir()
        build_sample(tmp / "sample.dxf")
        (tmp / "cfg.json").write_text(json.dumps({"min_plan_area_m2": 20}), encoding="utf-8")
        completed = subprocess.run(
            [sys.executable, "arch_extract.py", "--input", str(tmp / "sample.dxf"), "--out-dir", str(tmp / "cli"),
             "--config", str(tmp / "cfg.json")],
            capture_output=True, text=True, encoding="utf-8", check=True,
        )
        summary = json.loads(completed.stdout)
        self.assertEqual(summary["rooms_in_plans"], 2)
        overlay = ezdxf.readfile(tmp / "cli" / "arch_overlay.dxf")
        layers = {e.dxf.layer for e in overlay.modelspace()}
        self.assertTrue({"ARCH0_ROOM", "ARCH0_SHAFT", "ARCH0_ENVELOPE", "ARCH0_VIEW"} <= layers)


if __name__ == "__main__":
    unittest.main()
