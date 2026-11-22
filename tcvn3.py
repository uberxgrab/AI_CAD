"""Decode Vietnamese text typed in the legacy TCVN3 (ABC) encoding.

Old drawings use `.VnTime`-style fonts: the text is stored as single-byte TCVN3,
which a DWG/DXF reader sees as Latin-1 characters (`MÆT B»NG` for `MẶT BẰNG`).
TCVN3 has no capital toned letters; uppercase fonts (`.VnTimeH`) draw the lowercase
codes as capitals, so words typed in ASCII capitals are upper-cased after decoding.
"""
from __future__ import annotations

import re
import unicodedata

_TCVN3 = {
    0xA1: "Ă", 0xA2: "Â", 0xA3: "Ê", 0xA4: "Ô", 0xA5: "Ơ", 0xA6: "Ư", 0xA7: "Đ",
    0xA8: "ă", 0xA9: "â", 0xAA: "ê", 0xAB: "ô", 0xAC: "ơ", 0xAD: "ư", 0xAE: "đ",
    0xB5: "à", 0xB6: "ả", 0xB7: "ã", 0xB8: "á", 0xB9: "ạ",
    0xBB: "ằ", 0xBC: "ẳ", 0xBD: "ẵ", 0xBE: "ắ",
    0xC6: "ặ", 0xC7: "ầ", 0xC8: "ẩ", 0xC9: "ẫ", 0xCA: "ấ", 0xCB: "ậ",
    0xCC: "è", 0xCE: "ẻ", 0xCF: "ẽ", 0xD0: "é", 0xD1: "ẹ",
    0xD2: "ề", 0xD3: "ể", 0xD4: "ễ", 0xD5: "ế", 0xD6: "ệ",
    0xD7: "ì", 0xD8: "ỉ", 0xDC: "ĩ", 0xDD: "í", 0xDE: "ị",
    0xDF: "ò", 0xE1: "ỏ", 0xE2: "õ", 0xE3: "ó", 0xE4: "ọ",
    0xE5: "ồ", 0xE6: "ổ", 0xE7: "ỗ", 0xE8: "ố", 0xE9: "ộ",
    0xEA: "ờ", 0xEB: "ở", 0xEC: "ỡ", 0xED: "ớ", 0xEE: "ợ",
    0xEF: "ù", 0xF1: "ủ", 0xF2: "ũ", 0xF3: "ú", 0xF4: "ụ",
    0xF5: "ừ", 0xF6: "ử", 0xF7: "ữ", 0xF8: "ứ", 0xF9: "ự",
    0xFA: "ỳ", 0xFB: "ỷ", 0xFC: "ỹ", 0xFD: "ý", 0xFE: "ỵ",
}
_TABLE = str.maketrans({chr(code): text for code, text in _TCVN3.items()})
_CODES = "".join(chr(code) for code in _TCVN3)
# A TCVN3 code glued to an ASCII letter. Symbols such as Ø250 or 600×300 sit next to
# digits instead, so they are left alone.
_IN_WORD = re.compile(rf"[A-Za-z][{_CODES}]|[{_CODES}][A-Za-z]")
_CONTROL = re.compile(r"%%[UuOo]")
# Unicode Vietnamese letters that happen to sit in Latin-1. A string whose only
# non-ASCII characters are these ("PHÒNG KHÁCH") is real Unicode, not TCVN3.
_UNICODE_VI_LATIN1 = set("ÀÁÂÃÈÉÊÌÍÒÓÔÕÙÚÝàáâãèéêìíòóôõùúý")


def _font_stem(font: str | None) -> str:
    stem = (font or "").strip().lower()
    for suffix in (".ttf", ".shx"):
        if stem.endswith(suffix):
            stem = stem[: -len(suffix)]
    return stem.lstrip(".")


def is_tcvn3_font(font: str | None) -> bool:
    return _font_stem(font).startswith("vn")


def looks_tcvn3(text: str, font: str | None = None) -> bool:
    """True when `text` is probably TCVN3 seen as Latin-1."""
    if any(ord(ch) > 0xFF for ch in text):
        return False  # real Unicode (Vietnamese, CJK, symbols)
    if is_tcvn3_font(font):
        return any(ch in _CODES for ch in text)
    if all(ch.isascii() or ch in _UNICODE_VI_LATIN1 for ch in text):
        return False
    return bool(_IN_WORD.search(text))


def _upper_words(original: str, decoded: str) -> str:
    out = []
    for src, dst in zip(original.split(" "), decoded.split(" ")):
        letters = [ch for ch in src if ch.isascii() and ch.isalpha()]
        out.append(dst.upper() if letters and all(ch.isupper() for ch in letters) else dst)
    return " ".join(out)


def decode_tcvn3(text: str, font: str | None = None) -> str:
    decoded = text.translate(_TABLE)
    if is_tcvn3_font(font) and _font_stem(font).endswith("h"):
        return decoded.upper()
    return _upper_words(text, decoded)


def decode_cad_text(text: str, font: str | None = None) -> str:
    """Strip AutoCAD underline/overline codes and decode TCVN3 when detected."""
    text = _CONTROL.sub("", text or "").strip()
    return decode_tcvn3(text, font) if looks_tcvn3(text, font) else text


def normalize(text: str) -> str:
    """Lower-case, accent-free form for matching (`MẶT BẰNG` -> `mat bang`)."""
    text = unicodedata.normalize("NFD", text.replace("đ", "d").replace("Đ", "D"))
    text = "".join(ch for ch in text if unicodedata.category(ch) != "Mn")
    return re.sub(r"\s+", " ", text.casefold()).strip()
