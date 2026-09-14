import json
import subprocess
import unittest
import uuid
from pathlib import Path

import ezdxf

from geometry import build_all_segments, build_graph, extract_ducts
from parser import extract_entities, load_dxf
from rules import run_all_rules


TEST_CONFIG = {
    "snap_tolerance": 0.5,
    "snap_warning_distance": 3.0,
    "min_segment_length": 0.1,
    "overlap_tolerance": 0.01,
    "crossing_tolerance": 0.01,
    "component_min_size": 1,
    "main_component_strategy": "longest_total_length",
    "annotation_layer": "HVAC_ERRORS",
    "annotation_text_height": 10.0,
    "annotation_marker_radius": 5.0,
    "precision_digits": 3,
    "allowed_open_endpoint_layers": [],
    "include_layers": [],
    "exclude_layers": [],
    "loop_max_perimeter": 30.0,
    "loop_max_nodes": 5,
}


class PipelineTests(unittest.TestCase):
    def workspace_tmpdir(self):
        root = Path.cwd() / ".tmp_test_runs"
        root.mkdir(exist_ok=True)
        run_dir = root / f"run_{uuid.uuid4().hex}"
        run_dir.mkdir()
        return run_dir

    def create_doc(self):
        doc = ezdxf.new()
        msp = doc.modelspace()
        return doc, msp

    def test_parser_extracts_lines_and_polylines(self):
        doc, msp = self.create_doc()
        msp.add_line((0, 0), (10, 0), dxfattribs={"layer": "HVAC"})
        msp.add_lwpolyline([(10, 0), (20, 0), (20, 10)], dxfattribs={"layer": "HVAC"})

        tmp = self.workspace_tmpdir()
        path = tmp / "sample.dxf"
        doc.saveas(path)
        _, loaded_msp = load_dxf(str(path))
        entities = extract_entities(loaded_msp, TEST_CONFIG)

        self.assertEqual(len(entities), 2)
        self.assertEqual(entities[0].source_type, "LINE")
        self.assertEqual(len(entities[1].points), 3)

    def test_graph_and_rules_detect_expected_issues(self):
        doc, msp = self.create_doc()
        msp.add_line((0, 0), (10, 0), dxfattribs={"layer": "HVAC"})
        msp.add_line((12, 0), (20, 0), dxfattribs={"layer": "HVAC"})
        msp.add_line((5, -5), (5, 5), dxfattribs={"layer": "HVAC"})
        msp.add_line((30, 30), (40, 30), dxfattribs={"layer": "HVAC"})
        msp.add_line((50, 50), (60, 50), dxfattribs={"layer": "HVAC"})
        msp.add_line((50, 50), (60, 50), dxfattribs={"layer": "HVAC"})
        msp.add_lwpolyline([(70, 70), (80, 70), (80, 80), (70, 80), (70, 70)], dxfattribs={"layer": "HVAC"})

        tmp = self.workspace_tmpdir()
        path = tmp / "issues.dxf"
        doc.saveas(path)
        _, loaded_msp = load_dxf(str(path))
        entities = extract_entities(loaded_msp, TEST_CONFIG)
        ducts = extract_ducts(entities)
        segments = build_all_segments(ducts, TEST_CONFIG)
        graph = build_graph(segments, TEST_CONFIG)
        errors = run_all_rules(graph, segments, TEST_CONFIG)

        error_types = {error.error_type for error in errors}
        self.assertIn("OPEN_ENDPOINT", error_types)
        self.assertIn("DISCONNECTED_CLUSTER", error_types)
        self.assertIn("NEAR_MISS_CONNECTION", error_types)
        self.assertIn("CROSSING_WITHOUT_CONNECTION", error_types)
        self.assertIn("DUPLICATE_SEGMENT", error_types)
        self.assertIn("ABNORMAL_LOOP", error_types)

    def test_cli_generates_outputs(self):
        doc, msp = self.create_doc()
        msp.add_line((0, 0), (10, 0), dxfattribs={"layer": "HVAC"})
        msp.add_line((30, 0), (40, 0), dxfattribs={"layer": "HVAC"})

        tmp_path = self.workspace_tmpdir()
        input_path = tmp_path / "input.dxf"
        report_path = tmp_path / "report.json"
        overlay_path = tmp_path / "overlay.dxf"
        config_path = tmp_path / "config.json"
        doc.saveas(input_path)
        config_path.write_text(json.dumps(TEST_CONFIG), encoding="utf-8")

        result = subprocess.run(
            [
                str(Path.cwd() / "venv" / "Scripts" / "python.exe"),
                "main.py",
                "--input",
                str(input_path),
                "--report",
                str(report_path),
                "--overlay",
                str(overlay_path),
                "--config",
                str(config_path),
            ],
            cwd=Path.cwd(),
            capture_output=True,
            text=True,
            check=True,
        )

        self.assertTrue(report_path.exists())
        self.assertTrue(overlay_path.exists())
        self.assertTrue((tmp_path / "features.jsonl").exists())
        self.assertTrue((tmp_path / "feedback_template.jsonl").exists())
        summary = json.loads(result.stdout)
        self.assertGreaterEqual(summary["errors"], 1)


if __name__ == "__main__":
    unittest.main()
