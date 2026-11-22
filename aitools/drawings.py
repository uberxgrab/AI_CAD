"""Find drawings, convert DWG copies to DXF with accoreconsole, cache the results.

Source files are never written to or modified: a DWG is copied into the work
folder first, and caches (DXF, overview JSON) live in the work folder, keyed by
path + size + mtime of the source.
"""
from __future__ import annotations

import hashlib
import json
import re
import shutil
import subprocess
import time
from pathlib import Path
from typing import Callable

import arch_extract as ax

from . import config

Converter = Callable[[Path, Path], None]   # (source dwg, output dxf) -> None, raises on failure
_DOC_CACHE: dict = {}


def cache_key(path: Path) -> str:
    st = path.stat()
    raw = f"{path.resolve()}|{st.st_size}|{st.st_mtime_ns}"
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()[:12]


def _ascii_stem(path: Path) -> str:
    return re.sub(r"[^A-Za-z0-9]+", "_", path.stem).strip("_")[:30] or "drawing"


def cache_dir(path: Path) -> Path:
    return config.work_dir() / "cache" / f"{_ascii_stem(path)}_{cache_key(path)}"


def converted_dxf_path(path: Path) -> Path:
    return cache_dir(path) / "drawing.dxf"


def dxf_is_complete(path: Path) -> bool:
    try:
        with open(path, "rb") as fh:
            fh.seek(0, 2)
            fh.seek(max(0, fh.tell() - 200))
            return fh.read().strip().endswith(b"EOF")
    except OSError:
        return False


def _drawing_files() -> list[Path]:
    seen, out = set(), []
    for folder in config.drawing_dirs():
        if not folder.is_dir():
            continue
        for p in sorted(folder.iterdir()):
            if p.is_file() and p.suffix.lower() in (".dwg", ".dxf") and p not in seen:
                seen.add(p)
                out.append(p)
    return out


def list_drawings() -> list[dict]:
    """DWG/DXF files in the configured folders (file names only, no folders)."""
    rows = []
    for p in _drawing_files():
        row = {"name": p.name, "type": p.suffix.lower().lstrip("."),
               "size_mb": round(p.stat().st_size / 1e6, 2)}
        if p.suffix.lower() == ".dwg":
            row["converted"] = dxf_is_complete(converted_dxf_path(p))
        rows.append(row)
    return rows


def find_drawing(name_or_path: str) -> Path:
    p = Path(name_or_path)
    if p.is_file():
        return p
    wanted = p.name.casefold()
    files = _drawing_files()
    for f in files:
        if f.name.casefold() == wanted:
            return f
    stems = [f for f in files if f.stem.casefold() == wanted or f.stem.casefold() == Path(wanted).stem]
    if len(stems) == 1:
        return stems[0]
    if len(stems) > 1:
        raise FileNotFoundError(f"{name_or_path!r} is ambiguous: {[f.name for f in stems]}")
    raise FileNotFoundError(f"Drawing {name_or_path!r} not found. Use list_drawings for the names.")


# ---------------------------------------------------------------- conversion


def find_accoreconsole() -> list[Path]:
    root = Path(r"C:\Program Files\Autodesk")
    out = []
    for year in config.get_config()["acad_order"]:
        exe = root / f"AutoCAD {year}" / "accoreconsole.exe"
        if exe.is_file():
            out.append(exe)
    return out


def accore_converter(timeout: float | None = None) -> Converter:
    """Converter using accoreconsole (/isolate keeps the user's registry untouched)."""
    timeout = timeout or config.get_config()["convert_timeout_s"]

    def convert(src: Path, out: Path) -> None:
        exes = find_accoreconsole()
        if not exes:
            raise RuntimeError("No accoreconsole.exe found for the AutoCAD versions in acad_order.")
        errors = []
        for exe in exes:
            work = out.parent / "acad"
            if work.exists():
                shutil.rmtree(work, ignore_errors=True)
            (work / "iso").mkdir(parents=True)
            copy = work / "src.dwg"
            shutil.copyfile(src, copy)
            script = work / "run.scr"
            script.write_text("\n".join(["_.AUDIT", "_Y", "_.DXFOUT", f'"{out}"', "_V", "2013", "16",
                                         "_.QUIT", "_Y", ""]), encoding="ascii")
            if out.exists():
                out.unlink()
            try:
                subprocess.run([str(exe), "/isolate", "hvacai", str(work / "iso"), "/i", str(copy),
                                "/s", str(script), "/l", "en-US"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=timeout)
            except subprocess.TimeoutExpired:
                errors.append(f"{exe.parent.name}: timed out after {timeout:.0f} s")
                continue
            finally:
                shutil.rmtree(work, ignore_errors=True)
            if out.is_file() and dxf_is_complete(out):
                return
            errors.append(f"{exe.parent.name}: no complete DXF produced")
        raise RuntimeError("DWG to DXF conversion failed (" + "; ".join(errors) + ")")

    return convert


def convert_dwg(name_or_path: str, converter: Converter | None = None) -> dict:
    """Copy a DWG, convert it to DXF (cached by path+size+mtime), return the DXF path."""
    src = find_drawing(name_or_path)
    if src.suffix.lower() == ".dxf":
        return {"name": src.name, "dxf": str(src), "cached": True, "elapsed_s": 0.0,
                "size_mb": round(src.stat().st_size / 1e6, 2), "note": "already a DXF"}
    target = converted_dxf_path(src)
    if dxf_is_complete(target):
        return {"name": src.name, "dxf": str(target), "cached": True, "elapsed_s": 0.0,
                "size_mb": round(target.stat().st_size / 1e6, 2)}
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp = target.parent / "converting.dxf"
    t0 = time.perf_counter()
    (converter or accore_converter())(src, tmp)
    if not dxf_is_complete(tmp):
        raise RuntimeError("Converter did not produce a complete DXF.")
    tmp.replace(target)
    (target.parent / "meta.json").write_text(
        json.dumps({"source_name": src.name, "source_size": src.stat().st_size}), encoding="utf-8")
    return {"name": src.name, "dxf": str(target), "cached": False,
            "elapsed_s": round(time.perf_counter() - t0, 1),
            "size_mb": round(target.stat().st_size / 1e6, 2)}


def resolve_dxf(drawing: str, converter: Converter | None = None) -> Path:
    """A DXF path or a drawing name (DWG converted on demand) -> DXF file path."""
    p = Path(drawing)
    if p.is_file() and p.suffix.lower() == ".dxf":
        return p
    return Path(convert_dwg(drawing, converter)["dxf"])


def load_doc(dxf: Path):
    """Parsed ezdxf document, one-slot cache (big drawings take a minute to parse)."""
    key = (str(dxf.resolve()), dxf.stat().st_size, dxf.stat().st_mtime_ns)
    if _DOC_CACHE.get("key") != key:
        _DOC_CACHE.clear()
        _DOC_CACHE["doc"] = ax.load_drawing(dxf)
        _DOC_CACHE["key"] = key
    return _DOC_CACHE["doc"]
