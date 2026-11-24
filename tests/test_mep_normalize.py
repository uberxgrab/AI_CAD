import json
import shutil
import unittest
import uuid
from pathlib import Path

import ezdxf
from ezdxf.math import Vec2

import arch_extract as ax
import mep_normalize as mn

REPO = Path(__file__).resolve().parents[1]
MAP = {"layers": {"M-P-WS": "WS", "M-HVAC-DUCT-SA": "SA"}}


def build_mep(path: Path, schematic: bool = False) -> None:
    """Water supply tree, a single-line supply duct and a few rule breaks."""
    doc = ezdxf.new("R2013")
    msp = doc.modelspace()
    for name in ("M-P-WS", "0-Note", "M-HVAC-DUCT-SA", "EIA_1 CHWS DN50"):
        doc.layers.add(name)
    ws = {"layer": "M-P-WS"}
    msp.add_lwpolyline([(0, 0), (20000, 0)], dxfattribs=ws)                      # trunk, tee at 10000
    msp.add_line((10000, 0), (10000, 8000), dxfattribs={"layer": "M-P-WS", "color": 1})   # branch, override
    msp.add_line((2000, 0), (4000, 0), dxfattribs=ws)                            # drawn twice
    msp.add_line((10080, 8000), (15000, 8000), dxfattribs=ws)                    # 80 mm gap
    msp.add_spline([(15000, 8000), (16000, 8500), (17000, 8000)], dxfattribs=ws)
    note = {"layer": "0-Note"}
    msp.add_text("WS-PPR-DN63", height=250, dxfattribs={**note, "insert": (3000, 150)})
    msp.add_text("WS-PPR-DN50", height=250, dxfattribs={**note, "insert": (15000, 150)})
    msp.add_text("WS-PPR-DN25", height=250, dxfattribs={**note, "insert": (10150, 3000), "rotation": 90})
    msp.add_text("WS-PPR-DN90", height=250, dxfattribs={**note, "insert": (50000, 50000)})   # points at nothing

    sa = {"layer": "M-HVAC-DUCT-SA"}
    msp.add_line((0, 20000), (12000, 20000), dxfattribs=sa)        # one line, size changes inside it
    msp.add_leader([(3000, 20000), (3500, 21000), (4500, 21000)], dxfattribs=note)
    msp.add_text("SAD-600x300", height=250, dxfattribs={**note, "insert": (4600, 20950)})
    ml = msp.add_multileader_mtext("Standard")
    ml.set_content("SAD-400x300")
    ml.add_leader_line(ezdxf.render.mleader.ConnectionSide.left, [Vec2(9000, 20000)])
    ml.build(insert=Vec2(10000, 21500))
    msp.add_line((0, 30000), (6000, 30000), dxfattribs=sa)         # fresh air drawn on the SA layer
    msp.add_text("OAD-300x300", height=250, dxfattribs={**note, "insert": (2000, 30150)})
    msp.add_text("3x(SAG-600x600)", height=250, dxfattribs={**note, "insert": (8000, 32000)})

    msp.add_line((0, 40000), (5000, 40000), dxfattribs={"layer": "EIA_1 CHWS DN50"})
    if schematic:
        doc.paperspace().add_mtext("WATER SUPPLY SYSTEM SCHEMATIC DIAGRAM", dxfattribs={"char_height": 5})
    doc.saveas(path)


class Base(unittest.TestCase):
    def setUp(self):
        root = REPO / ".tmp_test_runs"
        root.mkdir(exist_ok=True)
        self.tmp = root / f"mep_{uuid.uuid4().hex}"
        self.tmp.mkdir()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.dxf = self.tmp / "mep.dxf"
        build_mep(self.dxf)

    def run_map(self, mapping=MAP, **kw):
        return mn.run(ax.load_drawing(self.dxf), mapping, **kw)

    @staticmethod
    def entity(result, layer, start=None):
        for e in result["entities"]:
            if e["layer"] == layer and (start is None or e["groups"][0]["start"] == list(start)):
                return e
        raise AssertionError(f"no entity on {layer} starting {start}")


class LabelParserTests(unittest.TestCase):
    def items(self, text):
        return [(i.kind, i.system, i.material, mn.size_text(i)) for i in mn.parse_label(text)]

    def test_pipe_labels_of_other_offices(self):
        self.assertEqual(self.items("WS-PPR-DN90"), [("pipe", "WS", "PPR", "D90")])
        self.assertEqual(self.items("INCOMMING-WS-HDPE-DN110"), [("pipe", "WS", "HDPE", "D110")])
        self.assertEqual(self.items("SUS 316-DN50"), [("pipe", None, "SUS316", "DN50")])
        self.assertEqual(self.items("CHWS DN150"), [("pipe", "CHWS", None, "DN150")])
        slope = mn.parse_label("WW-UPVC-DN110, I=1%%%")[0]
        self.assertEqual((slope.system, slope.material, slope.size, slope.slope), ("WW", "uPVC", 110.0, 1.0))

    def test_addin_labels(self):
        self.assertEqual(self.items("%%C15.9/9.5"), [("pipe", "RP", "CU", "Ø15.9/9.5")])
        drain = mn.parse_label("D27-1%")[0]
        self.assertEqual((drain.size, drain.slope), (27.0, 1.0))

    def test_duct_and_grille_labels(self):
        self.assertEqual(self.items("SAD-1200x800\nRAD-1200x700"),
                         [("duct", "SA", None, "1200x800"), ("duct", "RA", None, "1200x700")])
        self.assertEqual(self.items("EAD-150ø"), [("duct", "EA", None, "Ø150")])
        grilles = mn.parse_label("105x(SAG WITH HEPA-600x600)\n70x(RAG-600x600)")
        self.assertEqual([(g.kind, g.system, g.count) for g in grilles], [("terminal", "SA", 105), ("terminal", "RA", 70)])
        self.assertEqual(self.items("DOOR GRILL 800x200")[0][0], "terminal")

    def test_not_labels(self):
        self.assertEqual(mn.parse_label("SCALE: 1/100"), [])
        self.assertEqual(mn.parse_label("CONNECT TO AHU-1F-2-1"), [])


class TargetLayerTests(unittest.TestCase):
    def item(self, text):
        return mn.parse_label(text)[0]

    def test_boq_material_goes_to_template_layer(self):
        self.assertEqual(mn.target_layer("CHWS", self.item("DN50"), None), "EIA_1 CHWS DN50")
        self.assertEqual(mn.target_layer("COND", self.item("D27"), "uPVC"), "EIA_1 Pipe condensated DN27")
        self.assertEqual(mn.target_layer("RP", self.item("Ø15.9/9.5"), "CU"), "EIA_1 RP 15.9-9.5 (14)")
        self.assertEqual(mn.target_layer("SA", self.item("SAD-600x300"), None), "EIA_1 SUPPLY AIR")

    def test_other_material_or_size_keeps_the_layer(self):
        self.assertIsNone(mn.target_layer("CHWS", self.item("DN50"), "PPR"))
        self.assertIsNone(mn.target_layer("CHWS", self.item("DN55"), None))
        self.assertIsNone(mn.target_layer("WS", self.item("DN50"), "PPR"))

    def test_cw_layers_are_not_in_the_template(self):
        layer = mn.target_layer("CWS", self.item("DN100"), None)
        self.assertEqual(layer, "EIA_1 CWS DN100")
        self.assertFalse(mn.target_in_template(layer))
        self.assertTrue(mn.target_in_template("EIA_1 CHWS DN100"))

    def test_standard_layers_read_back(self):
        self.assertEqual(mn.parse_standard_layer("EIA_1 CHWS DN50")[0], "CHWS")
        self.assertEqual(mn.parse_standard_layer("EIA_1 RP 28.6-12.7 (33.6-44.8)")[1].pair, (28.6, 12.7))
        self.assertIsNone(mn.parse_standard_layer("M-P-WS"))


class NormaliseTests(Base):
    def test_sizes_by_label_split_at_the_tee(self):
        result = self.run_map()
        trunk = self.entity(result, "M-P-WS", (0, 0))
        left = {(g["size_text"], g["confidence"]) for g in trunk["groups"] if g["to_mm"] <= 10000}
        right = {(g["size_text"], g["confidence"]) for g in trunk["groups"] if g["from_mm"] >= 10000}
        self.assertEqual({s for s, _ in left}, {"D63"})
        self.assertIn(("D63", "high"), left)
        self.assertEqual(right, {("D50", "high")})
        self.assertIn("split", trunk["fixes"])
        self.assertEqual(trunk["groups"][0]["boq_prefix"], "WS")
        self.assertIsNone(trunk["groups"][0]["target_layer"])
        branch = self.entity(result, "M-P-WS", (10000, 0))
        self.assertEqual(branch["groups"][0]["size_text"], "D25")
        self.assertEqual(branch["overrides"], ["color=1"])
        self.assertIn("bylayer", branch["fixes"])

    def test_gap_is_reported_and_size_carried_across_it(self):
        result = self.run_map()
        after_gap = self.entity(result, "M-P-WS", (10080, 8000))
        self.assertEqual((after_gap["groups"][0]["size_text"], after_gap["groups"][0]["confidence"]), ("D25", "medium"))
        gaps = [i for i in result["issues"] if i["type"] == "gap"]
        self.assertEqual(len(gaps), 1)
        self.assertAlmostEqual(gaps[0]["gap_mm"], 80, delta=1)
        spline = next(e for e in result["entities"] if e["type"] == "SPLINE")
        self.assertEqual(spline["groups"][0]["size_text"], "D25")
        self.assertIn("curve_type", result["issue_counts"])

    def test_duplicate_is_flagged_and_left_out_of_the_boq(self):
        result = self.run_map()
        dup = self.entity(result, "M-P-WS", (2000, 0))
        self.assertEqual(dup["fixes"], ["delete_duplicate"])
        rows = {(r["line"], r["material"], r["size"]): r for r in result["boq_preview"] if r["kind"] == "pipe"}
        self.assertAlmostEqual(rows[("WS", "PPR", "D63")]["length_m"], 10.0, places=1)
        self.assertAlmostEqual(result["stats"]["duplicate_m"], 2.0, places=1)

    def test_unused_label_is_reported(self):
        result = self.run_map()
        unused = [i for i in result["issues"] if i["type"] == "label_unused"]
        self.assertEqual([i["text"] for i in unused], ["WS-PPR-DN90"])

    def test_two_duct_labels_on_one_line_cut_it_between_them(self):
        result = self.run_map()
        duct = self.entity(result, "M-HVAC-DUCT-SA", (0, 20000))
        groups = [(g["size_text"], g["confidence"]) for g in duct["groups"]]
        self.assertEqual(groups, [("600x300", "high"), ("400x300", "high")])
        self.assertAlmostEqual(duct["groups"][0]["to_mm"], 6000, delta=50)
        self.assertEqual(duct["groups"][0]["target_layer"], "EIA_1 SUPPLY AIR")
        self.assertNotIn("label_conflict", result["issue_counts"])

    def test_label_of_another_system_moves_the_duct_to_that_system(self):
        result = self.run_map()
        oa = self.entity(result, "M-HVAC-DUCT-SA", (0, 30000))
        self.assertEqual(oa["groups"][0]["target_layer"], "EIA_1 OUTSIDE AIR")
        self.assertEqual(oa["groups"][0]["system"], "OA")
        mismatch = [i for i in result["issues"] if i["type"] == "system_mismatch"]
        self.assertEqual(len(mismatch), 1)
        self.assertEqual(mismatch[0]["layer"], "M-HVAC-DUCT-SA")

    def test_duct_area_and_grille_count_in_the_preview(self):
        rows = self.run_map()["boq_preview"]
        duct = next(r for r in rows if r["kind"] == "duct" and r["size"] == "600x300")
        self.assertAlmostEqual(duct["length_m"], 6.0, delta=0.1)
        self.assertAlmostEqual(duct["area_m2"], 2 * 0.9 * duct["length_m"], places=2)
        self.assertIn({"kind": "terminal", "line": "SA grille 600x600", "count": 3}, rows)

    def test_standard_layer_counts_with_its_own_size(self):
        result = self.run_map()
        chws = self.entity(result, "EIA_1 CHWS DN50")
        self.assertEqual((chws["groups"][0]["confidence"], chws["groups"][0]["target_layer"]),
                         ("layer", "EIA_1 CHWS DN50"))

    def test_region_limits_the_run(self):
        result = self.run_map(region=(-1000, -1000, 21000, 9000))
        self.assertEqual({e["layer"] for e in result["entities"]}, {"M-P-WS"})

    def test_schematic_sheet_warns(self):
        build_mep(self.dxf, schematic=True)
        self.assertTrue(any("SCHEMATIC" in w for w in self.run_map()["warnings"]))
        build_mep(self.dxf)
        self.assertEqual(self.run_map()["warnings"], [])


class ProposalTests(Base):
    def test_layers_are_proposed_from_the_labels_that_point_at_them(self):
        mapping, rows, _ = mn.propose_map(ax.load_drawing(self.dxf))
        self.assertEqual(mapping["layers"]["M-P-WS"], {"system": "WS"})
        self.assertEqual(mapping["layers"]["M-HVAC-DUCT-SA"]["system"], "SA")
        ws = next(r for r in rows if r["layer"] == "M-P-WS")
        self.assertGreaterEqual(ws["label_hits"], 3)
        self.assertNotIn("EIA_1 CHWS DN50", mapping["layers"])

    def test_request_round_trip(self):
        req = {"version": 1, "dxf": "mep.dxf", "out": "norm.json", "map": MAP}
        (self.tmp / "request.json").write_text(json.dumps(req), encoding="utf-8")
        result = mn.run_request(self.tmp / "request.json")
        self.assertEqual(result["status"], "ok")
        saved = json.loads((self.tmp / "norm.json").read_text(encoding="utf-8"))
        self.assertEqual(saved["stats"], result["stats"])

    def test_request_error_is_written(self):
        (self.tmp / "request.json").write_text(json.dumps({"version": 1, "dxf": "missing.dxf"}), encoding="utf-8")
        result = mn.run_request(self.tmp / "request.json")
        self.assertEqual(result["status"], "error")
        self.assertTrue((self.tmp / "norm.json").is_file())


if __name__ == "__main__":
    unittest.main()
