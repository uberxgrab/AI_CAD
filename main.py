import argparse
import json
from collections import Counter
from pathlib import Path

from geometry import build_all_segments, build_graph, extract_ducts
from parser import extract_entities, load_dxf
from reporting import (
    export_feedback_template,
    export_features,
    export_overlay,
    export_report,
)
from rules import run_all_rules
from cad_addin.python_bridge import cad_addin_status


DEFAULT_CONFIG = {
    "snap_tolerance": 50.0,
    "snap_warning_distance": 250.0,
    "min_segment_length": 5.0,
    "overlap_tolerance": 1.0,
    "crossing_tolerance": 5.0,
    "component_min_size": 1,
    "main_component_strategy": "longest_total_length",
    "annotation_layer": "HVAC_ERRORS",
    "annotation_text_height": 120.0,
    "annotation_marker_radius": 75.0,
    "precision_digits": 3,
    "allowed_open_endpoint_layers": [],
    "include_layers": [],
    "exclude_layers": [],
    "loop_max_perimeter": 1500.0,
    "loop_max_nodes": 6,
}


def load_config(path: str | None) -> dict:
    config = dict(DEFAULT_CONFIG)
    if path:
        with open(path, "r", encoding="utf-8") as fh:
            config.update(json.load(fh))
    return config


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Validate HVAC CAD geometry from DXF and export reports."
    )
    parser.add_argument("--input", help="Path to the DXF input file.")
    parser.add_argument("--report", help="Path to the report file (.json or .csv).")
    parser.add_argument("--overlay", help="Path to the annotated DXF output.")
    parser.add_argument("--config", help="Optional JSON config overriding default thresholds.")
    parser.add_argument("--features", help="Optional JSONL feature export path.")
    parser.add_argument("--feedback-template", help="Optional JSONL feedback template export path.")
    parser.add_argument(
        "--cad-status",
        action="store_true",
        help="Show the separate AutoCAD ARX add-in build and installation status.",
    )
    return parser


def main() -> int:
    args = build_arg_parser().parse_args()
    if args.cad_status:
        print(json.dumps(cad_addin_status(), indent=2))
        return 0
    if not all((args.input, args.report, args.overlay)):
        raise SystemExit("--input, --report, and --overlay are required unless --cad-status is used.")
    config = load_config(args.config)

    doc, msp = load_dxf(args.input)
    raw_entities = extract_entities(msp, config)
    ducts = extract_ducts(raw_entities)
    segments = build_all_segments(ducts, config)
    graph = build_graph(segments, config)
    errors = run_all_rules(graph, segments, config)

    report_path = Path(args.report)
    overlay_path = Path(args.overlay)
    features_path = Path(args.features) if args.features else report_path.with_name("features.jsonl")
    feedback_path = (
        Path(args.feedback_template)
        if args.feedback_template
        else report_path.with_name("feedback_template.jsonl")
    )

    for path in (report_path, overlay_path, features_path, feedback_path):
        path.parent.mkdir(parents=True, exist_ok=True)

    export_report(errors, report_path)
    export_overlay(doc, errors, overlay_path, config)
    export_features(segments, graph, errors, features_path)
    export_feedback_template(errors, feedback_path)

    summary = {
        "input": str(Path(args.input).resolve()),
        "raw_entities": len(raw_entities),
        "duct_primitives": len(ducts),
        "segments": len(segments),
        "graph_nodes": graph.number_of_nodes(),
        "graph_edges": graph.number_of_edges(),
        "errors": len(errors),
        "severity_counts": dict(Counter(error.severity for error in errors)),
        "type_counts": dict(Counter(error.error_type for error in errors)),
        "report": str(report_path.resolve()),
        "overlay": str(overlay_path.resolve()),
        "features": str(features_path.resolve()),
        "feedback_template": str(feedback_path.resolve()),
    }
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
