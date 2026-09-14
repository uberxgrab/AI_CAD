from __future__ import annotations

import csv
import json
from pathlib import Path

import networkx as nx

from models import ValidationError


def export_report(errors: list[ValidationError], path: str | Path) -> None:
    path = Path(path)
    if path.suffix.lower() == ".csv":
        _export_csv(errors, path)
    else:
        _export_json(errors, path)


def _export_json(errors: list[ValidationError], path: Path) -> None:
    with path.open("w", encoding="utf-8") as fh:
        json.dump([error.as_dict() for error in errors], fh, indent=2)


def _export_csv(errors: list[ValidationError], path: Path) -> None:
    fieldnames = [
        "error_id",
        "error_type",
        "severity",
        "message",
        "suggested_fix",
        "entity_handles",
        "segment_ids",
        "location_x",
        "location_y",
        "rule_name",
        "debug_features",
    ]
    with path.open("w", encoding="utf-8", newline="") as fh:
        writer = csv.DictWriter(fh, fieldnames=fieldnames)
        writer.writeheader()
        for error in errors:
            writer.writerow(
                {
                    "error_id": error.error_id,
                    "error_type": error.error_type,
                    "severity": error.severity,
                    "message": error.message,
                    "suggested_fix": error.suggested_fix,
                    "entity_handles": ";".join(error.entity_handles),
                    "segment_ids": ";".join(error.segment_ids),
                    "location_x": error.location.get("x"),
                    "location_y": error.location.get("y"),
                    "rule_name": error.rule_name,
                    "debug_features": json.dumps(error.debug_features, ensure_ascii=False),
                }
            )


def export_overlay(doc, errors: list[ValidationError], path: str | Path, config: dict) -> None:
    path = Path(path)
    layer_name = config.get("annotation_layer", "HVAC_ERRORS")
    text_height = float(config.get("annotation_text_height", 100.0))
    marker_radius = float(config.get("annotation_marker_radius", max(text_height / 2.0, 10.0)))

    if layer_name not in doc.layers:
        doc.layers.add(name=layer_name)

    msp = doc.modelspace()
    for error in errors:
        x = float(error.location["x"])
        y = float(error.location["y"])
        msp.add_circle((x, y), marker_radius, dxfattribs={"layer": layer_name, "color": 1})
        msp.add_line(
            (x - marker_radius, y - marker_radius),
            (x + marker_radius, y + marker_radius),
            dxfattribs={"layer": layer_name, "color": 1},
        )
        msp.add_line(
            (x - marker_radius, y + marker_radius),
            (x + marker_radius, y - marker_radius),
            dxfattribs={"layer": layer_name, "color": 1},
        )
        msp.add_text(
            f"{error.error_type}:{error.error_id.split(':')[-1]}",
            dxfattribs={
                "layer": layer_name,
                "height": text_height,
                "insert": (x + marker_radius * 1.2, y + marker_radius * 1.2),
                "color": 1,
            },
        )

    doc.saveas(path)


def export_features(segments, graph, errors: list[ValidationError], path: str | Path) -> None:
    path = Path(path)
    flagged_segments = set()
    crossing_segments = set()
    for error in errors:
        flagged_segments.update(error.segment_ids)
        if error.error_type == "CROSSING_WITHOUT_CONNECTION":
            crossing_segments.update(error.segment_ids)

    node_degree = {node_id: graph.degree[node_id] for node_id in graph.nodes}
    component_cache: dict[str, set[str]] = {}

    with path.open("w", encoding="utf-8") as fh:
        for segment in segments:
            component_nodes = _component_nodes(graph, segment.node_start, component_cache)
            row = {
                "segment_id": segment.id,
                "handle": segment.handle,
                "layer": segment.layer,
                "length": segment.length,
                "angle": segment.angle,
                "node_start_degree": node_degree.get(segment.node_start, 0),
                "node_end_degree": node_degree.get(segment.node_end, 0),
                "component_size": len(component_nodes),
                "distance_to_nearest_node": _distance_to_nearest_other_node(graph, segment),
                "is_flagged": segment.id in flagged_segments,
                "has_crossing": segment.id in crossing_segments,
            }
            fh.write(json.dumps(row) + "\n")


def _component_nodes(graph, node_id: str | None, cache: dict[str, set[str]]) -> set[str]:
    if node_id is None or node_id not in graph:
        return set()
    if node_id in cache:
        return cache[node_id]
    component = nx.node_connected_component(graph, node_id)
    for member in component:
        cache[member] = component
    return component


def _distance_to_nearest_other_node(graph, segment) -> float | None:
    reference_node = segment.node_start
    if reference_node is None or reference_node not in graph:
        return None
    x, y = graph.nodes[reference_node]["point"]
    min_distance = None
    for node_id, data in graph.nodes(data=True):
        if node_id in {segment.node_start, segment.node_end}:
            continue
        dx = x - data["point"][0]
        dy = y - data["point"][1]
        distance = (dx * dx + dy * dy) ** 0.5
        if min_distance is None or distance < min_distance:
            min_distance = distance
    return min_distance


def export_feedback_template(errors: list[ValidationError], path: str | Path) -> None:
    path = Path(path)
    with path.open("w", encoding="utf-8") as fh:
        for error in errors:
            fh.write(
                json.dumps(
                    {
                        "error_id": error.error_id,
                        "status": "unreviewed",
                        "is_true_positive": None,
                        "is_fixed": None,
                        "notes": "",
                    }
                )
                + "\n"
            )
