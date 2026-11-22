"""Rooms inside one user-chosen plan region: the HVACROOMS back end.

The AutoCAD add-in writes the selected plan to a DXF snapshot plus a request JSON
(the wall layers the user ticked, the selection region, thresholds), runs this
script as a separate process and reads the result JSON back:

    venv\\Scripts\\python.exe arch_rooms.py --request C:\\tmp\\hvacrooms\\request.json

Geometry (method "polygonize"): wall curves on the chosen layers are noded and
polygonized into faces. Door swing arcs are replaced by their chords, facing wall
ends across an opening (up to `max_opening`) are bridged, and small drafting gaps
are snapped. Faces narrower than `min_room_width` (wall cavities) or smaller than
`min_room_area_m2` are dropped; the rest are rooms, and small X-marked or
labelled faces are shafts. Method "closing" keeps the older morphological closing
from arch_extract for comparison.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
import traceback
from pathlib import Path

from shapely import STRtree
from shapely.geometry import LineString, Point, Polygon
from shapely.ops import nearest_points, polygonize, unary_union

import arch_extract as ax

REQUEST_DEFAULTS = {
    "version": 1,
    "dxf": "snapshot.dxf",            # relative to the request file
    "out": "rooms.json",              # relative to the request file
    "wall_layers": [],                # exact layer names (bound-xref prefixes allowed)
    "region": None,                   # [[x, y], ...] selection polygon, or None
    "method": "polygonize",           # or "closing"
    "max_opening": 1800.0,            # widest opening bridged between facing wall ends
    "min_opening": 200.0,
    "min_wall": 60.0,                 # wall thickness range for wall-end caps
    "max_wall": 400.0,
    "gap_snap": 50.0,                 # drafting gaps closed by snapping
    "door_radius_min": 500.0,         # swing arcs in this radius range are doors
    "door_radius_max": 2000.0,
    "min_room_area_m2": 1.0,
    "min_room_width": 400.0,
    "shaft_max_area_m2": 6.0,
    "x_mark_min_length": 400.0,
    "x_mark_max_length": 8000.0,
    "flatten_distance": 20.0,
    "max_block_depth": 8,
    "gap_close": 450.0,               # method "closing" only
    "line_buffer": 10.0,              # method "closing" only
}

Seg = tuple[tuple[float, float], tuple[float, float]]

# Lines the user draws to split open-plan spaces; always a barrier.
SPLIT_LAYER = "HVAC-ROOM-SPLIT"


# ---------------------------------------------------------------- collection


def _door_chords(entity, config: dict) -> list[LineString] | None:
    """Chords hinge->both ends for a ~90 degree swing arc, else None."""
    kind = entity.dxftype()
    if kind == "ARC":
        radius = entity.dxf.radius
        sweep = (entity.dxf.end_angle - entity.dxf.start_angle) % 360
        center = entity.ocs().to_wcs(entity.dxf.center)
    elif kind == "ELLIPSE":  # a mirrored or scaled arc inside a block
        if abs(entity.dxf.ratio - 1) > 0.02:
            return None
        radius = entity.dxf.major_axis.magnitude
        sweep = math.degrees((entity.dxf.end_param - entity.dxf.start_param) % math.tau)
        center = entity.dxf.center
    else:
        return None
    if not (config["door_radius_min"] <= radius <= config["door_radius_max"] and 75 <= sweep <= 105):
        return None
    path = ax.ezpath.make_path(entity)
    hinge = (center.x, center.y)
    return [LineString([hinge, (p.x, p.y)]) for p in (path.start, path.end)]


def collect_selected(doc, req: dict):
    """Barrier curves on the chosen layers inside the region, texts and X marks."""
    wanted = {ax.clean_layer(name).casefold() for name in req["wall_layers"]} | {SPLIT_LAYER.casefold()}
    region = Polygon(req["region"]).buffer(1.0) if req.get("region") else None
    lines, texts, diagonals = [], [], []
    for layer, entity in ax.iter_flat(doc, doc.modelspace(), max_depth=req["max_block_depth"]):
        kind = entity.dxftype()
        if kind in ax.TEXT_TYPES:
            text = ax._text_of(doc, entity, layer)
            if text and (region is None or region.contains(Point(text.x, text.y))):
                texts.append(text)
            continue
        if kind not in ax.CURVE_TYPES:
            continue
        on_wall = layer.casefold() in wanted and ax.is_continuous(doc, entity)
        straight = kind in ("LINE", "LWPOLYLINE")
        if not (on_wall or straight):
            continue
        if on_wall:
            chords = _door_chords(entity, req)
            if chords is not None:
                lines.extend(c for c in chords if region is None or c.intersects(region))
                continue
        geom = ax._curve_geometry(entity, req["flatten_distance"])
        if geom is None or geom.length <= 0 or (region is not None and not geom.intersects(region)):
            continue
        if straight and ax.is_diagonal(geom, req["x_mark_min_length"], req["x_mark_max_length"]):
            diagonals.append(geom)
        if on_wall:
            lines.append(geom)
    marks, members = ax.find_x_marks(diagonals)
    drop = {id(diagonals[i]) for i in members}
    return [g for g in lines if id(g) not in drop], texts, marks


# ---------------------------------------------------------------- bridging


def _segments(lines: list[LineString]) -> list[Seg]:
    out = []
    for line in lines:
        coords = list(line.coords)
        out.extend((a, b) for a, b in zip(coords, coords[1:]) if math.dist(a, b) > 1e-6)
    return out


def _unit(a, b):
    length = math.dist(a, b)
    return ((b[0] - a[0]) / length, (b[1] - a[1]) / length), length


def _key(p, snap: float = 5.0):
    return (round(p[0] / snap), round(p[1] / snap))


def wall_end_caps(segs: list[Seg], config: dict) -> list[dict]:
    """Short segments closing a double-line wall end.

    A cap of length t (wall thickness) has, at both ends, a perpendicular segment at
    least 1.5 t long: the wall faces. `w` points out of the wall into the opening."""
    at: dict = {}
    for idx, (a, b) in enumerate(segs):
        at.setdefault(_key(a), []).append(idx)
        at.setdefault(_key(b), []).append(idx)
    caps = []
    for idx, (a, b) in enumerate(segs):
        u, length = _unit(a, b)
        if not config["min_wall"] <= length <= config["max_wall"]:
            continue
        into = []
        for end in (a, b):
            for j in at.get(_key(end), []):
                if j == idx:
                    continue
                c, d = segs[j]
                far = d if math.dist(c, end) < math.dist(d, end) else c
                v, flen = _unit(end, far)
                if flen >= 1.5 * length and abs(u[0] * v[0] + u[1] * v[1]) < 0.1:
                    into.append(v)
                    break
        if len(into) == 2 and into[0][0] * into[1][0] + into[0][1] * into[1][1] > 0.9:
            w = (-(into[0][0] + into[1][0]) / 2, -(into[0][1] + into[1][1]) / 2)
            norm = math.hypot(*w)
            caps.append({"a": a, "b": b, "u": u, "len": length, "w": (w[0] / norm, w[1] / norm),
                         "mid": ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)})
    return caps


def bridge_openings(caps: list[dict], config: dict) -> list[LineString]:
    """Close each opening between two facing wall-end caps (mutual nearest pairs)."""
    best: dict[int, tuple[float, int]] = {}
    for i, ci in enumerate(caps):
        for j, cj in enumerate(caps):
            if i == j or abs(ci["u"][0] * cj["u"][1] - ci["u"][1] * cj["u"][0]) > 0.05:
                continue
            d = (cj["mid"][0] - ci["mid"][0], cj["mid"][1] - ci["mid"][1])
            ahead = d[0] * ci["w"][0] + d[1] * ci["w"][1]
            back = d[0] * cj["w"][0] + d[1] * cj["w"][1]
            lateral = abs(d[0] * ci["u"][0] + d[1] * ci["u"][1])
            if not (config["min_opening"] <= ahead <= config["max_opening"]) or back >= 0:
                continue
            if lateral > 0.25 * ci["len"] or not 0.6 <= ci["len"] / cj["len"] <= 1.6:
                continue
            if i not in best or ahead < best[i][0]:
                best[i] = (ahead, j)
    bridges = []
    for i, (_dist, j) in best.items():
        if j > i and best.get(j, (None, -1))[1] == i:
            a, b = caps[i]["a"], caps[i]["b"]
            c, d = caps[j]["a"], caps[j]["b"]
            if math.dist(a, c) + math.dist(b, d) > math.dist(a, d) + math.dist(b, c):
                c, d = d, c
            bridges += [LineString([a, c]), LineString([b, d])]
    return bridges


def snap_gaps(segs: list[Seg], gap: float) -> list[LineString]:
    """Join dangling ends to the nearest other segment within `gap` (drafting gaps)."""
    if not segs:
        return []
    count: dict = {}
    for a, b in segs:
        count[_key(a)] = count.get(_key(a), 0) + 1
        count[_key(b)] = count.get(_key(b), 0) + 1
    geoms = [LineString(s) for s in segs]
    tree = STRtree(geoms)
    out = []
    for idx, (a, b) in enumerate(segs):
        for end in (a, b):
            if count[_key(end)] != 1:
                continue
            p = Point(end)
            best, best_d = None, None
            for j in tree.query(p.buffer(gap)):
                j = int(j)
                if j == idx:
                    continue
                dist = geoms[j].distance(p)
                if 1e-6 < dist <= gap and (best_d is None or dist < best_d):
                    best, best_d = j, dist
            if best is not None:
                q = nearest_points(p, geoms[best])[1]
                out.append(LineString([end, (q.x, q.y)]))
    return out


# ---------------------------------------------------------------- faces


def faces_polygonize(lines: list[LineString], req: dict) -> tuple[list[Polygon], list[LineString]]:
    segs = _segments(lines)
    bridges = bridge_openings(wall_end_caps(segs, req), req) + snap_gaps(segs, req["gap_snap"])
    noded = unary_union(lines + bridges)
    return [p for face in polygonize(noded) for p in ax._valid_polygons(face)], bridges


def faces_closing(lines: list[LineString], req: dict) -> list[Polygon]:
    faces = []
    for mass in ax.wall_masses(lines, req):
        for hole in mass.interiors:
            for poly in ax._valid_polygons(Polygon(hole)):
                faces.extend(ax._valid_polygons(poly.buffer(req["line_buffer"], join_style="mitre")))
    return faces


def _ring(poly: Polygon, tolerance: float = 0.5) -> list[list[float]]:
    """Open ring (first vertex not repeated) without duplicate or collinear vertices."""
    clean = poly.simplify(tolerance, preserve_topology=True)
    if clean.is_empty or clean.geom_type != "Polygon":
        clean = poly
    return [[round(x, 1), round(y, 1)] for x, y in list(clean.exterior.coords)[:-1]]


def extract(doc, req: dict) -> dict:
    lines, texts, marks = collect_selected(doc, req)
    warnings = []
    if not lines:
        warnings.append("No wall curves on the chosen layers inside the selection.")
    bridges: list[LineString] = []
    if req["method"] == "closing":
        faces = faces_closing(lines, req)
    else:
        faces, bridges = faces_polygonize(lines, req)

    region = Polygon(req["region"]) if req.get("region") else None
    min_area = req["min_room_area_m2"] * 1e6
    rooms = [f for f in faces if f.area >= min_area and ax.is_wide(f, req["min_room_width"])
             and (region is None or region.contains(f.representative_point()))]
    envelope = [p for p in ax._polygons(unary_union(faces).buffer(1).buffer(-1)) if p.area >= min_area] if faces else []

    text_tree = STRtree([Point(t.x, t.y) for t in texts]) if texts else None
    mark_tree = STRtree([Point(m.center) for m in marks]) if marks else None
    out_rooms = []
    for poly in sorted(rooms, key=lambda p: (-p.centroid.y, p.centroid.x)):
        room = ax.make_room(poly, f"R{len(out_rooms) + 1}", "", texts, text_tree, marks, mark_tree, req)
        inner = poly.representative_point()  # always inside, unlike the centroid
        out_rooms.append({
            "id": room.id, "kind": room.kind, "name": room.name, "area_m2": room.area_m2,
            "label_area_m2": room.label_area_m2, "label_error_pct": room.label_error_pct,
            "texts": room.texts, "label_point": [round(inner.x, 1), round(inner.y, 1)],
            "polygon": _ring(poly),
        })
    labelled = [r for r in out_rooms if r["label_error_pct"] is not None]
    return {
        "version": 1, "status": "ok", "units": "mm", "method": req["method"],
        "rooms": out_rooms,
        "envelope": [_ring(p) for p in sorted(envelope, key=lambda p: -p.area)[:1]],
        "stats": {
            "wall_curves": len(lines), "bridges": len(bridges), "x_marks": len(marks),
            "faces": len(faces), "rooms": sum(1 for r in out_rooms if r["kind"] == "room"),
            "shafts": sum(1 for r in out_rooms if r["kind"] == "shaft"),
            "total_area_m2": round(sum(r["area_m2"] for r in out_rooms), 2),
            "labelled": len(labelled),
            "labelled_within_5pct": sum(1 for r in labelled if abs(r["label_error_pct"]) <= 5),
        },
        "warnings": warnings,
    }


def load_request(path: Path) -> dict:
    req = dict(REQUEST_DEFAULTS)
    req.update(json.loads(path.read_text(encoding="utf-8-sig")))
    if req.get("version") != 1:
        raise ValueError(f"Unsupported request version {req.get('version')!r}")
    if req["method"] not in ("polygonize", "closing"):
        raise ValueError(f"Unknown method {req['method']!r}")
    return req


def run_request(request_path: str | Path) -> dict:
    request_path = Path(request_path)
    req = load_request(request_path)
    out = request_path.parent / req["out"]
    try:
        result = extract(ax.load_drawing(request_path.parent / req["dxf"]), req)
    except Exception as exc:  # the add-in shows the message instead of a dead process
        result = {"version": 1, "status": "error", "message": f"{type(exc).__name__}: {exc}",
                  "detail": traceback.format_exc(limit=5)}
    out.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract rooms from one plan region (HVACROOMS back end).")
    parser.add_argument("--request", required=True, help="request.json written by the add-in")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    result = run_request(args.request)
    summary = {"status": result["status"], **result.get("stats", {})}
    if result["status"] != "ok":
        summary["message"] = result.get("message")
    print(json.dumps(summary, ensure_ascii=False))
    return 0 if result["status"] == "ok" else 1


if __name__ == "__main__":
    raise SystemExit(main())
