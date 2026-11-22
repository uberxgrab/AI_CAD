"""PNG of one drawing region for a model with vision (matplotlib, Agg backend)."""
from __future__ import annotations

import json
import math
import time
import uuid
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from matplotlib.collections import LineCollection  # noqa: E402
from matplotlib.patches import Polygon as MplPolygon  # noqa: E402

import arch_extract as ax  # noqa: E402

from . import config, drawings, privacy, scan  # noqa: E402

CURVES = {"LINE", "LWPOLYLINE", "POLYLINE", "ARC", "CIRCLE", "ELLIPSE", "SPLINE"}
MAX_TEXTS = 300


def _segments(ent, flatten: float):
    kind = ent.dxftype()
    if kind == "LINE":
        s, e = ent.dxf.start, ent.dxf.end
        return [[(s.x, s.y), (e.x, e.y)]]
    geom = ax._curve_geometry(ent, flatten)
    return [list(geom.coords)] if geom is not None else []


def _nice(length: float) -> float:
    """Round scale-bar length (mm): 1, 2 or 5 times a power of ten at most `length`."""
    if length <= 0:
        return 1000.0
    p = 10 ** math.floor(math.log10(length))
    for m in (5, 2, 1):
        if m * p <= length:
            return m * p
    return p


def render_region(dxf: str | Path, region, wall_layers=None, rooms_json: str | Path | None = None,
                  max_px: int = 1600, mode: str | None = None) -> dict:
    mode = privacy.effective_mode(mode)
    if mode == "numbers_only":
        raise PermissionError("Rendering is disabled in privacy mode numbers_only.")
    region = scan.parse_region(region)
    if region is None:
        raise ValueError("render_region needs a region [minx, miny, maxx, maxy] in mm.")
    max_px = int(max(200, min(max_px, 4000)))
    dxf = Path(dxf)
    cfg = config.get_config()
    doc = drawings.load_doc(dxf)
    wanted = {ax.clean_layer(n).casefold() for n in (wall_layers or [])}
    t0 = time.perf_counter()
    grey, dark, texts = [], [], []
    for raw, clean, ent in scan.walk(doc, cfg["max_block_depth"]):
        kind = ent.dxftype()
        if kind in scan.TEXTS:
            if len(texts) < MAX_TEXTS:
                pts = scan.points_of(ent)
                if pts and region[0] <= pts[0][0] <= region[2] and region[1] <= pts[0][1] <= region[3]:
                    t = ax._text_of(doc, ent, clean)
                    if t:
                        texts.append(t)
            continue
        if kind not in CURVES:
            continue
        pts = scan.points_of(ent)
        if pts is None or not scan.intersects(scan.bbox_of(pts), region):
            continue
        try:
            segs = _segments(ent, 20.0)
        except Exception:
            continue
        (dark if clean.casefold() in wanted else grey).extend(segs)

    w, h = region[2] - region[0], region[3] - region[1]
    px_w, px_h = (max_px, max(1, round(max_px * h / w))) if w >= h else (max(1, round(max_px * w / h)), max_px)
    dpi = 100
    fig = plt.figure(figsize=(px_w / dpi, px_h / dpi), dpi=dpi)
    ax_ = fig.add_axes([0, 0, 1, 1])
    ax_.set_xlim(region[0], region[2])
    ax_.set_ylim(region[1], region[3])
    ax_.set_aspect("equal")
    ax_.axis("off")
    ax_.add_collection(LineCollection(grey, colors="#b8b8b8", linewidths=0.4))
    ax_.add_collection(LineCollection(dark, colors="#202020", linewidths=0.9))

    n_rooms = 0
    if rooms_json:
        data = json.loads(Path(rooms_json).read_text(encoding="utf-8"))
        for room in data.get("rooms", []):
            shaft = room["kind"] == "shaft"
            ax_.add_patch(MplPolygon(room["polygon"], closed=True, facecolor="#c040c0" if shaft else "#40a0e0",
                                     alpha=0.35, edgecolor="#1060a0", linewidth=0.8))
            lx, ly = room["label_point"]
            ax_.text(lx, ly, f"{room['id']}\n{room['area_m2']:g} m2", ha="center", va="center",
                     fontsize=max(5, min(9, px_w / 160)), color="#000080")
            n_rooms += 1

    shown_texts = 0
    for t in texts:
        masked = privacy.mask_text(t.text, mode)
        if masked is None or masked == privacy.MASK or "\n" in masked:
            continue
        ax_.text(t.x, t.y, masked[:40], fontsize=max(4, min(7, px_w / 230)), color="#a04000", clip_on=True)
        shown_texts += 1

    bar = _nice(w * 0.2)
    bx, by = region[0] + w * 0.03, region[1] + h * 0.04
    ax_.plot([bx, bx + bar], [by, by], color="k", linewidth=2)
    ax_.text(bx, by + h * 0.01, f"{bar / 1000:g} m", fontsize=8, va="bottom")

    out_dir = config.work_dir() / "renders"
    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / f"render_{time.strftime('%H%M%S')}_{uuid.uuid4().hex[:6]}.png"
    fig.savefig(out, dpi=dpi)
    plt.close(fig)
    return {"path": str(out), "width_px": px_w, "height_px": px_h, "mm_per_px": round(w / px_w, 1),
            "region_mm": list(region), "wall_curves": len(dark), "other_curves": len(grey),
            "rooms_drawn": n_rooms, "texts_drawn": shown_texts, "privacy": mode,
            "elapsed_s": round(time.perf_counter() - t0, 1)}
