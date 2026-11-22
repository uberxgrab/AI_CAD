"""Shared entity walking helpers (nested INSERTs flattened, raw layer names kept)."""
from __future__ import annotations

import math

import arch_extract as ax

TEXTS = ax.TEXT_TYPES


def walk(doc, max_depth: int = 8):
    """Yield (raw_layer, clean_layer, entity) for model space, expanding nested INSERTs.

    Like arch_extract.iter_flat but also keeps the full layer name (bound xrefs prefix
    theirs, `XR plan$0$A-Tuong`). Layer 0 inside a block takes the layer of its insert."""
    yield from _walk(doc.modelspace(), None, None, 0, max_depth)


def _walk(entities, parent_raw, parent_clean, depth, max_depth):
    for entity in entities:
        raw = entity.dxf.layer or "0"
        clean = ax.clean_layer(raw)
        if clean == "0" and parent_clean:
            raw, clean = parent_raw, parent_clean
        if entity.dxftype() == "INSERT":
            for attrib in getattr(entity, "attribs", []):
                yield raw, clean, attrib
            if depth >= max_depth:
                continue
            try:
                children = list(entity.virtual_entities())
            except Exception:
                continue
            yield from _walk(children, raw, clean, depth + 1, max_depth)
        else:
            yield raw, clean, entity


def points_of(entity):
    """Vertex list (x, y) used for bbox tests, or None."""
    kind = entity.dxftype()
    try:
        if kind == "LINE":
            s, e = entity.dxf.start, entity.dxf.end
            return [(s.x, s.y), (e.x, e.y)]
        if kind == "LWPOLYLINE":
            return [(p[0], p[1]) for p in entity.get_points("xy")]
        if kind == "POLYLINE":
            return [(v.dxf.location.x, v.dxf.location.y) for v in entity.vertices]
        if kind in ("ARC", "CIRCLE"):
            c, r = entity.dxf.center, entity.dxf.radius
            return [(c.x - r, c.y - r), (c.x + r, c.y + r)]
        if kind == "ELLIPSE":
            c, r = entity.dxf.center, entity.dxf.major_axis.magnitude
            return [(c.x - r, c.y - r), (c.x + r, c.y + r)]
        if kind == "SPLINE":
            return [(p.x, p.y) for p in entity.control_points]
        if kind in TEXTS or kind == "INSERT":
            p = entity.dxf.insert
            return [(p.x, p.y)]
    except Exception:
        return None
    return None


def bbox_of(points):
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    return min(xs), min(ys), max(xs), max(ys)


def intersects(bbox, region) -> bool:
    return not (bbox[2] < region[0] or bbox[0] > region[2] or bbox[3] < region[1] or bbox[1] > region[3])


def parse_region(region) -> tuple[float, float, float, float] | None:
    if region is None:
        return None
    try:
        x1, y1, x2, y2 = (float(v) for v in region)
    except (TypeError, ValueError):
        raise ValueError("region must be [minx, miny, maxx, maxy] in drawing units (mm).")
    if x2 <= x1 or y2 <= y1:
        raise ValueError("region must have maxx > minx and maxy > miny.")
    return (x1, y1, x2, y2)


class LinetypeCache:
    """Is an entity drawn with a continuous linetype (BYLAYER resolved, BYBLOCK counts)."""

    def __init__(self, doc):
        self.doc = doc
        self.layers: dict[str, bool] = {}

    def continuous(self, entity) -> bool:
        lt = (entity.dxf.get("linetype") or "BYLAYER").upper()
        if lt == "BYLAYER":
            name = entity.dxf.get("layer") or "0"
            hit = self.layers.get(name)
            if hit is None:
                hit = True
                try:
                    if self.doc.layers.has_entry(name):
                        hit = (self.doc.layers.get(name).dxf.linetype or "CONTINUOUS").upper() in (
                            "CONTINUOUS", "BYBLOCK", "")
                except Exception:
                    pass
                self.layers[name] = hit
            return hit
        return lt in ("CONTINUOUS", "BYBLOCK", "")


def bulge_length(chord: float, bulge: float) -> float:
    if abs(bulge) < 1e-9 or chord <= 0:
        return chord
    theta = 4 * math.atan(abs(bulge))
    return chord / (2 * math.sin(theta / 2)) * theta
