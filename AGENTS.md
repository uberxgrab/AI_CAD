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
- The managed C# AutoCAD add-in project is present in `cad_addin/`. It targets
  AutoCAD 2023, .NET Framework 4.8, x64, and C# 7.3. It is not an ObjectARX/C++
  project.
- `HVACCOUNT` in `cad_addin/src/TQO.cs` totals `Line` and `Polyline` lengths
  on layers whose names start with `!` and contain `x`.
- `HVACSTRETCH` in `cad_addin/src/HvacStretch.cs` has been implemented as a
  mixed native/dynamic stretch command. It accepts one Crossing rectangle,
  uses AutoCAD's real `STRETCH` for ordinary entities, and applies a dynamic
  block distance-property change when exactly one end of a supported dynamic
  block is crossed.
- `HVACQTO` exports a quantity takeoff to an Excel-compatible SpreadsheetML
  `.xml` workbook laid out like the VRV quotation template. The user considered
  it largely complete on 2026-11-10. See "`HVACQTO` quantity takeoff" below.
- `YOURSTRETCH` has been removed. `cad_addin/src/NativeStretch.cs` now holds an
  empty `NativeStretch` class with no command.
- On 2026-11-14 `HVACQTO` gained Excel formulas, shaft risers, and a
  contingency factor. See "Formulas, shaft risers, and contingency factor" below.
- `tests/HvacQtoCoreTests/` tests the AutoCAD-free `HVACQTO` logic. On
  2026-11-14 it ran 19,170 checks (6,390 under each of en-US, vi-VN, and tr-TR)
  with 0 failures.

### Current status by workstream

| Workstream | Status | Current capability / limitation |
| --- | --- | --- |
| CAD drafting tools | Active | `HVACSTRETCH` is the main drafting feature. |
| Dynamic block stretching | Implemented; requires in-AutoCAD acceptance testing | Supported property names are `Distance3`, `Distance`, `Length`, and `Width`; the property must be numeric, writable, and backed by a Dynamic Block Stretch Action. |
| Native STRETCH preview | Implemented; requires in-AutoCAD acceptance testing | `SendStringToExecute` sends STRETCH, the stored Crossing window, and base point only. AutoCAD owns the interactive second-point prompt for its normal preview; `PointMonitor` previews eligible dynamic blocks at the same time. |
| Quantity takeoff | `HVACQTO` largely complete | `HVACQTO` exports equipment, duct sheet area by thickness, and pipes. `HVACCOUNT` covers line/polyline lengths only. Valve takeoff and attribute-based equipment takeoff are planned. There is no common MEP object/quantity schema yet. |
| Heat-load calculation | Planned | No area/room extraction, design conditions, thermal assumptions, or calculation engine has been implemented. |
| DXF validation / AI error detection | Rule-based foundation present | The Python pipeline performs deterministic checks. AI-based inference, training/evaluation data, and CAD-facing issue review workflows are still planned. |
| Automated tests | Python and `HVACQTO` core | `tests/test_pipeline.py` covers the Python pipeline. `tests/HvacQtoCoreTests/` covers the AutoCAD-free `HVACQTO` logic and workbook output. There are no automated AutoCAD integration tests; add-in commands must be tested manually in a real AutoCAD session. |

### Next steps

1. Add a water-valve takeoff command. See "Planned: valve and attribute-based
   equipment takeoff" for findings and open questions. It needs an approved plan.
2. Add attribute-based equipment takeoff, so equipment descriptions come from
   block attributes instead of block names. It needs an approved plan.
3. Confirm the remaining `HVACQTO` items in a real drawing: real duct block names,
   the `Table` skip, the command-line report, and the settings dialog layout
   (shaft grid, remembered values).
4. Manually test `HVACSTRETCH` after `NETLOAD`ing the rebuilt x64 DLL. Test
   horizontal, vertical, and rotated blocks; each end independently; full
   block crossing; mixed ordinary/dynamic selections; Esc; and typed versus
   clicked second points.
5. If a particular dynamic block does not respond, verify in Block Editor that
   `Distance3` has a Linear parameter and associated Stretch Action. Record
   each supported block's property name and parameter direction.
6. Harden `HVACSTRETCH` from the manual-test findings: in particular, handle
   typed second-point input if `PointMonitor` does not provide a final cursor
   point, property limits/lookup tables, and dynamic parameters not aligned to
   the block's local X/Y axes.
7. Define a common MEP object/quantity schema before expanding takeoff further
   to fittings, insulation, and report exports.
8. Specify the heat-load input model (rooms/areas, orientations, envelope,
   occupancy, lighting, ventilation, and weather/design conditions), then add
   calculation tests before exposing an AutoCAD command.
9. Establish labelled drawing-error examples and evaluation criteria before
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

## `HVACQTO` quantity takeoff

### Code layout

| File | Role |
| --- | --- |
| `cad_addin/src/HvacQuantityTakeoff.cs` | AutoCAD side only: command, selection, reading the database into plain inputs, dialog, save prompt, command-line report. |
| `cad_addin/src/HvacQuantityTakeoffCore.cs` | All logic, with no `Autodesk.*` reference. Namespace `HvacAiCadAddin.Takeoff`. Entry points are `TakeoffAggregator.Aggregate`, `TakeoffWorkbookWriter.Write`, and `TakeoffClassifier`. |
| `cad_addin/src/TakeoffSettingsForm.cs` | WinForms dialog "Thiết lập bóc khối lượng": contingency factor, sheet thickness bands, shaft risers. It remembers the last accepted values in `%APPDATA%\HvacAiCadAddin\HVACQTO.settings`. |
| `tests/HvacQtoCoreTests/` | Console test project that links the Core file directly. |

Keep this split for new takeoff commands. Put rules in an AutoCAD-free file so
they can be tested without AutoCAD, and keep the AutoCAD file a thin reader.

### Behavior

- **Selection:** `[CommandMethod("HVACQTO", CommandFlags.UsePickSet)]`. Uses the
  pickfirst set from `SelectImplied()`, or else `GetSelection()` filtered to
  `INSERT,LINE,LWPOLYLINE,POLYLINE`. There is no Crossing window or corner prompt.
- **Order:** selection, then the settings dialog, then the save prompt, then
  reading and writing. Esc or Cancel at any step writes nothing.
- **Pipes**, by layer, first match wins, case-insensitive:

  | Priority | Layer contains | Output text |
  | --- | --- | --- |
  | 1 | `CHWS`, `CHWR`, `HWS`, `HWR` | `Ống thép hàn mạ kẽm DN200, SCH40` |
  | 2 | `a/b` pair, or `RP a-b` pair | `Ống đồng D15.9 dày 1.0mm, kèm bảo ôn 19mm` |
  | 3 | `CONDENSATED`, or a `D34`/`DN50` symbol | `Ống thoát nước ngưng uPVC Class 1 D27, kèm bảo ôn 13mm` |
  | - | anything else | Not exported; counted and listed on the command line |

  - Copper: text in parentheses is ignored. Each entity adds its full length to
    both sizes. Wall thickness is 0.8mm for D6.4 and D9.5, and 1.0mm for D12.7 to
    D41.3. Other sizes omit the thickness.
  - A hyphen pair counts as copper only after `RP` as its own word, so `15.9-9.5`
    and `GRP 15.9-9.5` are not copper.
  - A `D`/`DN` symbol must not touch a letter or digit on either side, so `D34`
    and `PIPE_D34` match, but `DRAIN`, `DIM`, `ADD34`, and `D34A` do not.
  - Steel always prints as DN and uPVC as D. A layer with no size keeps its row,
    shows the layer name, and never gets an invented size.
- **Ducts:** a dynamic block with a positive `Distance3` and a size in its name.
  The size is either exactly two dimensions (`600x300`) or `Ø`/`D`/`DN` plus a
  number. Three-dimension names (`Sa box-500x400x500`) and bare numbers
  (`FCU-2.8 kW`) are not ducts.
- **Sheet gauge:** four editable bands. The dialog shows the last accepted values,
  or these defaults on first use:

  | From (mm) | To (mm) | Thickness (mm) |
  | --- | --- | --- |
  | 100 | 550 | 0.58 |
  | 600 | 1200 | 0.75 |
  | 1250 | 1950 | 0.95 |
  | 2000 | none | 1.15 |

  The deciding size is the largest side for rectangular ducts and the diameter for
  round ducts. A size in a gap (for example 575) takes the next thicker band. A
  size below 100 goes to the first band and is reported as a warning.
- **Equipment:**

  | Block | Output | Unit |
  | --- | --- | --- |
  | `ODU-6 HP` | `Dàn nóng ODU 6 HP` | `bộ` |
  | `FCU-CAS-16 kW` | `Dàn lạnh VRV Cassette âm trần Inverter 16 kW` | `bộ` |
  | `Water Cooled Chiller - 420 RT` | `Chiller giải nhiệt nước:` and `- Công suất lạnh: 420 RT` | `bộ` |
  | `HVAC-NAME OF EQUIPMENTS`, `TITLE` starting `CHWP` or `CWP` | `Bơm tuần hoàn nước lạnh:` or `Bơm tuần hoàn nước giải nhiệt:`, with flow and head lines | `bộ` |
  | Anything else | Block name, plus attributes and dynamic properties except `Origin` | `cái` |

  - If the selection has a pump label, every `HVAC_PUMP` block is excluded, so
    pumps are not counted twice.
  - `FCU-2.2 kW`, `IDU-160000 BTUH`, `AHU-01`, and `TRANE_...` deliberately stay
    as fallback rows, because the templates have no confirmed wording for them.
- **Workbook:** a main sheet `DIEU HOA THONG GIO` with groups `I.1 Thiết bị`,
  `I.2 Ống gió`, and `I.3 Ống nước`, plus an appendix `PHU LUC ONG GIO`.
  - I.2 always has exactly four sheet-area rows, such as
    `Ống gió tôn mạ kẽm 0.58mm` in m2, including zero rows.
  - The appendix lists each duct size with count, total length, and area only.
  - Only description, unit, and quantity are filled. Prices, product codes,
    notes, project details, and the Chinese column stay empty.

### Formulas, shaft risers, and contingency factor

Approved by the user on 2026-11-14.

- **Contingency factor:** entered in the dialog, default 1.0. It is written to
  `F6` of `DIEU HOA THONG GIO` (label `Hệ số dự phòng:` in `D6:E6`, fill `FFF2CC`).
  - Every duct-band and pipe quantity multiplies by that cell, so editing `F6` in
    Excel updates the BOQ.
  - Equipment counts and the appendix are not multiplied; the appendix stays raw
    for checking against the drawing.
- **Formulas:** written in R1C1 notation (`ss:Formula`), each with a cached value
  so the file shows numbers before Excel recalculates.

  | Cell | Formula |
  | --- | --- |
  | Pipe row, main sheet | `=(12.345+3.2+3.5*2)*R6C6` |
  | Appendix length | `=2+1.5+3*1` |
  | Appendix area, rectangular | `=2*(0.6+0.3)*RC[-1]` |
  | Appendix area, round | `=PI()*0.25*RC[-1]` |
  | Band row, main sheet | `=('PHU LUC ONG GIO'!R2C4+'PHU LUC ONG GIO'!R5C4)*R6C6` |

  - Each drawn segment is one addend, in metres rounded to 3 decimals. Copper pairs
    add the same addend to both sizes.
  - A band with no duct size writes a plain 0 without a formula.
- **Shaft risers:** rows of kind, size, height (m), and number of runs, for pipes
  the drawing does not show.
  - Kinds are `Ống gió`, `Ống thép`, and `Ống nước ngưng` only; copper is not
    offered.
  - Sizes: `600x300` or `D250` for ducts, and `100`, `DN100`, `D34`, or `Ø34` for
    pipes.
  - A shaft run adds the addend `height*runs` to the row with the same
    description, or creates that row. Shaft ducts also count in the appendix and
    their thickness band.
- **Long formulas:** Excel rejects formulas over 8,192 characters. When a row's
  addends exceed 8,000 characters, its segments go to a third sheet
  `PHU LUC DOAN ONG`, one per row (`Đoạn n`, shaft rows `Trục kỹ thuật`), and the
  quantity becomes `SUM` over that range. The sheet appears only when needed.
- **Excel check:** the `--emit` option of the test project writes `formulas.xml`
  and `long.xml`. On 2026-11-14 both opened in real Excel; computed values matched
  and changing `F6` updated every quantity.

### Decisions made with the user

- **Format:** the export stays SpreadsheetML `.xml`, not `.xlsx`.
- **Copper:** `RP a-b` is accepted in addition to `a/b`. The user first chose `/`
  only, but AutoCAD layer names cannot contain `/`, so the real layers
  (`EIA_1 RP 15.9-9.5 (14)`) would never have matched.
- **Steel:** SCH40 is used for every size, following `chiller.xlsx`.
- **Units:** `bộ` and `cái` without Chinese text. Column headers stay bilingual as
  in the template. The VRV layout has no brand column, so none was added.
- **Template use:** the layout follows `C:/Users/Admin/Desktop/vrv.xlsx`. The
  chiller, pump, and steel wording follows `C:/Users/Admin/Desktop/chiller.xlsx`.
  Neither file is in the repo. Formatting is written in code, so the command
  never reads these files and copies no project name, price, or exchange rate.
- **Template formatting:**
  - Font: Times New Roman 12.
  - Fills: header `DAF2D0`, header `M7:N8` `E2ECF8`, section row `F2DCDB`, group
    rows `DCE6F1`, closing row `E6B8B7`.
  - Columns A to O only. SpreadsheetML `ss:Width` is the template width times 5.25.
  - Row height grows at about 40 characters per line in column B.

### Known limitations

- **Pump numbers:**
  - A comma before exactly three digits is a thousands separator, so `10,050 LPM`
    gives 10050. A side effect is that `0,125 mH2O` gives 125.
  - A dot is always a decimal point, so `10.050 LPM` still gives 10.05.
- **Pump head unit:** both `mH2O` and the drawing's typo `mH20` are read as head.
- **Sorting:** CWP pumps sort before CHWP pumps, because both share one rank and
  sort by text.
- **Control characters:** a control character in a block name or attribute makes
  the XML writer throw, so the export stops at `Could not write workbook`.
- **`MInsertBlock`:** it also derives from `BlockReference` and is read as a
  normal block.

## Planned: valve and attribute-based equipment takeoff

Both are new commands, so present a plan and get approval first. Reuse the
`HVACQTO` split: AutoCAD reader, AutoCAD-free core, and console tests.

### What the project drawing shows

These facts come from `test.dxf`. Its model space has only 47 anonymous block
inserts and no curves, so it is not an end-to-end fixture. Use the user's template
`CAD/MEP_DWT_3.dwg` (converted to DXF) or a real project drawing for that.

- **Valve and accessory block definitions:** `HVAC_GATE VALVE (NORMAL OPEN)`,
  `HVAC_GATE VALVE (NORMAL CLOSED)`, `HVAC_BUTTERFLY VALVE`,
  `HVAC_BALANCING VALVE`, `HVAC_NON RETURN VALVE`, `HVAC_BALL VALVE`,
  `HVAC_GLOBLE VALVE`, `HVAC_SAFETY VALVE`, `HVAC_Y STRAINER`,
  `HVAC_FLEXIBLE CONECTOR`, `HVAC_AIR VENT VALVE`,
  `HVAC_AUTOMATIC AIR VENT VALVE`, `HVAC_2 WAYS ELECTRICAL MODULATING VALVE`,
  `HVAC_2 WAYS ELECTRICAL MODULATING BUTTERLY VALVE`,
  `HVAC_2 WAYS ELECTRICAL MOTORIZED VALVE ON OFF`,
  `HVAC_2 WAYS ELECTRICAL MOTORIZED BUTTERFLY VALVE ON OFF`,
  `HVAC_PRESSURE REGULATING VALVE`, `HVAC_DIFFERENCE PRESSURE CONTROL VALVE`,
  `HVAC_FLOW SWITCH`, `HVAC_PRESSURE GAUSE`, `HVAC-TEMPERATURE GAUGE1`,
  `HVAC_TEST PLUG`.
  - The misspellings (`GLOBLE`, `CONECTOR`, `BUTTERLY`, `GAUSE`) are the real
    names. Match them as written.
- **Valve size:** none of these valve blocks has an attribute definition, so the
  block alone does not give a DN. Deciding where DN comes from is the main open
  question. Candidates are the pipe layer under the insertion point (for example
  `EIA_1 CHWS DN100`), the valve's own layer, or a dynamic property.
- **Blocks with attribute definitions:** only 9 exist:

  | Block | Tags |
  | --- | --- |
  | `HVAC-NAME OF EQUIPMENTS` | `TITLE`, `PROPERTIES` |
  | `FAN NAME` | `PROPERTIES`, `TITLE` |
  | `FAN NAME 02` | `PROPERTIES`, `TITLE`, `POWER` |
  | `FAN NAME 03` | `PROPERTIES`, `TITLE`, `DIMENSION`, `POWER` |
  | `Text louver` | `LEGEND`, `NAME` |
  | `CLEAN ROOM_DYNAMIC BLOCK` | `PRESSURE`, `CLASS`, `HUMIDITY`, `TEMP` |
  | `CAP NGUON` | `ONG-LUON-DAY`, `CAP-DIEN`, `APTOMAT` |
  | `LEVEL 2` | `CAODO`, `FL` |
  | `BMS point` | `POINT` |

  - Equipment data often sits in a separate label block next to the symbol, not
    on the symbol itself. For example, `TITLE=CHWP - 02` with
    `PROPERTIES=40mH20 - 10050 LPM`.
  - Counting both the label and the symbol double-counts, which is why `HVACQTO`
    excludes `HVAC_PUMP` when a pump label is present.

### Candidate wording from `chiller.xlsx`

This mapping is not approved. Confirm each pair with the user.

| CAD block | Template wording | Unit |
| --- | --- | --- |
| `HVAC_GATE VALVE ...` | `Van cổng đồng DN#` | `Cái` |
| `HVAC_BUTTERFLY VALVE` | `Van bướm tay gạt gang đúc DN#-PN16`, or `Van bướm tuabin gang đúc DN#-PN16` for gear-operated | `Cái` |
| `HVAC_BALANCING VALVE` | `Van cân bằng cơ DN#` | `Cái` |
| `HVAC_NON RETURN VALVE` | `Van một chiều gang đúc DN#` | `Cái` |
| `HVAC_FLEXIBLE CONECTOR` | `Khớp nối mềm cao su giảm chấn DN#` | `Cái` |
| `HVAC_AUTOMATIC AIR VENT VALVE` | `Van xả khí tự động DN#` | `Cái` |
| `HVAC_2 WAYS ELECTRICAL MODULATING VALVE` | `Van điện hai chiều đếm tỷ lệ DN# (Tuyến tính)` | `Cái` |
| `HVAC_2 WAYS ELECTRICAL MOTORIZED VALVE ON OFF` | `Van điện hai chiều DN# (on/off)` | `Cái` |
| `HVAC_PRESSURE GAUSE` | `Đồng hồ đo áp suất giảm chấn Inox 0~1.0MPA, mặt số 100, có ống đệm` | `Cái` |
| `HVAC-TEMPERATURE GAUGE1` | `Nhiệt kế lưỡng kim inox 0~50℃, mặt số 100` | `Cái` |
| `HVAC_FLOW SWITCH` | `Công tắc dòng chảy` | `Cái` |

- The template groups valves under headings such as `Van và phụ kiện Tầng ...`.
- No template wording was found for `HVAC_Y STRAINER`.
- **Chiller line pattern:** `Chiller giải nhiệt nước:` then `- Máy nén:`,
  `- Công suất lạnh:`, `- Hệ số COP:`, `- Khởi động:`, `- Môi chất lạnh:`,
  `- Nhiệt độ nước lạnh:`, `- Nhiệt độ nước giải nhiệt:`.
- **Pump line pattern:** `- Lưu lượng nước: N (LPM)`, `- Cột áp: N (mH2O)`,
  `- Động cơ:`, `- Khởi động:`.
- **Cooling tower:** starts with `Tháp giải nhiệt dạng hở:`.
- **Rule for all of these:** only print lines whose values exist in the drawing.
  Never invent COP, refrigerant, motor, or start method.

## AutoCAD add-in development notes

- **Two Debug output folders:**
  - `dotnet build ... -p:Platform=x64` writes `cad_addin\bin\x64\Debug\net48\`.
  - A build without `-p:Platform` writes `cad_addin\bin\Debug\net48\`, which is
    the folder `start.scr` loads.
  - Check the DLL timestamp before testing, or a test can silently run an old
    build.
- **Reloading the add-in:** AutoCAD keeps a `NETLOAD`ed assembly until it exits,
  so restart AutoCAD before loading a rebuilt DLL.
- **Pickfirst set:** a command that reads it needs `CommandFlags.UsePickSet`.
  Without the flag, AutoCAD clears the set before `SelectImplied()` runs.
- **Tables:** `Table` derives from `BlockReference`, so `entity as BlockReference`
  also accepts tables. Skip them explicitly.
- **Symbol names:** layer and block names cannot contain `<`, `>`, `/`, `\`,
  `"`, `:`, `;`, `?`, `*`, `|`, `=`, or a backtick. Do not design rules around
  those characters.
- **Language and packages:** stay within C# 7.3 and .NET Framework 4.8, and add
  no NuGet packages. `System.Windows.Forms` and `System.Drawing` are referenced
  for dialogs. Show dialogs with
  `Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog`.
- **SDK glob:** the add-in is an SDK-style project, so it compiles every `.cs`
  under `cad_addin/`. Keep test projects outside `cad_addin/`.
- **Encoding:** keep source files as UTF-8 without BOM and with LF endings.
  Output text includes Vietnamese and Chinese.
- **Debugging:**
  - Antigravity, a VS Code fork, cannot use Microsoft's C# Dev Kit or the
    `vsdbg` debugger, because their licences limit them to Microsoft products.
  - The community `netcoredbg` debugger is documented for .NET Core, not
    .NET Framework.
  - To debug the add-in, attach official VS Code (Microsoft C# extension) or
    Visual Studio to `acad.exe` with a `"type": "clr"` attach configuration. Do
    not use `coreclr`.
  - This setup has not been tried on this machine, and the repo has no
    `.vscode/launch.json`.
  - Only Visual Studio Build Tools 2026 is installed, not the full Visual Studio
    IDE.

## Working with templates and drawings

- **Reading `.xlsx`:** `openpyxl` is not in `venv`. Read templates with the
  standard library (`zipfile` plus `xml.etree`), and run
  `venv\Scripts\python.exe -I` from a scratch folder. Do not add a dependency just
  to read a template.
- **Theme colours:** template fills often use a theme index plus tint, not RGB.
  Resolve them against `xl/theme/theme1.xml`.
- **Desktop files:** treat the templates and other files on the Desktop as
  read-only inputs.
- **Excel through COM:** create your own Excel instance and quit only that
  instance. Never attach to or close the user's running Excel or AutoCAD.
- **Real layer names** in the project drawing look like `EIA_1 CHWR DN100`,
  `EIA_1 Pipe condensated DN27`, and `EIA_1 RP 28.6-12.7 (33.6-44.8)`. Use real
  names as test fixtures.

## Agent workflow notes

- **Session lifetime:** background agents stop when the Claude Code session
  ends, for example on closing the tab or reloading the window. Their transcript
  is kept, so resume them instead of starting over.
- **Independent tests:** a test agent should derive expected results from the
  approved spec, not from the implementation. When a test fails, check whether
  the code or the spec is wrong before changing the expectation.
- **After a fix:** rerun the full suite yourself instead of relying on an agent's
  report.
- **Process safety:** never kill processes you did not start. `acad.exe` and
  `EXCEL.EXE` are usually the user's.

## Project Structure & Module Organization

The Python HVAC DXF-validation pipeline is at the repository root. `main.py` is
the CLI entry point; `parser.py`, `geometry.py`, `rules.py`, `models.py`, and
`reporting.py` implement loading, analysis, rule checks, data models, and
exports. Default thresholds are in `config.json`. Keep representative CAD input
files in `CAD/` or as explicitly named root fixtures; generated reports and
annotated drawings belong in `out/`.

Python tests live in `tests/test_pipeline.py`, and `HVACQTO` core tests live in
`tests/HvacQtoCoreTests/`. The managed C# AutoCAD add-in is deliberately isolated
in `cad_addin/`: source is in `cad_addin/src/`, and build and load steps are in
`cad_addin/README.md`. `cad_addin/python_bridge.py` is a read-only discovery
helper. It still looks for an `.arx` file, so `--cad-status` reports
`built_addin: null`.

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

When a rule depends on drawing data, check a real drawing before writing the plan.
Real data changed several `HVACQTO` rules: hyphenated copper layers, pump data in
label blocks, and the `mH20` typo.

## Build, Test, and Development Commands

Use the checked-in virtual environment on Windows, or create an equivalent one
and install `requirements.txt`:

```powershell
venv\Scripts\python.exe -m unittest discover -s tests -v
venv\Scripts\python.exe main.py --input test.dxf --report out/report.json --overlay out/annotated.dxf --config config.json
venv\Scripts\python.exe main.py --cad-status
dotnet build cad_addin\HvacAiCadAddin.csproj --no-restore -p:Configuration=Debug -p:Platform=x64
dotnet run --project tests\HvacQtoCoreTests\HvacQtoCoreTests.csproj -c Debug
```

1. Run the Python pipeline tests.
2. Validate a drawing and write JSON/JSONL outputs plus an annotated DXF.
3. Report add-in discovery status.
4. Build the add-in into `cad_addin\bin\x64\Debug\net48\`.
5. Run the `HVACQTO` core tests. Their build output goes to
   `%TEMP%\HvacQtoCoreTests`, so no `bin/` or `obj/` appears in the repo. The run
   exits non-zero if any check fails.

## Coding Style & Naming Conventions

Use Python 4-space indentation, `snake_case` functions and variables,
`PascalCase` classes, and type annotations where practical. Keep modules small
and use `pathlib.Path` for file paths. Preserve the existing standard-library
`unittest` style; no formatter or linter is currently configured. In the add-in,
use C# 7.3 and keep AutoCAD API calls inside `cad_addin/src/`, with logic in
AutoCAD-free files where it can be tested.

## Testing Guidelines

Add focused `unittest` methods named `test_<behavior>` in `tests/`. Cover both
rule behavior and end-to-end CLI outputs when changing pipeline logic. Tests
create temporary data under `.tmp_test_runs/`; do not commit those artifacts.
Run the full discovery command before submitting changes.

For add-in logic:

- Add named checks to `tests/HvacQtoCoreTests/`, or to a similar console project
  for a new command.
- Use real layer and block names as fixtures, and cover boundary values.
- Parse generated workbooks back as XML to check the output.
- Build the add-in, then give the user manual AutoCAD test steps. A successful
  build does not prove that a command works.

## Commit & Pull Request Guidelines

No accessible repository history establishes a commit convention. Use concise,
imperative subjects such as `Add elbow overlap rule`. Keep commits scoped.
PRs should explain the changed validation behavior, list test commands run, link
the relevant issue when applicable, and include before/after report or overlay
samples for CAD-analysis changes. Do not commit local CAD installs, `venv/`, or
generated `out/` files unless intentionally updating a fixture.

There is no `.gitignore`. `cad_addin/bin/`, `cad_addin/obj/`, and `out/` are
tracked, and `.tmp_test_runs/` files have been staged by accident before. Check
`git status` and the staged list before committing.
