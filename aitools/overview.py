"""Plan views of a drawing, found by the automatic prototype of arch_extract."""
from __future__ import annotations

import json
import time
from pathlib import Path

import arch_extract as ax

from . import config, drawings, privacy


def _compute(dxf: Path) -> dict:
    cfg = dict(ax.DEFAULT_ARCH_CONFIG)
    doc = drawings.load_doc(dxf)
    cross_lines: list = []
    marks: list = []
    lines, texts = ax.collect(doc, cfg, cross_lines, marks)
    masses = ax.wall_masses(lines, cfg)
    views = ax.build_views(masses, texts, cfg, cross_lines, marks)
    rows = []
    for v in views:
        minx, miny, maxx, maxy = v.bbox
        rows.append({
            "id": v.id, "kind": v.kind, "title": v.title, "floor": v.floor,
            "bbox_mm": [round(minx), round(miny), round(maxx), round(maxy)],
            "size_m": [round((maxx - minx) / 1000, 1), round((maxy - miny) / 1000, 1)],
            "crossed_out": v.kind == "crossed_out",
            "rooms": len(v.rooms), "shafts": len(v.shafts),
        })
    return {"barrier_curves": len(lines), "texts": len(texts), "views": rows,
            "summary": ax.summarize(views)}


def drawing_overview(dxf: str | Path, mode: str | None = None, max_views: int = 40) -> dict:
    """Plan views (kind, title, floor, bbox, size, crossed-out flag, rooms found).

    The result is cached in the work folder; `rooms` is the number found by the
    automatic prototype (a rough count, not the HVACROOMS result)."""
    dxf = Path(dxf)
    mode = privacy.effective_mode(mode)
    cache = config.work_dir() / "cache" / f"overview_{drawings.cache_key(dxf)}.json"
    t0 = time.perf_counter()
    cached = cache.is_file()
    if cached:
        data = json.loads(cache.read_text(encoding="utf-8"))
    else:
        data = _compute(dxf)
        cache.parent.mkdir(parents=True, exist_ok=True)
        cache.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    # plans first, then other titled views, crossed-out sheets, untitled fragments; larger first
    order = {"plan": 0, "other": 1, "crossed_out": 2, "untitled": 3}
    ranked = sorted(data["views"], key=lambda v: (order.get(v["kind"], 4), -v["size_m"][0] * v["size_m"][1]))
    views = []
    for v in ranked[:max_views]:
        v = dict(v)
        v["title"] = privacy.mask_text(v["title"], mode) if v["title"] else None
        views.append(v)
    return {"drawing": privacy.safe_name(dxf), "units": "mm", "privacy": mode,
            "cached": cached, "elapsed_s": round(time.perf_counter() - t0, 1),
            "barrier_curves": data["barrier_curves"], "views_total": len(data["views"]), "views_shown": len(views),
            "views": views, "summary": data["summary"]}
