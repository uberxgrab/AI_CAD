"""Per-layer statistics: the main input for guessing which layers are walls."""
from __future__ import annotations

import math
import re
import time
from collections import Counter, defaultdict
from bisect import bisect_right
from pathlib import Path

import arch_extract as ax

from . import config, drawings, privacy, scan

CURVES = {"LINE", "LWPOLYLINE", "POLYLINE", "ARC", "CIRCLE", "ELLIPSE", "SPLINE"}
MAX_TEXT_CANDIDATES = 30
SEG_TRIM_AT = 20000
SEG_KEEP = 8000


class _Layer:
    __slots__ = ("counts", "length", "cont_len", "closed", "doors", "segs", "bbox", "raws", "cands", "text_n")

    def __init__(self):
        self.counts: Counter = Counter()
        self.length = 0.0
        self.cont_len = 0.0
        self.closed = 0
        self.doors = 0
        self.segs: list = []
        self.bbox = None
        self.raws: set = set()
        self.cands: list = []
        self.text_n = 0

    def extend(self, bbox):
        if self.bbox is None:
            self.bbox = list(bbox)
        else:
            b = self.bbox
            b[0], b[1] = min(b[0], bbox[0]), min(b[1], bbox[1])
            b[2], b[3] = max(b[2], bbox[2]), max(b[3], bbox[3])

    def add_seg(self, a, b):
        self.segs.append((a[0], a[1], b[0], b[1]))
        if len(self.segs) > SEG_TRIM_AT:
            self.segs.sort(key=lambda s: -math.hypot(s[2] - s[0], s[3] - s[1]))
            del self.segs[SEG_KEEP:]


def parallel_spacings(segs, lo: float = 50.0, hi: float = 500.0, keep: int = 3,
                      max_segs: int = 4000, min_len: float = 300.0, max_neighbors: int = 60) -> list[dict]:
    """Most common gaps between overlapping parallel segments (double-line walls: 100-300).

    Only the `max_segs` longest segments take part so the cost stays bounded."""
    cand = [s for s in segs if math.hypot(s[2] - s[0], s[3] - s[1]) >= min_len]
    cand.sort(key=lambda s: -math.hypot(s[2] - s[0], s[3] - s[1]))
    groups = defaultdict(list)
    for x1, y1, x2, y2 in cand[:max_segs]:
        ang = round(math.degrees(math.atan2(y2 - y1, x2 - x1)) % 180) % 180
        groups[ang].append((x1, y1, x2, y2))
    found: Counter = Counter()
    for ang, items in groups.items():
        ca, sa = math.cos(math.radians(ang)), math.sin(math.radians(ang))
        rows = []
        for x1, y1, x2, y2 in items:
            off = (-(x1 + x2) * sa + (y1 + y2) * ca) / 2
            t1, t2 = sorted((x1 * ca + y1 * sa, x2 * ca + y2 * sa))
            rows.append((off, t1, t2))
        rows.sort()
        offs = [r[0] for r in rows]
        for i, (off, a1, a2) in enumerate(rows):
            j_end = min(bisect_right(offs, off + hi), i + 1 + max_neighbors)
            for j in range(i + 1, j_end):
                d = offs[j] - off
                if d < lo:
                    continue
                _, b1, b2 = rows[j]
                overlap = min(a2, b2) - max(a1, b1)
                if overlap >= max(150.0, 0.3 * min(a2 - a1, b2 - b1)):
                    found[int(round(d / 10.0)) * 10] += 1
    return [{"mm": mm, "n": n} for mm, n in found.most_common(keep)]


def suggest(layer: str, classifier: ax.LayerClassifier) -> str | None:
    """Rule-based role from the layer name: wall, opening, excluded or None."""
    key = ax._layer_key(layer)
    if layer.lower() in classifier.extra:
        return "wall"
    if any(p.search(key) for p in classifier.exclude):
        return "excluded"
    if any(p.search(key) for p in classifier.wall):
        return "wall"
    if any(p.search(key) for p in classifier.opening):
        return "opening"
    return None


def _poly_data(entity):
    """(vertices xyb, closed) of a LWPOLYLINE / POLYLINE."""
    if entity.dxftype() == "LWPOLYLINE":
        return [tuple(p) for p in entity.get_points("xyb")], bool(entity.closed)
    pts = []
    for v in entity.vertices:
        loc = v.dxf.location
        pts.append((loc.x, loc.y, v.dxf.get("bulge", 0.0) or 0.0))
    return pts, bool(entity.is_closed)


def layer_profile(dxf: str | Path, region=None, max_layers: int = 60, mode: str | None = None,
                  max_seconds: float | None = None) -> dict:
    """Statistics per cleaned layer name, longest first (lengths in metres, boxes in mm)."""
    dxf = Path(dxf)
    mode = privacy.effective_mode(mode)
    region = scan.parse_region(region)
    cfg = config.get_config()
    max_seconds = max_seconds or cfg["profile_timeout_s"]
    t0 = time.perf_counter()
    doc = drawings.load_doc(dxf)
    load_s = time.perf_counter() - t0
    lt = scan.LinetypeCache(doc)
    classifier = ax.LayerClassifier(ax.DEFAULT_ARCH_CONFIG)
    layers: dict[str, _Layer] = defaultdict(_Layer)
    scanned = 0
    truncated = False
    for raw, clean, ent in scan.walk(doc, cfg["max_block_depth"]):
        scanned += 1
        if scanned % 20000 == 0 and time.perf_counter() - t0 > max_seconds:
            truncated = True
            break
        kind = ent.dxftype()
        is_text = kind in scan.TEXTS
        if kind not in CURVES and not is_text:
            continue
        pts = scan.points_of(ent)
        if pts is None:
            continue
        bbox = scan.bbox_of(pts)
        if region is not None and not scan.intersects(bbox, region):
            continue
        st = layers[clean]
        st.counts[kind] += 1
        st.extend(bbox)
        if raw != clean and len(st.raws) < 5:
            st.raws.add(raw)
        if is_text:
            st.text_n += 1
            if len(st.cands) < MAX_TEXT_CANDIDATES:
                text = ax._text_of(doc, ent, clean)
                if text and text.text not in st.cands:
                    st.cands.append(text.text)
            continue
        continuous = lt.continuous(ent)
        length = 0.0
        try:
            if kind == "LINE":
                s, e = ent.dxf.start, ent.dxf.end
                length = math.dist((s.x, s.y), (e.x, e.y))
                st.add_seg((s.x, s.y), (e.x, e.y))
            elif kind in ("LWPOLYLINE", "POLYLINE"):
                verts, closed = _poly_data(ent)
                if closed:
                    st.closed += 1
                pairs = list(zip(verts, verts[1:]))
                if closed and len(verts) > 2:
                    pairs.append((verts[-1], verts[0]))
                for a, b in pairs:
                    chord = math.dist(a[:2], b[:2])
                    length += scan.bulge_length(chord, a[2])
                    if abs(a[2]) < 1e-9 and chord > 0:
                        st.add_seg(a[:2], b[:2])
            elif kind == "ARC":
                r = ent.dxf.radius
                sweep = (ent.dxf.end_angle - ent.dxf.start_angle) % 360
                length = math.radians(sweep) * r
                if 500 <= r <= 2000 and 75 <= sweep <= 105:
                    st.doors += 1
            elif kind == "CIRCLE":
                length = math.tau * ent.dxf.radius
        except Exception:
            continue
        st.length += length
        if continuous:
            st.cont_len += length

    ranked = sorted(layers.items(), key=lambda kv: (-kv[1].length, -sum(kv[1].counts.values())))
    shown = []
    for name, st in ranked[:max_layers]:
        row = {"layer": name}
        if st.raws:
            row["full_names"] = sorted(st.raws)
        row["n"] = dict(st.counts)
        row["len_m"] = round(st.length / 1000, 1)
        row["continuous"] = round(st.cont_len / st.length, 2) if st.length > 0 else None
        if st.closed:
            row["closed_polylines"] = st.closed
        if st.doors:
            row["door_arcs"] = st.doors
        par = parallel_spacings(st.segs) if st.segs else []
        if par:
            row["parallel_mm"] = par
        row["bbox_mm"] = [round(v) for v in st.bbox]
        row["suggested"] = suggest(name, classifier)
        if st.text_n:
            row["text_count"] = st.text_n
            if mode != "numbers_only":
                seen = []
                for t in st.cands:
                    masked = privacy.mask_text(t, mode)
                    if masked is not None and masked != privacy.MASK:
                        seen.append(masked[:40])
                    if len(seen) == 5:
                        break
                if seen:
                    row["sample_texts"] = seen
        shown.append(row)
    return {
        "drawing": privacy.safe_name(dxf), "units": "mm (lengths in m)",
        "insunits": doc.header.get("$INSUNITS"), "region_mm": list(region) if region else None,
        "privacy": mode, "load_s": round(load_s, 1), "elapsed_s": round(time.perf_counter() - t0, 1),
        "entities_scanned": scanned, "truncated": truncated,
        "layers_total": len(ranked), "layers_shown": len(shown), "layers": shown,
    }
