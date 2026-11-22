import json
import tempfile
import unittest
from pathlib import Path

import heat_load

REPO = Path(__file__).resolve().parents[1]
TABLE = json.loads((REPO / "heat_load_table.json").read_text(encoding="utf-8"))


def calc(rooms, **extra):
    return heat_load.calculate({"rooms": rooms, **extra}, TABLE)


def one(name, kind="room", area=20.0, **extra):
    return calc([{"id": "R1", "name": name, "kind": kind, "area_m2": area, **extra}])["rooms"][0]


class ClassifyTests(unittest.TestCase):
    def test_unicode_and_accent_free_names(self):
        cases = {
            "PHÒNG NGỦ 1": "bedroom", "phong ngu": "bedroom", "Master bedroom": "bedroom",
            "Phòng khách": "living", "PHONG KHACH": "living",
            "Văn phòng": "office", "VAN PHONG": "office", "Office 2": "office",
            "Phòng họp": "meeting", "HỘI TRƯỜNG": "meeting",
            "Sảnh": "lobby", "Hành lang": "lobby",
            "Nhà hàng": "dining", "BẾP ĂN": "dining", "Bếp": "kitchen",
            "SUP.04": "hotel", "dlx.02": "hotel", "Phòng khách sạn": "hotel",
            "Cửa hàng": "retail", "Gym": "gym",
            "WC": "wc", "Vệ sinh": "wc", "Kho": "storage", "Phòng kỹ thuật": "technical",
            "Cầu thang": "stair", "Ban công": "balcony", "Lô gia": "balcony",
            "Phòng lạ": "other", "": "other",
        }
        for name, expected in cases.items():
            self.assertEqual(one(name)["room_type"], expected, name)

    def test_not_cooled_types_are_zero(self):
        for name in ("WC", "Kho", "Cầu thang", "Ban công", "Kỹ thuật"):
            room = one(name)
            self.assertFalse(room["cooled"])
            self.assertEqual(room["load_kw"], 0.0)

    def test_shaft_kind_is_zero(self):
        room = one("anything", kind="shaft")
        self.assertEqual((room["room_type"], room["load_kw"]), ("shaft", 0.0))

    def test_other_uses_default(self):
        room = one("Phòng lạ", area=10)
        self.assertEqual(room["w_per_m2"], TABLE["types"]["other"]["w_per_m2"])
        self.assertTrue(room["cooled"])


class LoadTests(unittest.TestCase):
    def test_formula_and_total(self):
        result = calc([
            {"id": "A", "name": "Phòng ngủ", "area_m2": 20},
            {"id": "B", "name": "WC", "area_m2": 5},
        ])
        w = TABLE["types"]["bedroom"]["w_per_m2"]
        expected = 20 * w * TABLE["safety_factor"] / 1000
        self.assertAlmostEqual(result["rooms"][0]["load_kw"], expected, places=3)
        self.assertAlmostEqual(result["total_kw"], expected, places=3)

    def test_safety_factor_override(self):
        w = TABLE["types"]["office"]["w_per_m2"]
        result = calc([{"id": "A", "name": "Office", "area_m2": 10}], safety_factor=1.0)
        self.assertAlmostEqual(result["rooms"][0]["load_kw"], 10 * w / 1000, places=3)

    def test_override_type(self):
        room = one("Phòng ngủ", room_type_override="meeting")
        self.assertEqual(room["room_type"], "meeting")
        room = one("Phòng ngủ", room_type_override="nonsense")
        self.assertEqual(room["room_type"], "other")


class FileTests(unittest.TestCase):
    def test_table_from_temp_file_and_cli(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            (folder / "t.json").write_text(json.dumps({
                "version": 1, "safety_factor": 1.0,
                "types": {"x": {"w_per_m2": 100, "cooled": True, "label_vi": "X", "keywords": ["zeta"]},
                          "other": {"w_per_m2": 50, "cooled": True, "label_vi": "O", "keywords": []}}}),
                encoding="utf-8")
            (folder / "request.json").write_text(json.dumps({
                "version": 1, "out": "r.json", "table": "t.json",
                "rooms": [{"id": "R1", "name": "Zeta room", "kind": "room", "area_m2": 10},
                          {"id": "R2", "name": "none", "kind": "room", "area_m2": 10}]}), encoding="utf-8")
            self.assertEqual(heat_load.main(["--request", str(folder / "request.json")]), 0)
            result = json.loads((folder / "r.json").read_text(encoding="utf-8"))
            self.assertEqual([r["room_type"] for r in result["rooms"]], ["x", "other"])
            self.assertAlmostEqual(result["total_kw"], 1.5)

    def test_missing_table_gives_error_result(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            (folder / "request.json").write_text(json.dumps({
                "version": 1, "out": "r.json", "table": "missing.json", "rooms": []}), encoding="utf-8")
            self.assertEqual(heat_load.main(["--request", str(folder / "request.json")]), 1)
            result = json.loads((folder / "r.json").read_text(encoding="utf-8"))
            self.assertEqual(result["status"], "error")
            self.assertIn("not found", result["message"])


if __name__ == "__main__":
    unittest.main()
