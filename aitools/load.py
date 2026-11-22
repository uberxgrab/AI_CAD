"""Cooling load for the rooms of a rooms.json, through heat_load.py."""
from __future__ import annotations

import json
from pathlib import Path

import heat_load

from . import config, privacy

TABLE = config.REPO / "heat_load_table.json"


def cooling_load(rooms_json: str | Path, type_overrides: dict | None = None,
                 safety_factor: float | None = None, mode: str | None = None) -> dict:
    """Area x W/m2 x safety factor per room. `type_overrides` maps room id -> room type
    key of the table. Room names are used locally for classification only."""
    mode = privacy.effective_mode(mode)
    data = json.loads(Path(rooms_json).read_text(encoding="utf-8"))
    if data.get("status") != "ok":
        raise ValueError("rooms_json does not hold a successful find_rooms result.")
    request = {"safety_factor": safety_factor, "rooms": []}
    for r in data["rooms"]:
        item = {"id": r["id"], "name": r.get("name") or "", "kind": r["kind"], "area_m2": r["area_m2"]}
        if type_overrides and r["id"] in type_overrides:
            item["room_type_override"] = type_overrides[r["id"]]
        request["rooms"].append(item)
    table = heat_load.load_table(TABLE)
    result = heat_load.calculate(request, table)
    rooms = [{"id": r["id"], "type": r["room_type"], "cooled": r["cooled"], "area_m2": r["area_m2"],
              "w_per_m2": r["w_per_m2"], "load_kw": r["load_kw"]} for r in result["rooms"]]
    return {
        "privacy": mode, "safety_factor": result["safety_factor"], "total_kw": result["total_kw"],
        "cooled_area_m2": round(sum(r["area_m2"] for r in rooms if r["cooled"]), 2),
        "rooms": rooms, "warnings": result["warnings"],
        "room_types": sorted(table["types"]),
        "table_is_placeholder": True,
        "table_note": "W/m2 values in heat_load_table.json are PLACEHOLDERS until the user supplies their own table.",
    }
