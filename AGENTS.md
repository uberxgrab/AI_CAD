# Repository Guidelines

## Product Context and Handoff Status

This repository is the foundation for an MEP design-assistance toolkit, with an
initial focus on HVAC. The intended product areas are:

- AutoCAD drafting-assistance commands for MEP/HVAC layouts.
- Quantity takeoff from CAD drawings.
- Area-based HVAC heat-load calculation.
- Drawing validation and AI-assisted detection of likely design/drafting
  mistakes.

### What is complete

- The Python DXF-validation pipeline is present at the repository root. It
  parses drawings, applies configured geometry/rule checks, and writes JSON,
  JSONL, and annotated-DXF outputs.
- The managed AutoCAD add-in project is present in `cad_addin/` and targets
  AutoCAD 2023, .NET Framework 4.8, and x64.
- `HVACCOUNT` in `cad_addin/src/TQO.cs` totals `Line` and `Polyline` lengths
  on layers whose names start with `!` and contain `x`.
- `HVACSTRETCH` in `cad_addin/src/HvacStretch.cs` has been implemented as a
  mixed native/dynamic stretch command. It accepts one Crossing rectangle,
  uses AutoCAD's real `STRETCH` for ordinary entities, and applies a dynamic
  block distance-property change when exactly one end of a supported dynamic
  block is crossed.
- The add-in compiled successfully with:

  ```powershell
  dotnet build cad_addin\HvacAiCadAddin.csproj --no-restore -p:Configuration=Debug -p:Platform=x64
  ```

### Current status by workstream

| Workstream | Status | Current capability / limitation |
| --- | --- | --- |
| CAD drafting tools | Active | `HVACSTRETCH` is the main current feature. `YOURSTRETCH` is an older custom grip-based experiment and is not the native-STRETCH solution. |
| Dynamic block stretching | Implemented; requires in-AutoCAD acceptance testing | Supported property names are `Distance3`, `Distance`, `Length`, and `Width`; the property must be numeric, writable, and backed by a Dynamic Block Stretch Action. |
| Native STRETCH preview | Implemented; requires in-AutoCAD acceptance testing | `SendStringToExecute` sends STRETCH, the stored Crossing window, and base point only. AutoCAD owns the interactive second-point prompt for its normal preview; `PointMonitor` previews eligible dynamic blocks at the same time. |
| Quantity takeoff | Initial implementation | `HVACCOUNT` covers line/polyline lengths only. No duct/fitting/equipment quantity model, schedule, or export workflow exists yet. |
| Heat-load calculation | Planned | No area/room extraction, design conditions, thermal assumptions, or calculation engine has been implemented. |
| DXF validation / AI error detection | Rule-based foundation present | The Python pipeline performs deterministic checks. AI-based inference, training/evaluation data, and CAD-facing issue review workflows are still planned. |
| Automated tests | Python only | `tests/test_pipeline.py` covers the Python pipeline. There are no automated AutoCAD integration tests; `HVACSTRETCH` must be tested manually in a real AutoCAD session. |

### Next steps

1. Manually test `HVACSTRETCH` after `NETLOAD`ing the rebuilt x64 DLL. Test
   horizontal, vertical, and rotated blocks; each end independently; full
   block crossing; mixed ordinary/dynamic selections; Esc; and typed versus
   clicked second points.
2. If a particular dynamic block does not respond, verify in Block Editor that
   `Distance3` has a Linear parameter and associated Stretch Action. Record
   each supported block's property name and parameter direction.
3. Harden `HVACSTRETCH` from the manual-test findings: in particular, handle
   typed second-point input if `PointMonitor` does not provide a final cursor
   point, property limits/lookup tables, and dynamic parameters not aligned to
   the block's local X/Y axes.
4. Define a common MEP object/quantity schema before expanding `HVACCOUNT` to
   ducts, pipes, fittings, equipment, insulation, and report exports.
5. Specify the heat-load input model (rooms/areas, orientations, envelope,
   occupancy, lighting, ventilation, and weather/design conditions), then add
   calculation tests before exposing an AutoCAD command.
6. Establish labelled drawing-error examples and evaluation criteria before
   adding AI prediction on top of the current deterministic rule engine.

### Important decisions: `HVACSTRETCH`

- Use the native AutoCAD `STRETCH` command for ordinary entities instead of
  reimplementing grip behavior. This preserves AutoCAD's established Crossing
  selection and entity-specific stretch semantics.
- Capture a single Crossing rectangle in `HVACSTRETCH` and replay those same
  coordinates into native STRETCH. This avoids making users select ordinary
  entities a second time.
- Do not use `Editor.Command` with a pre-supplied second point when preview is
  required. It completes STRETCH synchronously, so AutoCAD has no interactive
  rubber-band phase. `SendStringToExecute` stops after the base point so the
  real STRETCH command owns the second-point prompt and previews ordinary
  entities.
- Use `Editor.PointMonitor` for temporary dynamic-block preview during that
  native second-point prompt. Save original property values and insertion
  points, restore them when STRETCH ends/cancels, then commit the final dynamic
  property change after a successful native STRETCH.
- Select the dynamic stretch axis by projecting the drag vector onto the
  block's local X and Y axes. Bounds are taken from `BlockReference`
  `GeometricExtents` rather than the block definition because dynamic blocks
  can use anonymous definitions after actions run.
- Detect whether the start or end is crossed by projecting both the Crossing
  rectangle and block extents onto the chosen local axis. After changing the
  distance property, align the unselected end back to its original projection.
  This is intended to keep the opposite end fixed for either-end stretching.
- Current scope is intentionally limited to X/Y-aligned linear dynamic
  parameters. AutoCAD's managed `DynamicBlockReferenceProperty` API does not
  expose the parameter direction directly; blocks with arbitrary parameter
  directions need explicit per-block mapping or a more capable API strategy.

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

## Planning and Approval for Major Changes

Before making a major change, first prepare a concise implementation plan and
present it to the user for approval. Do not edit files for that major change
until the user approves the plan. Major changes include, but are not limited
to:

- Adding a new feature or command.
- Adding a new C# (`.cs`) source file.
- Changing the execution order or control flow of commands in a C# source file.
- Making a broad change that affects multiple modules or established workflows.

The plan should describe the intended behavior, the files or components likely
to change, and how the result will be checked. After approval, implement the
approved plan. If the scope needs to change materially, present the revised plan
and get approval before proceeding with the changed scope. Small fixes and
documentation-only edits do not require planning approval unless they are part
of a major change.

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
