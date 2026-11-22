import json
import subprocess
import sys
import unittest
import uuid
from pathlib import Path

import ezdxf

from arch_rooms import run_request


def rect(x1, y1, x2, y2):
    return [(x1, y1), (x2, y1), (x2, y2), (x1, y2)]


def build_plan(path: Path) -> None:
    """10 x 6 m flat with double-line walls.

    Left room 4.8 x 5.6 (a 1.2 x 1.2 X-marked shaft in its top-left corner), a
    partition at x = 5000..5200 with a 900 door (swing arc on the door layer), and
    in the right half a partition at y = 3000..3200 with a 1500 opening and no door.
    A room far to the right lies outside the selection region.
    """
    doc = ezdxf.new("R2018")
    msp = doc.modelspace()
    wall = {"layer": "A-Tuong"}
    door = {"layer": "cua-doors"}
    msp.add_lwpolyline(rect(0, 0, 10000, 6000), close=True, dxfattribs=wall)
    msp.add_lwpolyline(rect(200, 200, 9800, 5800), close=True, dxfattribs=wall)
    # vertical partition with a door opening at y = 1000..1900
    msp.add_lwpolyline(rect(5000, 200, 5200, 1000), close=True, dxfattribs=wall)
    msp.add_lwpolyline(rect(5000, 1900, 5200, 5800), close=True, dxfattribs=wall)
    msp.add_arc((5200, 1000), 900, 0, 90, dxfattribs=door)
    msp.add_line((5200, 1000), (6100, 1000), dxfattribs=door)
    # horizontal partition with a 1500 opening at x = 7000..8500
    msp.add_lwpolyline(rect(5200, 3000, 7000, 3200), close=True, dxfattribs=wall)
    msp.add_lwpolyline(rect(8500, 3000, 9800, 3200), close=True, dxfattribs=wall)
    # X-marked shaft, X on the wall layer
    msp.add_line((1400, 4600), (1400, 5800), dxfattribs=wall)
    msp.add_line((200, 4600), (1400, 4600), dxfattribs=wall)
    msp.add_line((200, 4600), (1400, 5800), dxfattribs=wall)
    msp.add_line((200, 5800), (1400, 4600), dxfattribs=wall)
    # furniture on an unselected layer
    msp.add_lwpolyline(rect(2000, 1000, 4000, 3000), close=True, dxfattribs={"layer": "NOI THAT"})
    msp.add_text("PHÒNG KHÁCH", dxfattribs={"layer": "A-Text", "insert": (2500, 3500)})
    msp.add_text("DT. 25,4M2", dxfattribs={"layer": "A-Text", "insert": (2500, 3000)})
    # outside the region
    msp.add_lwpolyline(rect(20000, 0, 24000, 4000), close=True, dxfattribs=wall)
    msp.add_lwpolyline(rect(20200, 200, 23800, 3800), close=True, dxfattribs=wall)
    doc.saveas(path)


class ArchRoomsTests(unittest.TestCase):
    def workspace_tmpdir(self):
        root = Path.cwd() / ".tmp_test_runs"
        root.mkdir(exist_ok=True)
        run_dir = root / f"rooms_{uuid.uuid4().hex}"
        run_dir.mkdir()
        return run_dir

    def write_request(self, tmp: Path, **overrides) -> Path:
        build_plan(tmp / "snapshot.dxf")
        request = {
            "version": 1, "dxf": "snapshot.dxf", "out": "rooms.json",
            "wall_layers": ["A-Tuong", "cua-doors"],
            "region": [[-500, -500], [10500, -500], [10500, 6500], [-500, 6500]],
        }
        request.update(overrides)
        path = tmp / "request.json"
        path.write_text(json.dumps(request, ensure_ascii=False), encoding="utf-8")
        return path

    def test_polygonize_closes_door_and_opening_and_finds_x_shaft(self):
        result = run_request(self.write_request(self.workspace_tmpdir()))
        self.assertEqual(result["status"], "ok")
        rooms = sorted((r for r in result["rooms"] if r["kind"] == "room"), key=lambda r: -r["area_m2"])
        areas = [r["area_m2"] for r in rooms]
        self.assertEqual(len(rooms), 3, areas)
        self.assertAlmostEqual(areas[0], 26.88 - 1.44, delta=0.05)   # left room minus shaft
        self.assertAlmostEqual(areas[1], 12.88, delta=0.05)          # right, below the 1500 opening
        self.assertAlmostEqual(areas[2], 11.96, delta=0.05)          # right, above it
        self.assertEqual(rooms[0]["name"], "PHÒNG KHÁCH")
        self.assertAlmostEqual(rooms[0]["label_error_pct"], 0.2, delta=0.5)
        shafts = [r for r in result["rooms"] if r["kind"] == "shaft"]
        self.assertEqual(len(shafts), 1)
        self.assertAlmostEqual(shafts[0]["area_m2"], 1.44, delta=0.05)
        self.assertEqual(len(result["envelope"]), 1)

    def test_room_polygon_is_an_open_ring(self):
        result = run_request(self.write_request(self.workspace_tmpdir()))
        ring = result["rooms"][0]["polygon"]
        self.assertNotEqual(ring[0], ring[-1])
        self.assertGreaterEqual(len(ring), 4)

    def test_split_layer_divides_an_open_space(self):
        tmp = self.workspace_tmpdir()
        path = self.write_request(tmp)
        doc = ezdxf.readfile(tmp / "snapshot.dxf")
        # split the left room at x = 3000 with a user line that is not a ticked layer
        doc.modelspace().add_line((3000, 200), (3000, 5800), dxfattribs={"layer": "HVAC-ROOM-SPLIT"})
        doc.saveas(tmp / "snapshot.dxf")
        result = run_request(path)
        left = sorted(r["area_m2"] for r in result["rooms"] if r["kind"] == "room" and r["label_point"][0] < 5000)
        self.assertEqual(len(left), 2, left)
        self.assertAlmostEqual(sum(left), 26.88 - 1.44, delta=0.05)

    def test_label_point_is_inside_the_room(self):
        from shapely.geometry import Point, Polygon
        result = run_request(self.write_request(self.workspace_tmpdir()))
        for room in result["rooms"]:
            self.assertTrue(Polygon(room["polygon"]).contains(Point(room["label_point"])), room["id"])

    def test_unticked_layer_is_ignored(self):
        result = run_request(self.write_request(self.workspace_tmpdir(), wall_layers=["cua-doors"]))
        self.assertEqual(result["status"], "ok")
        self.assertEqual(result["rooms"], [])
        self.assertTrue(result["warnings"] == [] or result["stats"]["wall_curves"] > 0)

    def test_errors_are_written_to_the_result_file(self):
        tmp = self.workspace_tmpdir()
        path = self.write_request(tmp, dxf="missing.dxf")
        completed = subprocess.run([sys.executable, "arch_rooms.py", "--request", str(path)],
                                   capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(completed.returncode, 1)
        result = json.loads((tmp / "rooms.json").read_text(encoding="utf-8"))
        self.assertEqual(result["status"], "error")
        self.assertIn("missing.dxf", result["message"])


if __name__ == "__main__":
    unittest.main()
