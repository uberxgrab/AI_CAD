"""Settings from the optional `aitools_config.json` at the repository root.

The environment variable AITOOLS_CONFIG may point to another file (used by tests).
"""
from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]

DEFAULTS = {
    "work_dir": None,                 # default %LOCALAPPDATA%\HvacAiCad\work
    "drawing_dirs": ["CAD"],          # relative to the repository root, or absolute
    "privacy": "normal",              # normal | redact | numbers_only (the minimum level)
    "acad_order": ["2023", "2021", "2024"],   # AutoCAD versions to try for DWG -> DXF
    "convert_timeout_s": 900,
    "profile_timeout_s": 600,
    "max_block_depth": 8,
}


def config_path() -> Path:
    env = os.environ.get("AITOOLS_CONFIG")
    return Path(env) if env else REPO / "aitools_config.json"


def get_config() -> dict:
    cfg = dict(DEFAULTS)
    path = config_path()
    if path.is_file():
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        cfg.update({k: v for k, v in data.items() if not k.startswith("_")})
    return cfg


def work_dir() -> Path:
    cfg = get_config()
    if cfg["work_dir"]:
        path = Path(os.path.expandvars(str(cfg["work_dir"])))
    else:
        base = os.environ.get("LOCALAPPDATA") or tempfile.gettempdir()
        path = Path(base) / "HvacAiCad" / "work"
    path.mkdir(parents=True, exist_ok=True)
    return path


def drawing_dirs() -> list[Path]:
    out = []
    for item in get_config()["drawing_dirs"]:
        p = Path(os.path.expandvars(str(item)))
        out.append(p if p.is_absolute() else REPO / p)
    return out
