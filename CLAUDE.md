# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

It is based on `AGENTS.md`, with details corrected against the code. Where the two differ, trust this file and
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
  - `tcvn3.py`, `arch_extract.py`, `arch_rooms.py`: "tầng 0" architecture reading
    (no AI; ezdxf + shapely). `tcvn3.py` decodes legacy TCVN3 Vietnamese text.
    `arch_extract.py` is the fully automatic prototype (views, titles, rooms by
    morphological closing, crossed-out sheets, X-marked shafts); it also holds the
    helpers the others import. `arch_rooms.py` is the `HVACROOMS` back end: one
    user-chosen region and user-ticked wall layers, rooms by polygonize.
  - `tests/test_pipeline.py`, `tests/test_arch_extract.py`, `tests/test_arch_rooms.py`:
    `unittest` suites.
  - `tests/HvacQtoCoreTests/`: C# console tests for `HVACQTO` core logic. Keep this
    outside `cad_addin/`, whose SDK glob would compile it into the add-in.
  - `tests/PipeCoreTests/`: C# console tests for the AutoCAD-free pipe core
    (`HVACPIPESIZE` run finding, `HVACREFRIG` cleaning, pipe tree and sizing, and `HVACDRAIN` tree, joints and sizing). Same rule: outside `cad_addin/`.
  - `tests/DuctCoreTests/`: C# console tests for `DuctGeometryCore.cs` (fitting
    rules, routing, hit tests, align, sizes, settings). Same rule.
  - `tests/DuctAcadTests/`: headless `accoreconsole` tests for `HVACDUCT` and
    `HVACDUCTALIGN` on copies of the template, run by `run-tests.ps1`. It links the
    duct sources from `cad_addin/src/` and references only `acdbmgd`/`accoremgd`.
    `tests/PipeAcadTests` is planned but not written.
  - `tests/RoomsCoreTests/`, `tests/LoadCoreTests/`, `tests/UnitCoreTests/`,
    `tests/RouteCoreTests/`, `tests/OduRiserCoreTests/`: C# console tests for the
    AutoCAD-free cores of `HVACROOMS`, `HVACLOAD`, `HVACPLACEUNITS`,
    `HVACROUTEDRAFT`, `HVACPLACEODU` and `HVACRISER`. They link
    `tests/PipeCoreTests/Check.cs`. Same rule. `tests/UnitCoreTests/Acad/` (no run
    script yet) and `tests/OduRiserCoreTests/Acad/run-tests.ps1 -WorkDwg <copy>` are
    headless accoreconsole tests of the writers.
  - `heat_load.py` + `heat_load_table.json`: the `HVACLOAD` back end. The W/m² values
    in the table are PLACEHOLDERS until the user sends their own table; all values,
    keywords and labels are read from the file.
- `CAD/`: representative CAD inputs. Explicitly named root fixtures such as
  `test.dxf` are also allowed.
  - `CAD/MEP_DWT_3.dwg`: the user's template drawing. It holds their duct blocks,
    refnet (branch joint) blocks, and pipe-size layers such as `EIA_1 CHWS DN40`,
    `EIA_1 Pipe condensated DN21`, and `EIA_1 RP 28.6-12.7 (33.6-44.8)`; the
    bracket appears to be a capacity range. Treat it as read-only, and convert a
    copy to DXF to inspect it (see Commands).
- `out/`: generated reports and annotated drawings.
- `cad_addin/`: managed C# AutoCAD add-in (AutoCAD 2023, .NET Framework 4.8,
  x64, C# 7.3). It is not an ObjectARX/C++ project.
  - `src/`: all AutoCAD API calls and CAD UI.
  - `python_bridge.py`: read-only add-in and AutoCAD discovery helper.
  - `start.scr`: `NETLOAD` script used when debugging.
- `docs/SESSION_HANDOFF.md`: summary of past Claude sessions, user decisions, work
  in progress, and setup on a new machine. Read it when resuming work.
- `docs/AI_MCP_PLAN.md`: the 4-phase plan for AI on top of the commands (MCP
  server, add-in bridge, AI-assisted recognition with a data flywheel, ducts and
  route ranking). Not started; it lists the decisions the user still has to make.
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
| `HVACSTRETCH` | `cad_addin/src/HvacStretch.cs` | Mixed native/dynamic stretch from one AutoCAD object selection (crossing/window, polygon, picks, Shift-removal). Main current feature. |
| `HVACQTO` | `cad_addin/src/HvacQuantityTakeoff.cs`, `HvacQuantityTakeoffCore.cs`, `TakeoffSettingsForm.cs` | Exports the pickfirst set, or a new `GetSelection`, to a SpreadsheetML `.xml` workbook laid out like the VRV quotation template, with formulas and a contingency factor cell. |
| `HVACCOUNT` | `cad_addin/src/TQO.cs` | Totals `Line` and `Polyline` lengths on layers that start with `!` and contain `x` (case-insensitive). |
| `HVACDUCT` | `cad_addin/src/HvacDuctDraw.cs`, `DuctRouteSession.cs`, `DuctDrawForm.cs`, `DuctBlockLibrary.cs`, `DuctGeometryCore.cs` | Draws duct runs as `REC_WxH`/`Round D` dynamic blocks on a template system layer (`Next point or [Size/Undo] <length>:` loop with `PointMonitor` preview). Continues from a duct end, branches with a tap from a side wall, and generates elbows, transitions and taps as polylines tagged `HVACAI_FITTING`, shortening `Distance3` by the takeups. Missing sizes are imported or cloned from the template. Fitting formulas (SMACNA-based) are in `AGENTS.md`. Partly tested in AutoCAD. |
| `HVACDUCTALIGN` | `cad_addin/src/HvacDuctAlign.cs`, `DuctAligner.cs` | Revit-style Align: pick a reference edge (nested in a duct or a plain curve), then a parallel duct edge, which is moved to be collinear (`Multiple` keeps the reference). Coaxial facing ducts are joined without asking (merged, resized, or with an eccentric transition); an end face at right angles to a side wall gets a tap. |
| `HVACPIPESIZE` | `cad_addin/src/HvacPipeSize.cs`, `PipeDrawingLibrary.cs`, `PipeSizeForm.cs` | Pick a LINE/LWPOLYLINE; finds the pipe run (corners pass, T nodes and layer changes stop it), highlights it, and moves it to the chosen type and size layer by cutting curves with `GetSplitCurves`. Needs in-AutoCAD acceptance testing. |
| `HVACREFRIG` | `cad_addin/src/HvacRefrig.cs`, `RefrigWriter.cs`, `RefrigSettingsForm.cs`, `NetworkTopologyCore.cs`, `RefrigSizingCore.cs`, `RefrigCapacityReader.cs`, `RefrigSizeWriter.cs`, `RefrigSizingUi.cs`, `SizingForm.cs` | Cleans a rough refrigerant LINE/LWPOLYLINE network (weld, orthogonalise, collapse jogs, merge collinear nodes; pipe ends never move), previews it, and inserts `Branch Connection (1)` refnets. With "Size pipes" on (default) it then sizes the pipes by the Daikin VRV IV tables: it reads each indoor unit's capacity (xdata of an earlier run, a block name with `x kW`, or an `HVAC-NAME OF EQUIPMENTS` label), shows `SizingForm`, and on Apply puts each segment on its `EIA_1 RP` layer, stores the capacity xdata and writes `%%C<gas>/<liquid>` labels (text height is a setting, default 150 mm). Cancel in the dialog writes nothing. Needs in-AutoCAD acceptance testing. |
| `HVACDRAIN` | `cad_addin/src/HvacDrain.cs`, `DrainWriter.cs`, `DrainSizingUi.cs`, `DrainSettingsForm.cs`, `DrainSettingsCore.cs`, `DrainNetworkCore.cs`, `DrainGeometryCore.cs`, `DrainSizingCore.cs`, `DrainPlanCore.cs` | Straightens a rough condensate LINE/LWPOLYLINE network with the HVACREFRIG cleaner (no refnets), builds the tree toward the picked drain point, cuts 90 degree bends and right-angle T joints with 45 degree pieces (300 mm slant, the branch lands downstream of the T), sizes each pipe from the indoor-unit kW (Daikin Si39: D27 for one unit, D34 to D110 by total kW and slope, never smaller toward the drain) in the shared `SizingForm`, and writes the pipes on their `EIA_1 Pipe condensated DN<n>` layers with `D<size>-<slope>%` labels. Needs in-AutoCAD acceptance testing. |
| `HVACPOWERWIRE` | `cad_addin/src/HvacPowerWire.cs`, `PowerWireCore.cs`, `PowerWireWriter.cs`, `PowerWireSettingsForm.cs` | Redraws a rough trunk-and-branch tree of LINEs/LWPOLYLINEs as the indoor-unit series power wire: cleans it with the HVACREFRIG cleaner (no refnets), builds the tree from the picked supply end, replaces each branch junction by a hook (drop to the unit, 45 degree slant of 300 mm, rise back to the trunk) in series order, ends the last unit with only its drop leg, keeps a plain drop (circled, warned) where a hook does not fit, and writes one polyline per wire on `EIA_5 SUPPLY WIRE` (or `EIA_5 SIGNAL WIRE`) with optional `2x1.5 MM2` cable text beside each rise leg. Needs in-AutoCAD acceptance testing. |
| `HVACCTRLWIRE` | `cad_addin/src/HvacCtrlWire.cs`, `ControlWireCore.cs`, `ControlWireWriter.cs`, `ControlWireSettingsForm.cs` | Draws control wires from a thermostat to many indoor units from one template LINE/LWPOLYLINE route: each other selected unit gets its own polyline on `EIA_5 CONTROL WIRE`, parallel to the template at k x 150 mm (the wire that leaves first is outermost, so none cross), starting at its own offset position at the thermostat end, then leaving perpendicular at the route point nearest the unit's terminal point (the template unit's terminal carried onto units of the same block, else the insertion point). Optional `2x0.75 MM2` cable text beside each exit leg. Straight segments only (arcs refused). Needs in-AutoCAD acceptance testing. |

| `HVACROOMS` | `cad_addin/src/HvacRooms.cs`, `RoomsCore.cs`, `RoomsExchange.cs`, `RoomsWriter.cs`, `RoomsSettingsForm.cs`; back end `arch_rooms.py` | Semi-automatic room finding. The user window/crossing-selects one floor plan and ticks the wall layers (walls, columns, glass, doors, facade; suggested by name, remembered). The selection is `WblockCloneObjects`-copied to a DXF snapshot in `%TEMP%\HvacAiCadAddin\rooms\<time>\` with `request.json`, and `arch_rooms.py` runs as a separate `python.exe` process (repo `venv` by default; never inside AutoCAD). Python nodes and polygonizes the wall curves after replacing door swing arcs by chords, bridging facing wall-end caps up to 1800 mm and snapping 50 mm gaps; faces narrower than 400 mm (wall cavities) or under 1 m² are dropped; small faces with an X of two diagonals, or a HKT/gen/shaft text, are shafts. Preview as transients, then Yes writes closed LWPOLYLINEs on `HVAC-ROOM` (shafts colour 6) and MText "name / area m²" on `HVAC-ROOM-TEXT`, xdata `HVACAI_ROOM`, one undo group; a rerun erases earlier tagged rooms inside the area first. Lines on `HVAC-ROOM-SPLIT` always count as walls (to split open spaces). Pipeline verified headless in AutoCAD 2024 `accoreconsole`; dialog, preview and picking need in-AutoCAD testing. |

| `HVACLOAD` | `cad_addin/src/HvacLoad.cs`, `LoadCore.cs`, `LoadWriter.cs`, `LoadSettingsForm.cs`; back end `heat_load.py` + `heat_load_table.json` | Area-based cooling load (user decision: area × W/m² by room type × safety factor, no detailed method). Reads `HVAC-ROOM` polylines; Python classifies each room from its name (WC, storage, technical, stair, balcony, shaft are not cooled) and computes kW; a grid dialog lets the user change types and W/m² (optionally saved back to the table); Python recomputes with the overrides. Writes xdata `HVACAI_LOAD` [1000 type][1040 W/m²][1040 kW][1040 safety] on each room, adds/replaces a "x.x kW" line in the room label, and writes `<drawing>_HVACLOAD.csv`. One undo group. Not yet run in AutoCAD. |
| `HVACPLACEUNITS` | `cad_addin/src/HvacPlaceUnits.cs`, `UnitPlacementCore.cs`, `UnitPlacementWriter.cs`, `UnitPlacementForm.cs` | Chooses Daikin VRV cassettes per room from `HVACAI_LOAD` (smallest index covering the load within a 3% tolerance; above the largest allowed, default 16 kW, splits into equal units, max 6) and places them inside the room (pole of inaccessibility for one, slabs along the long axis for several; 600 mm wall clearance, 300 mm between units; rotation = dominant wall direction). Template blocks `FCU-CAS-<kW> kW` (indices 25-140; no 2.2/8.0 kW in the template; body 950 square, base point mid top edge, body centre at (0,-475)) on `EIA_1 MECHANICAL EQUIPMENT`, imported via `TemplateSource` or drawn as a fallback. Tags `HVACAI_PLACED` [1000 room id][1071 index][1040 kW][1070 n][1070 of]; a rerun replaces units of the same rooms. Writer verified headless; dialog and preview not. |
| `HVACROUTEDRAFT` | `cad_addin/src/HvacRouteDraft.cs`, `RouteDraftCore.cs`, `RouteDraftWriter.cs`, `RouteDraftForm.cs` | Draws rough orthogonal trees from selected unit blocks to a picked target or the nearest `HVAC-ROOM` shaft (`[Shaft]`), on `HVAC-DRAFT-REFRIG`/`-DRAIN`/`-POWER` with xdata `HVACAI_DRAFT`, for the user to run `HVACREFRIG`/`HVACDRAIN`/`HVACPOWERWIRE` on. Leaves end at the unit's refrigerant / drain port: insertion + R(rot)·S(scale, mirroring)·offset per block-name pattern. The template cassettes show no ports, so the `FCU-CAS`/`IU-CAS` defaults (refrigerant (475,-300), drain (475,-650), leaving +X) are ASSUMPTIONS to check; the power wire and unknown blocks use the insertion point. Default mode "corridor" when rooms exist: Dijkstra on a cost grid (200 mm, coarsened to 120000 cells; corridor/shaft 1, room 5, outside 3, wall crossing 2, bend 2; corridors = `HVACAI_LOAD` type lobby or names hành lang/sảnh/corridor/lobby/hall), grown from a ≥1500 mm target stub, no 4-way joints, branch starts kept ~2 m apart for refnets. Units in one room often chain inside the room. Older modes (best of trunk-and-branches and rectilinear Prim) remain and are the fallback without rooms. Outputs pass `RefrigCleaner`/`NetworkTopologyBuilder` in tests (about 120 ms for 30 units). |
| `HVACPLACEODU` | `cad_addin/src/HvacPlaceOdu.cs`, `OduCore.cs`, `OduWriter.cs`, `OduForm.cs` | Selects the indoor units of one VRV system (capacity from `HVACAI_PLACED`, a `n kW` block name or the `HVAC-NAME OF EQUIPMENTS` attribute), sums the Daikin indices and picks the outdoor HP with the existing `VrvTables` rules (smallest HP covering the total, ratio 50-130%). Above 54 HP (index 1350) it prints a balanced split proposal instead of splitting. Template blocks `ODU-6 HP`…`ODU-24 HP` (even HP only; nearest used with a warning; base point mid top edge) on `EIA_1 MECHANICAL EQUIPMENT`, with a label on `EIA_1 MECHANICAL EQUIPMENT ANNOTATIONS`. User picks the point (`Rotate90`); xdata `HVACAI_ODU` [1070 HP][1071 ODU index][1071 indoor index][1040 ratio %][1070 units]; `Replace` swaps an existing ODU. |
| `HVACRISER` | `cad_addin/src/HvacRiser.cs`, `RiserCore.cs`, `RiserWriter.cs` | Multi-floor refrigerant riser: per floor (farthest from the ODU first) the user selects that floor's units, picks its riser point (or `[Shaft]`) and names it; then picks the ODU or types its HP. Cumulative index per riser segment, sizes from `VrvTables.BetweenRefnets` capped by the main pipe (`MainByHp`, up-size asked when the estimated 90 m equivalent length is reached; floor height 3600 mm assumed, settable), refnets `FirstKit`/`LaterKit`. Writes circle + MText markers on `HVAC-RISER` at each floor and a schedule Table, xdata `HVACAI_RISER`; rerun with the same riser name replaces. Each floor's horizontal network is then run through HVACREFRIG with its root at that floor's riser point. |

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
- Curves are classified by layer, first match wins: `CHWS`/`CHWR`/`HWS`/`HWR`/
  `CWS`/`CWR` is steel, an `a/b` or `RP a-b` pair is copper, and `CONDENSATED` or a
  `D34`/`DN50` symbol is uPVC. Other layers are not exported and are reported on
  the command line. Curves tagged with the `HVACAI_FITTING` xdata app (fittings drawn
  by the duct commands) are skipped and only counted.
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
| Heat-load calculation | Area-based (`HVACLOAD`) | Area × W/m² × safety factor per room type; W/m² table is a placeholder pending the user's values. No envelope, orientation or design-condition model. |
| Rooms → units → rough routes | `HVACROOMS` → `HVACLOAD` → `HVACPLACEUNITS` → `HVACPLACEODU` → `HVACROUTEDRAFT` → `HVACRISER` → existing `HVACREFRIG`/`HVACDRAIN`/`HVACPOWERWIRE` → `HVACQTO` | Each step is a separate command with preview and one undo group. The user tested the first four in AutoCAD on 2026-11-22 (relatively few bugs, not listed yet). `HVACPLACEODU`, `HVACRISER` and the port/corridor routing were added the same day and are not yet tested interactively. |
| DXF validation / AI error detection | Rule-based foundation | Deterministic checks only. AI inference, labelled data, and CAD-facing review workflows are planned. |
| Room extraction (tầng 0) | Prototype + `HVACROOMS` | Geometry only. Real-drawing limits: open-plan spaces merge (use `HVAC-ROOM-SPLIT`), openings wider than 1800 mm or single-line walls leak, the room name is the longest text inside. `DT. x M2` labels in the sample drawing are whole-unit areas, so they only roughly score rooms. |
| Automated tests | Python, C# cores, headless duct | `tests/HvacQtoCoreTests/`, `tests/PipeCoreTests/`, `tests/DuctCoreTests/` and `tests/RoomsCoreTests/` cover the AutoCAD-free logic. `tests/DuctAcadTests/` runs non-interactive duct code in `accoreconsole`. Jigs, previews, osnap and picking have no automated tests, so commands must still be tested manually in AutoCAD. |

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
- Select objects once with `Editor.GetSelection`, so crossing/window by dragging,
  polygons, repeated selections, single picks and Shift-removal behave as in native
  STRETCH. Read back each object's selection area (crossing/window rectangle or
  polygon, from `CrossingOrWindowSelectedObject.GetPickPoints`) and replay the
  user's selection into native STRETCH: each area (`_C`, `_W`, `_CP`, `_WP`), then
  `_R` with `(handent ...)` for objects the areas would select but the user
  removed, then picked objects as `(handent ...)`. Users do not select ordinary
  entities twice. The previous single-Crossing-rectangle behaviour is tagged
  `hvacstretch-before-selection`.
- Do not use `Editor.Command` with a pre-supplied second point when preview is
  required. It completes STRETCH synchronously, leaving no rubber-band phase.
  `SendStringToExecute` stops after the base point so STRETCH owns the
  second-point prompt.
- Use `Editor.PointMonitor` for temporary dynamic-block preview. Save original
  property values and insertion points, restore them when STRETCH ends or
  cancels, then commit the final property change after a successful STRETCH.
- Choose the dynamic stretch axis by projecting the drag vector onto the block's
  local X and Y axes. Take bounds from the visible entities of the reference's
  current block record (its anonymous representation), measured in block
  coordinates and projected through `BlockTransform`
  (`DynamicBlockHelpers.TryGetDisplayedProjectionRange`). Do not use
  `BlockReference.GeometricExtents`: it includes the 41 invisible fixed 3000-long
  polylines in the `REC_`/`Round D` duct blocks, and is a world-aligned box that is
  too wide for rotated references. It is only the fallback when nothing visible can
  be measured. The previous behaviour is tagged `hvacstretch-before-s1`.
- Detect which end is crossed by projecting the rectangle of the area that selected
  the block (a polygon's bounding rectangle) and block extents onto the chosen axis.
  A block selected by a pick counts as fully crossed and is moved whole. After
  changing the distance property, align the unselected end back to its original
  projection to keep it fixed.
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

# Build the add-in (first installed of AutoCAD 2023, 2021, 2024, see cad_addin\AcadDir.props;
# AcadDir picks another install)
dotnet build cad_addin\HvacAiCadAddin.csproj --no-restore -p:Configuration=Debug -p:Platform=x64
dotnet build cad_addin\HvacAiCadAddin.csproj -p:Configuration=Debug -p:Platform=x64 "-p:AcadDir=C:\Program Files\Autodesk\AutoCAD 2024"

# Run the HVACROOMS / HVACLOAD / HVACPLACEUNITS / HVACROUTEDRAFT core tests (output in %TEMP%)
dotnet run --project tests\RoomsCoreTests\RoomsCoreTests.csproj -c Debug
dotnet run --project tests\LoadCoreTests\LoadCoreTests.csproj -c Debug
dotnet run --project tests\UnitCoreTests\UnitCoreTests.csproj -c Debug
dotnet run --project tests\RouteCoreTests\RouteCoreTests.csproj -c Debug
dotnet run --project tests\OduRiserCoreTests\OduRiserCoreTests.csproj -c Debug

# Tầng 0: automatic prototype on a whole DXF, or the HVACROOMS back end on a request
venv\Scripts\python.exe arch_extract.py --input plan.dxf --out-dir out\arch [--config c.json]
venv\Scripts\python.exe arch_rooms.py --request C:\tmp\rooms\request.json

# Run the HVACQTO core tests (build output goes to %TEMP%\HvacQtoCoreTests)
dotnet run --project tests\HvacQtoCoreTests\HvacQtoCoreTests.csproj -c Debug

# Run the pipe core tests (HVACPIPESIZE / HVACREFRIG; build output goes to %TEMP%\PipeCoreTests)
dotnet run --project tests\PipeCoreTests\PipeCoreTests.csproj -c Debug

# Run the duct core tests (HVACDUCT / HVACDUCTALIGN geometry; build output goes to %TEMP%)
dotnet run --project tests\DuctCoreTests\DuctCoreTests.csproj -c Debug

# Run the headless duct tests in accoreconsole on template copies (exit 0 = all pass)
powershell -File tests\DuctAcadTests\run-tests.ps1 [-TimeoutSec 600] [-NoBuild]
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

To inspect a DWG, convert a copy in a scratch folder to DXF with AutoCAD's core
console. It runs as a separate headless process and never touches a running
`acad.exe`; the 26 MB template converts in a few seconds. Run it from PowerShell,
because Git Bash may rewrite arguments that start with `/`.

```powershell
& "C:\Program Files\Autodesk\AutoCAD 2023\accoreconsole.exe" /i C:\scratch\in.dwg /s C:\scratch\dxfout.scr
```

`dxfout.scr` gives one response per line:

```
_.DXFOUT
"C:\scratch\in.dxf"
16
_.QUIT
_Y
```

### Running add-in code headless

Non-interactive add-in code (block import, `Distance3`, curve splitting, layer
import) can be tested in `accoreconsole` on a copy of a drawing. Jigs,
`PointMonitor`, transient graphics, osnap and typed input cannot; test those by
hand in AutoCAD.

- The test DLL may reference only `acdbmgd.dll` and `accoremgd.dll`, never
  `acmgd.dll`.
- Always pass `/isolate <userid> <folder>`. SECURELOAD and TRUSTEDPATHS are stored
  in the registry and shared with the user's AutoCAD, and `/isolate` redirects
  those writes. Even then, set `SECURELOAD 0` before `NETLOAD` and `SECURELOAD 1`
  before `_.QUIT`; otherwise the 0 persists in the isolated profile and the user's
  startup LISP loads.
- Wrap every `[CommandMethod]` body in try/catch. An unhandled exception kills
  `accoreconsole` and truncates the log.
- Stdout is UTF-16, so write results to your own log file.

```powershell
& "C:\Program Files\Autodesk\AutoCAD 2023\accoreconsole.exe" /isolate hvactest "C:\scratch\iso" /i C:\scratch\copy.dwg /s C:\scratch\run.scr /l en-US
```

## AutoCAD API facts verified on the template

Each of these was checked in `accoreconsole` on a copy of `CAD/MEP_DWT_3.dwg`.

- **Block names.** In a live database, the template's dynamic definitions have
  their real names, so `bt["REC_600x300"]` works and `DynamicBlockTableRecord.Name`
  is `REC_600x300`. Only DXF readers see `*U544`; they need the
  `AcDbDynamicBlockTrueName` xdata.
- **Duct blocks.** Every `REC_*`/`Round D*` block has one parameter, `Distance3`,
  along local +X from the base point (the duct start on the centreline).
  - The visible outline is one closed LWPOLYLINE, W wide (±W/2), where W is the
    first number in the name.
  - The block also holds 41 invisible polylines fixed at 3000 long, so do not use
    `GeometricExtents`.
  - 61 template inserts are mirrored (X scale -1).
- **Setting dynamic properties.** Call `AppendEntity`, then
  `AddNewlyCreatedDBObject`, then set the dynamic property. A property set
  before `AppendEntity` is silently ignored.
- **New duct sizes.**
  - Clone the master into a `new Database(true, true)` with `WblockCloneObjects`.
  - Rename it there; AutoCAD rewrites the TrueName xdata itself.
  - Move the visible outline's vertices with `SetPointAt`, keeping the vertex
    count and order.
  - Clone it back with `DuplicateRecordCloning.Ignore`.

  The copy stays dynamic and independent after save and reopen. Use
  `TemplateSource` (`cad_addin/src/TemplateImport.cs`) to import blocks and layers.
  Imported layers bring their custom linetypes and text styles with them.
- **Side databases.** Call `entity.SetDatabaseDefaults(db)` before setting
  properties on an entity meant for a side database. Setting `Layer` by name
  resolves against the working database and later fails with `eWrongDatabase`.
- **`GetSplitCurves`.**
  - The pieces keep layer, colour, linetype, xdata, elevation, widths and bulges.
  - The pieces are not in the database; append them yourself.
  - Sort the parameters first, or the pieces overlap.
  - `GetParameterAtPoint` throws unless the point is within about 1e-10 of the
    curve, so call `GetClosestPointTo` first.
- **`IntersectWith` is not usable for connectivity.**
  - Its gap tolerance grows with coordinate size: 1e-4 at the origin, 0.03 at the
    template's 4e6.
  - It returns no point for collinear touching or overlapping segments.
  - The point it returns lies on only one of the two curves.

  Use endpoint-distance checks with an explicit tolerance instead.
- **Undo.** One command is one undo group, however many transactions it commits.

## Known pitfalls

- **DWG input is rejected.** `parser.load_dxf` raises on `.dwg`, and ezdxf cannot
  read DWG either. Convert a copy to DXF first (see Commands).
- **`requirements.txt` is UTF-16 LE with a BOM.** Plain text tools and some editors
  show it garbled or with null bytes. Keep the encoding when editing it, or
  re-save it as UTF-8 deliberately.
- **Two Debug output folders.** The build above (`-p:Platform=x64`) writes to
  `cad_addin\bin\x64\Debug\net48\`, but `start.scr` loads
  `cad_addin\bin\Debug\net48\`, which is where a build without `-p:Platform` goes. Confirm which DLL AutoCAD loads, or a test can
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
- **`out/` is tracked.** `.gitignore` excludes `cad_addin/bin/`, `cad_addin/obj/`,
  `.tmp_test_runs/`, `__pycache__/`, and `venv/`, but the four sample reports in
  `out/` are still in git. Running the CLI overwrites them, so check `git status`
  and do not stage them by accident.
- **Machine-specific paths.** `start.scr` and the `.csproj` hard-code
  `C:\Users\Admin\hvac_ai`; the `.csproj` takes the AutoCAD folder from `AcadDir`
  (default 2023).
- **Windows consoles are cp1252.** Python that prints Vietnamese must call
  `sys.stdout.reconfigure(encoding="utf-8")` (the CLIs do), and `python -I` ignores
  `PYTHONIOENCODING`.
- **TCVN3 versus Unicode.** Unicode capitals such as `Ò`, `Á` sit in Latin-1, so a
  string is decoded as TCVN3 only when it also holds a code outside that set (or its
  font is `.Vn*`); see `tcvn3.looks_tcvn3`.

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
