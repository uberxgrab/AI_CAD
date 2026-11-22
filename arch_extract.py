"""Tang 0: geometry-only extraction from architectural DXF drawings.

Finds the floor-plan views in model space, the rooms of each view, the outer
envelope and shaft candidates, without AI. Walls are recognised from layer-name
hints; door openings are closed by a morphological closing of the wall mass, and
every enclosed hole of that mass is a room. Area labels such as `DT. 26,2M2` are
read back to score the result.

    venv\\Scripts\\python.exe arch_extract.py --input plan.dxf --out-dir out/arch/plan
"""
from __future__ import annotations

import argparse
import json
import math
import re
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path

import ezdxf
from ezdxf import path as ezpath
from ezdxf import recover
import logging

from shapely import STRtree, make_valid
from shapely.geometry import LineString, Point, Polygon, box
from shapely.ops import unary_union

from tcvn3 import decode_cad_text, normalize

# virtual_entities() of dynamic blocks logs one warning per ignored representation record
logging.getLogger("ezdxf").setLevel(logging.ERROR)

DEFAULT_ARCH_CONFIG = {
    # Regexes matched against the accent-free, lower-cased layer name with the
    # bound-xref prefix (`XR name$0$`) removed and punctuation turned into spaces.
    "wall_layer_patterns": [
        r"\btuong\b", r"\bwalls?\b", r"\bcot\b", r"\bcolumns?\b", r"\bvach\b",
        r"\bkinh\b", r"\bglass\b", r"\bbe ?tong\b", r"墙", r"柱",
    ],
    "opening_layer_patterns": [r"\bcua\b", r"\bdoors?\b", r"\bwindows?\b", r"门", r"窗"],
    "exclude_layer_patterns": [r"hatch", r"\bdim\b", r"\btext\b", r"kich thuoc"],
    "extra_wall_layers": [],          # exact layer names to add for one drawing
    "line_buffer": 10.0,              # half thickness given to each wall line (mm)
    "gap_close": 450.0,               # closing radius; openings < 2x this are closed
    "min_room_area_m2": 1.0,
    "min_room_width": 400.0,          # thinner holes are double-line wall cavities
    "shaft_max_area_m2": 6.0,         # X-marked faces up to this size are shafts
    "x_mark_min_length": 400.0,       # diagonal length range of a shaft X mark
    "x_mark_max_length": 8000.0,
    "view_gap": 6000.0,               # wall masses closer than this form one view
    "cross_line_length": 15000.0,     # longer diagonal lines are sheet cross-outs, not walls
    "min_plan_area_m2": 40.0,         # smaller titled views (key plans) are kind "other"
    "title_search": 12000.0,          # how far outside a view to look for its title
    "frame_max_thickness": 60.0,      # big single-line rings thinner than this are sheet frames
    "flatten_distance": 20.0,         # chord error for arcs, circles and splines
    "max_block_depth": 8,
    "overlay_text_height": 200.0,
}

TITLE_PLAN = re.compile(r"mat bang|平面|\bplan\b")
TITLE_OTHER = re.compile(r"mat cat|mat dung|chi tiet|剖面|立面|大样|详图|\bsection\b|\belevation\b")
FLOOR = re.compile(r"\btang\s+([0-9a-z][0-9a-z ,.+-]*)")
AREA_LABEL = re.compile(r"(?:\bdt\.?\s*|\bs\s*=\s*)?(\d+(?:[.,]\d+)?)\s*m(?:2|²)\b")
AREA_PREFIX = re.compile(r"\bdt\b|\bs\s*=|m2|m²")
SHAFT = re.compile(r"hop ky thuat|truc ky thuat|\bhkt\b|\bgen\b|\bshaft\b|管井|风井|电井|水井")
CURVE_TYPES = {"LINE", "LWPOLYLINE", "POLYLINE", "ARC", "CIRCLE", "ELLIPSE", "SPLINE"}
TEXT_TYPES = {"TEXT", "MTEXT", "ATTRIB"}


@dataclass(slots=True)
class ArchText:
    text: str
    raw: str
    layer: str
    x: float
    y: float


@dataclass(slots=True)
class XMark:
    """Two diagonals crossing at their midpoints: the usual shaft / void symbol."""
    center: tuple[float, float]
    bbox: tuple[float, float, float, float]


@dataclass(slots=True)
class ArchRoom:
    id: str
    view: str
    kind: str                      # room | shaft
    area_m2: float
    polygon: list[tuple[float, float]]
    name: str | None = None
    texts: list[str] = field(default_factory=list)
    label_area_m2: float | None = None
    label_error_pct: float | None = None


@dataclass(slots=True)
class ArchView:
    id: str
    kind: str                      # plan | other | untitled
    title: str | None
    floor: str | None
    bbox: tuple[float, float, float, float]
    envelope: list[list[tuple[float, float]]]
    rooms: list[ArchRoom] = field(default_factory=list)
    shafts: list[ArchRoom] = field(default_factory=list)


# ---------------------------------------------------------------- loading


def load_drawing(path: str | Path):
    path = Path(path)
    if path.suffix.lower() != ".dxf":
        raise ValueError("Input must be a DXF file; convert DWG with accoreconsole first.")
    try:
        return ezdxf.readfile(path)
    except ezdxf.DXFStructureError:
        doc, _auditor = recover.readfile(path)
        return doc


def clean_layer(layer: str | None) -> str:
    """`XR plan$0$A-Tuong` -> `A-Tuong` (bound xrefs prefix their layers)."""
    return (layer or "0").split("$0$")[-1].strip() or "0"


def _layer_key(layer: str) -> str:
    return re.sub(r"[^\w]+", " ", normalize(layer)).strip()


class LayerClassifier:
    def __init__(self, config: dict):
        self.wall = [re.compile(p) for p in config["wall_layer_patterns"]]
        self.opening = [re.compile(p) for p in config["opening_layer_patterns"]]
        self.exclude = [re.compile(p) for p in config["exclude_layer_patterns"]]
        self.extra = {name.lower() for name in config.get("extra_wall_layers", [])}
        self._cache: dict[str, bool] = {}

    def is_barrier(self, layer: str) -> bool:
        hit = self._cache.get(layer)
        if hit is None:
            key = _layer_key(layer)
            if layer.lower() in self.extra:
                hit = True
            elif any(p.search(key) for p in self.exclude):
                hit = False
            else:
                hit = any(p.search(key) for p in self.wall + self.opening)
            self._cache[layer] = hit
        return hit


def _font_of(doc, entity) -> str | None:
    style = doc.styles.get(getattr(entity.dxf, "style", "Standard") or "Standard")
    return style.dxf.font if style is not None else None


def _text_of(doc, entity, layer: str) -> ArchText | None:
    kind = entity.dxftype()
    if kind == "MTEXT":
        raw = entity.plain_text()
    else:
        raw = entity.dxf.text
    if not raw or not raw.strip():
        return None
    point = entity.dxf.insert
    return ArchText(decode_cad_text(raw, _font_of(doc, entity)), raw, layer, float(point.x), float(point.y))


def iter_flat(doc, entities, parent_layer: str | None = None, depth: int = 0, max_depth: int = 8):
    """Yield (layer, entity) for model-space entities, expanding nested INSERTs.

    Entities on layer 0 inside a block take the layer of the insert that holds them.
    """
    for entity in entities:
        layer = clean_layer(entity.dxf.layer)
        if layer == "0" and parent_layer:
            layer = parent_layer
        if entity.dxftype() == "INSERT":
            for attrib in getattr(entity, "attribs", []):
                yield layer, attrib
            if depth >= max_depth:
                continue
            try:
                children = list(entity.virtual_entities())
            except Exception:
                continue
            yield from iter_flat(doc, children, layer, depth + 1, max_depth)
        else:
            yield layer, entity


def _curve_geometry(entity, flatten: float):
    kind = entity.dxftype()
    if kind == "LINE":
        s, e = entity.dxf.start, entity.dxf.end
        if math.dist((s.x, s.y), (e.x, e.y)) < 1e-6:
            return None
        return LineString([(s.x, s.y), (e.x, e.y)])
    try:
        points = [(v.x, v.y) for v in ezpath.make_path(entity).flattening(flatten)]
    except Exception:
        return None
    if len(points) < 2:
        return None
    return LineString(points)


def is_continuous(doc, entity) -> bool:
    """Walls are drawn continuous; hidden, dashed and centre lines (section cuts,
    overhead edges, axes) must not split rooms. BYBLOCK counts as continuous."""
    linetype = (entity.dxf.get("linetype") or "BYLAYER").upper()
    if linetype == "BYLAYER":
        name = entity.dxf.layer
        layer = doc.layers.get(name) if name and doc.layers.has_entry(name) else None
        linetype = (layer.dxf.linetype if layer is not None else "CONTINUOUS").upper()
    return linetype in {"CONTINUOUS", "BYBLOCK", ""}


def is_cross_line(geom: LineString, min_length: float) -> bool:
    """A long straight diagonal: designers cross out obsolete sheets with an X."""
    if len(geom.coords) != 2 or geom.length < min_length:
        return False
    (x1, y1), (x2, y2) = geom.coords
    angle = math.degrees(math.atan2(abs(y2 - y1), abs(x2 - x1)))
    return 20.0 <= angle <= 70.0


def is_diagonal(geom: LineString, min_length: float, max_length: float) -> bool:
    if len(geom.coords) != 2 or not (min_length <= geom.length <= max_length):
        return False
    (x1, y1), (x2, y2) = geom.coords
    angle = math.degrees(math.atan2(abs(y2 - y1), abs(x2 - x1)))
    return 20.0 <= angle <= 70.0


def find_x_marks(diagonals: list[LineString]) -> tuple[list[XMark], set[int]]:
    """Pairs of opposite-slope diagonals crossing near both midpoints.

    Returns the marks and the indexes of the diagonals that form them."""
    marks: dict[tuple[int, int], XMark] = {}
    members: set[int] = set()
    if not diagonals:
        return [], members
    tree = STRtree(diagonals)
    for i, a in enumerate(diagonals):
        (ax1, ay1), (ax2, ay2) = a.coords
        rising = (ax2 - ax1) * (ay2 - ay1) > 0
        for j in tree.query(a):
            j = int(j)
            if j <= i:
                continue
            b = diagonals[j]
            (bx1, by1), (bx2, by2) = b.coords
            if ((bx2 - bx1) * (by2 - by1) > 0) == rising or not 0.5 <= a.length / b.length <= 2.0:
                continue
            point = a.intersection(b)
            if point.geom_type != "Point":
                continue
            tol = 0.15 * min(a.length, b.length)
            if point.distance(a.centroid) > tol or point.distance(b.centroid) > tol:
                continue
            bounds = (min(a.bounds[0], b.bounds[0]), min(a.bounds[1], b.bounds[1]),
                      max(a.bounds[2], b.bounds[2]), max(a.bounds[3], b.bounds[3]))
            marks.setdefault((round(point.x / 10), round(point.y / 10)), XMark((point.x, point.y), bounds))
            members.update((i, j))
    return list(marks.values()), members


def collect(doc, config: dict, cross_lines: list | None = None,
            marks: list | None = None) -> tuple[list, list[ArchText]]:
    """Return (barrier curves, texts).

    Cross-out diagonals go to `cross_lines` and shaft X marks to `marks` when those
    lists are given. X-mark diagonals on wall layers are not barriers, so they do not
    cut a shaft into four triangles."""
    classifier = LayerClassifier(config)
    lines, texts, diagonals = [], [], []
    flatten = config["flatten_distance"]
    cross_min = config["cross_line_length"]
    x_min, x_max = config["x_mark_min_length"], config["x_mark_max_length"]
    for layer, entity in iter_flat(doc, doc.modelspace(), max_depth=config["max_block_depth"]):
        kind = entity.dxftype()
        if kind in TEXT_TYPES:
            text = _text_of(doc, entity, layer)
            if text:
                texts.append(text)
            continue
        if kind not in CURVE_TYPES:
            continue
        barrier = classifier.is_barrier(layer) and is_continuous(doc, entity)
        straight = kind in ("LINE", "LWPOLYLINE")
        if not (barrier or (straight and (marks is not None or cross_lines is not None))):
            continue
        geom = _curve_geometry(entity, flatten)
        if geom is None or geom.length <= 0:
            continue
        if is_cross_line(geom, cross_min):
            if cross_lines is not None:
                cross_lines.append(geom)
            continue
        if marks is not None and straight and is_diagonal(geom, x_min, x_max):
            diagonals.append(geom)
        if barrier:
            lines.append(geom)
    if marks is not None:
        found, members = find_x_marks(diagonals)
        marks.extend(found)
        drop = {id(diagonals[i]) for i in members}
        lines = [g for g in lines if id(g) not in drop]
    return lines, texts


def x_marked(poly: Polygon, marks: list[XMark], tree: STRtree | None) -> bool:
    """An X mark centred in `poly` that spans most of its box."""
    if tree is None:
        return False
    env = poly.envelope.area
    for idx in tree.query(poly):
        mark = marks[int(idx)]
        if poly.contains(Point(mark.center)):
            x1, y1, x2, y2 = mark.bbox
            if (x2 - x1) * (y2 - y1) >= 0.5 * env:
                return True
    return False


def is_crossed_out(bbox, cross_lines: list) -> bool:
    """Both arms of a sheet's cross-out X (opposite slopes) pass through the view.

    The X is centred on the sheet, so its crossing point may lie outside the plan."""
    zone = box(*bbox)
    slopes = set()
    for g in cross_lines:
        if g.intersects(zone):
            (x1, y1), (x2, y2) = g.coords
            slopes.add((x2 - x1) * (y2 - y1) > 0)
    return len(slopes) == 2


# ---------------------------------------------------------------- geometry


def _polygons(geom) -> list[Polygon]:
    if geom.is_empty:
        return []
    if geom.geom_type == "Polygon":
        return [geom]
    return [p for g in getattr(geom, "geoms", []) for p in _polygons(g)]


def _valid_polygons(geom) -> list[Polygon]:
    return _polygons(geom if geom.is_valid else make_valid(geom))


def wall_masses(lines: list, config: dict) -> list[Polygon]:
    """Wall mass after a morphological closing that bridges door openings."""
    if not lines:
        return []
    # square caps: round caps leave jambs too rounded for the closing to bridge a door
    raw = unary_union(lines).buffer(config["line_buffer"], cap_style="square", join_style="mitre")
    r = config["gap_close"]
    closed = raw.buffer(r, join_style="mitre", mitre_limit=10).buffer(-r, join_style="mitre", mitre_limit=10)
    return _polygons(closed)


def _filled(group: list[Polygon]):
    return unary_union([Polygon(m.exterior) for m in group])


def is_frame(mass: Polygon, max_thickness: float, min_area: float = 200e6) -> bool:
    """A sheet frame or title-block border: a large ring drawn with single lines.

    Mean thickness 2*area/boundary is ~2x the line buffer for a single line, but the
    real wall thickness (100-300 mm) for double-line walls closed into a solid mass."""
    if Polygon(mass.exterior).area < min_area:
        return False
    return 2 * mass.area / mass.boundary.length < max_thickness


def group_views(masses: list[Polygon], gap: float) -> list[list[Polygon]]:
    """Cluster wall masses (frames already removed) into views.

    Masses are clustered unfilled, so one view's outline does not swallow a
    neighbour; clusters lying inside another cluster's outline (columns or free
    walls in a large room) then join it.
    """
    if not masses:
        return []
    regions = _polygons(unary_union([m.buffer(gap / 2) for m in masses]))
    tree = STRtree(regions)
    groups: list[list[Polygon]] = [[] for _ in regions]
    for mass in masses:
        point = mass.representative_point()
        for idx in tree.query(point):
            if regions[idx].contains(point):
                groups[idx].append(mass)
                break
    groups = [g for g in groups if g]
    filled = [_filled(g) for g in groups]
    order = sorted(range(len(groups)), key=lambda i: -filled[i].area)
    owner: dict[int, int] = {}
    for pos, i in enumerate(order):
        point = groups[i][0].representative_point()
        for j in order[:pos]:
            if j not in owner and filled[j].contains(point):
                owner[i] = j
                break
    merged: dict[int, list[Polygon]] = {i: list(groups[i]) for i in order if i not in owner}
    for i, j in owner.items():
        merged[j].extend(groups[i])
    return list(merged.values())


def parse_area_label(text: str) -> float | None:
    key = normalize(text)
    if not AREA_PREFIX.search(key):
        return None
    match = AREA_LABEL.search(key)
    return float(match.group(1).replace(",", ".")) if match else None


def _ring(coords) -> list[tuple[float, float]]:
    return [(round(x, 1), round(y, 1)) for x, y in coords]


def _pick_title(bbox, titles: list[ArchText], used: set[int], search: float):
    minx, miny, maxx, maxy = bbox
    zone = box(minx - search / 2, miny - search, maxx + search / 2, maxy + search / 2)
    cx = (minx + maxx) / 2
    best, best_d = None, None
    for idx, text in enumerate(titles):
        if idx in used or not zone.contains(Point(text.x, text.y)):
            continue
        # prefer titles under the plan, near its centre line
        d = abs(text.x - cx) + abs(text.y - miny) * 2
        if best_d is None or d < best_d:
            best, best_d = idx, d
    return best


def _name_from(texts: list[ArchText]) -> str | None:
    words = [t.text for t in texts
             if parse_area_label(t.text) is None and re.search(r"[^\W\d_]", t.text)]
    return max(words, key=len) if words else None


def is_wide(poly: Polygon, min_width: float) -> bool:
    """False for slivers narrower than `min_width` (double-line wall cavities)."""
    return not poly.buffer(-min_width / 2, join_style="mitre").is_empty


def build_views(masses: list[Polygon], texts: list[ArchText], config: dict,
                cross_lines: list | None = None, marks: list[XMark] | None = None) -> list[ArchView]:
    min_room = config["min_room_area_m2"] * 1e6

    def wide(poly: Polygon) -> bool:
        return is_wide(poly, config["min_room_width"])

    titles = [t for t in texts if TITLE_PLAN.search(normalize(t.text)) or TITLE_OTHER.search(normalize(t.text))]
    text_tree = STRtree([Point(t.x, t.y) for t in texts]) if texts else None
    mark_tree = STRtree([Point(m.center) for m in marks]) if marks else None
    masses = [m for m in masses if not is_frame(m, config["frame_max_thickness"])]
    mass_tree = STRtree(masses) if masses else None

    views: list[ArchView] = []
    used_titles: set[int] = set()
    groups = group_views(masses, config["view_gap"])
    for group in sorted(groups, key=lambda g: (-unary_union(g).bounds[3], unary_union(g).bounds[0])):
        union = unary_union(group)
        bbox = tuple(round(v, 1) for v in union.bounds)
        t_idx = _pick_title(bbox, titles, used_titles, config["title_search"])
        title = titles[t_idx].text if t_idx is not None else None
        if t_idx is not None:
            used_titles.add(t_idx)
        key = normalize(title) if title else ""
        big_enough = (bbox[2] - bbox[0]) * (bbox[3] - bbox[1]) >= config["min_plan_area_m2"] * 1e6
        is_plan = TITLE_PLAN.search(key) and not TITLE_OTHER.search(key) and big_enough
        kind = "plan" if is_plan else ("other" if title else "untitled")
        if cross_lines and is_crossed_out(bbox, cross_lines):
            kind = "crossed_out"
        floor = FLOOR.search(key)
        view = ArchView(
            id=f"V{len(views) + 1}", kind=kind, title=title,
            floor=floor.group(1).strip().upper() if floor else None, bbox=bbox,
            envelope=[_ring(p.exterior.coords) for p in _polygons(unary_union([Polygon(m.exterior) for m in group]))
                      if p.area >= 20e6],
        )

        rooms: list[Polygon] = []
        for mass in group:
            for hole in mass.interiors:
                for poly in _valid_polygons(Polygon(hole)):
                    if poly.area < min_room:
                        continue
                    inner = [masses[i] for i in mass_tree.query(poly) if masses[i] is not mass
                             and poly.contains(masses[i].representative_point())]
                    if inner:
                        poly = poly.difference(unary_union(inner))
                    # give back the line buffer so the room reaches the wall faces
                    poly = poly.buffer(config["line_buffer"], join_style="mitre")
                    rooms.extend(p for p in _valid_polygons(poly) if p.area >= min_room and wide(p))

        for poly in sorted(rooms, key=lambda p: (-p.centroid.y, p.centroid.x)):
            room = make_room(poly, f"{view.id}-R{len(view.rooms) + len(view.shafts) + 1}", view.id,
                             texts, text_tree, marks or [], mark_tree, config)
            (view.shafts if room.kind == "shaft" else view.rooms).append(room)
        views.append(view)
    return views


def make_room(poly: Polygon, room_id: str, view_id: str, texts: list[ArchText], text_tree: STRtree | None,
              marks: list[XMark], mark_tree: STRtree | None, config: dict) -> ArchRoom:
    """Name, area label and kind of one room polygon.

    A room is a shaft when a text inside names it (HKT, gen, shaft...) or when it is
    small and carries the X mark."""
    inside = [texts[int(i)] for i in text_tree.query(poly)
              if poly.contains(Point(texts[int(i)].x, texts[int(i)].y))] if text_tree else []
    labels = [a for a in (parse_area_label(t.text) for t in inside) if a]
    area = poly.area / 1e6
    named_shaft = any(SHAFT.search(normalize(t.text)) for t in inside)
    marked_shaft = area <= config["shaft_max_area_m2"] and x_marked(poly, marks, mark_tree)
    room = ArchRoom(
        id=room_id, view=view_id, kind="shaft" if named_shaft or marked_shaft else "room",
        area_m2=round(area, 2), polygon=_ring(poly.exterior.coords),
        name=_name_from(inside), texts=[t.text for t in inside][:12],
    )
    if labels:
        room.label_area_m2 = labels[0]
        room.label_error_pct = round((area - labels[0]) / labels[0] * 100, 1)
    return room


# ---------------------------------------------------------------- outputs


def summarize(views: list[ArchView]) -> dict:
    plans = [v for v in views if v.kind == "plan"]
    labelled = [r for v in plans for r in v.rooms if r.label_error_pct is not None]
    return {
        "views": len(views),
        "plan_views": len(plans),
        "crossed_out_views": sum(1 for v in views if v.kind == "crossed_out"),
        "rooms_in_plans": sum(len(v.rooms) for v in plans),
        "shafts_in_plans": sum(len(v.shafts) for v in plans),
        "labelled_rooms": len(labelled),
        "labelled_within_5pct": sum(1 for r in labelled if abs(r.label_error_pct) <= 5),
        "labelled_within_10pct": sum(1 for r in labelled if abs(r.label_error_pct) <= 10),
    }


def export_json(views: list[ArchView], summary: dict, path: Path, source: str) -> None:
    payload = {"source": source, "units": "mm", "summary": summary, "views": [asdict(v) for v in views]}
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")


def export_overlay(views: list[ArchView], path: Path, config: dict) -> None:
    """Overlay-only DXF in drawing coordinates; attach it as an xref at 0,0 to check."""
    doc = ezdxf.new("R2018", setup=True)
    for name, color in (("ARCH0_VIEW", 8), ("ARCH0_ENVELOPE", 1), ("ARCH0_ROOM", 3),
                        ("ARCH0_SHAFT", 6), ("ARCH0_TEXT", 2)):
        doc.layers.add(name, color=color)
    msp = doc.modelspace()
    h = config["overlay_text_height"]
    for view in views:
        minx, miny, maxx, maxy = view.bbox
        msp.add_lwpolyline([(minx, miny), (maxx, miny), (maxx, maxy), (minx, maxy)], close=True,
                           dxfattribs={"layer": "ARCH0_VIEW"})
        msp.add_text(f"{view.id} {view.kind} {view.title or ''}", height=h * 2,
                     dxfattribs={"layer": "ARCH0_VIEW", "insert": (minx, maxy + h)})
        for ring in view.envelope:
            msp.add_lwpolyline(ring, close=True, dxfattribs={"layer": "ARCH0_ENVELOPE"})
        for room in view.rooms + view.shafts:
            layer = "ARCH0_ROOM" if room.kind == "room" else "ARCH0_SHAFT"
            msp.add_lwpolyline(room.polygon, close=True, dxfattribs={"layer": layer})
            label = f"{room.id} {room.area_m2:.1f}m2"
            if room.label_area_m2 is not None:
                label += f" (DT {room.label_area_m2:g}, {room.label_error_pct:+.1f}%)"
            if room.name:
                label += f" {room.name}"
            point = make_valid(Polygon(room.polygon)).representative_point()
            msp.add_text(label, height=h, dxfattribs={"layer": "ARCH0_TEXT", "insert": (point.x, point.y)})
    doc.saveas(path)


def run(input_path: str | Path, out_dir: str | Path, config: dict | None = None) -> dict:
    cfg = dict(DEFAULT_ARCH_CONFIG)
    cfg.update(config or {})
    doc = load_drawing(input_path)
    cross_lines: list = []
    marks: list[XMark] = []
    lines, texts = collect(doc, cfg, cross_lines, marks)
    masses = wall_masses(lines, cfg)
    views = build_views(masses, texts, cfg, cross_lines, marks)
    summary = summarize(views)

    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    export_json(views, summary, out / "arch.json", str(Path(input_path).resolve()))
    export_overlay(views, out / "arch_overlay.dxf", cfg)
    return {"barrier_curves": len(lines), "texts": len(texts), **summary,
            "json": str((out / "arch.json").resolve()), "overlay": str((out / "arch_overlay.dxf").resolve())}


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract plan views, rooms, envelope and shafts from an architectural DXF.")
    parser.add_argument("--input", required=True, help="DXF file (convert DWG first).")
    parser.add_argument("--out-dir", required=True, help="Folder for arch.json and arch_overlay.dxf.")
    parser.add_argument("--config", help="JSON file overriding DEFAULT_ARCH_CONFIG keys.")
    args = parser.parse_args()
    config = json.loads(Path(args.config).read_text(encoding="utf-8")) if args.config else None
    sys.stdout.reconfigure(encoding="utf-8")  # Windows consoles default to cp1252
    print(json.dumps(run(args.input, args.out_dir, config), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
