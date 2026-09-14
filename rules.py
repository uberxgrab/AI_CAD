from __future__ import annotations

import itertools
from collections import defaultdict

import networkx as nx
from shapely.geometry import Point
from shapely.geometry import LineString

from geometry import point_distance
from models import Segment, ValidationError


def _segment_line(segment: Segment) -> LineString:
    return LineString([segment.start, segment.end])


def _segment_midpoint(segment: Segment) -> dict[str, float]:
    return {
        "x": (segment.start[0] + segment.end[0]) / 2.0,
        "y": (segment.start[1] + segment.end[1]) / 2.0,
    }


def _dedupe_handles(segments: list[Segment]) -> list[str]:
    return sorted({segment.handle for segment in segments})


def detect_open_endpoints(graph: nx.Graph, segments: list[Segment], config: dict) -> list[ValidationError]:
    allowed_layers = {layer.lower() for layer in config.get("allowed_open_endpoint_layers", [])}
    errors: list[ValidationError] = []
    edges_by_node: dict[str, list[str]] = defaultdict(list)
    segments_by_id = {segment.id: segment for segment in segments}

    for left, right, data in graph.edges(data=True):
        edges_by_node[left].append(data["segment_id"])
        edges_by_node[right].append(data["segment_id"])

    for node_id, degree in graph.degree():
        if degree != 1:
            continue
        segment_id = edges_by_node[node_id][0]
        segment = segments_by_id[segment_id]
        if segment.layer.lower() in allowed_layers:
            continue
        point = graph.nodes[node_id]["point"]
        errors.append(
            ValidationError(
                error_id=f"OPEN_ENDPOINT:{node_id}",
                error_type="OPEN_ENDPOINT",
                severity="high",
                message="Endpoint has no matching continuation in the HVAC network.",
                suggested_fix="Connect this endpoint to the intended trunk or verify a missing duct segment.",
                entity_handles=[segment.handle],
                segment_ids=[segment.id],
                location={"x": point[0], "y": point[1]},
                rule_name="detect_open_endpoints",
                debug_features={"node_degree": degree, "layer": segment.layer},
            )
        )
    return errors


def detect_disconnected_components(graph: nx.Graph, segments: list[Segment], config: dict) -> list[ValidationError]:
    if graph.number_of_edges() == 0:
        return []

    component_min_size = int(config.get("component_min_size", 1))
    segments_by_id = {segment.id: segment for segment in segments}
    components = []
    for nodes in nx.connected_components(graph):
        total_length = 0.0
        segment_ids = []
        for _, _, data in graph.subgraph(nodes).edges(data=True):
            total_length += float(data.get("length", 0.0))
            segment_ids.append(data["segment_id"])
        components.append((nodes, total_length, segment_ids))

    components.sort(key=lambda item: item[1], reverse=True)
    main_nodes, _, _ = components[0]
    errors: list[ValidationError] = []

    for nodes, total_length, segment_ids in components[1:]:
        if len(segment_ids) < component_min_size:
            continue
        involved = [segments_by_id[segment_id] for segment_id in segment_ids]
        points = [graph.nodes[node_id]["point"] for node_id in nodes]
        centroid_x = sum(point[0] for point in points) / len(points)
        centroid_y = sum(point[1] for point in points) / len(points)
        errors.append(
            ValidationError(
                error_id=f"DISCONNECTED_CLUSTER:{len(errors)+1}",
                error_type="DISCONNECTED_CLUSTER",
                severity="high",
                message="A disconnected duct cluster was found outside the main HVAC network.",
                suggested_fix="Check for missing connecting segments, layer mismatches, or accidental detached geometry.",
                entity_handles=_dedupe_handles(involved),
                segment_ids=sorted(segment_ids),
                location={"x": centroid_x, "y": centroid_y},
                rule_name="detect_disconnected_components",
                debug_features={
                    "component_edges": len(segment_ids),
                    "component_total_length": total_length,
                    "main_component_nodes": len(main_nodes),
                },
            )
        )
    return errors


def detect_near_miss_connections(segments: list[Segment], config: dict) -> list[ValidationError]:
    snap_tolerance = float(config.get("snap_tolerance", 0.0))
    warning_distance = float(config.get("snap_warning_distance", snap_tolerance))
    errors: list[ValidationError] = []
    endpoints = []

    for segment in segments:
        if segment.original_start:
            endpoints.append((segment, segment.original_start, segment.node_start))
        if segment.original_end:
            endpoints.append((segment, segment.original_end, segment.node_end))

    seen_pairs = set()
    for left, right in itertools.combinations(endpoints, 2):
        left_segment, left_point, left_node = left
        right_segment, right_point, right_node = right
        if left_segment.id == right_segment.id or left_node == right_node:
            continue
        pair_key = tuple(sorted((left_segment.id, right_segment.id)))
        if pair_key in seen_pairs:
            continue
        distance = point_distance(left_point, right_point)
        if snap_tolerance < distance <= warning_distance:
            seen_pairs.add(pair_key)
            errors.append(
                ValidationError(
                    error_id=f"NEAR_MISS_CONNECTION:{left_segment.id}:{right_segment.id}",
                    error_type="NEAR_MISS_CONNECTION",
                    severity="medium",
                    message="Two nearby duct endpoints are close enough to indicate a probable missed connection.",
                    suggested_fix="Review the gap and snap or extend the duct endpoints if they should join.",
                    entity_handles=_dedupe_handles([left_segment, right_segment]),
                    segment_ids=sorted([left_segment.id, right_segment.id]),
                    location={"x": (left_point[0] + right_point[0]) / 2.0, "y": (left_point[1] + right_point[1]) / 2.0},
                    rule_name="detect_near_miss_connections",
                    debug_features={"distance": distance},
                )
            )
    return errors


def detect_crossing_without_node(segments: list[Segment], graph: nx.Graph, config: dict) -> list[ValidationError]:
    tolerance = float(config.get("crossing_tolerance", 0.0))
    errors: list[ValidationError] = []
    graph_points = [Point(data["point"]) for _, data in graph.nodes(data=True)]
    seen_pairs = set()

    for left, right in itertools.combinations(segments, 2):
        pair_key = tuple(sorted((left.id, right.id)))
        if pair_key in seen_pairs:
            continue
        if {left.node_start, left.node_end} & {right.node_start, right.node_end}:
            continue
        if left.bbox[2] < right.bbox[0] or right.bbox[2] < left.bbox[0]:
            continue
        if left.bbox[3] < right.bbox[1] or right.bbox[3] < left.bbox[1]:
            continue

        intersection = _segment_line(left).intersection(_segment_line(right))
        if intersection.is_empty or intersection.geom_type != "Point":
            continue
        if any(intersection.distance(node_point) <= tolerance for node_point in graph_points):
            continue

        seen_pairs.add(pair_key)
        errors.append(
            ValidationError(
                error_id=f"CROSSING_WITHOUT_CONNECTION:{left.id}:{right.id}",
                error_type="CROSSING_WITHOUT_CONNECTION",
                severity="medium",
                message="Two duct segments cross geometrically without a graph node at the intersection.",
                suggested_fix="Confirm whether this crossing should be a junction or whether one segment belongs to a different elevation.",
                entity_handles=_dedupe_handles([left, right]),
                segment_ids=sorted([left.id, right.id]),
                location={"x": intersection.x, "y": intersection.y},
                rule_name="detect_crossing_without_node",
                debug_features={"crossing_tolerance": tolerance},
            )
        )
    return errors


def detect_duplicate_or_overlapping_segments(segments: list[Segment], config: dict) -> list[ValidationError]:
    tolerance = float(config.get("overlap_tolerance", 0.0))
    errors: list[ValidationError] = []
    seen_pairs = set()

    for left, right in itertools.combinations(segments, 2):
        pair_key = tuple(sorted((left.id, right.id)))
        if pair_key in seen_pairs:
            continue
        if left.bbox[2] + tolerance < right.bbox[0] or right.bbox[2] + tolerance < left.bbox[0]:
            continue
        if left.bbox[3] + tolerance < right.bbox[1] or right.bbox[3] + tolerance < left.bbox[1]:
            continue

        line_left = _segment_line(left)
        line_right = _segment_line(right)
        intersection = line_left.intersection(line_right)
        if intersection.is_empty:
            continue

        is_duplicate = line_left.distance(line_right) <= tolerance and (
            line_left.equals_exact(line_right, tolerance)
            or getattr(intersection, "length", 0.0) > tolerance
        )
        if not is_duplicate:
            continue

        seen_pairs.add(pair_key)
        errors.append(
            ValidationError(
                error_id=f"DUPLICATE_SEGMENT:{left.id}:{right.id}",
                error_type="DUPLICATE_SEGMENT",
                severity="low",
                message="Overlapping or duplicate duct geometry was detected.",
                suggested_fix="Remove the duplicate segment or merge overlapping geometry into a single duct centerline.",
                entity_handles=_dedupe_handles([left, right]),
                segment_ids=sorted([left.id, right.id]),
                location=_segment_midpoint(left),
                rule_name="detect_duplicate_or_overlapping_segments",
                debug_features={"overlap_tolerance": tolerance},
            )
        )
    return errors


def detect_abnormal_loops(graph: nx.Graph, config: dict) -> list[ValidationError]:
    loop_max_perimeter = float(config.get("loop_max_perimeter", 0.0))
    loop_max_nodes = int(config.get("loop_max_nodes", 0))
    errors: list[ValidationError] = []
    edge_lengths = {(min(left, right), max(left, right)): data["length"] for left, right, data in graph.edges(data=True)}

    for index, cycle in enumerate(nx.cycle_basis(graph), start=1):
        if len(cycle) < 3:
            continue
        perimeter = 0.0
        cycle_edges = []
        for left, right in zip(cycle, cycle[1:] + cycle[:1]):
            edge_key = (min(left, right), max(left, right))
            perimeter += edge_lengths.get(edge_key, 0.0)
            cycle_edges.append(edge_key)
        if perimeter > loop_max_perimeter and len(cycle) > loop_max_nodes:
            continue

        points = [graph.nodes[node_id]["point"] for node_id in cycle]
        centroid_x = sum(point[0] for point in points) / len(points)
        centroid_y = sum(point[1] for point in points) / len(points)
        segment_ids = []
        handles = set()
        for left, right in cycle_edges:
            data = graph.get_edge_data(left, right)
            if data:
                segment_ids.append(data["segment_id"])
                handles.add(data["handle"])

        errors.append(
            ValidationError(
                error_id=f"ABNORMAL_LOOP:{index}",
                error_type="ABNORMAL_LOOP",
                severity="low",
                message="A small closed duct loop was detected and may indicate drafting duplication or an unintended cycle.",
                suggested_fix="Verify whether this loop is intentional; remove redundant segments if it is not part of the design.",
                entity_handles=sorted(handles),
                segment_ids=sorted(segment_ids),
                location={"x": centroid_x, "y": centroid_y},
                rule_name="detect_abnormal_loops",
                debug_features={"cycle_nodes": len(cycle), "perimeter": perimeter},
            )
        )
    return errors


def run_all_rules(graph: nx.Graph, segments: list[Segment], config: dict) -> list[ValidationError]:
    errors: list[ValidationError] = []
    errors.extend(detect_open_endpoints(graph, segments, config))
    errors.extend(detect_disconnected_components(graph, segments, config))
    errors.extend(detect_near_miss_connections(segments, config))
    errors.extend(detect_crossing_without_node(segments, graph, config))
    errors.extend(detect_duplicate_or_overlapping_segments(segments, config))
    errors.extend(detect_abnormal_loops(graph, config))
    return errors
