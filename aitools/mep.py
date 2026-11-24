"""mep_map: recognise pipe / duct layers of another office and normalise them (mep_normalize).

One tool for the whole job, as the user asked (2026-11-23): without a map it proposes one
(which layers the size labels point at, plus layer-name keywords) and previews the result with
that proposal; with a map it saves the map under a project name and previews with it. The full
per-curve result is written to a JSON file for the HVACNORM command; the model only gets a
compact summary.
"""
from __future__ import annotations

import json
import re
import time
import uuid
from pathlib import Path

import mep_normalize as mn

from . import config, drawings, privacy

MAP_NAME = re.compile(r"^[\w\- .]{1,60}$")


def maps_dir() -> Path:
    """%LOCALAPPDATA%\\HvacAiCad\\mep_maps (beside the work folder, outside the repository)."""
    path = config.work_dir().parent / "mep_maps"
    path.mkdir(parents=True, exist_ok=True)
    return path


def map_path(name: str) -> Path:
    if not name or not MAP_NAME.match(name):
        raise ValueError("map_name must be 1-60 letters, digits, spaces, '-', '_' or '.'.")
    return maps_dir() / f"{name.strip()}.json"


def list_maps() -> list[str]:
    return sorted(p.stem for p in maps_dir().glob("*.json"))


def load_map(name: str) -> dict:
    path = map_path(name)
    if not path.is_file():
        raise FileNotFoundError(f"No saved map {name!r}. Saved maps: {list_maps()}")
    return json.loads(path.read_text(encoding="utf-8"))


def save_map(name: str, layer_map: dict) -> Path:
    data = {"version": 1, "name": name, "saved": time.strftime("%Y-%m-%d %H:%M:%S"), "layers": {}}
    for layer, value in layer_map.items():
        entry = {"system": value} if isinstance(value, str) else dict(value or {})
        code = str(entry.get("system") or "").strip().upper()
        if not code:
            raise ValueError(f"Layer {layer!r} has no system code.")
        entry["system"] = mn.SYSTEM_ALIASES.get(code, code)
        data["layers"][str(layer)] = entry
    path = map_path(name)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
    return path


def _text(text: str | None, mode: str) -> str | None:
    """Size labels are technical codes and pass in redact mode; other text follows privacy."""
    if text is None or mode == "numbers_only":
        return None
    text = text.replace("\n", " / ")[:60]
    if mode == "normal" or mn.parse_label(text):
        return text
    return privacy.mask_text(text, mode)


def _clean(obj):
    """numpy floats from shapely -> plain floats, for JSON."""
    if isinstance(obj, dict):
        return {k: _clean(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [_clean(v) for v in obj]
    if hasattr(obj, "item") and not isinstance(obj, (str, bytes)):
        return obj.item()
    return obj


def mep_map(dxf: str | Path, layer_map: dict | None = None, map_name: str | None = None,
            region=None, mode: str | None = None, max_issues: int = 40, max_rows: int = 60) -> dict:
    from . import scan
    mode = privacy.effective_mode(mode)
    box = scan.parse_region(region)
    dxf = Path(dxf)
    t0 = time.perf_counter()
    doc = drawings.load_doc(dxf)
    confirmed = True
    proposal_rows = None
    saved = None
    if layer_map:
        mapping = {"layers": dict(layer_map), "name": map_name or ""}
        if map_name:
            saved = save_map(map_name, layer_map)
    elif map_name:
        mapping = load_map(map_name)
        saved = map_path(map_name)
    else:
        mapping, proposal_rows, _ = mn.propose_map(doc, box, config.get_config()["max_block_depth"])
        confirmed = False
    result = _clean(mn.run(doc, mapping, box, config.get_config()["max_block_depth"]))
    folder = config.work_dir() / "mep" / f"{time.strftime('%Y%m%d_%H%M%S')}_{uuid.uuid4().hex[:6]}"
    folder.mkdir(parents=True)
    result_json = folder / "norm.json"
    result["map"] = mn.normalise_map(mapping)["layers"]
    result["map"] = {layer: entry for layer, entry in result["map"].values()}
    result_json.write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")

    issues = []
    for issue in result["issues"][:max_issues]:
        row = {k: v for k, v in issue.items() if k not in ("text", "label")}
        if "text" in issue:
            t = _text(issue["text"], mode)
            if t is not None:
                row["text"] = t
        issues.append(row)
    out = {
        "map_confirmed": confirmed,
        "map_name": map_name,
        "map_path": str(saved) if saved else None,
        "map": {layer: entry["system"] for layer, entry in result["map"].items()},
        "stats": result["stats"],
        "warnings": result["warnings"],
        "boq_preview": result["boq_preview"][:max_rows],
        "boq_rows_total": len(result["boq_preview"]),
        "issue_counts": result["issue_counts"],
        "issues": issues,
        "result_json": str(result_json),
        "elapsed_s": round(time.perf_counter() - t0, 1),
        "privacy": mode,
    }
    if proposal_rows is not None:
        rows = []
        for r in _clean(proposal_rows)[:max_rows]:
            r = dict(r)
            r["sample_labels"] = [t for t in (_text(s, mode) for s in r["sample_labels"]) if t]
            rows.append(r)
        out["proposal"] = rows
        out["system_codes"] = {code: f"{info['kind']}: {info['name']}" for code, info in mn.SYSTEMS.items()}
        out["saved_maps"] = list_maps()
    return out
