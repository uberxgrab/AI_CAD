"""MEP pipe and duct normalisation: the back end of the `mep_map` MCP tool and of `HVACNORM`.

Drawings from other offices put pipes and single-line ducts on their own layers and write the
size only in nearby text (`WS-PPR-DN90`, `WW-UPVC-DN110, I=1%`, `SAD-1200x800`). This module

1. reads every size label (TEXT, MTEXT, ATTRIB, MULTILEADER, LEADER; plain leader lines on
   note layers are followed to their far end),
2. collects the curves of the layers a *map* assigns to a system (WS, CHWS, SA, ...),
3. gives each curve piece (one polyline segment, or a whole line/arc/spline) a size:
   `layer` (the layer name says it), `high` (a label points at it), `medium` (carried along an
   unbranched run from a labelled piece), `low` (carried across branches), or `none`,
4. checks the add-in rules: colour/linetype/lineweight overrides, duplicated or overlapping
   curves, small gaps, curve types HVACQTO does not count, unsized pieces, unused labels,
5. proposes, per curve, the template layer (`EIA_1 CHWS DN50`, `EIA_1 Pipe condensated DN27`,
   `EIA_1 RP 15.9-9.5 (14)`, `EIA_1 SUPPLY AIR`) when the system and material match the BOQ, or
   keeps the layer and gives a BOQ prefix (`WS`) for other materials (user decision of
   2026-11-23: no new layers),
6. summarises a BOQ preview, and without a map proposes one (from which layers the labels
   point at, and from layer-name keywords).

Nothing here writes to a drawing. The add-in applies the result (preview + Yes/No).

    venv\\Scripts\\python.exe mep_normalize.py --request C:\\tmp\\norm\\request.json
"""
from __future__ import annotations

import argparse
import json
import math
import re
import sys
import traceback
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path

import networkx as nx
from shapely.geometry import LineString, Point, Polygon
from shapely.ops import substring
from shapely.strtree import STRtree

import arch_extract as ax

# ---------------------------------------------------------------------------------------------
# Catalogue (mirrors cad_addin/src/PipeCatalogCore.cs and the template MEP_DWT_3.dwg)
# ---------------------------------------------------------------------------------------------

WATER_SIZES = (15, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150, 200, 250, 300, 350, 400, 450, 500, 600)
CONDENSATE_SIZES = (21, 27, 34, 48, 60, 90, 110)
REFRIGERANT_SIZES = (
    "9.5-6.4", "12.7-6.4", "15.9-6.4", "15.9-9.5 (14)", "19.1-9.5 (22.4)", "22.2-9.5 (28)",
    "28.6-12.7 (33.6-44.8)", "28.6-15.9 (50.4-61.6)", "34.9-15.9 (67.2)", "34.9-19.1 (72.8-95.2)",
    "41.3-19.1 (100.8-151.2)")
# Water systems whose sized layers the template holds; CWS/CWR/HWS/HWR layers exist only when a
# drawing already has them (HVACPIPESIZE creates them), and no new layers are made here.
TEMPLATE_WATER = {"CHWS", "CHWR"}

PLASTICS = {"PPR", "HDPE", "uPVC", "PE", "CPVC", "ABS"}
STEELS = {"STEEL", "GI"}

# kind, the template target family, the materials that count as "the BOQ material", a description.
SYSTEMS: dict[str, dict] = {
    "CHWS": {"kind": "pipe", "eia": "water", "materials": STEELS, "name": "chilled water supply"},
    "CHWR": {"kind": "pipe", "eia": "water", "materials": STEELS, "name": "chilled water return"},
    "CWS": {"kind": "pipe", "eia": "water", "materials": STEELS, "name": "condenser water supply"},
    "CWR": {"kind": "pipe", "eia": "water", "materials": STEELS, "name": "condenser water return"},
    "HWS": {"kind": "pipe", "eia": "water", "materials": STEELS, "name": "heating hot water supply"},
    "HWR": {"kind": "pipe", "eia": "water", "materials": STEELS, "name": "heating hot water return"},
    "COND": {"kind": "pipe", "eia": "condensate", "materials": {"uPVC"}, "name": "AC condensate"},
    "RP": {"kind": "pipe", "eia": "refrigerant", "materials": {"CU"}, "name": "refrigerant"},
    "WS": {"kind": "pipe", "eia": None, "name": "domestic cold water"},
    "DHW": {"kind": "pipe", "eia": None, "name": "domestic hot water"},
    "WW": {"kind": "pipe", "eia": None, "name": "waste water"},
    "SW": {"kind": "pipe", "eia": None, "name": "soil water"},
    "RW": {"kind": "pipe", "eia": None, "name": "rain water"},
    "VP": {"kind": "pipe", "eia": None, "name": "vent pipe"},
    "PW": {"kind": "pipe", "eia": None, "name": "process waste water"},
    "FP": {"kind": "pipe", "eia": None, "name": "fire protection"},
    "SP": {"kind": "pipe", "eia": None, "name": "sprinkler"},
    "CA": {"kind": "pipe", "eia": None, "name": "compressed air"},
    "N2": {"kind": "pipe", "eia": None, "name": "nitrogen"},
    "AR": {"kind": "pipe", "eia": None, "name": "argon"},
    "GAS": {"kind": "pipe", "eia": None, "name": "fuel gas"},
    "SA": {"kind": "duct", "eia": "duct", "layer": "EIA_1 SUPPLY AIR", "name": "supply air"},
    "RA": {"kind": "duct", "eia": "duct", "layer": "EIA_1 RETURN AIR", "name": "return air"},
    "OA": {"kind": "duct", "eia": "duct", "layer": "EIA_1 OUTSIDE AIR", "name": "outside / fresh air"},
    "EA": {"kind": "duct", "eia": "duct", "layer": "EIA_1 EXHAUST AIR", "name": "exhaust air"},
    "TA": {"kind": "duct", "eia": "duct", "layer": "EIA_1 TOILET AIR", "name": "toilet exhaust"},
    "KE": {"kind": "duct", "eia": "duct", "layer": "EIA_3 KITCHEN EXHAUST AIR", "name": "kitchen exhaust"},
    "SE": {"kind": "duct", "eia": "duct", "layer": "EIA_2 EXHAUST SMOKE", "name": "smoke exhaust"},
    "PA": {"kind": "duct", "eia": "duct", "layer": "EIA_2 STAIR PRESSURISATION", "name": "stair pressurisation"},
    "MA": {"kind": "duct", "eia": "duct", "layer": "EIA_2 MAKUP AIR", "name": "make-up air"},
    "PE": {"kind": "duct", "eia": None, "name": "process exhaust"},
}
DUCT_MATERIALS = {"GI", "STEEL"}

# Label / layer words that name a system. Duct label tokens end in D (duct) or G (grille):
# SAD, RAG, ... are handled separately.
SYSTEM_ALIASES = {
    "CHS": "CHWS", "CHR": "CHWR", "CDS": "CWS", "CDR": "CWR", "CTWS": "CWS", "CTWR": "CWR",
    "CD": "COND", "CON": "COND", "CONDENSATE": "COND", "CONDENSATED": "COND", "DRAIN": "COND",
    "REF": "RP", "RFG": "RP", "REFRIGERANT": "RP",
    "CW": "WS", "DCW": "WS", "WATER SUPPLY": "WS", "HW": "DHW", "HWS-D": "DHW",
    "WD": "WW", "SOIL": "SW", "RAIN": "RW", "VENT": "VP", "V": "VP",
    "FA": "OA", "FRESH": "OA", "EXH": "EA", "TOILET": "TA", "KEA": "KE", "SEA": "SE",
}

MATERIAL_PATTERNS = (
    (re.compile(r"SUS\s*-?\s*(304|316)L?", re.I), lambda m: "SUS" + m.group(1)),
    (re.compile(r"(?<![A-Z])PP-?R(?![A-Z])", re.I), lambda m: "PPR"),
    (re.compile(r"(?<![A-Z])HDPE(?![A-Z])", re.I), lambda m: "HDPE"),
    (re.compile(r"(?<![A-Z])(?:U-?PVC|PVC-?U|PVC)(?![A-Z])", re.I), lambda m: "uPVC"),
    (re.compile(r"(?<![A-Z])CPVC(?![A-Z])", re.I), lambda m: "CPVC"),
    (re.compile(r"(?<![A-Z])(?:GI|GALV\w*)(?![A-Z])", re.I), lambda m: "GI"),
    (re.compile(r"(?<![A-Z])(?:STEEL|MS|CS|BS|SCH\s*\d+)(?![A-Z])", re.I), lambda m: "STEEL"),
    (re.compile(r"(?<![A-Z])(?:CU|COPPER)(?![A-Z])", re.I), lambda m: "CU"),
    (re.compile(r"(?<![A-Z])PE(?:100|80)?(?![A-Z])", re.I), lambda m: "PE"),
)

# Layer-name words used to guess a layer's system when no label points at it.
LAYER_KEYWORDS = (
    ("duct", r"DUCT|ONG\s*GIO|风管"),
    ("SA", r"(?<![A-Z])SA(?![A-Z])|SUPPLY\s*AIR|送风"),
    ("RA", r"(?<![A-Z])RA(?![A-Z])|RETURN\s*AIR|回风"),
    ("OA", r"(?<![A-Z])(OA|FA)(?![A-Z])|OUTSIDE\s*AIR|FRESH\s*AIR|新风"),
    ("EA", r"(?<![A-Z])EA(?![A-Z])|EXHAUST|排风"),
    ("CHWS", r"CHWS|CHILLED\s*WATER\s*SUPPLY|冷冻供水|空冷供水"),
    ("CHWR", r"CHWR|CHILLED\s*WATER\s*RETURN|冷冻回水|空冷回水"),
    ("CWS", r"CTWS|CWS|COOLING\s*WATER\s*SUPPLY|冷却供水"),
    ("CWR", r"CTWR|CWR|COOLING\s*WATER\s*RETURN|冷却回水"),
    ("RP", r"(?<![A-Z])(REF|RP)(?![A-Z])|REFRIG|冷媒"),
    ("COND", r"CONDENS|HVAC.*DRAIN|AC.*DRAIN|冷凝"),
    ("WS", r"(?<![A-Z])WS(?![A-Z])|WATER\s*SUPPLY|COLD\s*WATER|CAP\s*NUOC|给水"),
    ("WW", r"(?<![A-Z])(WW|WD)(?![A-Z])|WASTE|SEWER|THOAT\s*NUOC|排水|污水|废水"),
    ("RW", r"RAIN|雨水"),
)
NOT_MEP_LAYER = re.compile(
    r"DIM|HATCH|TEXT|NOTE|TITLE|FRAME|AXIS|GRID|XREF|TUONG|WALL|DOOR|WINDOW|CUA|NOITHAT|FURN|"
    r"COLUMN|COT|DEFPOINTS|VIEWPORT|EQUIP|SYMBOL|TAG|ANNO|LABEL|TERMINAL|GRILLE|DIFFUSER", re.I)
LEADER_LAYER = re.compile(r"NOTE|TEXT|LEAD|ANNO|TAG|LABEL|CHU|GHI", re.I)
SCHEMATIC_WORDS = re.compile(r"SCHEMATIC|RISER\s+DIAGRAM|SO\s*DO\s*NGUYEN\s*LY|SƠ\s*ĐỒ", re.I)

DEFAULTS = {
    "snap_mm": 20.0,            # endpoints closer than this are one node
    "gap_max_mm": 150.0,        # open ends closer than this to another curve are reported as gaps
    "arrow_radius_mm": 300.0,   # a leader arrow must touch a piece within this distance
    "text_radius_mm": None,     # plain text search radius; None = 4 x text height, 600..3000 mm
    "max_pieces": 400000,       # discovery falls back to layer names above this
    "max_issues": 2000,
}

CONFIDENCES = ("layer", "high", "medium", "low", "none")


# ---------------------------------------------------------------------------------------------
# Label parsing
# ---------------------------------------------------------------------------------------------

NUM = r"\d+(?:[.,]\d+)?"
DIA = r"(?:Ø|ø|Φ|φ|%%[cC])"
RE_TERMINAL = re.compile(r"(\d+)\s*[xX×]\s*\(([^)]*)\)")
RE_RECT = re.compile(r"(?:(?P<sys>[A-Za-z]{2,4})\s*-?\s*)?(?<!\d)(?P<w>\d{2,4})\s*[xX×*]\s*(?P<h>\d{2,4})(?!\d)")
RE_ROUND_DUCT = re.compile(
    r"(?P<sys>[A-Za-z]{2,3}[DdGg])\s*-?\s*(?:(?P<d1>\d{2,4})\s*" + DIA + r"|" + DIA + r"\s*(?P<d2>\d{2,4}))")
RE_PAIR = re.compile(
    r"(?<![\d.])" + DIA + r"?\s*(?P<g>\d{1,2}\.\d{1,2})\s*[/-]\s*" + DIA + r"?\s*(?P<l>\d{1,2}\.\d{1,2})(?![\d.])")
RE_PIPE = re.compile(
    r"(?<![A-Za-z0-9])(?P<p>DN|D|" + DIA + r")\s*-?\s*(?P<n>" + NUM + r")(?![\d]|\s*[xX×*]\s*\d)")
RE_GRILLE_WORD = re.compile(r"GRILL|DIFFUS|LOUV|REGISTER|(?<![A-Z])(DG|LG|SG|RG)(?![A-Z])", re.I)
RE_DRAIN_SLOPE = re.compile(r"\s*-\s*(" + NUM + r")\s*%")
RE_SLOPE = re.compile(r"(?:I|i|S|slope)\s*=\s*(?P<s>" + NUM + r")\s*%")
RE_TOKEN = re.compile(r"[A-Za-z][A-Za-z0-9]*")


@dataclass
class SizeItem:
    kind: str                     # pipe | duct | terminal
    system: str | None = None
    material: str | None = None
    size: float | None = None     # pipe nominal size / outside diameter, or round duct diameter
    prefix: str | None = None     # how the size was written: DN, D, Ø
    pair: tuple | None = None     # refrigerant (gas, liquid)
    width: float | None = None
    height: float | None = None
    slope: float | None = None
    count: int | None = None
    text: str = ""

    def key(self) -> tuple:
        return (self.kind, self.size, self.pair, self.width, self.height)

    def as_dict(self) -> dict:
        d = {"kind": self.kind}
        for name in ("system", "material", "size", "prefix", "pair", "width", "height", "slope", "count"):
            value = getattr(self, name)
            if value is not None:
                d[name] = list(value) if isinstance(value, tuple) else value
        return d


def _num(text: str) -> float:
    return float(text.replace(",", "."))


def _material(text: str) -> str | None:
    for pattern, make in MATERIAL_PATTERNS:
        m = pattern.search(text)
        if m:
            return make(m)
    return None


def system_from_token(token: str) -> str | None:
    t = token.upper()
    if t in SYSTEMS:
        return t
    if t in SYSTEM_ALIASES:
        return SYSTEM_ALIASES[t]
    return None


def _duct_system(token: str | None) -> tuple[str | None, bool]:
    """`SAD` -> ("SA", False), `RAG` -> ("RA", True): the system and whether it is a grille."""
    if not token:
        return None, False
    t = token.upper()
    if len(t) >= 3 and t[-1] in "DG":
        base = system_from_token(t[:-1])
        if base and SYSTEMS[base]["kind"] == "duct":
            return base, t[-1] == "G"
    base = system_from_token(t)
    if base and SYSTEMS[base]["kind"] == "duct":
        return base, False
    return None, False


def _line_system(text: str) -> str | None:
    """The last token before the size that names a system: `INCOMMING-WS-HDPE-DN110` -> WS."""
    found = None
    for token in RE_TOKEN.findall(text):
        sys_ = system_from_token(token)
        if sys_ is None:
            sys_, _ = _duct_system(token)
        if sys_:
            found = sys_
    return found


def parse_label(text: str) -> list[SizeItem]:
    """Size items in one label. Each line is read separately; a line gives at most one item
    per size it holds (`SAD-1200x800` and `RAD-1200x700` on two lines give two items)."""
    items: list[SizeItem] = []
    if not text:
        return items
    for line in re.split(r"[\r\n]+|\\P", text):
        line = line.strip()
        if not line or len(line) > 120:
            continue
        slope_m = RE_SLOPE.search(line)
        slope = _num(slope_m.group("s")) if slope_m else None
        material = _material(line)
        rest = line
        for m in RE_TERMINAL.finditer(line):
            inner = m.group(2)
            sys_, _ = _duct_system(next(iter(RE_TOKEN.findall(inner)), None))
            r = RE_RECT.search(inner)
            d = RE_ROUND_DUCT.search(inner)
            if not (r or d):
                continue          # "2x(...)" without a size is not a grille count
            item = SizeItem("terminal", system=sys_, count=int(m.group(1)), text=line)
            if r:
                item.width, item.height = _num(r.group("w")), _num(r.group("h"))
            elif d:
                item.size = _num(d.group("d1") or d.group("d2"))
            items.append(item)
            rest = rest.replace(m.group(0), " ")
        for m in RE_RECT.finditer(rest):
            sys_, grille = _duct_system(m.group("sys"))
            if sys_ is None and m.group("sys"):
                sys_ = _line_system(rest[:m.start()] + " " + m.group("sys"))
            elif sys_ is None:
                sys_ = _line_system(rest[:m.start()])
            grille = grille or (sys_ is None and RE_GRILLE_WORD.search(line) is not None)
            kind = "terminal" if grille else "duct"
            items.append(SizeItem(kind, system=sys_, width=_num(m.group("w")), height=_num(m.group("h")),
                                  count=1 if grille else None, material=material, text=line))
            rest = rest.replace(m.group(0), " ")
        for m in RE_ROUND_DUCT.finditer(rest):
            sys_, grille = _duct_system(m.group("sys"))
            if sys_ is None:
                continue
            d = _num(m.group("d1") or m.group("d2"))
            items.append(SizeItem("terminal" if grille else "duct", system=sys_, size=d, prefix="Ø",
                                  count=1 if grille else None, material=material, text=line))
            rest = rest.replace(m.group(0), " ")
        for m in RE_PAIR.finditer(rest):
            g, l = _num(m.group("g")), _num(m.group("l"))
            if 6 <= l < g <= 60:
                items.append(SizeItem("pipe", system=_line_system(rest[:m.start()]) or "RP", material="CU",
                                      pair=(g, l), text=line))
                rest = rest.replace(m.group(0), " ")
        for m in RE_PIPE.finditer(rest):
            n = _num(m.group("n"))
            if not 6 <= n <= 2000:
                continue
            prefix = m.group("p").upper()
            prefix = "Ø" if prefix not in ("DN", "D") else prefix
            own_slope = RE_DRAIN_SLOPE.match(rest, m.end())     # HVACDRAIN labels: D27-1%
            items.append(SizeItem("pipe", system=_line_system(rest[:m.start()]), material=material, size=n,
                                  prefix=prefix, slope=_num(own_slope.group(1)) if own_slope else slope,
                                  text=line))
    return items


def size_text(item: SizeItem, material: str | None = None) -> str:
    """`D90` for plastic pipes (outside diameter), `DN50` for steel, `Ø15.9/9.5`, `600x300`."""
    if item.pair:
        return f"Ø{_fmt(item.pair[0])}/{_fmt(item.pair[1])}"
    if item.width is not None:
        return f"{_fmt(item.width)}x{_fmt(item.height)}"
    if item.size is None:
        return "?"
    mat = material or item.material
    if item.kind == "duct" or item.prefix == "Ø":
        return f"Ø{_fmt(item.size)}"
    if mat in PLASTICS or (mat is None and item.prefix == "D"):
        return f"D{_fmt(item.size)}"
    return f"DN{_fmt(item.size)}"


def _fmt(v: float) -> str:
    return f"{v:.1f}".rstrip("0").rstrip(".") if v != int(v) else str(int(v))


# ---------------------------------------------------------------------------------------------
# Standard (template) layers
# ---------------------------------------------------------------------------------------------

RE_EIA_WATER = re.compile(r"^EIA_1 (CHWS|CHWR|CWS|CWR|HWS|HWR) DN(\d+)$", re.I)
RE_EIA_COND = re.compile(r"^EIA_1 Pipe condensated DN(\d+)$", re.I)


def parse_standard_layer(layer: str) -> tuple[str, SizeItem] | None:
    """A catalogue layer read back into its system and size, or None."""
    name = layer.strip()
    m = RE_EIA_WATER.match(name)
    if m and int(m.group(2)) in WATER_SIZES:
        return m.group(1).upper(), SizeItem("pipe", system=m.group(1).upper(), size=float(m.group(2)), prefix="DN")
    m = RE_EIA_COND.match(name)
    if m and int(m.group(1)) in CONDENSATE_SIZES:
        return "COND", SizeItem("pipe", system="COND", size=float(m.group(1)), prefix="D", material="uPVC")
    if name.upper().startswith("EIA_1 RP "):
        text = name[9:].strip()
        for known in REFRIGERANT_SIZES:
            if known.casefold() == text.casefold():
                g, l = known.split(" ")[0].split("-")
                return "RP", SizeItem("pipe", system="RP", pair=(float(g), float(l)), material="CU")
    for code, info in SYSTEMS.items():
        if info.get("layer") and info["layer"].casefold() == name.casefold():
            return code, None
    return None


def target_layer(system: str, item: SizeItem | None, material: str | None) -> str | None:
    """The template layer a piece of this system, size and material belongs on, or None when
    the material is not the BOQ material or the size has no layer."""
    info = SYSTEMS.get(system)
    if not info or not info.get("eia"):
        return None
    mats = info.get("materials", DUCT_MATERIALS if info["kind"] == "duct" else set())
    if material and material not in mats:
        return None
    family = info["eia"]
    if family == "duct":
        return info["layer"]
    if item is None:
        return None
    if family == "water" and item.size is not None and int(item.size) == item.size and int(item.size) in WATER_SIZES:
        return f"EIA_1 {system} DN{int(item.size)}"
    if family == "condensate" and item.size is not None and int(item.size) in CONDENSATE_SIZES:
        return f"EIA_1 Pipe condensated DN{int(item.size)}"
    if family == "refrigerant" and item.pair:
        key = f"{_fmt(item.pair[0])}-{_fmt(item.pair[1])}"
        for known in REFRIGERANT_SIZES:
            if known.split(" ")[0] == key:
                return "EIA_1 RP " + known
    return None


def target_in_template(layer: str) -> bool:
    m = RE_EIA_WATER.match(layer)
    if m:
        return m.group(1).upper() in TEMPLATE_WATER
    return True


# ---------------------------------------------------------------------------------------------
# Drawing walk
# ---------------------------------------------------------------------------------------------

@dataclass
class Piece:
    curve: int
    seg: int                   # segment index inside the curve (vertex seg -> seg + 1)
    coords: list
    length: float
    line: LineString = None
    system: str | None = None
    size: SizeItem | None = None
    confidence: str = "none"
    label: int | None = None   # index of the label that sized it (directly or by propagation)
    duplicate: bool = False
    d0: float = 0.0            # where the piece starts along its segment (after cuts at tees)


@dataclass
class Curve:
    handle: str | None
    layer: str
    raw_layer: str
    dxftype: str
    in_block: bool
    overrides: list
    pieces: list = field(default_factory=list)
    system: str | None = None


@dataclass
class Label:
    items: list
    point: tuple
    height: float
    angle: float | None
    layer: str
    handle: str | None
    text: str
    arrows: list = field(default_factory=list)
    box: object = None         # shapely geometry of the text area, for leader-line search
    used: bool = False


def _bulge_points(p1, p2, bulge: float, sagitta: float = 20.0) -> list:
    if abs(bulge) < 1e-9:
        return [p2]
    chord = math.dist(p1, p2)
    if chord < 1e-9:
        return [p2]
    theta = 4 * math.atan(bulge)
    radius = chord / (2 * math.sin(abs(theta) / 2))
    mx, my = (p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2
    dx, dy = (p2[0] - p1[0]) / chord, (p2[1] - p1[1]) / chord
    h = radius * math.cos(abs(theta) / 2)
    sign = 1 if bulge > 0 else -1
    cx, cy = mx - sign * dy * h, my + sign * dx * h
    a1 = math.atan2(p1[1] - cy, p1[0] - cx)
    steps = max(2, min(64, int(abs(theta) * radius / max(1.0, math.sqrt(8 * radius * sagitta)) + 2)))
    return [(cx + radius * math.cos(a1 + theta * k / steps), cy + radius * math.sin(a1 + theta * k / steps))
            for k in range(1, steps + 1)]


def curve_segments(entity) -> list[list]:
    """Segments of a curve as point lists: one per polyline segment (arcs flattened), one for a
    line, arc or spline. Empty for anything else."""
    kind = entity.dxftype()
    try:
        if kind == "LINE":
            s, e = entity.dxf.start, entity.dxf.end
            return [[(s.x, s.y), (e.x, e.y)]]
        if kind in ("LWPOLYLINE", "POLYLINE"):
            if kind == "LWPOLYLINE":
                pts = [(p[0], p[1], p[2]) for p in entity.get_points("xyb")]
                closed = entity.closed
            else:
                if not (entity.is_2d_polyline or entity.is_3d_polyline):
                    return []
                pts = [(v.dxf.location.x, v.dxf.location.y, v.dxf.get("bulge", 0.0)) for v in entity.vertices]
                closed = entity.is_closed
            if closed and len(pts) > 1:
                pts.append(pts[0])
            segs = []
            for i in range(len(pts) - 1):
                p1, p2 = pts[i][:2], pts[i + 1][:2]
                segs.append([p1] + _bulge_points(p1, p2, pts[i][2]))
            return segs
        if kind in ("ARC", "SPLINE"):
            pts = [(v.x, v.y) for v in entity.flattening(20.0)]
            return [pts] if len(pts) >= 2 else []
    except Exception:
        return []
    return []


def _overrides(entity) -> list:
    out = []
    color = entity.dxf.get("color", 256)
    if color != 256 or entity.dxf.hasattr("true_color"):
        out.append(f"color={color}")
    lt = (entity.dxf.get("linetype") or "BYLAYER")
    if lt.upper() != "BYLAYER":
        out.append(f"linetype={lt}")
    lw = entity.dxf.get("lineweight", -1)
    if lw != -1:
        out.append(f"lineweight={lw}")
    return out


def _text_of(entity) -> str:
    kind = entity.dxftype()
    try:
        if kind == "MTEXT":
            return entity.plain_text()
        if kind in ("TEXT", "ATTRIB"):
            return entity.plain_text()
        if kind == "MULTILEADER":
            from ezdxf.tools.text import plain_mtext
            mt = entity.context.mtext
            return plain_mtext(mt.default_content) if mt is not None else ""
    except Exception:
        return ""
    return ""


def _label_geometry(entity, text: str):
    """(centre point, height, angle in radians or None, text area polygon)."""
    kind = entity.dxftype()
    if kind == "MULTILEADER":
        mt = entity.context.mtext
        p = mt.insert
        h = entity.context.char_height or 250.0
        lines = max(1, text.count("\n") + 1)
        width = max(len(s) for s in text.split("\n")) * h * 0.7 if text else h
        box = LineString([(p.x, p.y), (p.x + width, p.y - h * lines)]).envelope.buffer(h * 0.3)
        return (box.centroid.x, box.centroid.y), h, None, box
    if kind == "MTEXT":
        p = entity.dxf.insert
        h = entity.dxf.get("char_height", 250.0) or 250.0
        try:
            angle = math.radians(entity.get_rotation())
        except Exception:
            angle = 0.0
        lines = text.split("\n") or [""]
        width = max(len(s) for s in lines) * h * 0.7
        tall = h * len(lines) * 1.5
        att = entity.dxf.get("attachment_point", 1)
        col, row = (att - 1) % 3, (att - 1) // 3
        left, top = -width * col / 2, tall * row / 2
        c, s = math.cos(angle), math.sin(angle)
        corners = [(p.x + x * c - y * s, p.y + x * s + y * c)
                   for x, y in ((left, top), (left + width, top), (left + width, top - tall), (left, top - tall))]
        box = Polygon(corners)
        return (box.centroid.x, box.centroid.y), h, angle, box
    p = entity.dxf.insert
    h = entity.dxf.get("height", 250.0) or 250.0
    angle = math.radians(entity.dxf.get("rotation", 0.0))
    width = max(1, len(text)) * h * 0.7 * (entity.dxf.get("width", 1.0) or 1.0)
    c, s = math.cos(angle), math.sin(angle)
    halign = entity.dxf.get("halign", 0)
    valign = entity.dxf.get("valign", 0)
    if halign and entity.dxf.hasattr("align_point"):
        p = entity.dxf.align_point
    start = -width / 2 if halign in (1, 4) else (-width if halign == 2 else 0.0)
    rise = {0: 0.0, 1: 0.0, 2: -h / 2, 3: -h}.get(valign, 0.0)
    if halign == 4:
        rise = -h / 2
    a = (p.x + start * c - rise * s, p.y + start * s + rise * c)
    b = (a[0] + width * c, a[1] + width * s)
    up = (-s * h, c * h)
    box = Polygon([a, b, (b[0] + up[0], b[1] + up[1]), (a[0] + up[0], a[1] + up[1])])
    centre = ((a[0] + b[0]) / 2 + up[0] / 2, (a[1] + b[1]) / 2 + up[1] / 2)
    return centre, h, angle, box


def _in_region(points, region) -> bool:
    if region is None:
        return True
    return any(region[0] <= x <= region[2] and region[1] <= y <= region[3] for x, y in points)


@dataclass
class Scan:
    curves: list
    labels: list
    note_lines: list           # (layer, coords) of possible leader lines
    leaders: list              # LEADER arrow + far end
    layer_lengths: Counter
    layer_types: dict
    schematic: bool


def scan_drawing(doc, region=None, max_depth: int = 8, wanted=None) -> Scan:
    """Walk model space once. `wanted(layer) -> bool` limits which curve layers keep their
    geometry (all when None)."""
    from aitools import scan as walker   # shared flattening walk (keeps raw xref layer names)

    curves: list[Curve] = []
    labels: list[Label] = []
    notes: list = []
    leaders: list = []
    lengths: Counter = Counter()
    types: dict = defaultdict(Counter)
    schematic = False
    for raw, clean, e in walker.walk(doc, max_depth):
        kind = e.dxftype()
        if kind in ("TEXT", "MTEXT", "ATTRIB", "MULTILEADER"):
            text = _text_of(e)
            if not text:
                continue
            if not schematic and SCHEMATIC_WORDS.search(text):
                schematic = True
            items = parse_label(text)
            if not items:
                continue
            try:
                centre, h, angle, box = _label_geometry(e, text)
            except Exception:
                continue
            arrows = []
            if kind == "MULTILEADER":
                try:
                    for leader in e.context.leaders:
                        for ll in leader.lines:
                            if ll.vertices:
                                v = ll.vertices[0]
                                arrows.append((v.x, v.y))
                except Exception:
                    pass
            if not _in_region([centre] + arrows, region):
                continue
            labels.append(Label(items, centre, h, angle, clean, e.dxf.get("handle"), text, arrows, box))
            continue
        if kind == "LEADER":
            try:
                vs = [(float(v[0]), float(v[1])) for v in e.vertices]
                if len(vs) >= 2:
                    leaders.append((vs[0], vs[-1]))
            except Exception:
                pass
            continue
        segs = curve_segments(e)
        if not segs:
            continue
        seg_len = [LineString(s).length for s in segs]
        total = sum(seg_len)
        lengths[clean] += total
        types[clean][kind] += 1
        if LEADER_LAYER.search(clean) and total < 20000 and kind in ("LINE", "LWPOLYLINE", "POLYLINE"):
            pts = [segs[0][0]] + [s[-1] for s in segs]
            notes.append((clean, pts))
        if wanted is not None and not wanted(clean):
            continue
        all_pts = [p for s in segs for p in (s[0], s[-1])]
        if not _in_region(all_pts, region):
            continue
        handle = e.dxf.get("handle")
        curve = Curve(handle, clean, raw, kind, handle is None, _overrides(e) if handle else [])
        idx = len(curves)
        for i, (s, length) in enumerate(zip(segs, seg_len)):
            if length > 1e-6:
                curve.pieces.append(Piece(idx, i, s, length, LineString(s)))
        if curve.pieces:
            curves.append(curve)
    # LEADER arrows: give each the label whose text is nearest its far end.
    for arrow, far in leaders:
        best, bd = None, None
        for lab in labels:
            d = lab.box.distance(Point(far)) if lab.box is not None else math.dist(lab.point, far)
            if bd is None or d < bd:
                best, bd = lab, d
        if best is not None and bd <= max(600.0, 3 * best.height):
            best.arrows.append(arrow)
    # Sheet titles usually sit in paper space ("WATER SUPPLY SYSTEM SCHEMATIC DIAGRAM").
    for layout in doc.layouts:
        if schematic or layout.name == "Model":
            continue
        for e in layout.query("TEXT MTEXT"):
            if SCHEMATIC_WORDS.search(_text_of(e)):
                schematic = True
                break
    return Scan(curves, labels, notes, leaders, lengths, types, schematic)


def attach_note_lines(labels: list, notes: list, mapped_layers: set) -> None:
    """A plain line or polyline on a note layer that starts at a label without arrows is a
    hand-drawn leader: its far end becomes the label's arrow."""
    if not notes:
        return
    geoms = [LineString(pts) for _, pts in notes]
    tree = STRtree(geoms)
    for lab in labels:
        if lab.arrows or lab.box is None:
            continue
        reach = max(150.0, 1.5 * lab.height)
        for j in tree.query(lab.box.buffer(reach)):
            layer, pts = notes[j]
            if layer.casefold() in mapped_layers:
                continue
            a, b = Point(pts[0]), Point(pts[-1])
            da, db = lab.box.distance(a), lab.box.distance(b)
            if min(da, db) <= reach and max(da, db) > reach:
                lab.arrows.append(pts[-1] if da < db else pts[0])


# ---------------------------------------------------------------------------------------------
# Map
# ---------------------------------------------------------------------------------------------

def normalise_map(raw: dict | None) -> dict:
    """{"name", "layers": {layer: {"system", "material"?, "size"?, "prefix"?}}, settings...}.
    A layer value may also be just the system code."""
    raw = dict(raw or {})
    layers = {}
    for layer, value in (raw.get("layers") or {}).items():
        entry = {"system": value} if isinstance(value, str) else dict(value or {})
        code = str(entry.get("system") or "").strip().upper()
        code = SYSTEM_ALIASES.get(code, code)
        if not code or code in ("IGNORE", "NONE"):
            continue
        entry["system"] = code
        layers[str(layer).strip().casefold()] = (str(layer).strip(), entry)
    systems = {str(k).upper(): dict(v) for k, v in (raw.get("systems") or {}).items()}
    out = {k: raw[k] for k in DEFAULTS if k in raw}
    out.update({"name": raw.get("name") or "", "layers": layers, "systems": systems,
                "label_aliases": {str(k).upper(): str(v).upper() for k, v in (raw.get("label_aliases") or {}).items()}})
    return out


def system_info(code: str, mapping: dict) -> dict:
    info = dict(SYSTEMS.get(code, {"kind": "pipe", "eia": None, "name": code}))
    info.update(mapping.get("systems", {}).get(code, {}))
    return info


# ---------------------------------------------------------------------------------------------
# Sizing
# ---------------------------------------------------------------------------------------------

class UnionFind:
    def __init__(self):
        self.parent: dict = {}

    def find(self, a):
        self.parent.setdefault(a, a)
        while self.parent[a] != a:
            self.parent[a] = self.parent[self.parent[a]]
            a = self.parent[a]
        return a

    def union(self, a, b):
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.parent[rb] = ra


def split_at_tees(pieces: list, tol: float) -> list:
    """Cut pieces where another piece's end lands on their interior (within tol). A main run
    is often one line through all its branch take-offs, and its size changes at them."""
    if not pieces:
        return pieces
    tree = STRtree([p.line for p in pieces])
    cuts: dict = defaultdict(list)
    for i, p in enumerate(pieces):
        for end in (p.coords[0], p.coords[-1]):
            P = Point(end)
            for j in tree.query(P.buffer(tol)):
                j = int(j)
                if j == i:
                    continue
                q = pieces[j]
                if q.line.distance(P) > tol:
                    continue
                d = q.line.project(P)
                if tol < d < q.length - tol:
                    cuts[id(q)].append(d)
    return apply_cuts(pieces, cuts, tol)


def apply_cuts(pieces: list, cuts: dict, tol: float) -> list:
    """Split pieces at distances along them; `cuts` maps id(piece) to distances."""
    out = []
    for p in pieces:
        merged: list = []
        for d in sorted(cuts.get(id(p), ())):
            if d <= tol or d >= p.length - tol:
                continue
            if not merged or d - merged[-1] > tol:
                merged.append(d)
        if not merged:
            out.append(p)
            continue
        bounds = [0.0] + merged + [p.length]
        for a, b in zip(bounds, bounds[1:]):
            coords = list(substring(p.line, a, b).coords)
            line = LineString(coords)
            out.append(Piece(p.curve, p.seg, coords, line.length, line, p.system, p.size, p.confidence,
                             p.label, p.duplicate, p.d0 + a))
    return out


def _leaving(piece: Piece, side: int) -> tuple:
    """Unit direction in which a piece leaves the node at its start (side 0) or end (side 1)."""
    pts = piece.coords if side == 0 else piece.coords[::-1]
    x0, y0 = pts[0]
    for x1, y1 in pts[1:]:
        d = math.hypot(x1 - x0, y1 - y0)
        if d > 1e-6:
            return ((x1 - x0) / d, (y1 - y0) / d)
    return (0.0, 0.0)


def build_graph(pieces: list, snap: float, gap_max: float):
    """Piece adjacency for one system. Returns (graph, chain-edge graph, open ends, gaps).

    Nodes are piece ends snapped together; an end that lands on another piece's interior is a
    T (the through piece counts twice at that node). An edge is a chain edge when its node has
    degree 2 (no branch). Open ends within gap_max of another piece are reported and bridged."""
    n = len(pieces)
    ends = []
    for i, p in enumerate(pieces):
        ends.append((i, 0, p.coords[0]))
        ends.append((i, 1, p.coords[-1]))
    uf = UnionFind()
    grid: dict = defaultdict(list)
    cell = max(snap, 1e-6)
    for k, (i, side, pt) in enumerate(ends):
        gx, gy = int(math.floor(pt[0] / cell)), int(math.floor(pt[1] / cell))
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for k2 in grid.get((gx + dx, gy + dy), ()):
                    if math.dist(pt, ends[k2][2]) <= snap:
                        uf.union(k, k2)
        grid[(gx, gy)].append(k)
    node_of = [uf.find(k) for k in range(len(ends))]
    members: dict = defaultdict(list)
    sides: dict = defaultdict(list)       # node -> (piece, which end)
    for k, node in enumerate(node_of):
        members[node].append(ends[k][0])
        sides[node].append((ends[k][0], ends[k][1]))
    tree = STRtree([p.line for p in pieces]) if pieces else None
    through: dict = defaultdict(set)      # node -> pieces passing through it (T)
    node_point = {}
    for k, node in enumerate(node_of):
        node_point.setdefault(node, ends[k][2])
    for node, pt in node_point.items():
        P = Point(pt)
        own = set(members[node])
        for j in tree.query(P.buffer(snap)):
            if j in own:
                continue
            line = pieces[j].line
            if line.distance(P) <= snap and math.dist(pt, pieces[j].coords[0]) > snap \
                    and math.dist(pt, pieces[j].coords[-1]) > snap:
                through[node].add(int(j))
    graph = nx.Graph()
    graph.add_nodes_from(range(n))
    chain = nx.Graph()
    chain.add_nodes_from(range(n))
    degree = {node: len(ps) + 2 * len(through.get(node, ())) for node, ps in members.items()}
    for node, ps in members.items():
        group = list(ps) + list(through.get(node, ()))
        for a in range(len(group)):
            for b in range(a + 1, len(group)):
                i, j = group[a], group[b]
                if i == j:
                    continue
                w = (pieces[i].length + pieces[j].length) / 2
                graph.add_edge(i, j, weight=w)
                if degree[node] == 2:
                    chain.add_edge(i, j, weight=w)
        if degree[node] >= 3 and not through.get(node):
            # A run goes straight through a tee or a cross: its two collinear pieces stay one
            # run (chain); the branch leaving sideways does not.
            dirs = [(i, _leaving(pieces[i], side)) for i, side in sides[node]]
            for a in range(len(dirs)):
                for b in range(a + 1, len(dirs)):
                    (i, u), (j, v) = dirs[a], dirs[b]
                    if i != j and u[0] * v[0] + u[1] * v[1] < -math.cos(math.radians(12)):
                        chain.add_edge(i, j, weight=(pieces[i].length + pieces[j].length) / 2)
    open_ends, gaps = [], []
    bridged: set = set()
    for node, ps in members.items():
        if degree[node] != 1:
            continue
        i = ps[0]
        pt = node_point[node]
        P = Point(pt)
        best, bd = None, None
        for j in tree.query(P.buffer(gap_max)):
            if j == i:
                continue
            d = pieces[j].line.distance(P)
            if d > snap and d <= gap_max and (bd is None or d < bd):
                best, bd = int(j), d
        if best is not None and (min(i, best), max(i, best)) in bridged:
            continue
        if best is not None:
            bridged.add((min(i, best), max(i, best)))
            gaps.append((i, best, round(bd, 1), pt))
            w = (pieces[i].length + pieces[best].length) / 2
            graph.add_edge(i, best, weight=w)
            chain.add_edge(i, best, weight=w)
        else:
            open_ends.append((i, pt))
    return graph, chain, open_ends, gaps


def _compatible(item: SizeItem, code: str, info: dict) -> bool:
    if item.kind != info["kind"]:
        return False
    return item.system is None or item.system == code


def label_anchors(lab: Label, cfg: dict) -> list:
    """(geometry, search radius, is plain text) per place a label points at: its leader
    arrows, or else its text area (measured from the text's edge, not its centre: a
    600 mm high label 18 characters long is 5.7 m wide)."""
    if lab.arrows:
        return [(Point(a), cfg["arrow_radius_mm"], False) for a in lab.arrows]
    r = cfg["text_radius_mm"] or min(2000.0, max(300.0, 2.5 * lab.height))
    return [(lab.box if lab.box is not None else Point(lab.point), r, True)]


def assign_labels(labels: list, pieces: list, codes: list, infos: dict, cfg: dict) -> list:
    """Direct assignments: label items to the piece they point at. Returns issue dicts for
    labels that found no piece or lost a conflict."""
    issues = []
    if not pieces:
        return issues
    tree = STRtree([p.line for p in pieces])

    def candidates(li: int, ii: int, strict: bool) -> list:
        lab, item = labels[li], labels[li].items[ii]
        found = []
        for P, radius, by_text in label_anchors(lab, cfg):
            for j in tree.query(P.buffer(radius)):
                p = pieces[j]
                if p.duplicate:
                    continue
                info = infos[p.system]
                if item.kind != info["kind"]:
                    continue
                if strict != (item.system is None or item.system == p.system):
                    continue
                d = p.line.distance(P)
                if d > radius:
                    continue
                score = d
                if by_text and lab.angle is not None:
                    (x1, y1), (x2, y2) = p.coords[0], p.coords[-1]
                    a = math.atan2(y2 - y1, x2 - x1)
                    diff = abs((a - lab.angle + math.pi / 2) % math.pi - math.pi / 2)
                    score *= 0.7 if diff < math.radians(10) else 1.3
                at = p.line.project(P if isinstance(P, Point) else P.centroid)
                found.append((score, li, ii, int(j), at))
        return sorted(found)[:6]

    taken: dict = {}        # piece -> (label, item, position along the piece)
    placed: set = set()     # (label, item)
    had_any: set = set()
    blocked: dict = {}      # (label, item) -> (piece, its position, the holder's position)

    def place(cands: list, mismatch: bool) -> None:
        for score, li, ii, j, at in sorted(cands):
            if (li, ii) in placed:
                continue
            item = labels[li].items[ii]
            if j in taken:
                other = labels[taken[j][0]].items[taken[j][1]]
                if other.key() != item.key():
                    blocked.setdefault((li, ii), (j, at, taken[j][2]))
                    continue
            taken.setdefault(j, (li, ii, at))
            placed.add((li, ii))
            p = pieces[j]
            p.size, p.confidence, p.label = item, "high", li
            labels[li].used = True
            if mismatch:
                issues.append({"type": "system_mismatch", "label": li, "point": _pt(labels[li].point),
                               "text": item.text, "piece": j,
                               "detail": f"label says {item.system}, layer is {p.system}"})

    # Strict pass: the label's system (if it names one) must be the layer's system. Relaxed pass
    # for what is left: same kind (pipe/duct), other system, e.g. OAD labels on an SA layer.
    items = [(li, ii) for li, lab in enumerate(labels) for ii, it in enumerate(lab.items) if it.kind != "terminal"]
    strict = []
    for li, ii in items:
        c = candidates(li, ii, True)
        had_any.update((li, ii) for _ in c[:1])
        strict += c
    place(strict, False)
    relaxed = []
    for li, ii in items:
        if (li, ii) not in placed and labels[li].items[ii].system is not None:
            c = candidates(li, ii, False)
            had_any.update((li, ii) for _ in c[:1])
            relaxed += c
    place(relaxed, True)
    for li, ii in items:
        if (li, ii) in placed:
            continue
        lab, item = labels[li], labels[li].items[ii]
        if (li, ii) in had_any:
            issue = {"type": "label_conflict", "label": li, "point": _pt(lab.point), "text": item.text,
                     "detail": "its pipe already carries a different size label"}
            if (li, ii) in blocked:
                j, at, other = blocked[(li, ii)]
                issue["_cut"] = (j, (at + other) / 2, abs(at - other))
            issues.append(issue)
        else:
            issues.append({"type": "label_unused", "label": li, "point": _pt(lab.point), "text": item.text,
                           "detail": "no matching pipe/duct near this label"})
    return issues


def propagate(pieces: list, graph, chain) -> None:
    """Sizes for unlabelled pieces: nearest labelled piece along an unbranched run (medium),
    then along the network across branches (low)."""
    for confidence, g in (("medium", chain), ("low", graph)):
        sources = [i for i, p in enumerate(pieces) if p.size is not None]
        if not sources:
            return
        dist, paths = nx.multi_source_dijkstra(g, sources, weight="weight")
        for i, p in enumerate(pieces):
            if p.size is None and i in paths:
                src = pieces[paths[i][0]]
                p.size, p.label, p.confidence = src.size, src.label, confidence


def find_duplicates(pieces: list, tol: float = 2.0) -> list:
    """Pieces lying inside another piece (drawn twice) are marked duplicate; partial overlaps
    longer than 50 mm are reported."""
    issues = []
    if not pieces:
        return issues
    tree = STRtree([p.line for p in pieces])
    seen = set()
    for i, p in enumerate(pieces):
        zone = p.line.buffer(tol)
        for j in tree.query(zone):
            j = int(j)
            if j == i or (min(i, j), max(i, j)) in seen:
                continue
            q = pieces[j]
            if p.curve == q.curve and abs(p.seg - q.seg) <= 1:
                continue
            inside_p = p.line.within(q.line.buffer(tol))
            inside_q = q.line.within(zone)
            if inside_p or inside_q:
                seen.add((min(i, j), max(i, j)))
                victim = i if (inside_p and (not inside_q or i > j)) else j
                pieces[victim].duplicate = True
                continue
            shared = p.line.intersection(q.line.buffer(tol)).length
            if shared > 50 and shared > 0.2 * min(p.length, q.length):
                seen.add((min(i, j), max(i, j)))
                issues.append({"type": "overlap", "pieces": [i, j], "length_mm": round(shared),
                               "point": _pt(p.line.interpolate(0.5, normalized=True).coords[0])})
    return issues


def _pt(p) -> list:
    return [round(p[0], 1), round(p[1], 1)]


# ---------------------------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------------------------

def run(doc, mapping: dict | None, region=None, max_depth: int = 8, scan: Scan | None = None) -> dict:
    """Normalise the curves of the mapped layers (plus catalogue layers already standard).
    Returns the full result (see module doc)."""
    mapping = normalise_map(mapping)
    cfg = dict(DEFAULTS)
    cfg.update({k: mapping[k] for k in DEFAULTS if k in mapping})
    layer_map = mapping["layers"]
    for alias, code in mapping["label_aliases"].items():
        SYSTEM_ALIASES.setdefault(alias, code)

    def wanted(layer: str) -> bool:
        return layer.casefold() in layer_map or parse_standard_layer(layer) is not None

    sc = scan or scan_drawing(doc, region, max_depth, wanted)
    curves = [c for c in sc.curves if wanted(c.layer)]
    attach_note_lines(sc.labels, sc.note_lines, set(layer_map))
    labels = sc.labels

    # Systems and fixed sizes per curve.
    by_system: dict = defaultdict(list)
    infos: dict = {}
    for ci, c in enumerate(curves):
        std = parse_standard_layer(c.layer)
        entry = layer_map.get(c.layer.casefold(), (c.layer, {}))[1]
        fixed = None
        if std is not None:
            c.system, fixed = std
        else:
            c.system = entry["system"]
            if entry.get("size"):
                parsed = parse_label(str(entry["size"]))
                fixed = parsed[0] if parsed else None
            else:
                parsed = [i for i in parse_label(c.layer) if i.kind != "terminal"]
                fixed = parsed[0] if len(parsed) == 1 else None
        infos[c.system] = system_info(c.system, mapping)
        for p in c.pieces:
            p.curve = ci
            p.system = c.system
            if fixed is not None:
                p.size, p.confidence = fixed, "layer"
            by_system[c.system].append(p)
    for code in list(by_system):
        by_system[code] = split_at_tees(by_system[code], cfg["gap_max_mm"])

    def regroup() -> list:
        for c in curves:
            c.pieces = []
        for ps in by_system.values():
            for p in ps:
                curves[p.curve].pieces.append(p)
        for c in curves:
            c.pieces.sort(key=lambda p: (p.seg, p.d0))
        return [p for c in curves for p in c.pieces]

    # Two labels with different sizes on one piece: the size changes inside it (a reducer that
    # a single-line drawing does not show). Cut halfway between the two label positions and
    # assign again.
    pieces = regroup()
    dup_issues = find_duplicates(pieces)
    for d in dup_issues:
        a, b = (pieces[k] for k in d.pop("pieces"))
        d["handles"] = [curves[a.curve].handle, curves[b.curve].handle]
        d["system"] = a.system
    for attempt in range(3):
        for p in pieces:
            if p.confidence != "layer":
                p.size, p.confidence, p.label = None, "none", None
        for lab in labels:
            lab.used = False
        assigned = assign_labels(labels, pieces, list(by_system), infos, cfg)
        cuts: dict = defaultdict(list)
        for issue in assigned:
            cut = issue.get("_cut")
            if cut and cut[2] > 2 * cfg["snap_mm"]:
                cuts[id(pieces[cut[0]])].append(cut[1])
        if not cuts or attempt == 2:
            break
        for code in list(by_system):
            by_system[code] = apply_cuts(by_system[code], cuts, cfg["snap_mm"])
        pieces = regroup()
    issues: list = []
    for issue in assigned:
        issue.pop("_cut", None)
        if "piece" in issue:
            p = pieces[issue.pop("piece")]
            issue.update(handle=curves[p.curve].handle, layer=curves[p.curve].layer)
        issues.append(issue)
    gaps_all = []
    for code, ps in by_system.items():
        live = [p for p in ps if not p.duplicate]
        graph, chain, open_ends, gaps = build_graph(live, cfg["snap_mm"], cfg["gap_max_mm"])
        propagate(live, graph, chain)
        for i, j, d, pt in gaps:
            gaps_all.append({"type": "gap", "system": code, "gap_mm": d, "point": _pt(pt),
                             "handles": [curves[live[i].curve].handle, curves[live[j].curve].handle]})
    issues += dup_issues + gaps_all

    # Per curve: groups of consecutive pieces with the same size, target layer, fixes.
    entities = []
    for ci, c in enumerate(curves):
        info = infos[c.system]
        groups = []
        along = 0.0
        for p in c.pieces:
            mat = _material_of(p, c.layer, c.system, mapping)
            key = (p.size.key() if p.size else None, p.confidence, p.duplicate, mat)
            if groups and groups[-1]["_key"] == key:
                g = groups[-1]
                g["seg_to"] = p.seg
                g["length_mm"] += p.length
                g["end"] = _pt(p.coords[-1])
                g["_pts"].extend(p.coords[1:])
            else:
                groups.append({"_key": key, "seg_from": p.seg, "seg_to": p.seg, "length_mm": p.length,
                               "start": _pt(p.coords[0]), "end": _pt(p.coords[-1]), "from_mm": round(along, 1),
                               "size": p.size.as_dict() if p.size else None,
                               "size_text": size_text(p.size, mat) if p.size else None,
                               "material": mat, "confidence": p.confidence, "duplicate": p.duplicate,
                               "label": labels[p.label].text if p.label is not None else None,
                               "_item": p.size, "_pts": list(p.coords)})
            along += p.length
        for g in groups:
            item = g.pop("_item")
            g.pop("_key")
            g["length_mm"] = round(g["length_mm"], 1)
            g["to_mm"] = round(g["from_mm"] + g["length_mm"], 1)
            path = LineString(g.pop("_pts"))
            if len(path.coords) > 2:
                path = path.simplify(5.0)
            g["path"] = [_pt(c) for c in path.coords]
            code = item.system if item is not None and item.system in SYSTEMS else c.system
            g["system"] = code
            tgt = target_layer(code, item, g["material"]) if (item is not None or info["kind"] == "duct") else None
            g["target_layer"] = tgt
            g["target_in_drawing"] = bool(tgt) and doc.layers.has_entry(tgt)
            g["target_in_template"] = bool(tgt) and target_in_template(tgt)
            g["boq_prefix"] = None if tgt else (layer_map.get(c.layer.casefold(), (None, {}))[1].get("prefix")
                                                or code)
        fixes = []
        if c.overrides:
            fixes.append("bylayer")
        if all(g["duplicate"] for g in groups):
            fixes.append("delete_duplicate")
        if len({(g["target_layer"], g["size_text"]) for g in groups if not g["duplicate"]}) > 1:
            fixes.append("split")
        entities.append({"handle": c.handle, "layer": c.layer, "type": c.dxftype, "system": c.system,
                         "kind": info["kind"], "in_block": c.in_block, "overrides": c.overrides,
                         "fixes": fixes, "groups": groups})
        if c.overrides:
            issues.append({"type": "override", "handle": c.handle, "layer": c.layer, "detail": ", ".join(c.overrides),
                           "point": _pt(c.pieces[0].coords[0])})
        if c.dxftype in ("SPLINE", "ARC"):
            issues.append({"type": "curve_type", "handle": c.handle, "layer": c.layer, "detail": c.dxftype,
                           "point": _pt(c.pieces[0].coords[0])})
        if c.in_block:
            issues.append({"type": "in_block", "layer": c.layer, "detail": "inside a block, cannot be changed",
                           "point": _pt(c.pieces[0].coords[0])})
        unsized = [g for g in groups if g["confidence"] == "none" and not g["duplicate"]]
        if unsized:
            issues.append({"type": "unsized", "handle": c.handle, "layer": c.layer,
                           "length_mm": round(sum(g["length_mm"] for g in unsized)),
                           "point": _pt(c.pieces[0].coords[0])})

    stats = _stats(curves, labels)
    warnings = []
    if sc.schematic:
        warnings.append("The drawing contains SCHEMATIC / riser diagram text: schematic lengths are not "
                        "plan lengths and must not go into the BOQ.")
    if any(c.in_block for c in curves):
        warnings.append("Some curves are inside blocks/xrefs: they are counted but cannot be changed.")
    order = {t: k for k, t in enumerate(("override", "label_conflict", "gap", "overlap", "unsized", "curve_type",
                                         "label_unused", "in_block"))}
    issues.sort(key=lambda i: order.get(i["type"], 99))
    return {"version": 1, "status": "ok", "map_name": mapping["name"], "stats": stats, "warnings": warnings,
            "boq_preview": boq_preview(entities, labels), "issue_counts": dict(Counter(i["type"] for i in issues)),
            "issues": issues[:cfg["max_issues"]], "entities": entities,
            "labels": [{"text": lab.text, "point": _pt(lab.point), "arrows": [_pt(a) for a in lab.arrows],
                        "used": lab.used, "items": [i.as_dict() for i in lab.items]} for lab in labels]}


def _material_of(p: Piece, layer: str, code: str, mapping: dict) -> str | None:
    """The label's material, else the map's for the layer, else the map's for the system."""
    if p.size is not None and p.size.material:
        return p.size.material
    entry = mapping["layers"].get(layer.casefold(), (None, {}))[1]
    mat = entry.get("material") or mapping.get("systems", {}).get(code, {}).get("material")
    return _material(str(mat)) or str(mat) if mat else None


def _stats(curves: list, labels: list) -> dict:
    by_conf = Counter()
    for c in curves:
        for p in c.pieces:
            if not p.duplicate:
                by_conf[p.confidence] += p.length
    total = sum(by_conf.values()) or 1.0
    sized = [lab for lab in labels if any(i.kind != "terminal" for i in lab.items)]
    return {"curves": len(curves), "pieces": sum(len(c.pieces) for c in curves),
            "length_m": round(total / 1000, 1),
            "duplicate_m": round(sum(p.length for c in curves for p in c.pieces if p.duplicate) / 1000, 1),
            "by_confidence_pct": {k: round(100 * by_conf[k] / total, 1) for k in CONFIDENCES},
            "labels": len(sized), "labels_used": sum(1 for lab in sized if lab.used),
            "grille_labels": len(labels) - len(sized)}


def boq_preview(entities: list, labels: list) -> list:
    """Length (and duct surface) per BOQ line: a template layer, or prefix + material + size."""
    rows: dict = {}
    for e in entities:
        for g in e["groups"]:
            if g["duplicate"]:
                continue
            if g["target_layer"] and e["kind"] == "pipe":
                key = (e["kind"], g["target_layer"], "", g["size_text"] or "?")
            elif e["kind"] == "duct":
                key = (e["kind"], g["target_layer"] or g["system"], "GI" if g["target_layer"] else (g["material"] or "?"),
                       g["size_text"] or "?")
            else:
                key = (e["kind"], g["boq_prefix"] or e["system"], g["material"] or "?", g["size_text"] or "?")
            row = rows.setdefault(key, {"kind": key[0], "line": key[1], "material": key[2], "size": key[3],
                                        "length_m": 0.0, "area_m2": 0.0, "by_confidence_m": Counter()})
            m = g["length_mm"] / 1000
            row["length_m"] += m
            row["by_confidence_m"][g["confidence"]] += m
            size = g["size"] or {}
            if e["kind"] == "duct":
                if size.get("width"):
                    row["area_m2"] += 2 * (size["width"] + size["height"]) / 1000 * m
                elif size.get("size"):
                    row["area_m2"] += math.pi * size["size"] / 1000 * m
    terminals: Counter = Counter()
    for lab in labels:
        for item in lab.items:
            if item.kind == "terminal":
                name = f"{item.system or '?'} grille {size_text(item) if (item.width or item.size) else '?'}"
                terminals[name] += item.count or 1
    out = []
    for row in sorted(rows.values(), key=lambda r: (r["kind"], r["line"], r["material"], _size_sort(r["size"]))):
        row["length_m"] = round(row["length_m"], 2)
        row["area_m2"] = round(row["area_m2"], 2) if row["kind"] == "duct" else None
        row["by_confidence_m"] = {k: round(v, 2) for k, v in row["by_confidence_m"].items()}
        out.append(row)
    for name, n in sorted(terminals.items()):
        out.append({"kind": "terminal", "line": name, "count": n})
    return out


def _size_sort(text: str):
    nums = re.findall(r"\d+(?:\.\d+)?", text or "")
    return tuple(float(n) for n in nums) or (0.0,)


# ---------------------------------------------------------------------------------------------
# Discovery: propose a map
# ---------------------------------------------------------------------------------------------

def guess_system_from_name(layer: str) -> tuple[str | None, str | None]:
    """(system, kind) from layer-name keywords, or (None, None)."""
    name = layer.upper()
    kind = "duct" if re.search(LAYER_KEYWORDS[0][1], name, re.I) else None
    for code, pattern in LAYER_KEYWORDS[1:]:
        if re.search(pattern, name, re.I):
            info = SYSTEMS[code]
            if kind == "duct" and info["kind"] != "duct":
                continue
            if kind is None and info["kind"] == "duct" and code in ("SA", "RA", "EA", "OA") and \
                    not re.search(r"AIR|DUCT|风", name, re.I):
                continue
            return code, info["kind"]
    return None, kind


def propose_map(doc, region=None, max_depth: int = 8, cfg: dict | None = None) -> tuple[dict, list, Scan]:
    """A map proposal from which layers the size labels point at (and layer-name keywords),
    with per-layer evidence."""
    cfg = dict(DEFAULTS, **(cfg or {}))

    def wanted(layer: str) -> bool:
        return parse_standard_layer(layer) is None and not NOT_MEP_LAYER.search(layer) and layer != "0"

    sc = scan_drawing(doc, region, max_depth, wanted)
    attach_note_lines(sc.labels, sc.note_lines, set())
    pieces = [(c.layer, p) for c in sc.curves for p in c.pieces]
    hits: dict = defaultdict(Counter)
    kinds: dict = defaultdict(Counter)
    samples: dict = defaultdict(list)
    if pieces and len(pieces) <= cfg["max_pieces"]:
        tree = STRtree([p.line for _, p in pieces])
        for lab in sc.labels:
            sized = [i for i in lab.items if i.kind != "terminal"]
            if not sized:
                continue
            best = None
            for P, radius, _ in label_anchors(lab, cfg):
                for j in tree.query(P.buffer(radius)):
                    d = pieces[j][1].line.distance(P)
                    if d <= radius and (best is None or d < best[0]):
                        best = (d, pieces[j][0])
            if best is None:
                continue
            layer = best[1]
            for item in sized:
                hits[layer][item.system or "?"] += 1
                kinds[layer][item.kind] += 1
            if len(samples[layer]) < 4 and lab.text not in samples[layer]:
                samples[layer].append(lab.text.replace("\n", " / ")[:60])
    rows = []
    for layer, length in sc.layer_lengths.most_common():
        if not wanted(layer) or length < 1000:
            continue
        guess, gkind = guess_system_from_name(layer)
        n_hits = sum(hits[layer].values())
        if n_hits < 2 and (guess is None or length < 10000):
            continue      # layer-legend strips draw one 3 m line per layer
        coded = [(s, n) for s, n in hits[layer].most_common() if s != "?"]
        system = coded[0][0] if coded else guess
        kind = kinds[layer].most_common(1)[0][0] if kinds[layer] else (gkind or (SYSTEMS[system]["kind"] if system in SYSTEMS else None))
        if system is None and kind == "duct":
            system = None
        rows.append({"layer": layer, "proposed_system": system, "kind": kind, "label_hits": n_hits,
                     "label_systems": dict(hits[layer]), "name_guess": guess, "length_m": round(length / 1000, 1),
                     "types": dict(sc.layer_types[layer]), "sample_labels": samples[layer]})
    rows.sort(key=lambda r: (-r["label_hits"], -r["length_m"]))
    layers = {r["layer"]: {"system": r["proposed_system"]} for r in rows if r["proposed_system"]}
    return {"name": "", "layers": layers}, rows, sc


# ---------------------------------------------------------------------------------------------
# Request / CLI (HVACNORM)
# ---------------------------------------------------------------------------------------------

def run_request(request_path: str | Path) -> dict:
    """request.json: {"version": 1, "dxf", "out", "map": {...} or "map_path", "region": [x1,y1,x2,y2]?}."""
    request_path = Path(request_path)
    req = json.loads(request_path.read_text(encoding="utf-8-sig"))
    out = request_path.parent / req.get("out", "norm.json")
    try:
        if req.get("version") != 1:
            raise ValueError(f"Unsupported request version {req.get('version')!r}")
        mapping = req.get("map")
        if mapping is None and req.get("map_path"):
            mapping = json.loads(Path(req["map_path"]).read_text(encoding="utf-8-sig"))
        doc = ax.load_drawing(request_path.parent / req["dxf"])
        region = req.get("region")
        if region and isinstance(region[0], (list, tuple)):      # polygon [[x, y], ...] -> bbox
            xs, ys = [q[0] for q in region], [q[1] for q in region]
            region = (min(xs), min(ys), max(xs), max(ys))
        rows = None
        if mapping is None:
            mapping, rows, _ = propose_map(doc, region)
        result = run(doc, mapping, region, req.get("max_block_depth", 8))
        result["map"] = {orig: entry for orig, entry in normalise_map(mapping)["layers"].values()}
        result["map_proposed"] = rows is not None
        if rows is not None:
            result["proposal"] = rows
    except Exception as exc:
        result = {"version": 1, "status": "error", "message": f"{type(exc).__name__}: {exc}",
                  "detail": traceback.format_exc(limit=5)}
    out.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Normalise MEP pipe/duct layers and sizes (HVACNORM back end).")
    parser.add_argument("--request", help="request.json written by the add-in")
    parser.add_argument("--dxf", help="run directly on a DXF (prints a summary)")
    parser.add_argument("--map", help="map JSON; without it a map is proposed")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    if args.request:
        result = run_request(args.request)
    elif args.dxf:
        doc = ax.load_drawing(Path(args.dxf))
        mapping = json.loads(Path(args.map).read_text(encoding="utf-8-sig")) if args.map else None
        if mapping is None:
            mapping, rows, _ = propose_map(doc)
            print(json.dumps({"proposal": rows[:30]}, ensure_ascii=False, indent=1))
        result = run(doc, mapping)
    else:
        parser.error("give --request or --dxf")
    summary = {k: result.get(k) for k in ("status", "stats", "issue_counts", "warnings", "message")}
    summary["boq_preview"] = result.get("boq_preview", [])[:40]
    print(json.dumps(summary, ensure_ascii=False, indent=1))
    return 0 if result["status"] == "ok" else 1


if __name__ == "__main__":
    raise SystemExit(main())
