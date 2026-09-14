"""Read-only discovery utilities for exposing CAD add-in state in HVAC AI."""

from __future__ import annotations

import os
from pathlib import Path


def cad_addin_status(project_root: Path | None = None) -> dict[str, object]:
    """Return build and AutoCAD discovery information without modifying AutoCAD."""
    root = project_root or Path(__file__).resolve().parent.parent
    candidates = [
        root / "cad_addin" / "build" / "Release" / "HvacAiCadAddin.arx",
        root / "cad_addin" / "build" / "Debug" / "HvacAiCadAddin.arx",
    ]
    autocad_roots = sorted(Path(os.environ.get("ProgramFiles", r"C:\\Program Files")).glob("Autodesk/AutoCAD*"))
    return {
        "addin_source": str(root / "cad_addin"),
        "built_addin": next((str(path) for path in candidates if path.is_file()), None),
        "objectarx_sdk": os.environ.get("OBJECTARX_SDK"),
        "autocad_installations": [str(path) for path in autocad_roots if path.is_dir()],
    }
