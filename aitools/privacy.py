"""Privacy modes for everything that leaves this machine toward the model.

normal       texts, names and images pass through.
redact       texts are kept only when they are area labels (`DT. 26,2M2`), contain a
             room-type word from heat_load_table.json, or are short codes (<= 8 chars);
             all others become "[text]". Only file names, never folders, are reported.
numbers_only no texts at all and no images.

The configured mode is the minimum: a call may ask for a stricter mode, never a looser.
"""
from __future__ import annotations

import json
import re
from functools import lru_cache
from pathlib import Path

import arch_extract as ax
from tcvn3 import normalize

from . import config

MODES = ("normal", "redact", "numbers_only")
MASK = "[text]"
MAX_KEPT_LEN = 40
SHORT_CODE = 8


def strictest(a: str, b: str) -> str:
    return max(a, b, key=MODES.index)


def effective_mode(requested: str | None = None) -> str:
    minimum = config.get_config()["privacy"]
    if minimum not in MODES:
        raise ValueError(f"Configured privacy {minimum!r} is not one of {MODES}.")
    if requested is None:
        return minimum
    if requested not in MODES:
        raise ValueError(f"privacy must be one of {MODES}, not {requested!r}.")
    return strictest(minimum, requested)


FILLER = {"phong", "tang", "so", "no", "room", "floor", "level", "va", "and", "cua", "the"}


@lru_cache(maxsize=1)
def _keyword_patterns() -> tuple:
    table = json.loads((config.REPO / "heat_load_table.json").read_text(encoding="utf-8-sig"))
    patterns = []
    for entry in table["types"].values():
        for kw in entry.get("keywords", []):
            key = normalize(str(kw))
            if key:
                patterns.append(re.compile(r"(?<![a-z0-9])" + re.escape(key) + r"(?![a-z0-9])"))
    return tuple(patterns)


def keep_text(text: str) -> bool:
    t = (text or "").strip()
    if not t:
        return False
    if ax.parse_area_label(t) is not None:
        return len(t) <= MAX_KEPT_LEN
    if len(t) <= SHORT_CODE:
        return True
    if len(t) <= MAX_KEPT_LEN:
        return _only_room_words(normalize(t))
    return False


def _only_room_words(key: str) -> bool:
    """True when the text is made of room-type keywords, digits and filler words only,
    so "PHONG HOP CONG TY ACME" (a client name next to a keyword) is still masked."""
    hit = False
    for p in _keyword_patterns():
        key, n = p.subn(" ", key)
        hit = hit or n > 0
    words = re.findall(r"[^\W\d_]+", key)
    return hit and all(w in FILLER for w in words)


def mask_text(text: str | None, mode: str) -> str | None:
    """Text as it may be shown in `mode`: None (omit), the text, or "[text]"."""
    if text is None or mode == "numbers_only":
        return None
    if mode == "normal":
        return text
    return text.strip() if keep_text(text) else MASK


def safe_name(path: str | Path) -> str:
    return Path(path).name
