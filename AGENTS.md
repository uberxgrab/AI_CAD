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
  mixed native/dynamic stretch command. It selects objects with AutoCAD's own
  selection prompt (crossing/window by dragging, polygons, several selections,
  picks, Shift-removal), uses AutoCAD's real `STRETCH` for ordinary entities, and applies a dynamic
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
| Native STRETCH preview | Implemented; requires in-AutoCAD acceptance testing | `SendStringToExecute` sends STRETCH, the replayed selection (areas, removals, picks), and base point only. AutoCAD owns the interactive second-point prompt for its normal preview; `PointMonitor` previews eligible dynamic blocks at the same time. |
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
- Select objects once in `HVACSTRETCH` with `Editor.GetSelection` (message
  "Select objects to stretch by crossing-window or crossing-polygon..."), so
  selection behaves like native STRETCH: implied crossing/window by dragging,
  `CP`/`WP` polygons, repeated selections, single picks, and Shift-removal. Read
  each object's selection area back from `CrossingOrWindowSelectedObject.
  GetPickPoints` (a plain rectangle comes back as four corners, a polygon as its
  vertices) and replay the user's selection into native STRETCH: each area
  (`_C`, `_W`, `_CP`, `_WP`), then `_R` plus `(handent "<handle>")` for objects
  the areas would select but the user removed, then `_A` and `(handent ...)` for
  picked objects. This avoids making users select ordinary entities a second
  time. Each dynamic block is judged against the rectangle of the area that
  selected it (a polygon's bounding rectangle); a picked block counts as fully
  crossed and is moved whole. The previous single-Crossing-rectangle behaviour
  is tagged `hvacstretch-before-selection`. Whether `(handent ...)` is accepted at
  STRETCH's "Select objects:" prompt is the first thing to test manually.
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
- Detect whether the start or end is crossed by projecting both the
  selection-area rectangle and block extents onto the chosen local axis. After changing the
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
  | 1 | `CHWS`, `CHWR`, `HWS`, `HWR`, `CWS`, `CWR` | `Ống thép hàn mạ kẽm DN200, SCH40` |
  | 2 | `a/b` pair, or `RP a-b` pair | `Ống đồng D15.9 dày 1.0mm, kèm bảo ôn 19mm` |
  | 3 | `CONDENSATED`, or a `D34`/`DN50` symbol | `Ống thoát nước ngưng uPVC Class 1 D27, kèm bảo ôn 13mm` |
  | - | anything else | Not exported; counted and listed on the command line |

  Curves carrying the `HVACAI_FITTING` xdata app (fittings drawn by `HVACDUCT`/
  `HVACDUCTALIGN`) are skipped before classification: they are not taken off and not
  reported as unexported; the command line shows only their count.

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

## Drafting commands: `HVACDUCT`, `HVACDUCTALIGN`, `HVACPIPESIZE`, `HVACREFRIG`

The user approved this spec on 2026-11-18. Read the "AutoCAD API facts verified on
the template" section of `CLAUDE.md` first. Units are drawing millimetres.

### Shared rules

- **File layout.** Follow the `HVACQTO` split.
  - **AutoCAD-free core:** plain doubles only, no `Point3d`; tested by a console
    project under `tests/<Name>/`, built like `tests/HvacQtoCoreTests`.
  - **Non-interactive AutoCAD classes:** need only `acdbmgd`/`accoremgd`, so a
    headless `accoreconsole` test DLL can link them.
  - **Thin command classes:** prompts, dialogs, jig or preview.
- **Template.** Blocks and layers come from `CAD/MEP_DWT_3.dwg` through
  `TemplateSource` and `TemplateLocator` (`cad_addin/src/TemplateImport.cs`). Never
  hard-code the path.
- **Fitting tag.** Generated fittings are closed LWPOLYLINEs on the duct's system
  layer, with colour and linetype ByLayer. They are not blocks.
  - Tag every one with xdata: registered app `HVACAI_FITTING`, then 1000 = the kind
    (`ELBOW`, `TRANSITION`, `TAP`), then 1000 = a size text.
  - `HVACQTO` will skip curves that carry this app.
- **Duct references.** A duct is a `BlockReference` whose
  `DynamicBlockTableRecord` name is `REC_<W>x<H>` or `Round D<d>`.
  - Start = `Position`.
  - Axis = `BlockTransform` X axis, normalised. This also holds for mirrored
    inserts.
  - Length = `Distance3` × X scale length.
  - Plan width = W or d.
  - Use `DynamicBlockHelpers` to get and set `Distance3` and to measure bounds.
- **Undo.** Each command is one undo group. Esc must leave nothing half-written.
- **Do not edit** `HvacQuantityTakeoff*.cs`, `HvacStretch.cs`, `TakeoffSettingsForm.cs`
  or `TQO.cs`; the coordinator owns those.
  - `DynamicBlockHelpers.cs` and `TemplateImport.cs` may only gain new members;
    keep existing signatures and behaviour.
  - Only the pipe stream may add to `TemplateImport.cs`.
- **Settings.** Dialogs remember their last values in
  `%APPDATA%\HvacAiCadAddin\<COMMAND>.settings`, like `TakeoffSettingsForm`.

### Fitting dimensions (defaults; all adjustable in the dialogs)

W is the plan width and d the round diameter. ΔW and ΔH are the size changes.

| Fitting | Rule |
| --- | --- |
| Rectangular elbow | Centreline R = 0.75·W (inner 0.25·W, outer 1.25·W); the user's template habit. Takeup per leg = R·tan(θ/2). Outline: end face, outer arc, end face, inner arc. If R < W/2, the inner radius is 0 (square throat). |
| Round elbow | R = 1.5·d, drawn as a smooth arc outline in plan. Also draw the centreline arc like the `Round D` blocks (linetype and colour of the block's centreline). |
| Concentric transition | L = max(W_large/2, 1.207·ΔW, 1.207·ΔH). SMACNA 1995 Fig 2-7 limits the included angle to 45° diverging and 60° converging; 1.207 is the stricter 45° case. Outline: a trapezoid centred on the axis. |
| Eccentric transition (align) | L = max(W_large/2, 1.732·ΔW). Fig 2-7: 30° max from the flat side, which lies on the aligned edge. |
| Rectangular branch (shoe tap) | 45° lead-in L = max(Wb/2, 102). SMACNA Fig 2-6 gives W/4 with a 4" minimum, W being the branch width; the user's habit is Wb/2. The opening on the main wall is Wb + L, with the 45° side upstream and the downstream side square. The branch duct starts L from the main wall; there is no extra neck. |
| Round branch (conical tap) | Cone base d+51 on the main wall, narrowing to d over 152, then a 51 collar. The branch duct starts 203 from the wall. |
| Rectangular-to-round | Same as a concentric transition, with d in place of the width. |

If a straight duct would come out shorter than 0 after the takeups, retry that
corner with R = W/2. If it is still too short, refuse the point and say why.

### `HVACDUCT`: draw duct runs

1. **Dialog.**
   - Shape (rectangular or round), and W×H or d. Offer the sizes found in the
     drawing and the template, and accept free entry.
   - System layer: the template's duct layers, read with
     `TemplateSource.GetLayerNames()`. They include `EIA_1 SUPPLY AIR`,
     `EIA_1 RETURN AIR`, `EIA_1 OUTSIDE AIR`, `EIA_1 EXHAUST AIR`,
     `EIA_1 PRIMARY SUPPLY AIR`, `EIA_1 PRIMARY RETURN AIR`, `EIA_1 TOILET AIR`, and
     `EIA_2 EXHAUST SMOKE`. Use exact names from the template, never guesses.
   - Default segment length, and the fitting factors from the table above.
2. **Start point.**
   - On the end face of an existing duct: continue that run.
   - On the side wall of an existing duct: start a branch with a tap. The branch
     runs perpendicular to the main, away from it.
   - Anywhere else: a free start.
3. **Loop.** Prompt `Next point or [Size/Undo] <length>:`.
   - A click, osnap, ortho, polar or typed direct distance gives the next vertex.
   - Enter on an empty input uses the dialog length toward the current cursor
     direction, or the previous segment's direction, or +X.
   - `Size` changes the size from the current vertex and inserts a transition.
   - `Undo` removes the last segment and its fittings and restores the previous
     segment's length.
   - Esc or Enter at the first prompt ends the command.
4. **Preview.** While a point is being picked, show the pending segment's outline
   and the elbow at the previous vertex as transient graphics, updated from
   `PointMonitor` as in `HVACSTRETCH`. Apply ortho in the preview when `ORTHOMODE`
   is on.
5. **Commit.**
   - Each straight segment is one block reference on the system layer, with
     `Distance3` equal to the centreline length minus the takeups.
   - When the next vertex adds an elbow, shorten the previous block.
   - Continuing from an existing duct may need an elbow and/or a transition at its
     end, which shortens that duct as well. If the end at `Position` is the one
     that moves, move `Position` too.
6. **Missing size.** Look in the drawing first, then import from the template. If
   the template lacks it, clone the nearest size of the same shape through a
   scratch database, rename it, and move the visible outline's ±W/2 vertices. This
   recipe was verified in `accoreconsole`; see `CLAUDE.md`.
7. **Optional.** If the last point lies on another duct's side wall, finish with a
   tap.

### `HVACDUCTALIGN`: align duct edges (Revit Align)

1. **Pick the reference edge.** Use `GetNestedEntity`: a line or polyline segment
   inside a duct reference, or a plain LINE/LWPOLYLINE. Map it to world
   coordinates with `PromptNestedEntityResult.Transform`.
2. **Pick the edge to align.** It must be a segment inside a duct reference, B,
   parallel to the reference within 1°; otherwise say why and ask again.
   - Translate B perpendicular to the edge so the two edges become collinear.
   - The keyword `Multiple` keeps the same reference for further picks.
3. **Join.** If the reference belongs to duct A and A and B are coaxial-parallel
   with facing ends, join without asking (user request 2026-11-19).
   - Same size: merge into one block. A's `Distance3` (and `Position`, if B lies
     before A's start) is stretched to cover B's far end, and B is erased.
   - Equal widths but different heights: change B's `Distance3` (and its
     `Position`, if needed) so B's facing end meets A's.
   - Different widths: add an eccentric transition with its flat side on the
     aligned edge, and adjust B to meet it.
   - If B would end up shorter than 100, skip the join and say so.
4. **Branch tap (user request 2026-11-19).** If the reference is a side wall of
   duct A and the picked edge is an end face of duct B at right angles to A
   (within 1°), always join with a tap, without asking:
   - B keeps its centreline and far end; its near end moves to the tap lead from
     A's wall (rect L = max(Wb/2, 102), round 152 + 51).
   - The tap (same shape as HVACDUCT's) goes on A's layer and elevation, tagged
     `HVACAI_FITTING` kind `TAP`. A's flow is its drawing direction, so a rect
     tap's 45° side faces A's start.
   - Refuse with the reason if the tap does not fit on A, B does not run out from
     that wall, or B would end up shorter than 100.

### `HVACPIPESIZE`: size a pipe run

**Layer catalogue:**

| Type | Layer name | Sizes |
| --- | --- | --- |
| CHWS, CHWR | `EIA_1 CHWS DN<n>`, `EIA_1 CHWR DN<n>` | DN15, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150, 200, 250, 300, 350, 400, 450, 500, 600 |
| Condensate | `EIA_1 Pipe condensated DN<n>` | DN21, 27, 34, 48, 60, 90, 110 |
| Refrigerant | The template's exact `EIA_1 RP` names, bracket included | 9.5-6.4, 12.7-6.4, 15.9-6.4, 15.9-9.5 (14), 19.1-9.5 (22.4), 22.2-9.5 (28), 28.6-12.7 (33.6-44.8), 28.6-15.9 (50.4-61.6), 34.9-15.9 (67.2), 34.9-19.1 (72.8-95.2), 41.3-19.1 (100.8-151.2) |
| CWS, CWR, HWS, HWR (new) | `EIA_1 CWS DN<n>`, `EIA_1 CWR DN<n>`, `EIA_1 HWS DN<n>`, `EIA_1 HWR DN<n>` | Same DN list as CHW |

**Creating a missing layer:**
- If the layer is in the template, import it with `TemplateSource.ImportLayers`.
- New CW/HW layers are not in the template:
  - Colour: per DN, from the CHW table (DN15 1, 20 2, 25 3, 32 4, 40 5, 50 6,
    65 21, 80 45, 100 86, 125 140, 150 162, 200 190, 250 20, 300 61, 350 127,
    400 187, 450 154, 500 221, 600 16).
  - Linetype: copied from the template. CWS and CWR take the `CWS`/`CWR` linetypes
    of `EIA_1 COOLING WATER SUPPLY`/`RETURN`. HWS and HWR take the linetypes of
    `EIA_1 HOT WATER SUPPLY`/`RETURN`.
  - Add a linetype import to `TemplateSource` if needed.

**Behaviour:**

1. **Pick a point** on a LINE or LWPOLYLINE. The core finds the run:
   - Build a graph of every LINE/LWPOLYLINE segment on pipe layers: the picked
     layer, the catalogue layers, and the template's unsized water, drain and gas
     layers.
   - Weld endpoints within 1 mm (configurable).
   - A segment end touching another segment's interior is a T node and splits
     that segment.
   - Interior crossings are not nodes, because pipes cross at different levels.
   - From the picked segment, walk both ways through degree-2 nodes (corners).
     Stop at degree 1, at degree 3 or more, and where the next segment is on a
     different layer.
   - Do not stop at valves or other blocks; pipes run through them in the template.
2. **Highlight the run** and show the dialog: pipe type and size, preselected with
   the last values.
3. **Apply.** Split the source curves at the run boundaries with `GetSplitCurves`,
   with sorted parameters and points snapped via `GetClosestPointTo`.
   - Run pieces go to the new layer, with colour and linetype ByLayer.
   - Other pieces keep everything.
   - Reuse the original entity for one piece so its handle survives, and append
     the rest.
   - Arcs (bulges) are allowed.
   - Closed polylines split into open pieces.
4. **Loop** to the next pick. Enter ends the command.

### `HVACREFRIG`: clean up a rough refrigerant network

1. **Select the input.** Select the rough LINE/LWPOLYLINE network, or use the
   pickfirst set, then pick the outdoor-unit end as the root.
2. **Settings,** with defaults:

   | Setting | Default |
   | --- | --- |
   | Weld tolerance | 50 |
   | Angle tolerance | 10° |
   | Align tolerance | 200 |
   | Jog tolerance | 200 |
   | Refnet scale s | 1.24 |
   | R1: min pipe from a refnet outlet to the next refnet inlet | 1000 |
   | R2: min straight pipe from an outlet to the next bend or refnet inlet | 500 |
   | R3: max pipe from the first refnet inlet to the farthest indoor unit | 40000 |
   | R3: construction allowance (warn above max - allowance, 37 m) | 3000 |
   | Size pipes (step 6) | On |
   | Write size labels | On |
   | Label height (mm; the label's side offset equals it, so it scales with it) | 150 |
   | Capacity search radius for equipment labels (mm) | 1500 |
   | Base angle | UCS X |

3. **Clean the geometry,** in the core and in this fixed order:
   1. Weld endpoints.
   2. Split at T-touches.
   3. Classify each edge as horizontal, vertical, 45° or other, against the base
      angle.
   4. Chain same-class edges.
   5. Cluster nearly collinear parallel chains whose projections do not overlap.
      Cap each cluster's span at the align tolerance so merges cannot chain on.
      Set each cluster to the length-weighted mean coordinate, or to its anchor,
      which never moves.
   6. Collapse jogs shorter than the jog tolerance.
   7. Delete zero-length edges.
   8. Dissolve collinear degree-2 nodes when both sides are on the same layer.
4. **Validate before writing.** Abort, highlight and say why if any check fails:
   - same number of connected components;
   - no new crossings or overlaps;
   - anchors unmoved;
   - still a tree.

   Show a transient preview and ask for confirmation, then write in one
   transaction.
5. **Refnets.** Run a BFS from the root. At each degree-3 node N:
   - The upstream edge is the edge toward the root, with direction d̂ from the
     upstream edge into N.
   - The through child is the one collinear with d̂; the branch is the other.
   - Set the refnet back from N by D (at least the R2 distance, default 500) and
     insert `Branch Connection (1)`, imported from the template if missing, on the
     upstream pipe's layer.
     - At scale s: main outlet at N − D·d̂, inlet at N − (D + 750·s)·d̂, rotation
       along d̂.
     - Local geometry: inlet (0,0), main outlet (750,0), branch outlet (750,−200),
       all pointing local +X.
     - Mirror the block (X scale −s, rotation + 180°) when the branch is on the left
       of d̂.
   - Cut the pipes:
     - the upstream pipe ends at the inlet;
     - the through pipe starts at the main outlet and runs straight on through N,
       which stops being a node of the main;
     - a new pipe of length D, on the branch's layer, runs parallel to the main from
       the branch outlet (N − D·d̂ + 200·s·n̂, n̂ the unit normal toward the branch) to
       N + 200·s·n̂;
     - the original branch pipe starts at N + 200·s·n̂.
   - **Piping rules** (settings above), measured on the finished geometry along the
     pipe, with the refnet bodies counted (750·s on the main; the straight distance
     between the inlet and branch outlet ports on a branch):
     - R1: from an outlet (main or branch) to the inlet of the next refnet.
     - R2: from an outlet to the next bend or the next refnet inlet. The D-long branch
       pipe ends at a bend, so D ≥ R2 meets it on both outlets. A pipe end is not a
       change of flow, so nothing is checked there.
     - R3: from the inlet of the first refnet (nearest the root) to the farthest
       indoor unit; every pipe end other than the root is a unit. A warning above
       max − allowance, worded differently above max. It never moves anything.
   - **Choosing D.** Every D starts at the R2 distance. Going from the leaves back to
     the root, a refnet's D is raised to what the refnets after it need, so that
     D_up ≥ D_down + R1 + 750·s − (pipe length between the two T nodes). D is capped
     by the upstream run less 750·s (the run includes the upstream refnet's D on a
     through pipe, so those caps chain back) and by the room for the D-long branch
     pipe: the pipe must not cross or touch another pipe, or another new branch pipe,
     so a clash lowers that refnet's cap (1 mm short of the clash) and the plan is
     made again.
   - **Rule not met.** If the cap stops a rule being met, the refnet is still
     inserted at the capped D (the closest to compliant), the point is circled in the
     preview and a warning gives the rule, the measured value and the required value.
     Warnings print before the Yes/No question and again in the summary. When the
     network is already clean and nothing is written, the R3 distance and the
     warnings are still printed, for refnet blocks whose three ports meet pipe ends.
   - Report, and do not modify, these cases:
     - the upstream run is shorter than 750·s plus the least D (counting the largest D
       of the refnet just upstream on a through pipe);
     - the branch's first leg is shorter than 200·s;
     - no collinear through child (a bull-head tee);
     - degree 4 or more;
     - an existing `Branch Connection (1)` at N;
     - the D-long branch pipe cannot clear another pipe even at the least D.
6. **Pipe sizing** (user request 2026-11-20, "Size pipes" on by default). After the cleaning the
   command sizes the refrigerant pipes for a Daikin VRV IV heat pump (RXYQ-T/U). It goes on even
   when the network is already clean. `HVACPIPESIZE` still sizes a single run by hand.

   **Flow.**
   1. **Tree.** `NetworkTopologyCore.cs` builds the pipe tree from the root. A refnet, planned or
      already in the drawing, is one node that joins its inlet, main outlet and branch outlet.
      A pipe drawn through a refnet body is left as drawn. A junction with no refnet (a case the
      cleaning reported) is a node of its own and is sized like a refnet, with a warning.
      - *Segments* are the pipes between nodes, one polyline each from upstream to downstream, with
        the pieces of a path merged and a path cut where it passes a refnet port. Kinds: `Main`
        (root to the first refnet, or to the only unit), `BetweenRefnets`, `ToUnit` (last refnet to
        an indoor unit).
      - *Units* are the pipe ends other than the root (`E1`, `E2`, ...). Each segment lists the units
        downstream of it. Segments are numbered `S1`, `S2`, ... in the order met from the root,
        main outlet before branch outlet.
      - Pipes not connected to the root are written as they were.
   2. **Capacity prefill,** per unit, first match wins (`RefrigCapacityReader.cs`):
      1. xdata `HVACAI_UNIT` (1071 index, 1040 kW) that an earlier run stored on the pipe curve
         that ends at the unit;
      2. a block whose extents, grown by the weld tolerance, contain the pipe end and whose name has
         `<number> kW` (`FCU-CAS-2.8 kW`, `IU-5.6 kW`; the dynamic block's true name is used);
      3. the nearest `HVAC-NAME OF EQUIPMENTS` block within the capacity search radius (default
         1500 mm) whose attribute `PROPERTIES` has `<number> kW`; its `TITLE` (for example
         `FCU-1.1.1`) is shown as the source name. A label serves one unit, nearest pair first;
      4. empty. The user must fill it before Apply.

      A kW value maps to the nearest index. Where the value is not within 6 percent of a nominal
      capacity the source column says "nearest index".
   3. **Dialog** (`SizingForm.cs`, WinForms, generic so a condensate command can reuse it: the caller
      passes the unit rows, the capacity and size choices, and a recompute callback).
      - Header: outdoor unit HP (`Auto` or 4 to 54), the indoor total, the connection ratio, the
        longest pipe from the root (actual length, and with 0.5 m per refnet), and the check box
        "Upsize main pipe (equivalent length >= 90 m)", off by default.
      - Left grid "Indoor units": `#`, source (block, label, xdata or empty), capacity (combo such
        as `25 · 2.8 kW`). Several rows can be selected and set at once with "Apply to selected rows".
      - Right grid "Pipe segments": `#`, kind, total index, units, automatic size, size (combo of the
        catalogue's RP sizes), a pen mark when the size was set by hand, and the warning. Sizes set by
        hand are kept when anything else changes; choosing the automatic size clears the mark, and
        "Clear sizes set by hand" clears them all. A line lists the refnet kits.
      - Warnings and notes box, then Recalculate, Apply and Cancel. Everything recomputes live.
        Apply is disabled while a unit has no capacity or a segment has no size.
      - **Preview.** Before the dialog shows, transient graphics mark `E1`, `E2`, ... at the pipe ends
        and `S1`, `S2`, ... at the segment midpoints, the segments coloured by size, refnet bodies in
        magenta and the cases the checks circled in yellow. Selecting a row highlights its segment or
        unit and redraws the preview. *Not tested:* whether AutoCAD repaints transients while the
        modal dialog is open; if it does not, the preview drawn at the start still shows.
   4. **Apply** writes, in the same transaction as the cleaned network: each segment's polyline on its
      RP layer (colour, linetype and lineweight ByLayer; missing layers imported from the template
      as `HVACPIPESIZE` does); each new refnet, and each refnet block already in the drawing, on its
      inlet pipe's layer (a block on a locked layer is left alone); the `HVACAI_UNIT` xdata on the
      pipe that ends at each unit; and the size labels. **Cancel** writes nothing. One command is one
      undo group.
   5. **Size labels** (option "Write size labels", default on). A TEXT `%%C<gas>/<liquid>` (for
      example `%%C15.9/9.5`) on layer `EIA_1 GAS ANNOTATIONS` (imported from the template when
      missing), style `T_unicode` when the drawing has it, otherwise the current style. Height is
      the setting "Label height" (default 150 mm, above 0). The text is centred on a point offset
      from the midpoint of the segment's longest straight piece by the same distance as the height,
      to the left of its reading direction, and rotated along the piece, kept readable in
      (-90, 90] degrees. Each label carries xdata `HVACAI_RPLABEL`; a later run removes labels
      with that tag within 600 mm of the network and writes new ones (labels on a locked layer are
      left in place).

   **Tables** (`RefrigSizingCore.cs`). Sizes are gas/liquid outer diameters in mm. A pair is mapped
   to the template layer by its gas and liquid sizes; the bracket on the layer name is the
   outdoor unit kW range and is part of the name. Every standard pair below exists in the template.
   Cells marked *unconfirmed* were not found in the sources; check them against the Daikin data book.

   | Index | 15 | 20 | 25 | 32 | 40 | 50 | 63 | 71 | 80 | 100 | 125 | 140 | 200 | 250 |
   | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
   | kW | 1.7 (unconfirmed) | 2.2 | 2.8 | 3.6 | 4.5 | 5.6 | 7.1 | 8.0 (unconfirmed) | 9.0 | 11.2 | 14.0 | 16.0 (unconfirmed) | 22.4 | 28.0 |

   | Refnet to unit, by the unit's index | Gas/liquid |
   | --- | --- |
   | 15 to 50 | 12.7/6.4 |
   | 63 to 140 | 15.9/9.5 |
   | 200 | 19.1/9.5 |
   | 250 | 22.2/9.5 |

   | Refnet to refnet, by the total index downstream | Gas/liquid |
   | --- | --- |
   | below 150 | 15.9/9.5 |
   | 150 to below 200 | 19.1/9.5 |
   | 200 to below 290 | 22.2/9.5 |
   | 290 to below 420 | 28.6/12.7 |
   | 420 to below 640 | 28.6/15.9 |
   | 640 to below 920 | 34.9/19.1 |
   | 920 and over | 41.3/19.1 (liquid unconfirmed) |

   A pipe between refnets is never larger than the standard main pipe: gas and liquid are each capped.

   | Main (outdoor unit to the first refnet, or to the only unit), by outdoor HP | Gas/liquid |
   | --- | --- |
   | 5 | 15.9/9.5 |
   | 6 | 19.1/9.5 (unconfirmed) |
   | 8 | 19.1/9.5 |
   | 10 | 22.2/9.5 |
   | 12 to 16 | 28.6/12.7 |
   | 18 to 22 | 28.6/15.9 |
   | 24 | 34.9/15.9 |
   | 26 to 34 | 34.9/19.1 |
   | 36 to 54 | 41.3/19.1 |

   4 HP is not in the table and is taken as 5 HP (unconfirmed). HP choices are 4, 5, 6, 8, 10, 12,
   14, ... 54. The outdoor unit's index is HP x 25 (unconfirmed above 8 HP, where 8 HP is 200).

   - **Default HP** is the smallest HP whose index is at least the indoor total (the largest HP when
     none is); the user can change it. The **connection ratio** is the indoor total over the outdoor
     index; a ratio outside 50 to 130 percent warns.
   - **Upsizing the main pipe** (optional, equivalent length of 90 m or more). One step on the liquid
     side (6.4, 9.5, 12.7, 15.9, 19.1, 22.2) and on the gas side (9.5, 12.7, 15.9, 19.1, 22.2, then
     25.4, 28.6 then 31.8, 34.9 then 38.1; 41.3 has no step). There is no gas step for 18 to 22 HP,
     where the sources conflict. The sizes 25.4, 31.8 and 38.1 are not in the template, and neither
     are most upsized pairs, so the command tries the full step, then the liquid step alone, then the
     gas step alone, and uses the first pair the template has a layer for. Otherwise it keeps the
     standard size. A warning says what was done. With the template's layers only these upsize:
     4, 5, 6 and 8 HP (gas only), 12 to 16 HP and 24 HP (liquid only).
   - **Refnet kits** (information, shown in the dialog, nothing is drawn). The first refnet after the
     outdoor unit, by HP: 4 to 6 `KHRQ22M20T`, 8 and 10 `KHRQ22M29T9`, 12 to 22 `KHRQ22M64T`, 24 to
     54 `KHRQ22M75T`. A later refnet, by the total index downstream: below 200 `KHRQ22M20T`, 200 to
     below 290 `KHRQ22M29T9`, 290 to below 640 `KHRQ22M64T`, 640 and over `KHRQ22M75T`.
   - **Warnings.** A unit with no capacity (this blocks Apply). A pipe between refnets larger than the
     main pipe (possible after a manual change). A pipe to a unit that is not the unit's connection
     size. A connection ratio outside 50 to 130 percent. The refnet rules R1 to R3 and the
     junction cases from the cleaning appear in the same box. Table cells in use that are
     unconfirmed are listed as notes.

   **Sources** (Daikin):
   - RXYQ-T installer reference guide:
     https://www.daikin.fr/content/dam/document-library/Installer-reference-guide/ac/vrv/RYYQ-T,%20RYMQ-T,%20RXYQ-T,%20RXYQ-T9_4PEN370475-1B_Installer%20and%20user%20reference%20guide_English.pdf
   - RXYTQ-TYF installer reference guide:
     https://www.daikinoman.com/content/dam/document-library/Installer-reference-guide/ac/vrv/rxytq-tyf/RXYTQ-TYF_4PEN388989-1B_Installer%20and%20user%20reference%20guide_English.pdf
   - RYYQ-T / RXYQ-T refrigerant pipe selection:
     https://www.daikin.es/content/dam/MDM/TechnicalDrawings/VRV/EN/RYYQ-T_RXYQ-T_RYMQ-T_refpipsel_4P329765-1C_2_EN.pdf
   - RXYQ-U refrigerant pipe selection:
     https://www.daikin.es/content/dam/MDM/TechnicalDrawings/VRV/RXYQ-U/en/RXYQ-U_refpipsel_3D079540F_1_EN.pdf

   **Choices the code made** where the request left room (for review):
   - A refnet block already in the drawing is moved onto its inlet pipe's layer too.
   - When the network is already clean, Apply still rewrites every pipe as a new polyline (handles
     change, other properties are copied from the source curve), like a cleaning run does.
   - A segment polyline replaces the paths it merges; its properties come from the source curve that
     gave it most of its length.
   - The preview colours are fixed per catalogue size and are not a layer colour.

### `HVACDRAIN`: draw and size a condensate network

The user approved the plan on 2026-11-20. The user draws a rough condensate network of LINEs and
polylines from the indoor units to the drain point; the command straightens it, draws 45 degree
joints, sizes each pipe from the indoor-unit capacities and writes it on the `EIA_1 Pipe condensated
DN<n>` layers with a size label. Flow runs toward the root (the drain point). Needs in-AutoCAD
acceptance testing.

1. **Select** the pickfirst set, or a new selection of `LINE,LWPOLYLINE`, then pick the drain end
   (the end of the pipe that reaches the riser or floor drain) as the root. A curve on a locked layer
   is refused.
2. **Settings** (`DrainSettingsForm`, remembered in `%APPDATA%\HvacAiCadAddin\HVACDRAIN.settings`):

   | Setting | Default |
   | --- | --- |
   | Weld tolerance, angle tolerance, align tolerance, jog tolerance | 50 mm, 10 degrees, 200 mm, 200 mm (the cleaner's) |
   | Base angle | UCS X |
   | Slant length (the 45 degree piece; the cut is its cos 45 part, 212.13 mm) | 300 mm |
   | Size pipes | on |
   | Condensate factor | 0.71 L/h per kW |
   | Slope | 1/100 (or 1/50) |
   | Capacity search radius from a pipe end | 1500 mm |
   | Write size labels | on |
   | Label height (also the distance of the label from the pipe) | 150 mm |
   | Label format (`{size}`, `{slope}` in percent) | `D{size}-{slope}%`, for example `D34-1%` |
   | Label layer (blank: each pipe's own size layer) | `EIA_1 GAS ANNOTATIONS` |

3. **Clean.** `RefrigCleaner.Run` with no root point (`DrainPlanCore.cs`): the same weld, T-split,
   orthogonalise, jog and collinear steps and the same checks as `HVACREFRIG`. With no root there are
   no refnets and none of the R1, R2, R3 rules. Pipe ends never move.
4. **Tree** (`DrainNetworkCore.cs`). Built from the cleaned paths, flowing toward the root. Key nodes:
   the root (it must be a pipe end), the indoor units (the other degree-1 ends), the T junctions
   (degree 3) and the crosses (degree 4 or more). A segment is the pipe between two key nodes, with
   its bends as interior points and collinear points dropped, from the upstream node to the
   downstream node. Segments are numbered `S1`, `S2`, ... from the root so that a segment has a smaller
   number than the segments that flow into it; units are `E1`, `E2`, ... in the order met. Each
   segment lists the units upstream of it. Pipes of another network are kept as cleaned and not sized,
   with a warning. A loop is refused.
5. **45 degree joints** (`DrainGeometryCore.cs`). With cut = slant length x cos 45:
   - A 90 degree bend (within 5 degrees) is chamfered: the corner is cut by the cut distance on each
     leg and a 45 degree piece of the slant length joins the cuts.
   - A right-angle T (the pipe toward the root and one upstream pipe in line, the other upstream pipe
     the branch) keeps its main straight. The branch stops one cut distance short of the main's
     centreline and a slant of the slant length runs to the main, landing one cut distance
     DOWNSTREAM (toward the root) of the original T point. The slant belongs to the branch (its size,
     its layer); the main's upstream pipe is extended to the landing point and the pipe toward the
     root starts there.
   - Two upstream pipes in line that meet a downstream pipe at right angles (a T whose stem is the
     pipe toward the root) both get a slant and meet on the stem one cut distance downstream of the
     joint (a Y). The user did not describe this case; this is the code's choice.
   - A leg that cannot take the cuts it is given (its two cuts add up to more than its length,
     counting the chamfer or slant of the neighbouring corner on the same leg, or the landing would
     pass the next node) keeps the original corner or T. When a leg is too short for two features,
     the one at its upstream end gives way and the other is made. Each kept joint is circled and
     reported with the leg's length and the length it needed.
   - A bend that is not a right angle is left. A T that is not at right angles is left and reported,
     unless it already leaves at 45 degrees (a Y), so running the command again on its own result
     changes nothing. A joint of four or more pipes is not handled: it stays a plain cross and is
     reported.
6. **Sizing** (`DrainSizingCore.cs`, Daikin Si39 basis). An indoor unit drains 2 L/h per HP and 1 HP
   class is 2.8 kW, so 0.71 L/h per kW. The sizes are the catalogue's condensate sizes (D21, D27, D34,
   D48, D60, D90, D110) and map to `EIA_1 Pipe condensated DN<n>` through `PipeCatalog.LayerName`.

   | Pipe | Daikin allowable flow, 1/100 | 1/50 | Largest total kW, 1/100 | 1/50 |
   | --- | --- | --- | --- | --- |
   | one indoor unit | | | D27, any capacity | |
   | D34 (VP30) | 88 L/h | 125 L/h | 120 | 175 |
   | D48 (VP40) | 175 L/h | 247 L/h | 245 | 346 |
   | D60 (VP50) | 334 L/h | 473 L/h | 465 | 662 |
   | D90 | none (*unconfirmed*) | none | 900 | 1280 (scaled by 662 / 465) |
   | D110 | none (*unconfirmed*) | none | above 900 | above 1280 |

   - **Rules.** A pipe that drains exactly one unit is D27. Otherwise the size is the first band
     whose largest total kW covers the pipe's total. A pipe is never smaller than any pipe that flows
     into it: its automatic size is the larger of its own table size and the sizes in use of those
     pipes (a size set by hand counts).
   - **Other factor.** The table limits are for 0.71 L/h per kW. Another factor scales them by
     0.71 / factor (more litres per kW, lower limits).
   - **Unconfirmed.** The D90 and D110 limits are extrapolated and are flagged in the dialog
     (the row warns, a note explains). D21 is never chosen automatically. The limits are the user's
     figures: the allowable flows over 0.714 L/h per kW (2 L/h per 2.8 kW) give 123, 245 and 468 kW
     at 1/100, which the user rounded down to 120, 245 and 465, and 175, 346 and 662 kW at 1/50.
   - **Sources.** Daikin REYQ service manual Si39-406:
     https://manualshive.com/en/manual/daikin/reyq96mtju/4175824?page=51 ; VRV II Si39-303:
     https://manualshive.com/en/manual/daikin/vrv-ii-series/2459187?page=46 ; FXFQ drain pipe VP25:
     https://manualshive.com/en/manual/daikin/fxfq100bveb/2459709?page=28 . IPC/IMC 314.2.2 was named
     by the user for comparison; it was not read for this implementation.
7. **Capacity prefill,** per unit (`RefrigCapacityReader.cs`, shared with `HVACREFRIG`): xdata
   `HVACAI_UNIT` on the pipe curve that ends at the unit (kW from its real value), a block whose
   extents grown by the weld tolerance contain the pipe end and whose name has `<number> kW`
   (`FCU-CAS-2.8 kW`), the nearest `HVAC-NAME OF EQUIPMENTS` block within the capacity search radius
   whose `PROPERTIES` attribute has `<number> kW` (its `TITLE`, for example `FCU-1.1.1`, is the source
   name), or empty. On Apply the capacity of each unit is written back as `HVACAI_UNIT` xdata (the
   nearest Daikin capacity index, then the kW) on the pipe that ends there, so a later run reads it.
8. **Dialog** (`DrainSizingUi.cs` over the shared `SizingForm`, in kW mode: the capacity cells are
   numbers). Left grid "Indoor units": `#`, source, capacity in kW, with "Apply to selected rows".
   Right grid "Pipe segments": `#`, flow in L/h, total kW, units, automatic size, size (a combo of D21
   to D110), a pen mark for a size set by hand (kept when anything else changes), and the warning
   (for example `smaller than upstream D34`). Below: the warnings (kept joints, crosses, other
   networks, missing capacities), Recalculate, Apply and Cancel. Everything recomputes live. Apply is
   disabled while a unit has no capacity. Before the dialog shows, transient graphics draw the
   straightened pipes with their chamfers and slants coloured by size, `E1`, ... at the pipe ends,
   `S1`, ... at the segment label spots, the drain point circled in red and the reported cases
   circled in yellow; selecting a row highlights its unit or segment in white. *Not tested:* whether
   AutoCAD repaints transients while the modal dialog is open; if it does not, the preview drawn when
   the dialog opened still shows. With "Size pipes" off there is no dialog: the same preview in green
   and a Yes/No question like `HVACREFRIG`, and the pipes keep their layers.
9. **Apply** (`DrainWriter.cs`), one transaction, one undo group: one polyline per segment on its size
   layer (colour, linetype and lineweight ByLayer, the rest copied from the source curve that gave the
   segment most of its length, elevation kept), pipes of other networks as cleaned, the originals
   erased, the capacity xdata, and the labels. Missing size layers are imported from the template with
   `PipeDrawingLibrary.EnsureLayer`, which also brings the layer's linetype (`Broken Line`). Cancel
   writes nothing.
10. **Size labels.** A TEXT `D<size>-<slope>%` (for example `D34-1%`) at the midpoint of the segment's
    longest straight piece, offset by the label height to the left of its reading direction, rotated
    along the piece and readable in (-90, 90] degrees (the shared `SizeLabels.TryPlace`), on the label
    layer setting (default `EIA_1 GAS ANNOTATIONS`, imported from the template when missing, the layer
    `HVACREFRIG` uses for its labels; no condensate annotation layer is documented, so check this
    choice) in style `T_unicode` when the drawing has it. Each label carries xdata `HVACAI_DRAINLABEL`;
    a later run removes labels with that tag within 4 label heights (at least 600 mm) of the network
    and writes new ones.
11. **Choices the code made** (for review):
    - The cleaner's tolerances are in the settings dialog; its refnet settings are not used.
    - The Y case above, and giving way at the upstream end of a short leg.
    - D90 and D110 limits, and the 1/50 limit of D90, are extrapolated.
    - Other networks in the selection are kept as cleaned (as `HVACREFRIG` does) rather than refused.
    - Files: `HvacDrain.cs` (command), `DrainSettingsCore.cs`, `DrainNetworkCore.cs`,
      `DrainGeometryCore.cs`, `DrainSizingCore.cs`, `DrainPlanCore.cs` (AutoCAD-free; tested by
      `tests/PipeCoreTests/DrainTests.cs`), `DrainWriter.cs`, `DrainSizingUi.cs`,
      `DrainSettingsForm.cs`. It reuses `RefrigCleaner`, `RefrigCapacityReader`, `SizingForm`,
      `SizeLabels`, `PipeTransients` and the label and xdata helpers of `RefrigSizeWriter`
      (`SizingForm` gained column header settings and `RefrigSizeWriter` an app and layer parameter on
      its label helpers; `HVACREFRIG` behaves as before). `NetworkTopologyCore` was not reused: its
      upstream and downstream run the other way (from the outdoor unit), it treats every junction
      as a refnet and it has no cross or collinear handling.

### Pipe stream: implementation notes (`HVACPIPESIZE`, `HVACREFRIG`)

Files, all under `cad_addin/src/`. The core is AutoCAD-free (namespace
`HvacAiCadAddin.Pipes`, shares `Vec2`/`Outline` with `DuctGeometryCore.cs`):

| File | Role |
| --- | --- |
| `PipeCatalogCore.cs`, `PipeSettingsCore.cs` | Layer catalogue, colours, pipe-layer name filter, remembered dialog values. |
| `PipeNetworkCore.cs` | Welded graph with T nodes, run finder, cut plan (`CurveSplitPlan`). |
| `RefrigCleanerCore.cs` | Cleaning order, validation, refnet placement, output paths. |
| `NetworkTopologyCore.cs` | Pipe tree from the root through new and existing refnets: segments, units, downstream units. |
| `RefrigSizingCore.cs` | Daikin VRV IV tables, sizing of the tree, warnings, capacity text parsing, label placement. |
| `PipeDrawingLibrary.cs`, `RefrigWriter.cs`, `RefrigSizeWriter.cs`, `RefrigCapacityReader.cs` | Non-interactive AutoCAD code: read curves and capacities, layers, split, write (cleaned, or cleaned and sized). |
| `HvacPipeSize.cs`, `HvacRefrig.cs`, `RefrigSizingUi.cs`, `PipeSizeForm.cs`, `RefrigSettingsForm.cs`, `SizingForm.cs`, `PipeTransients.cs` | Commands, dialogs (`SizingForm` is generic), transient graphics. |
| `DrainSettingsCore.cs`, `DrainNetworkCore.cs`, `DrainGeometryCore.cs`, `DrainSizingCore.cs`, `DrainPlanCore.cs` | `HVACDRAIN` cores: settings, tree toward the drain point, 45 degree joints, Daikin Si39 sizing, the cleaning pipeline. |
| `HvacDrain.cs`, `DrainWriter.cs`, `DrainSizingUi.cs`, `DrainSettingsForm.cs` | `HVACDRAIN` command, writer, sizing session over `SizingForm`, settings dialog. |

Settings live in `%APPDATA%\HvacAiCadAddin\HVACPIPESIZE.settings`,
`HVACREFRIG.settings` and `HVACDRAIN.settings`.

Where the spec left a choice, the code does this (all conservative):

- **Unsized pipe layers** (for the run graph) are the drawing's layers whose names have a
  water, drain, gas, pipe or refrigerant word and no annotation/equipment word
  (`PipeCatalog.IsPipeLayer`), plus the picked layer. The template's real layer list could
  not be read without AutoCAD; check this filter against it.
- **Weld tolerance** of `HVACPIPESIZE` is the `Weld` keyword at the pick prompt (default 1 mm).
- **Locked layers:** a run or selection that includes a curve on a locked layer is refused.
- **`HVACPIPESIZE` split:** run pieces get the layer, colour and linetype ByLayer; the longest
  piece reuses the original entity (handle kept), the others are appended. A tiny segment
  (ends welded together) inside a run does not split it.
- **`HVACREFRIG` anchors** are all pipe ends (degree-1 nodes after weld and T-splits): the
  root and every leaf. They never move; a chain with anchors at both ends off the line stays as
  drawn. Pinned chains seed clusters first, then free chains join the nearest cluster.
- **Orthogonalised joints** sit on the intersection of their chains' lines. Where two parallel
  chains that cannot share a line meet at a joint, the joint sits midway and both stay slightly
  off-axis; the jog tolerance does not apply there.
- **Input checks:** arcs, loops (not a tree), and a root that is not a pipe end are refused.
  Pipes drawn twice over the same two joints are kept once.
- **Writing:** the cleaned network is written as one polyline per run between junctions
  (layer from the run, other properties copied from the source curve that gave most length),
  the originals are erased, and refnets are inserted, all in one transaction. With sizing on,
  `RefrigSizeWriter` writes one polyline per segment instead (step 6).
- **Refnet cases reported and left unchanged**, besides the spec's: a branch that does not
  leave the main at right angles (within the angle tolerance), and networks with no root.
  The upstream-run check counts the run left after an earlier refnet's turn on a branch, and
  the earlier refnet's largest D on a through pipe.
- **Piping rules:** R1, R2 and R3 are measured on the finished graph (pipes plus refnet bodies), and
  the D choice only aims at them. A bend is a turn of more than 1 degree. R2 is not checked at
  a pipe end. R3 needs a refnet: with none, it is skipped. Existing refnet blocks join the
  graph only when all three ports meet pipe ends within the weld tolerance.
- The block is inserted with `Scale3d(+-s, s, s)`; attributes of the block are added
  with their default text.

### Verification

- **Core tests:** cover each formula in the tables above, mirrored and rotated
  ducts, run finding (corners, T nodes, crossings, layer changes, closed
  polylines, arcs), orthogonalisation (the cluster span cap, anchors, validation
  failures) and refnet placement (mirroring, short-leg reports). Run every check
  under en-US, vi-VN and tr-TR.
- **Pipe core tests:** `tests/PipeCoreTests` (about 2200 checks per culture) cover the
  above for the pipe stream; run them with `dotnet run --project tests\PipeCoreTests\PipeCoreTests.csproj -c Debug`.
  `tests/PipeAcadTests` is not written yet.
- **Headless tests:** an `accoreconsole` test project per stream
  (`tests/DuctAcadTests`, `tests/PipeAcadTests`), each with its own `/isolate`
  profile folder under `%TEMP%`. Cover block import, clone/rename/widen, inserting
  and shortening ducts, splitting and relayering curves, layer creation, and refnet
  insertion, all on a copy of the template.
- **Manual tests in AutoCAD:** the preview, osnap/ortho/typed input, Esc
  handling, and nested-entity picking.

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

ZWCAD 2026 (see `CLAUDE.md`, section "ZWCAD", for the verified facts): the same add-in is
built for ZWCAD by `cad_addin\HvacAiCadAddin.Zw.csproj`, which compiles namespace-swapped
copies of `cad_addin\src` (the sources stay AutoCAD-first, so write AutoCAD code only):

```powershell
dotnet build cad_addin\HvacAiCadAddin.Zw.csproj -p:Configuration=Debug -p:Platform=x64
powershell -File tests\Zw\run-zw-tests.ps1 [-Suite smoke|writers|all]   # separate ZWCAD.exe per suite
powershell -File tests\run-core-tests.ps1                                # every AutoCAD-free core test
powershell -File cad_addin\register-zwcad.ps1 [-Unregister]               # per-user autoload (HKCU)
```

Never close or send input to a ZWCAD, `acad.exe` or Excel process you did not start; the
ZWCAD runner stops only the process id it launched. New source files in `cad_addin\src` are
picked up by the Zw project automatically; a new test file or linked source needs adding to
BOTH the accoreconsole test project and its `tests\Zw\Zw*Tests` twin.

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


## HVACPOWERWIRE: draw the indoor-unit power wire

The user approved this spec on 2026-11-20. Units are drawing millimetres. The command turns a
rough tree of LINEs and polylines into the series (daisy-chain) power supply wire of the indoor
units. It needs in-AutoCAD acceptance testing.

1. **Input.** The user draws a rough tree: a trunk from the supply end, and a short branch off
   it to each indoor unit. The end of a branch is the unit's terminal point P. Select the lines
   (pickfirst or a new selection), then pick the supply end of the wire (the root).
2. **Settings,** with defaults, remembered in `%APPDATA%\HvacAiCadAddin\HVACPOWERWIRE.settings`:

   | Setting | Default |
   | --- | --- |
   | Weld tolerance | 50 |
   | Angle tolerance | 10° |
   | Base angle | UCS X |
   | Hook slant size s | 300 |
   | A trunk end past the last branch is a unit from | 1000 |
   | Wire layer | `EIA_5 SUPPLY WIRE` (or `EIA_5 SIGNAL WIRE`) |
   | Write cable text | on |
   | Text | `2x1.5 MM2` |
   | Text height | 275 |

3. **Clean.** `RefrigCleaner.Run` with no root: weld, T-splits, orthogonalise against the base
   angle, merge jogs and collinear nodes. With no root it places no refnets and checks no piping
   rules. Pipe ends never move, so a branch is straightened onto its unit and the T joint on the
   trunk follows it. The same input checks apply (arcs and loops are refused).
4. **Tree.** `DrainNetwork.Build` makes the tree from the wire end nearest the picked point (it
   must be within the pick tolerance). The key nodes are the root, the units (wire ends), the T
   junctions and crosses. Lines of another network are kept as cleaned and reported. A joint of 4
   or more wires is refused.
5. **The hook.** At each T junction the through branch is the one that goes on straighter along the
   arriving direction `a` (unit vector along the trunk); the other is the branch, direction `b`
   from the trunk toward the unit, length L. The wire replaces the junction by the hook of the
   template's legend glyph (three lines in `EIA_5 SUPPLY WIRE`: a 922 drop, a 45° 200 x 200 slant
   and a 722 rise, tops level):
   - the wire arrives along the trunk at T and runs down the branch to P (the drop leg, length L);
   - from P a 45° slant to Q = P + s·a − s·b (s along the trunk, away from the arrival side, and s
     back toward the trunk; length s·1.414);
   - from Q straight back to the trunk at T + s·a (the rise leg, L − s long, parallel to the drop
     leg and s from it), then on along the trunk to the next unit.
6. **Series order.** Units are numbered U1, U2, ... in depth-first order along the trunk from the
   root, branches in order of distance along it. One wire is one continuous polyline.
   - **Last unit.** The last unit of a wire gets only the drop leg (no slant, no rise), and the
     wire ends there. A unit at the end of the trunk itself (a wire end with no branch) is the last
     unit: the wire just ends there.
   - **Trunk end past the last branch.** A tail shorter than the trunk-end setting (and at least
     one hook, s + 50) is an overshoot: the trunk is trimmed at the last junction and its branch is
     the last unit. A longer tail is a unit of its own, so the earlier branches are all hooked.
   - **Plain drop.** A branch that is shorter than s + 50, has a bend, does not leave the trunk at a
     right angle (within the angle tolerance), or whose next bend or junction along the trunk is
     closer than s + 50, keeps a plain drop: the trunk goes straight on and the branch stays a
     separate line from T to P, circled in the preview and warned. A plain drop is not in series.
   - **Sub-trunks.** A branch that has junctions of its own is a sub-trunk. Its units get a separate
     wire that starts at the trunk, numbered in depth-first order where it starts, and each wire
     has its own last unit.
7. **Preview and confirm.** Transient graphics: the wire in green, the supply end in a red circle,
   the plain drops in yellow circles and the labels U1, U2, ... in series order. Notes (trimmed
   ends, sub-trunks) and warnings print on the command line. Yes/No, default Yes. Cancel writes
   nothing.
8. **Write,** in one transaction (one undo group):
   - One new LWPOLYLINE per wire, and per plain drop, on the chosen layer (colour, linetype and
     lineweight ByLayer, elevation of the supply-end pipe). The layer is imported from the template
     when the drawing lacks it. The selected curves are erased.
   - **Cable text** when on: a TEXT with the string, style `T_unicode` if the drawing has it
     (otherwise the current style), the text height, on the wire layer, tagged with
     `HVACAI_POWERWIRE` xdata. It runs along the rise leg (rotation 90° from the trunk), from 0.25
     of its height above the slant's top, with its glyphs on the side the trunk goes on, one text
     height off the leg. It reads upright (rotation in (−90°, 90°]; text that would read the other
     way is right justified so it still ends at the leg). A unit with no rise leg gets the text
     beside its drop leg, a trunk-end unit beside the end of the wire.
   - A rerun replaces the tagged texts within `s + 3 x text height` of the old or the new wire.

**Choices the code made** (for review):

- **Trunk end or overshoot.** Whether a tail past the last branch is a unit is not drawn anywhere;
  the rule above (a setting, default 1000) decides. A trimmed tail is reported.
- **Plain drops** are separate lines, because a wire cannot go down a branch and back in series. A
  last unit never needs a hook, so it never gets a warning.
- **Text side.** The approved spec puts the text beside the rise leg; the template's legend text sits on the
  other side of the drop leg, about 450 from it.
- **Text layer** is the wire layer (the template's `2x1.5 MM2` texts are on `EIA_5 SIGNAL WIRE`).
- **Through branch** of a junction is the straighter one; a bull-head tee has no straight branch, so
  it gets a plain drop.
- Files: `PowerWireCore.cs` (AutoCAD-free: settings, planner, hook and text geometry; tested by
  `tests/PipeCoreTests/PowerWireTests.cs`), `PowerWireWriter.cs`, `PowerWireSettingsForm.cs`,
  `HvacPowerWire.cs` (command). It reuses `RefrigCleaner`, `DrainNetwork`, `PipeTransients`,
  `PipeDrawingLibrary` (`EnsureNamedLayer`, `FindTextStyle`) and `RefrigSizeWriter` (`EnsureRegApp`,
  `RemoveOldLabels`) unchanged.

**Manual tests in AutoCAD** (after `NETLOAD` of the rebuilt x64 DLL):

1. Draw a trunk with three short branches down to three unit symbols, slightly off square, and run
   `HVACPOWERWIRE`. Pick the supply end. Check the preview: green wire with a hook at each branch,
   U1 to U3 in order from the supply end, the last unit with only its drop leg.
2. Answer Yes. Check the layer, one polyline per wire, the originals gone, the `2x1.5 MM2` texts
   beside each rise leg, and that one `UNDO` removes everything.
3. Run again on a new rough drawing near the old wire: the old texts are replaced.
4. Draw one branch only 200 long, or two branches 200 apart: they keep a plain drop and are circled.
5. Try a rotated UCS and a base angle other than 0, a trunk end 500 past the last branch (trimmed)
   and 3000 past it (a unit), a branch with its own T (a sub-trunk), and Esc at each prompt.

## HVACCTRLWIRE: draw control wires from a template route

The user approved this spec on 2026-11-20. Units are drawing millimetres. The command wires the
thermostat's control cable to many indoor units from one sample route the user has already drawn.
It needs in-AutoCAD acceptance testing.

1. **Input,** in this order:
   - Select the indoor-unit block references (pickfirst or a new selection filtered to `INSERT`).
   - Pick the **template**: one LINE or LWPOLYLINE that already runs from the thermostat to one
     indoor unit. That unit should be one of the selected blocks, or at least a block that touches
     the template's unit end (within 500 mm of its extents).
   - Confirm the **thermostat point**. The prompt is `Pick the thermostat point or [Other end]
     <circled end>:`. The default is the template end farther from the template's unit (the end
     that touches no selected block); it is circled in red. `Other` switches ends, a picked point
     chooses the template end nearest to it, and Enter accepts the circled end.
   - A dialog (`ControlWireSettingsForm`).
2. **Settings,** remembered in `%APPDATA%\HvacAiCadAddin\HVACCTRLWIRE.settings`:

   | Setting | Default |
   | --- | --- |
   | Offset spacing d | 150 (must be above 0) |
   | Layer | `EIA_5 CONTROL WIRE` (fixed; imported from the template when missing; ACI 11, linetype `Broken Line`) |
   | Write cable text | on |
   | Text | `2x0.75 MM2` |
   | Text height | 275 |

3. **Terminal point of each unit.** The template route runs from the thermostat point T0 to the
   template unit's terminal point (its far end).
   - A unit of the **same block** (the same effective name; a dynamic block by its real
     definition name) gets the same block-local point: the template end goes through the inverse
     of the template unit's `BlockTransform` and then through the other unit's `BlockTransform`
     (`ControlWireAffine.TryMapTerminal`, which also handles rotated and mirrored inserts).
   - A **different block** gets its insertion point, and the command line says so.
   - The template's own unit is served by the template and gets no new wire. The `k = 0` option the
     spec mentioned for it is not built.
4. **Layout** (`ControlWirePlanner`, AutoCAD-free). Each other unit gets its own open LWPOLYLINE:
   - It runs parallel to the template at k x d, on the side of the route its terminal lies on
     (left or right, looking from the thermostat), with mitre joins at corners.
   - **Separate starts.** The wires do not share a start point. At the thermostat end each wire
     starts at its own offset position: T0 shifted by k x d along the first segment's normal. There
     is no fan-out.
   - **Exit.** At the point of the route nearest the unit's terminal (the foot of the
     perpendicular, or the route's end or start when the terminal lies beyond it, or the corner
     vertex nearest it) the wire leaves the parallel run, perpendicular to the route segment it
     leaves from, out to the terminal's lateral distance, then along the segment to the terminal
     when it is not straight out (two orthogonal legs). Only this last part leaves the parallel run.
   - **Order.** On each side the wire that leaves the route first (nearest the thermostat along
     it) is the outermost (largest k), and the last to leave is k = 1. A wire that leaves earlier
     must be outside the wires that run on, or its exit leg would cut across them. If an unusual
     layout still crosses (for instance a terminal nearer to the route than its offset), every
     order of up to six wires on a side is tried and the one with fewest crossings is kept.
   - A terminal exactly on the route goes to the side with fewer wires.
   - **Arcs.** Straight segments only. A template with a bulge, or a closed one, is refused
     (`arcs are not supported`) and nothing is drawn.
5. **Warnings** (yellow circles in the preview and lines on the command line):
   - a terminal more than 3000 mm beyond the end of the route (the wire runs the whole route);
   - a terminal nearer to the route than its wire's offset (the connection turns back);
   - two wires that cross or overlap (for example two units exiting at the same point);
   - a wire that crosses the template route;
   - an offset that breaks down at a corner (a sharp corner or a short segment).
6. **Preview and confirm.** Transient graphics: the new wires in green, the template in cyan, the
   thermostat in a red circle, `W1`, `W2`, ... with the unit each wire serves, and the warnings.
   `W1` is the first wire to leave the route. Yes/No, default Yes. Cancel writes nothing.
7. **Write,** in one transaction (one undo group; the template is never modified):
   - One LWPOLYLINE per wire on `EIA_5 CONTROL WIRE`, colour, linetype and lineweight ByLayer,
     the template's elevation. The layer (with its linetype) is imported from the template when the
     drawing lacks it.
   - **Cable text** when on: one TEXT per wire, middle-centre justified, on the wire layer, style
     `T_unicode` if the drawing has it (otherwise the current style), rotated along the longest
     leg of the connection and kept readable (rotation in (-90, 90] degrees), one text height off
     that leg.
   - Every new wire and text carries `HVACAI_CTRLWIRE` xdata (`WIRE`, label, unit handle; or
     `TEXT`, label). A rerun does not replace earlier wires: it counts the tagged wires already in
     the drawing and says so.

**Choices the code made** (for review):

- **Thermostat pick.** A picked point selects the nearest template end rather than moving T0 to
  the picked point, so the wires start at offsets of the template's own end.
- **Touching** a template end means within 500 mm of the block's extents (`GeometricExtents`, or the
  insertion point when the extents cannot be measured).
- **Same block** means the same effective block name. A dynamic block stretched to a different
  `Distance3` keeps the same local terminal, which may not be where its geometry now ends.
- **Exit at a corner.** A terminal nearest a corner vertex leaves from the first segment of that
  corner, so its two legs are drawn in that segment's frame.
- Files: `ControlWireCore.cs` (AutoCAD-free: settings, `ControlWireAffine`, `ControlWirePlanner`;
  tested by `tests/PipeCoreTests/ControlWireTests.cs`), `ControlWireWriter.cs` (block reading and
  the write transaction), `ControlWireSettingsForm.cs`, `HvacCtrlWire.cs` (command). It reuses
  `PipeTransients`, `PipeDrawingLibrary` (`ReadCurves`, `EnsureNamedLayer`, `FindTextStyle`),
  `PipeTemplateHolder` and `RefrigSizeWriter.EnsureRegApp` unchanged.

**Manual tests in AutoCAD** (after `NETLOAD` of the rebuilt x64 DLL):

1. Place a thermostat and four to six copies of one indoor-unit block on both sides of a straight
   corridor, and draw a LWPOLYLINE from the thermostat to one unit's terminal point. Run
   `HVACCTRLWIRE`, select the units (the template's unit included), pick the template, press Enter at
   the thermostat prompt (check the circle is on the thermostat end), and accept the dialog.
2. Check the preview: each wire starts at its own offset from the thermostat end (150 mm apart),
   runs parallel, leaves at the point nearest its unit and ends on the same point of its block as
   the template does on its own. W1 is the first to leave; no two wires cross.
3. Answer Yes. Check the layer and `Broken Line` linetype, the `2x0.75 MM2` texts beside the exit
   legs, the template unchanged, and that one `UNDO` removes everything. Run again: the command
   line reports the earlier wires.
4. Try a template with a corner (L shape), a rotated and a mirrored unit, a different block among
   the units (it should use its insertion point), a unit beyond the end of the route, the `Other`
   keyword at the thermostat prompt, a template with an arc (refused), and Esc at each prompt.
