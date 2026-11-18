# AutoCAD managed add-in for HVAC AI

This directory is deliberately separate from the Python HVAC validation engine. It contains a C# managed AutoCAD add-in that runs *inside* AutoCAD and can later call the HVAC AI application through a stable JSON/file or local-HTTP contract.

## What to install

Install a licensed AutoCAD for Windows, Visual Studio with the **.NET desktop development** workload, and the .NET Framework 4.8 targeting pack. This project targets AutoCAD 2023 and .NET Framework 4.8.

The project references AutoCAD's installed managed assemblies directly:

- `C:\Program Files\Autodesk\AutoCAD 2023\AcCoreMgd.dll`
- `C:\Program Files\Autodesk\AutoCAD 2023\AcDbMgd.dll`
- `C:\Program Files\Autodesk\AutoCAD 2023\AcMgd.dll`

Each reference has `Copy Local` set to `False`, so the Autodesk DLLs are never copied beside the built add-in.

For AutoCAD 2027, Autodesk documents Visual Studio 2026 / VC tools 14.44 and .NET 10 for managed code.  This project is native C++, but still needs the corresponding ObjectARX release and compiler toolset.  For a different AutoCAD version, use that release's matching SDK and set the database library name below.

## Build

Open `HvacAiCadAddin.csproj` in Visual Studio, select the `Debug|x64` configuration, and build. Or build from a Visual Studio developer prompt:

```powershell
msbuild HvacAiCadAddin.csproj /p:Configuration=Release /p:Platform=x64
```

The resulting assembly is `bin\x64\Release\HvacAiCadAddin.dll`. The `Debug|x64` project configuration launches `C:\Program Files\Autodesk\AutoCAD 2023\acad.exe` when debugging.

## Load and try it

1. In AutoCAD, run `NETLOAD` and select the generated `HvacAiCadAddin.dll` file.
2. Run `HVACAI` to invoke the main command, which obtains the active `Document`,
   its `Database`, and its `Editor`.
3. Run `ME` to write `Hello from HVAC AI!` to AutoCAD's command line.
4. Select objects, then run `HVACQTO`, or run it and select objects when prompted.
   In the settings dialog, set the contingency factor, the duct sheet thickness
   table, and any shaft risers the drawing does not show. Then choose where to save
   the Excel-compatible XML workbook. The dialog remembers the last values.

`HVACQTO` writes one main sheet laid out like the VRV quotation template, with
equipment, ducts, and pipes as groups, plus a duct appendix sheet. Only the
description, unit, and quantity columns are filled. Prices, product codes, notes,
and project details stay empty.

Duct and pipe quantities are Excel formulas, for example `=(12.345+3.2+3.5*2)*R6C6`
for a pipe with two drawn segments and two 3.5 m shaft runs. Every such formula
multiplies by the contingency factor in cell `F6` of the main sheet, so editing
that cell updates the BOQ. A row with too many segments for one Excel formula is
listed segment by segment on an extra `PHU LUC DOAN ONG` sheet.

Dynamic blocks with a positive `Distance3` and a block name containing a size such
as `600x300`, `D250`, or `Ø250` are ducts. Duct area uses the rectangular perimeter
or circular circumference multiplied by `Distance3`. The main sheet totals that
area per sheet thickness, and the appendix lists each size with its count, length,
and area.

Lines and polylines are classified by layer, first match wins:

| Layer contains | Exported as |
| --- | --- |
| `CHWS`, `CHWR`, `HWS`, `HWR`, `CWS`, `CWR` | Galvanized welded steel pipe, SCH40 |
| A pair such as `15.9/9.5` or `RP 15.9-9.5` | Copper pipe, with the full length added to both sizes |
| `CONDENSATED`, or a symbol such as `D34` or `DN50` | uPVC Class 1 condensate pipe |

Other layers are not exported, and the command line reports them. A hyphen pair
counts as copper only after `RP`, and text in parentheses is ignored, so
`EIA_1 RP 28.6-12.7 (33.6-44.8)` gives sizes 28.6 and 12.7.

The export assumes drawing units are millimetres and converts lengths to metres
and surface areas to square metres.

AutoCAD keeps a loaded add-in until it exits, so restart AutoCAD before you
`NETLOAD` a rebuilt DLL.

## Integration boundary

Keep CAD UI and AutoCAD database calls in `src/`. Put reusable HVAC analysis in the existing Python modules. The planned integration contract is a JSON request containing the active DWG path, selected object handles, and add-in options; Python returns a JSON result for the managed command to turn into AutoCAD annotations. This avoids embedding Python or AI dependencies in the AutoCAD process.

`python_bridge.py` is an intentionally dependency-free discovery helper for a future HVAC AI UI/CLI option.  It identifies whether a generated ARX exists and which AutoCAD installation is available; it does not attempt to automate or alter a drawing.
