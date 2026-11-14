# CLAUDE.md

Guidance for Claude Code in this repository. It is based on `AGENTS.md`, with
details corrected against the code. Where the two differ, trust this file and
verify against the source.

## Product

Foundation for an MEP design-assistance toolkit, focused first on HVAC:

- AutoCAD drafting-assistance commands for MEP/HVAC layouts.
- Quantity takeoff from CAD drawings.
- Area-based HVAC heat-load calculation.
- Drawing validation and AI-assisted detection of likely design/drafting
  mistakes.

## Repository layout

- Repository root: Python DXF-validation pipeline.
  - `main.py`: CLI entry point.
  - `parser.py`, `geometry.py`, `rules.py`, `models.py`, `reporting.py`:
    loading, analysis, rule checks, data models, and exports.
  - `config.json`: default thresholds.
  - `tests/test_pipeline.py`: `unittest` suite.
  - `tests/HvacQtoCoreTests/`: C# console tests for `HVACQTO` core logic. Keep this
    outside `cad_addin/`, whose SDK glob would compile it into the add-in.
- `CAD/`: representative CAD inputs. Explicitly named root fixtures such as
  `test.dxf` are also allowed.
- `out/`: generated reports and annotated drawings.
- `cad_addin/`: managed C# AutoCAD add-in (AutoCAD 2023, .NET Framework 4.8,
  x64, C# 7.3). It is not an ObjectARX/C++ project.
  - `src/`: all AutoCAD API calls and CAD UI.
  - `python_bridge.py`: read-only add-in and AutoCAD discovery helper.
  - `start.scr`: `NETLOAD` script used when debugging.
- `.claude/agents/research.md`: read-only `research` subagent for AutoCAD .NET API,
  HVAC reference, and ezdxf questions. It treats this file as authoritative.

Reusable HVAC analysis belongs in the Python modules. The planned integration is
a JSON/file or local-HTTP contract, so do not embed Python or AI dependencies in
the AutoCAD process.

## Python pipeline architecture

`main.py` runs one linear pipeline. Each stage hands plain dataclasses from
`models.py` to the next:

```
parser.load_dxf -> extract_entities      RawEntity
  -> geometry.extract_ducts              DuctPrimitive
  -> build_all_segments                  Segment
  -> build_graph                         networkx.Graph of snapped endpoints
  -> rules.run_all_rules                 ValidationError
  -> reporting.export_*                  report, overlay DXF, features, feedback template
```

- **Input scope:** only `LINE`, `LWPOLYLINE`, and `POLYLINE` count, plus `INSERT`s
  flattened through `virtual_entities()`. A flattened entity on layer `0` takes the
  insert's layer. An `INSERT` that raises is skipped silently. `extract_ducts` is a
  pass-through, so every curve is treated as a duct; there is no HVAC classification
  in Python yet.
- **Segments are mutated by `build_graph`.** `Segment.start`/`end` are rewritten to
  the snapped cluster centres, while `original_start`/`original_end` keep the
  pre-snap rounded values. Rules run after that, so they see snapped coordinates, and
  `detect_near_miss_connections` compares against the originals.
- **Snapping** is union-find over endpoints within `snap_tolerance`, bucketed on a
  grid. Node ids are `N:<x>:<y>` from the rounded snapped point, and edges carry
  `segment_id`, `handle`, `layer`, and `length`.
- **Rules** are the six `detect_*` functions listed in `run_all_rules`
  (`rules.py`). A new rule is a function returning `ValidationError`s plus
  one `errors.extend(...)` line there. `error_id` is `<TYPE>:<suffix>`, and the
  overlay label uses the suffix.
- **Config:** `DEFAULT_CONFIG` in `main.py` and `config.json` hold the same keys;
  `--config` overrides by `dict.update`. Units are drawing units (mm for these
  drawings).
- `--report` writes CSV when the path ends in `.csv`, otherwise JSON. `--features`
  and `--feedback-template` default to `features.jsonl` and
  `feedback_template.jsonl` beside the report.

## AutoCAD commands

| Command | Source | Behavior |
| --- | --- | --- |
| `HVACSTRETCH` | `cad_addin/src/HvacStretch.cs` | Mixed native/dynamic stretch from one Crossing rectangle. Main current feature. |
| `HVACQTO` | `cad_addin/src/HvacQuantityTakeoff.cs`, `HvacQuantityTakeoffCore.cs`, `TakeoffSettingsForm.cs` | Exports the pickfirst set, or a new `GetSelection`, to a SpreadsheetML `.xml` workbook laid out like the VRV quotation template, with formulas and a contingency factor cell. |
| `HVACCOUNT` | `cad_addin/src/TQO.cs` | Totals `Line` and `Polyline` lengths on layers that start with `!` and contain `x` (case-insensitive). |
| `FIRSTMESSAGE` | `cad_addin/src/Message.cs` | Writes a hello message to the command line. |

`HVACQTO` classification:

- Dynamic blocks with a positive `Distance3` and a size in the block name
  (`600x300`, `D250`, `Ø250`, `DN250`) are ducts. Three-dimension names and bare
  numbers are not sizes. Duct area is totalled per sheet thickness, using bands
  set in a dialog on each run. The largest side or the diameter picks the band.
- Other blocks are equipment. `ODU-n HP`, `FCU-CAS-n kW`, `Water Cooled Chiller -
  n RT`, and `HVAC-NAME OF EQUIPMENTS` labels titled `CHWP` or `CWP` get template
  wording. Everything else keeps its block name and CAD parameters.
- If the selection has a pump label, `HVAC_PUMP` symbols are excluded so pumps are
  not counted twice.
- Curves are classified by layer, first match wins: `CHWS`/`CHWR`/`HWS`/`HWR` is
  steel, an `a/b` or `RP a-b` pair is copper, and `CONDENSATED` or a `D34`/`DN50`
  symbol is uPVC. Other layers are not exported and are reported on the command
  line.
- A settings dialog before the takeoff asks for the contingency factor (default
  1.0), the thickness bands, and shaft risers (duct, steel, or condensate pipe,
  with size, height, and runs). It remembers the last values.
- Quantities are Excel formulas (`l1+l2+...`, `2*(W+H)*L`) multiplied by the
  factor cell `F6` on `DIEU HOA THONG GIO`. See `AGENTS.md` for details.
- Drawing units are assumed to be millimetres. Output is in metres and square
  metres.

## Status

| Workstream | Status | Current capability / limitation |
| --- | --- | --- |
| Dynamic block stretching | Implemented; needs in-AutoCAD acceptance testing | Supported properties are `Distance3`, `Distance`, `Length`, and `Width`. The property must be numeric, writable, and backed by a Dynamic Block Stretch Action. |
| Native STRETCH preview | Implemented; needs in-AutoCAD acceptance testing | AutoCAD owns the second-point prompt and its preview. `PointMonitor` previews eligible dynamic blocks at the same time. |
| Quantity takeoff | Initial implementation | `HVACQTO` and `HVACCOUNT` exist. There is no common MEP object/quantity schema yet. |
| Heat-load calculation | Planned | No room extraction, design conditions, thermal assumptions, or calculation engine exists. |
| DXF validation / AI error detection | Rule-based foundation | Deterministic checks only. AI inference, labelled data, and CAD-facing review workflows are planned. |
| Automated tests | Python and `HVACQTO` core | `tests/HvacQtoCoreTests/` covers the AutoCAD-free takeoff logic and workbook output. There are no AutoCAD integration tests, so add-in commands must be tested manually in AutoCAD. |

## Next steps

1. Manually test `HVACSTRETCH` after `NETLOAD`ing the rebuilt x64 DLL: horizontal,
   vertical, and rotated blocks; each end independently; full block crossing;
   mixed ordinary/dynamic selections; Esc; typed versus clicked second points.
2. If a dynamic block does not respond, verify in Block Editor that `Distance3`
   has a Linear parameter and an associated Stretch Action. Record each supported
   block's property name and parameter direction.
3. Harden `HVACSTRETCH` from those findings: typed second-point input if
   `PointMonitor` gives no final cursor point, property limits/lookup tables, and
   dynamic parameters not aligned to the block's local X/Y axes.
4. Define a common MEP object/quantity schema before expanding takeoff to pipes,
   fittings, insulation, and further report exports.
5. Specify the heat-load input model (rooms/areas, orientations, envelope,
   occupancy, lighting, ventilation, weather/design conditions), then add
   calculation tests before exposing an AutoCAD command.
6. Establish labelled drawing-error examples and evaluation criteria before
   adding AI prediction on top of the rule engine.

## `HVACSTRETCH` design decisions

Do not reverse these without discussing it with the user.

- Use native AutoCAD `STRETCH` for ordinary entities instead of reimplementing
  grip behavior. This preserves AutoCAD's Crossing selection and entity-specific
  stretch semantics.
- Capture one Crossing rectangle and replay the same coordinates into native
  STRETCH, so users do not select ordinary entities twice.
- Do not use `Editor.Command` with a pre-supplied second point when preview is
  required. It completes STRETCH synchronously, leaving no rubber-band phase.
  `SendStringToExecute` stops after the base point so STRETCH owns the
  second-point prompt.
- Use `Editor.PointMonitor` for temporary dynamic-block preview. Save original
  property values and insertion points, restore them when STRETCH ends or
  cancels, then commit the final property change after a successful STRETCH.
- Choose the dynamic stretch axis by projecting the drag vector onto the block's
  local X and Y axes. Take bounds from `BlockReference.GeometricExtents`, not the
  block definition, because dynamic blocks can use anonymous definitions after
  actions run.
- Detect which end is crossed by projecting the Crossing rectangle and block
  extents onto the chosen axis. After changing the distance property, align the
  unselected end back to its original projection to keep it fixed.
- Scope is limited to X/Y-aligned linear parameters.
  `DynamicBlockReferenceProperty` does not expose parameter direction, so
  arbitrary directions need per-block mapping or a more capable API strategy.

## Plan before major changes

Before a major change, present a concise plan and wait for the user's approval.
Do not edit files for that change until it is approved. Major changes include:

- Adding a feature or command.
- Adding a C# (`.cs`) source file.
- Changing the execution order or control flow of commands in a C# source file.
- A broad change affecting multiple modules or established workflows.

The plan should cover intended behavior, files or components likely to change,
and how the result will be checked. If the scope changes materially, present a
revised plan and get approval again. Small fixes and documentation-only edits do
not need approval unless they are part of a major change.

## Commands

Run from the repository root in PowerShell:

```powershell
# Run the Python tests
venv\Scripts\python.exe -m unittest discover -s tests -v

# Run one Python test
venv\Scripts\python.exe -m unittest tests.test_pipeline.PipelineTests.test_cli_generates_outputs -v

# Validate a drawing (--input, --report, and --overlay are required)
venv\Scripts\python.exe main.py --input test.dxf --report out/report.json --overlay out/annotated.dxf --config config.json

# Report add-in and AutoCAD discovery status
venv\Scripts\python.exe main.py --cad-status

# Build the add-in
dotnet build cad_addin\HvacAiCadAddin.csproj --no-restore -p:Configuration=Debug -p:Platform=x64

# Run the HVACQTO core tests (build output goes to %TEMP%\HvacQtoCoreTests)
dotnet run --project tests\HvacQtoCoreTests\HvacQtoCoreTests.csproj -c Debug
```

`main.py` also accepts `--features` and `--feedback-template` for JSONL exports.

The `HVACQTO` core test runner has no per-test filter. It runs every check under
en-US, vi-VN, and tr-TR, and exits non-zero on any failure. Its other modes:

```powershell
# Write sample workbooks for template and Excel checks
dotnet run --project tests\HvacQtoCoreTests\HvacQtoCoreTests.csproj -c Debug -- --emit <dir>

# Print results for inputs the spec leaves open (counts nothing)
dotnet run --project tests\HvacQtoCoreTests\HvacQtoCoreTests.csproj -c Debug -- --probe
```

## Known pitfalls

- **DWG input is rejected.** `parser.load_dxf` raises on `.dwg`, so `CAD/test.dwg`
  must be converted to DXF first. Only `test.dxf` works with `main.py` as is.
- **`requirements.txt` is UTF-16 LE with a BOM.** Plain text tools and some editors
  show it garbled or with null bytes. Keep the encoding when editing it, or
  re-save it as UTF-8 deliberately.
- **Two Debug output folders.** The build above writes to
  `cad_addin\bin\x64\Debug\net48\`, but `start.scr` loads
  `cad_addin\bin\Debug\net48\`. Confirm which DLL AutoCAD loads, or a test can
  silently run an older build.
- **`--cad-status` reports `built_addin: null`.** `python_bridge.py` still looks
  for `HvacAiCadAddin.arx` under `cad_addin/build/`, so it never finds the managed
  DLL. The `objectarx_sdk` field is a leftover too.
- **`cad_addin/README.md` is partly stale.** It lists `HVACAI` and `ME`, which do
  not exist, and it still has ObjectARX/native C++ text.
- **AutoCAD layer names cannot contain `/`.** Real copper layers look like
  `EIA_1 RP 15.9-9.5 (14)`, so `HVACQTO` also accepts `RP a-b`. Hyphen pairs without
  `RP` are not copper, and text in parentheses is ignored.
- **A loaded add-in cannot be replaced.** AutoCAD keeps a `NETLOAD`ed assembly until
  it exits. Restart AutoCAD before loading a rebuilt DLL.
- **Build outputs are tracked.** There is no `.gitignore`, and `cad_addin/bin/`,
  `cad_addin/obj/`, and `out/` files are in git. Check `git status` and do not
  stage them by accident.
- **Machine-specific paths.** `start.scr` and the `.csproj` hard-code
  `C:\Users\Admin\hvac_ai` and the AutoCAD 2023 install path.

## Coding style

- Python: 4-space indentation, `snake_case` functions and variables, `PascalCase`
  classes, type annotations where practical, small modules, and `pathlib.Path`
  for file paths. No formatter or linter is configured.
- C#: stay within C# 7.3 and .NET Framework 4.8. Keep AutoCAD API calls in
  `cad_addin/src/`, and keep Autodesk references at `Private=False` so their DLLs
  are not copied to the output.

## Testing

- Add focused `unittest` methods named `test_<behavior>` under `tests/`.
- Cover both rule behavior and end-to-end CLI output when changing pipeline logic.
- Tests create temporary data in `.tmp_test_runs/`. Do not commit it.
- Run the full discovery command before submitting changes.
- C# changes: build the add-in, then give the user manual AutoCAD test steps.
  A successful build does not prove a command works.

## Commits and pull requests

- Use concise, imperative subjects such as `Add elbow overlap rule`, and keep
  commits scoped.
- PRs should explain the validation behavior changed, list test commands run,
  link the relevant issue when applicable, and include before/after report or
  overlay samples for CAD-analysis changes.
- Do not commit local CAD installs, `venv/`, or generated `out/` files unless
  intentionally updating a fixture.
