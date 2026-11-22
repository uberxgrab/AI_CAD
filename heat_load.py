"""Area-based cooling load per room: the HVACLOAD back end.

The AutoCAD add-in writes a request JSON (the rooms picked in the drawing) and runs
this script as a separate process, then reads the result JSON back:

    venv\\Scripts\\python.exe heat_load.py --request C:\\tmp\\hvacload\\request.json

Request: {version, out, table, safety_factor?, rooms:[{id, name, kind, area_m2,
room_type_override?}]}. `out` and `table` are relative to the request file.
Result:  {version, status "ok"|"error", message?, safety_factor, rooms:[{id, name,
room_type, label_vi, cooled, w_per_m2, area_m2, load_kw}], total_kw, warnings}.

Room types, W/m2 values and name keywords all come from the table file
(heat_load_table.json); the first type whose keyword matches the accent-free room
name wins, so the file order is the priority. Load kW = area * W/m2 * safety / 1000,
0 for types that are not cooled (WC, storage, stairs, shafts...).
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import traceback
from pathlib import Path

from tcvn3 import normalize

OTHER = "other"
SHAFT = "shaft"
DEFAULT_W_PER_M2 = 180.0


class TableError(Exception):
    pass


def load_table(path: Path) -> dict:
    try:
        table = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    except FileNotFoundError:
        raise TableError(f"Table file not found: {path}")
    except (OSError, ValueError) as exc:
        raise TableError(f"Table file {path} cannot be read: {exc}")
    types = table.get("types") if isinstance(table, dict) else None
    if not isinstance(types, dict) or not types:
        raise TableError(f"Table file {path} has no 'types'.")
    return table


def _keyword_pattern(keyword: str) -> "re.Pattern | None":
    key = normalize(keyword)
    if not key:
        return None
    return re.compile(r"(?<![a-z0-9])" + re.escape(key) + r"(?![a-z0-9])")


def classify(name: str, kind: str, table: dict) -> str:
    """Room type key for a room name; shafts are always `shaft`."""
    types = table["types"]
    if kind == "shaft":
        return SHAFT if SHAFT in types else OTHER
    text = normalize(name or "")
    if not text:
        return OTHER
    for key, entry in types.items():
        for keyword in entry.get("keywords", []):
            pattern = _keyword_pattern(str(keyword))
            if pattern and pattern.search(text):
                return key
    return OTHER


def room_load(area_m2: float, w_per_m2: float, cooled: bool, safety: float) -> float:
    return round(area_m2 * w_per_m2 * safety / 1000.0, 4) if cooled else 0.0


def calculate(request: dict, table: dict) -> dict:
    warnings: list = []
    safety = request.get("safety_factor")
    if safety is None:
        safety = table.get("safety_factor", 1.0)
    safety = float(safety)
    types = table["types"]
    rooms = []
    total = 0.0
    for item in request.get("rooms", []):
        name = item.get("name") or ""
        kind = item.get("kind") or "room"
        area = float(item.get("area_m2") or 0.0)
        key = item.get("room_type_override") or classify(name, kind, table)
        if key not in types:
            warnings.append(f"{item.get('id')}: unknown room type '{key}', using '{OTHER}'.")
            key = OTHER
        entry = types.get(key, {})
        cooled = bool(entry.get("cooled", True)) and kind != "shaft"
        w = float(entry.get("w_per_m2", DEFAULT_W_PER_M2)) if cooled else 0.0
        load = room_load(area, w, cooled, safety)
        total += load
        rooms.append({
            "id": item.get("id"), "name": name, "room_type": key,
            "label_vi": entry.get("label_vi", key), "cooled": cooled,
            "w_per_m2": w, "area_m2": area, "load_kw": load,
        })
    return {
        "version": 1, "status": "ok", "safety_factor": safety,
        "table_note": table.get("note", ""), "rooms": rooms,
        "total_kw": round(total, 4), "warnings": warnings,
    }


def run(request_path: Path) -> dict:
    request = json.loads(request_path.read_text(encoding="utf-8-sig"))
    table_path = Path(request.get("table") or "heat_load_table.json")
    if not table_path.is_absolute():
        table_path = request_path.parent / table_path
    return calculate(request, load_table(table_path))


def main(argv: "list | None" = None) -> int:
    parser = argparse.ArgumentParser(description="Area-based cooling load (HVACLOAD back end).")
    parser.add_argument("--request", required=True, type=Path)
    args = parser.parse_args(argv)
    request_path: Path = args.request
    out_path = request_path.parent / "load.json"
    try:
        out_name = json.loads(request_path.read_text(encoding="utf-8-sig")).get("out")
        if out_name:
            out_path = request_path.parent / out_name
    except (OSError, ValueError):
        pass
    try:
        result = run(request_path)
        code = 0
    except Exception as exc:  # noqa: BLE001 - the add-in reads the message from the file
        result = {"version": 1, "status": "error", "message": str(exc), "rooms": [],
                  "total_kw": 0.0, "warnings": []}
        if not isinstance(exc, TableError):
            result["trace"] = traceback.format_exc()
        code = 1
    out_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"{result['status']}: {out_path}")
    return code


if __name__ == "__main__":
    sys.exit(main())
