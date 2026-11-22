"""Rooms in one region of a drawing: wraps the HVACROOMS back end (arch_rooms)."""
from __future__ import annotations

import json
import time
import uuid
from pathlib import Path

import arch_rooms

from . import config, drawings, privacy


def find_rooms(dxf: str | Path, region, wall_layers, max_opening: float = 1800.0,
               include_polygons: bool = False, mode: str | None = None, **overrides) -> dict:
    """Run arch_rooms.extract on one region. `wall_layers` are exact layer names (the
    `layer` values of layer_profile). Writes request.json and rooms.json in a work
    sub-folder and returns stats plus a compact room list."""
    from . import scan
    mode = privacy.effective_mode(mode)
    box = scan.parse_region(region)
    if box is None:
        raise ValueError("find_rooms needs a region [minx, miny, maxx, maxy] in mm.")
    if not wall_layers:
        raise ValueError("find_rooms needs wall_layers: exact layer names (see layer_profile).")
    dxf = Path(dxf)
    folder = config.work_dir() / "rooms" / f"{time.strftime('%Y%m%d_%H%M%S')}_{uuid.uuid4().hex[:6]}"
    folder.mkdir(parents=True)
    x1, y1, x2, y2 = box
    request = {"version": 1, "dxf": str(dxf), "out": "rooms.json", "wall_layers": list(wall_layers),
               "region": [[x1, y1], [x2, y1], [x2, y2], [x1, y2]], "max_opening": float(max_opening),
               "max_block_depth": config.get_config()["max_block_depth"], **overrides}
    (folder / "request.json").write_text(json.dumps(request, ensure_ascii=False, indent=1), encoding="utf-8")
    req = dict(arch_rooms.REQUEST_DEFAULTS)
    req.update(request)
    t0 = time.perf_counter()
    result = arch_rooms.extract(drawings.load_doc(dxf), req)   # same call run_request makes
    rooms_json = folder / "rooms.json"
    rooms_json.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    rows = []
    for r in result["rooms"]:
        row = {"id": r["id"], "kind": r["kind"], "area_m2": r["area_m2"], "label_point": r["label_point"]}
        name = privacy.mask_text(r["name"], mode) if r["name"] else None
        if name is not None:
            row["name"] = name
        if r["label_area_m2"] is not None:
            row["label_area_m2"] = r["label_area_m2"]
            row["label_error_pct"] = r["label_error_pct"]
        if include_polygons:
            row["polygon"] = r["polygon"]
        rows.append(row)
    return {"status": result["status"], "elapsed_s": round(time.perf_counter() - t0, 1), "privacy": mode,
            "stats": result["stats"], "warnings": result["warnings"], "rooms": rows,
            "rooms_json": str(rooms_json)}
