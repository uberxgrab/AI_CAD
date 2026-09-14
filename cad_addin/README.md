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

## Integration boundary

Keep CAD UI and AutoCAD database calls in `src/`. Put reusable HVAC analysis in the existing Python modules. The planned integration contract is a JSON request containing the active DWG path, selected object handles, and add-in options; Python returns a JSON result for the managed command to turn into AutoCAD annotations. This avoids embedding Python or AI dependencies in the AutoCAD process.

`python_bridge.py` is an intentionally dependency-free discovery helper for a future HVAC AI UI/CLI option.  It identifies whether a generated ARX exists and which AutoCAD installation is available; it does not attempt to automate or alter a drawing.
