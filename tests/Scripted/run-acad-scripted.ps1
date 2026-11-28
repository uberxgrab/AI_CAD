# Scripted end-to-end tests of the interactive commands on AutoCAD 2023, in accoreconsole.
#
#   powershell -File tests\Scripted\run-acad-scripted.ps1 [-Scenario all|name,name] [-TimeoutSec 240] [-NoBuild] [-Acc <accoreconsole.exe>]
#
# How it works (see CLAUDE.md, "Scripted end-to-end tests"):
# - The production add-in DLL (cad_addin, x64 Debug) runs UNCHANGED in accoreconsole with HVACAI_TEST=1: in test mode
#   its modal dialogs are accepted automatically with their default values (plain WinForms ShowDialog, so no AcMgd
#   dialog service is needed), nothing remembered is read or written, and the template is the copy named by
#   HVACAI_TEST_TEMPLATE. Commands that need the screen (HVACSTRETCH's PointMonitor preview, jigs) are exercised
#   as far as accoreconsole allows; whatever differs shows up in the dump.
# - Phase 1: one accoreconsole run builds every scenario drawing from a blank Database (HVACTESTBUILD in
#   ScriptedTests.dll) and saves it as a DWG. Phase 2: one accoreconsole run per scenario opens that DWG
#   (/i), NETLOADs add-in and test DLL, feeds tests\Scripted\scenarios\<name>.scr, dumps the model space to JSON
#   (HVACTESTDUMP), and quits.
# - tests\Scripted\compare.py checks every dump against tests\Scripted\expected\<name>.json, and against the
#   ZWCAD dump of the same scenario when one exists in the same work folder tree (-ZwDump <folder>).
# - accoreconsole runs with /isolate (own profile folder), so SECURELOAD and TRUSTEDPATHS writes never reach the
#   registry keys of the user's AutoCAD; SECURELOAD is 0 before NETLOAD and 1 before QUIT. acad.exe is never
#   touched; only the accoreconsole processes this script starts are ever stopped. The template is only copied.
# - Exit code 0: every scenario ran and every check passed; 1 failed checks; 2 a run did not finish.
param(
    [string]$Scenario = 'all',
    [int]$TimeoutSec = 240,
    [switch]$NoBuild,
    [string]$Acc = 'C:\Program Files\Autodesk\AutoCAD 2023\accoreconsole.exe',
    [string]$ZwDump = '',
    [string]$Work = '',
    [switch]$StrictKnown
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'ScriptedCommon.ps1')
if (-not (Test-Path -LiteralPath $Acc)) { Write-Error "accoreconsole.exe not found: $Acc"; exit 2 }
$scenarioDir = Join-Path $PSScriptRoot 'scenarios'
$names = Get-ScenarioNames -ScenarioDir $scenarioDir -Wanted $Scenario

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $Work) { $Work = Join-Path $env:TEMP "HvacScripted\$stamp\acad" }
$dumpDir = Join-Path $Work 'dump'
$iso = Join-Path $env:TEMP 'HvacScripted\iso-acad'
New-Item -ItemType Directory -Force $Work, $dumpDir, $iso | Out-Null

if (-not $NoBuild) {
    foreach ($proj in @('cad_addin\HvacAiCadAddin.csproj', 'tests\Scripted\ScriptedTests\ScriptedTests.csproj')) {
        Write-Output "building $proj"
        $out = & dotnet build (Join-Path $repo $proj) -c Debug -p:Platform=x64 2>&1
        if ($LASTEXITCODE -ne 0) { $out | Select-Object -Last 15 | ForEach-Object { Write-Output $_ }; Write-Error "build of $proj failed"; exit 2 }
    }
}
$addinSrc = Join-Path $repo 'cad_addin\bin\x64\Debug\net48\HvacAiCadAddin.dll'
$testSrc = Join-Path $env:TEMP 'HvacScripted\build\acad\bin\x64\Debug\net48\ScriptedTests.dll'
foreach ($f in @($addinSrc, $testSrc)) { if (-not (Test-Path -LiteralPath $f)) { Write-Error "missing $f (build first)"; exit 2 } }
$dllDir = Join-Path $Work 'dll'
New-Item -ItemType Directory -Force $dllDir | Out-Null
Copy-Item -LiteralPath $addinSrc, $testSrc $dllDir -Force
$addin = Join-Path $dllDir 'HvacAiCadAddin.dll'
$tests = Join-Path $dllDir 'ScriptedTests.dll'
$template = Join-Path $Work 'template.dwg'
Copy-Item -LiteralPath (Join-Path $repo 'CAD\MEP_DWT_3.dwg') $template -Force

$log = Join-Path $Work 'scripted.log'
Remove-Item $log -ErrorAction SilentlyContinue
$env:HVACAI_TEST = '1'
$env:HVACAI_TEST_TEMPLATE = $template
$env:HVACAI_TEST_LOG = $log
$env:HVACAI_TEST_WORK = $Work
$env:HVACTEST_WORK = $Work
$env:HVACTEST_LOG = $log
$env:HVACTEST_CAD = 'acad'
$env:HVACTEST_CAD_VERSION = 'AutoCAD 2023 accoreconsole'
Remove-Item Env:HVACAI_TEST_SETTINGS, Env:HVACTEST_ONLY -ErrorAction SilentlyContinue

function Invoke-Acc {
    param([string]$Label, [string]$Drawing, [string[]]$ScriptLines)
    $dir = Join-Path $Work $Label
    New-Item -ItemType Directory -Force $dir | Out-Null
    $scr = Join-Path $dir 'run.scr'
    Set-Content -Encoding ASCII -Path $scr -Value $ScriptLines
    $out = Join-Path $dir 'stdout.txt'
    $argList = "/isolate hvacscr `"$iso`" "
    if ($Drawing) { $argList += "/i `"$Drawing`" " }
    $argList += "/s `"$scr`" /l en-US"
    $p = Start-Process -FilePath $Acc -ArgumentList $argList -RedirectStandardOutput $out -PassThru -NoNewWindow
    $null = $p.Handle
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $finished = $p.WaitForExit($TimeoutSec * 1000)
    if (-not $finished) {
        Write-Host "[$Label] TIMEOUT after $TimeoutSec s: stopping my accoreconsole pid $($p.Id)"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        $null = $p.WaitForExit(15000)
    }
    Write-Host ("[{0}] accoreconsole exit {1} after {2} s" -f $Label, $(if ($p.HasExited) { $p.ExitCode } else { 'n/a' }), [int]$sw.Elapsed.TotalSeconds)
    return $finished
}

$header = @('SECURELOAD', '0', 'NETLOAD', "`"$addin`"", 'NETLOAD', "`"$tests`"")
$footer = @('HVACTESTRESTORE', 'SECURELOAD', '1', '_.QUIT', '_Y')

# ---------------------------------------------------------------- phase 1: build the scenario drawings
$env:HVACTEST_ONLY = ($names -join ',')
$host_dwg = Join-Path $Work 'host.dwg'   # the drawing the build run starts with (the template itself is read by TemplateSource)
Copy-Item -LiteralPath $template $host_dwg -Force
$ok = Invoke-Acc -Label 'build' -Drawing $host_dwg -ScriptLines ($header + @('HVACTESTBUILD') + $footer)
Remove-Item Env:HVACTEST_ONLY
$lines = Read-ScenarioLog $log
$lines | Where-Object { $_ -like 'FAIL*' } | ForEach-Object { Write-Host "[build] $_" }
$missing = @($names | Where-Object { -not (Test-Path (Join-Path $Work "drawings\$_.dwg")) })
if ($missing.Count -gt 0) { Write-Host "scenario drawings not built: $($missing -join ', ')" }

# ---------------------------------------------------------------- phase 2: one run per scenario
$notFinished = @()
foreach ($name in $names) {
    $dwg = Join-Path $Work "drawings\$name.dwg"
    if (-not (Test-Path -LiteralPath $dwg)) { $notFinished += $name; continue }
    $body = Get-ScenarioBody -ScenarioDir $scenarioDir -Name $name -Cad 'acad'
    $tail = Join-Path $Work "$name\tail.scr"
    New-Item -ItemType Directory -Force (Join-Path $Work $name) | Out-Null
    $script = New-ScenarioScript -Pre ($header + @('HVACTESTINFO', 'HVACTESTTRACE', 'HVACTESTSAFE', '_.ZOOM', '_E')) -Body $body -Footer $footer -TailPath $tail
    $ok = Invoke-Acc -Label $name -Drawing $dwg -ScriptLines $script.Lines
    if (-not (Test-Path (Join-Path $dumpDir "$name.acad.json"))) { $notFinished += $name }
}

Write-Output ''
Write-Output "log: $log"
$lines = Read-ScenarioLog $log
$lines | Where-Object { $_ -like 'FAIL*' -or $_ -like 'TESTMODE*' } | ForEach-Object { Write-Output $_ }

# ---------------------------------------------------------------- compare
$cads = @('acad')
$dumps = $dumpDir
if ($ZwDump) {
    foreach ($f in Get-ChildItem -LiteralPath $ZwDump -Filter '*.zwcad.*') { Copy-Item -LiteralPath $f.FullName $dumpDir -Force }
    $cads = @('acad', 'zwcad')
}
$cmp = Invoke-ScenarioCompare -Repo $repo -DumpDir $dumpDir -Scenarios $names -Cads $cads -ReportPath (Join-Path $Work 'report.txt') -Strict:$StrictKnown
Write-Output "work folder: $Work"
if ($notFinished.Count -gt 0) { Write-Output "DID NOT FINISH: $($notFinished -join ', ')"; exit 2 }
exit $cmp.ExitCode
