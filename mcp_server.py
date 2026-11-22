"""MCP server "hvac-cad": read-only tools over DXF drawings (Phase 1, docs/AI_MCP_PLAN.md).

Runs over stdio (never print to stdout here). Geometry stays local; the model only
sees compact summaries and, unless privacy forbids it, PNG renders. AutoCAD is only
used headless (accoreconsole) to convert a copy of a DWG to DXF.

    venv\\Scripts\\python.exe mcp_server.py
"""
from __future__ import annotations

import asyncio
import functools
import logging
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from mcp.server.mcpserver import Image, MCPServer  # noqa: E402
from mcp.server.mcpserver.exceptions import ToolError  # noqa: E402

from aitools import config, drawings, load, overview, privacy, profile, render, rooms  # noqa: E402

log = logging.getLogger("hvac-cad")


def _setup_logging() -> None:
    handler = logging.FileHandler(config.work_dir() / "mcp_server.log", encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    root = logging.getLogger()
    root.setLevel(logging.INFO)
    root.handlers[:] = [handler]


INSTRUCTIONS = """\
Read-only tools for HVAC-oriented reading of architectural / MEP drawings. All coordinates
and sizes are in the drawing units, assumed millimetres. Typical flow: list_drawings ->
drawing_overview (which plan views exist) -> layer_profile on one plan region (which layers
are walls) -> render_region to look at it -> find_rooms with the exact wall layer names ->
cooling_load. Drawings are DXF; a DWG name is converted from a copy automatically (can take
minutes for big files). Nothing is ever written to the drawing. The cooling-load table holds
placeholder W/m2 values. get_privacy_mode tells which text/image limits apply."""

server = MCPServer("hvac-cad", instructions=INSTRUCTIONS)


def tool(fn):
    """Run in a worker thread, log the call, turn exceptions into readable tool errors."""
    @functools.wraps(fn)
    async def wrapper(*args, **kwargs):
        t0 = time.perf_counter()
        try:
            result = await asyncio.to_thread(fn, *args, **kwargs)
            log.info("%s ok %.1fs", fn.__name__, time.perf_counter() - t0)
            return result
        except Exception as exc:  # noqa: BLE001
            log.exception("%s failed", fn.__name__)
            raise ToolError(f"{fn.__name__} failed: {type(exc).__name__}: {exc}") from None
    return server.tool()(wrapper)


@tool
def list_drawings() -> list:
    """List the DWG/DXF drawings in the configured drawing folders (file names, size in MB,
    and for DWG whether a converted DXF is already cached)."""
    return drawings.list_drawings()


@tool
def convert_dwg(drawing: str) -> dict:
    """Convert a COPY of a DWG to DXF with AutoCAD's accoreconsole (headless, the source is
    never touched) and cache it by path+size+date. `drawing` is a file name from
    list_drawings or a full path. Can take minutes for large drawings. Returns the DXF path
    in the work folder; other tools also accept the drawing name directly."""
    return drawings.convert_dwg(drawing)


@tool
def drawing_overview(drawing: str, max_views: int = 40, privacy_mode: str | None = None) -> dict:
    """Plan views found automatically in model space (nested/xref blocks included): kind
    (plan, other, untitled, crossed_out), title, floor, bbox in mm [minx, miny, maxx, maxy],
    size in metres, number of rooms and shafts the automatic prototype found (a rough
    count). Sorted plans first, then larger first; at most `max_views` are listed
    (views_total says how many exist: big drawings have hundreds of small fragments).
    `drawing` is a DXF path or a drawing name. Cached; reports elapsed seconds."""
    return overview.drawing_overview(drawings.resolve_dxf(drawing), privacy_mode, max_views)


@tool
def layer_profile(drawing: str, region: list[float] | None = None, max_layers: int = 60,
                  privacy_mode: str | None = None) -> dict:
    """Per-layer statistics, longest first, to decide which layers are walls, columns, glass,
    doors. Per layer: cleaned name (and full xref names), entity counts by type, total curve
    length in m, share of continuous linetype, closed polylines, door-like arcs (radius
    500-2000 mm, ~90 deg), the commonest gaps between parallel lines in mm (double-line
    walls give 100-300), bbox in mm, sample texts, and the rule-based role guess
    (wall/opening/excluded/null). `region` = [minx, miny, maxx, maxy] in mm limits the scan
    (use a plan bbox from drawing_overview); omit for the whole drawing."""
    return profile.layer_profile(drawings.resolve_dxf(drawing), region, max_layers, privacy_mode)


@tool
def render_region(drawing: str, region: list[float], wall_layers: list[str] | None = None,
                  rooms_json: str | None = None, max_px: int = 1600,
                  privacy_mode: str | None = None) -> list:
    """PNG of one region (mm: [minx, miny, maxx, maxy]): all linework thin grey, `wall_layers`
    (exact layer names as in layer_profile) dark, rooms from a find_rooms `rooms_json` filled
    and labelled with id and area, texts only if privacy allows, and a scale bar. Refused in
    privacy mode numbers_only. Returns the image and its saved path."""
    info = render.render_region(drawings.resolve_dxf(drawing), region, wall_layers, rooms_json,
                                max_px, privacy_mode)
    return [Image(path=info["path"]), str({k: v for k, v in info.items()})]


@tool
def find_rooms(drawing: str, region: list[float], wall_layers: list[str], max_opening: float = 1800.0,
               include_polygons: bool = False, privacy_mode: str | None = None) -> dict:
    """Find rooms and shafts in one plan region with the HVACROOMS geometry (nodes and
    polygonizes the wall curves, bridges door openings up to max_opening mm, drops faces
    under 1 m2 or narrower than 400 mm). `wall_layers` are EXACT layer names (walls, columns,
    glass, doors; lines on HVAC-ROOM-SPLIT always count). Returns stats, a compact room list
    (id, kind room|shaft, area m2, label point, name, area label if any) and the rooms_json
    path for render_region / cooling_load. Polygons only with include_polygons."""
    return rooms.find_rooms(drawings.resolve_dxf(drawing), region, wall_layers, max_opening,
                            include_polygons, privacy_mode)


@tool
def cooling_load(rooms_json: str, type_overrides: dict[str, str] | None = None,
                 safety_factor: float | None = None, privacy_mode: str | None = None) -> dict:
    """Area-based cooling load of the rooms in a find_rooms `rooms_json`: area x W/m2 of the
    room type x safety factor, in kW. The type comes from the room name; `type_overrides`
    maps room id to a type key (e.g. {"R3": "office"}). The W/m2 table is a PLACEHOLDER until
    the user supplies theirs. Returns per-room type and kW, the total and the valid types."""
    return load.cooling_load(rooms_json, type_overrides, safety_factor, privacy_mode)


@tool
def get_privacy_mode() -> dict:
    """The privacy mode in force (the configured minimum) and what it means. Every tool
    accepts privacy_mode to ask for a stricter mode, never a looser one."""
    mode = privacy.effective_mode()
    return {"mode": mode, "modes": {
        "normal": "texts, names and images are returned",
        "redact": "texts kept only if area labels, room-type words or short codes; others become [text]",
        "numbers_only": "no texts and no images"}, "drawing_folders_reported": "file names only"}


def main() -> None:
    _setup_logging()
    log.info("hvac-cad server starting")
    server.run("stdio")


if __name__ == "__main__":
    main()
