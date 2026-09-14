from __future__ import annotations

import math
from collections import defaultdict

import networkx as nx

from models import DuctPrimitive, Segment


def round_point(point: tuple[float, float], digits: int) -> tuple[float, float]:
    return (round(float(point[0]), digits), round(float(point[1]), digits))


def point_distance(a: tuple[float, float], b: tuple[float, float]) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def extract_ducts(raw_entities) -> list[DuctPrimitive]:
    ducts: list[DuctPrimitive] = []
    for raw in raw_entities:
        ducts.append(
            DuctPrimitive(
                handle=raw.handle,
                layer=raw.layer,
                points=list(raw.points),
                source_type=raw.source_type,
                block_name=raw.block_name,
                metadata=dict(raw.metadata),
            )
        )
    return ducts


def build_all_segments(ducts: list[DuctPrimitive], config: dict) -> list[Segment]:
    precision = int(config.get("precision_digits", 3))
    min_length = float(config.get("min_segment_length", 0.0))
    segments: list[Segment] = []
    index = 1

    for duct in ducts:
        for start, end in zip(duct.points, duct.points[1:]):
            rounded_start = round_point(start, precision)
            rounded_end = round_point(end, precision)
            length = point_distance(rounded_start, rounded_end)
            if length < min_length:
                continue

            angle = math.degrees(
                math.atan2(rounded_end[1] - rounded_start[1], rounded_end[0] - rounded_start[0])
            )
            bbox = (
                min(rounded_start[0], rounded_end[0]),
                min(rounded_start[1], rounded_end[1]),
                max(rounded_start[0], rounded_end[0]),
                max(rounded_start[1], rounded_end[1]),
            )
            segments.append(
                Segment(
                    id=f"SEG{index:06d}",
                    handle=duct.handle,
                    layer=duct.layer,
                    source_type=duct.source_type,
                    start=rounded_start,
                    end=rounded_end,
                    length=length,
                    angle=angle,
                    bbox=bbox,
                    block_name=duct.block_name,
                    original_start=rounded_start,
                    original_end=rounded_end,
                    metadata=dict(duct.metadata),
                )
            )
            index += 1

    return segments


def _bucket_key(point: tuple[float, float], tolerance: float) -> tuple[int, int]:
    if tolerance <= 0:
        return (0, 0)
    return (math.floor(point[0] / tolerance), math.floor(point[1] / tolerance))


def snap_endpoints(segments: list[Segment], tolerance: float) -> dict:
    endpoints: list[tuple[str, str, tuple[float, float]]] = []
    for segment in segments:
        endpoints.append((segment.id, "start", segment.start))
        endpoints.append((segment.id, "end", segment.end))

    endpoint_map = {index: endpoint for index, endpoint in enumerate(endpoints)}
    parent = list(range(len(endpoints)))

    def find(index: int) -> int:
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    def union(left: int, right: int) -> None:
        left_root = find(left)
        right_root = find(right)
        if left_root != right_root:
            parent[right_root] = left_root

    buckets: dict[tuple[int, int], list[int]] = defaultdict(list)
    bucket_size = tolerance if tolerance > 0 else 1.0
    for index, (_, _, point) in endpoint_map.items():
        buckets[_bucket_key(point, bucket_size)].append(index)

    for key, indices in buckets.items():
        neighbor_indices: list[int] = []
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                neighbor_indices.extend(buckets.get((key[0] + dx, key[1] + dy), []))
        for left in indices:
            for right in neighbor_indices:
                if right <= left:
                    continue
                if point_distance(endpoint_map[left][2], endpoint_map[right][2]) <= tolerance:
                    union(left, right)

    grouped: dict[int, list[int]] = defaultdict(list)
    for index in endpoint_map:
        grouped[find(index)].append(index)

    cluster_centers: dict[int, tuple[float, float]] = {}
    for root, members in grouped.items():
        x = sum(endpoint_map[index][2][0] for index in members) / len(members)
        y = sum(endpoint_map[index][2][1] for index in members) / len(members)
        cluster_centers[root] = (x, y)

    snapped_lookup: dict[tuple[str, str], tuple[float, float]] = {}
    membership: dict[tuple[str, str], int] = {}
    for root, members in grouped.items():
        center = cluster_centers[root]
        for index in members:
            segment_id, endpoint_name, _ = endpoint_map[index]
            snapped_lookup[(segment_id, endpoint_name)] = center
            membership[(segment_id, endpoint_name)] = root

    return {"snapped_points": snapped_lookup, "membership": membership, "clusters": grouped}


def build_graph(segments: list[Segment], config: dict) -> nx.Graph:
    tolerance = float(config.get("snap_tolerance", 0.0))
    precision = int(config.get("precision_digits", 3))
    snap_data = snap_endpoints(segments, tolerance)

    graph = nx.Graph()
    for segment in segments:
        snapped_start = round_point(snap_data["snapped_points"][(segment.id, "start")], precision)
        snapped_end = round_point(snap_data["snapped_points"][(segment.id, "end")], precision)
        start_id = f"N:{snapped_start[0]}:{snapped_start[1]}"
        end_id = f"N:{snapped_end[0]}:{snapped_end[1]}"

        segment.start = snapped_start
        segment.end = snapped_end
        segment.node_start = start_id
        segment.node_end = end_id

        for node_id, point in ((start_id, snapped_start), (end_id, snapped_end)):
            if not graph.has_node(node_id):
                graph.add_node(node_id, point=point)

        graph.add_edge(
            start_id,
            end_id,
            segment_id=segment.id,
            handle=segment.handle,
            layer=segment.layer,
            length=segment.length,
        )

    return graph
