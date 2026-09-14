from __future__ import annotations

from dataclasses import asdict, dataclass, field


Point = tuple[float, float]


@dataclass(slots=True)
class RawEntity:
    handle: str
    layer: str
    source_type: str
    points: list[Point]
    block_name: str | None = None
    linetype: str | None = None
    color: int | None = None
    metadata: dict = field(default_factory=dict)


@dataclass(slots=True)
class DuctPrimitive:
    handle: str
    layer: str
    points: list[Point]
    source_type: str
    block_name: str | None = None
    metadata: dict = field(default_factory=dict)


@dataclass(slots=True)
class Segment:
    id: str
    handle: str
    layer: str
    source_type: str
    start: Point
    end: Point
    length: float
    angle: float
    bbox: tuple[float, float, float, float]
    block_name: str | None = None
    original_start: Point | None = None
    original_end: Point | None = None
    node_start: str | None = None
    node_end: str | None = None
    metadata: dict = field(default_factory=dict)

    def as_dict(self) -> dict:
        return asdict(self)


@dataclass(slots=True)
class ValidationError:
    error_id: str
    error_type: str
    severity: str
    message: str
    suggested_fix: str
    entity_handles: list[str]
    segment_ids: list[str]
    location: dict[str, float]
    rule_name: str
    debug_features: dict = field(default_factory=dict)

    def as_dict(self) -> dict:
        return asdict(self)


@dataclass(slots=True)
class RunSummary:
    raw_entities: int
    duct_primitives: int
    segments: int
    graph_nodes: int
    graph_edges: int
    errors: int
