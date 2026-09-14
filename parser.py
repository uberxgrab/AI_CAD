from __future__ import annotations

from typing import Iterable

import ezdxf

from models import RawEntity


SUPPORTED_ENTITY_TYPES = {"LINE", "LWPOLYLINE", "POLYLINE"}


def load_dxf(path: str):
    if path.lower().endswith(".dwg"):
        raise ValueError("DWG is not supported directly. Convert the file to DXF first.")
    if not path.lower().endswith(".dxf"):
        raise ValueError("Input must be a DXF file.")
    doc = ezdxf.readfile(path)
    return doc, doc.modelspace()


def _normalize_layer_name(layer: str | None) -> str:
    return (layer or "0").strip()


def _should_include_layer(layer: str, config: dict) -> bool:
    include_layers = {item.lower() for item in config.get("include_layers", []) if item}
    exclude_layers = {item.lower() for item in config.get("exclude_layers", []) if item}
    normalized = layer.lower()
    if include_layers and normalized not in include_layers:
        return False
    if normalized in exclude_layers:
        return False
    return True


def _line_points(entity) -> list[tuple[float, float]]:
    return [
        (float(entity.dxf.start.x), float(entity.dxf.start.y)),
        (float(entity.dxf.end.x), float(entity.dxf.end.y)),
    ]


def _polyline_points(entity) -> list[tuple[float, float]]:
    if entity.dxftype() == "LWPOLYLINE":
        points = [(float(x), float(y)) for x, y, *_ in entity.get_points()]
        if entity.closed and len(points) > 1:
            points.append(points[0])
        return points

    points = [(float(vertex.dxf.location.x), float(vertex.dxf.location.y)) for vertex in entity.vertices]
    if entity.is_closed and len(points) > 1:
        points.append(points[0])
    return points


def _entity_points(entity) -> list[tuple[float, float]] | None:
    entity_type = entity.dxftype()
    if entity_type == "LINE":
        return _line_points(entity)
    if entity_type in {"LWPOLYLINE", "POLYLINE"}:
        return _polyline_points(entity)
    return None


def _build_raw_entity(entity, *, handle: str, layer: str, block_name: str | None, source_type: str) -> RawEntity | None:
    points = _entity_points(entity)
    if not points or len(points) < 2:
        return None

    return RawEntity(
        handle=handle,
        layer=_normalize_layer_name(layer),
        source_type=source_type,
        points=points,
        block_name=block_name,
        linetype=getattr(entity.dxf, "linetype", None),
        color=getattr(entity.dxf, "color", None),
        metadata={},
    )


def _iter_supported_virtual_entities(insert) -> Iterable[RawEntity]:
    block_name = insert.dxf.name
    parent_layer = _normalize_layer_name(insert.dxf.layer)
    for index, virtual in enumerate(insert.virtual_entities()):
        if virtual.dxftype() not in SUPPORTED_ENTITY_TYPES:
            continue
        virtual_layer = _normalize_layer_name(getattr(virtual.dxf, "layer", parent_layer))
        layer = parent_layer if virtual_layer == "0" else virtual_layer
        raw = _build_raw_entity(
            virtual,
            handle=f"{insert.dxf.handle}:{index}",
            layer=layer,
            block_name=block_name,
            source_type=virtual.dxftype(),
        )
        if raw:
            raw.metadata["insert_handle"] = insert.dxf.handle
            raw.metadata["insert_name"] = block_name
            raw.metadata["insert_layer"] = parent_layer
            yield raw


def extract_entities(msp, config: dict) -> list[RawEntity]:
    entities: list[RawEntity] = []
    for entity in msp:
        entity_type = entity.dxftype()

        if entity_type in SUPPORTED_ENTITY_TYPES:
            layer = _normalize_layer_name(entity.dxf.layer)
            if not _should_include_layer(layer, config):
                continue
            raw = _build_raw_entity(
                entity,
                handle=entity.dxf.handle,
                layer=layer,
                block_name=None,
                source_type=entity_type,
            )
            if raw:
                entities.append(raw)
            continue

        if entity_type == "INSERT":
            try:
                for raw in _iter_supported_virtual_entities(entity):
                    if _should_include_layer(raw.layer, config):
                        entities.append(raw)
            except Exception:
                continue

    return entities
