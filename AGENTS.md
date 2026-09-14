# Repository Guidelines

## Project Structure & Module Organization

The Python HVAC DXF-validation pipeline is at the repository root. `main.py` is
the CLI entry point; `parser.py`, `geometry.py`, `rules.py`, `models.py`, and
`reporting.py` implement loading, analysis, rule checks, data models, and
exports. Default thresholds are in `config.json`. Keep representative CAD input
files in `CAD/` or as explicitly named root fixtures; generated reports and
annotated drawings belong in `out/`.

Tests live in `tests/test_pipeline.py`. The native AutoCAD ObjectARX add-in is
deliberately isolated in `cad_addin/`: C++ source is in `cad_addin/src/`, and
its build and SDK requirements are documented in `cad_addin/README.md`.

## Build, Test, and Development Commands

Use the checked-in virtual environment on Windows, or create an equivalent one
and install `requirements.txt`:

```powershell
venv\Scripts\python.exe -m unittest discover -s tests -v
venv\Scripts\python.exe main.py --input test.dxf --report out/report.json --overlay out/annotated.dxf --config config.json
venv\Scripts\python.exe main.py --cad-status
```

The first command runs the pipeline tests. The second validates a drawing and
writes JSON/JSONL outputs plus an annotated DXF. The third reports add-in
discovery status. Build the ARX add-in only from an x64 Visual Studio developer
prompt, following `cad_addin/README.md`.

## Coding Style & Naming Conventions

Use Python 4-space indentation, `snake_case` functions and variables,
`PascalCase` classes, and type annotations where practical. Keep modules small
and use `pathlib.Path` for file paths. Preserve the existing standard-library
`unittest` style; no formatter or linter is currently configured. Use C++17 in
the add-in and keep AutoCAD API calls inside `cad_addin/src/`.

## Testing Guidelines

Add focused `unittest` methods named `test_<behavior>` in `tests/`. Cover both
rule behavior and end-to-end CLI outputs when changing pipeline logic. Tests
create temporary data under `.tmp_test_runs/`; do not commit those artifacts.
Run the full discovery command before submitting changes.

## Commit & Pull Request Guidelines

No accessible repository history establishes a commit convention. Use concise,
imperative subjects such as `Add elbow overlap rule`. Keep commits scoped.
PRs should explain the changed validation behavior, list test commands run, link
the relevant issue when applicable, and include before/after report or overlay
samples for CAD-analysis changes. Do not commit local CAD installs, `venv/`, or
generated `out/` files unless intentionally updating a fixture.
