# Runs the ZWCAD tests: a smoke test of the add-in loaded into ZWCAD, and the writer tests that also
# exist for accoreconsole (HVACDUCT/HVACDUCTALIGN, HVACPLACEUNITS, HVACPLACEODU/HVACRISER, HVACNORM/
# HVACDUCTLINE), run inside ZWCAD on COPIES of the drawings.
#
#   powershell -File tests\Zw\run-zw-tests.ps1 [-Suite smoke|writers|scripted|all|duct|unit|oduriser|norm] [-Scenario all|name,name] [-ZwDir D:\ZWCAD] [-TimeoutSec 300] [-NoBuild]
#
# ZWCAD has no console like accoreconsole, so every suite runs in its own, separate ZWCAD.exe instance
# (started with  ZWCAD.exe [drawing] /b run.scr ), with the user's ZWCAD profile and settings. The script
# NETLOADs DLL copies from a fresh work folder %TEMP%\HvacAiZwTests\<timestamp>\, so the build output is
# never locked. Results come from log files the test DLLs write (PASS/FAIL/SUMMARY lines).
#
# Drawings: ZWCAD opens a drawing with missing SHX fonts (the template has them) with a blocking "Missing SHX Files"
# dialog, so the duct suite does not open its work copy as a document: HVACZWUSEWORKDWG (ZwSmokeTests) reads it into
# a side database and makes that the working database. The unit suite gets a synthetic rooms drawing from
# HVACZWMAKEROOMS the same way. The oduriser and norm suites use the blank Drawing1 ZWCAD starts with.
#
# The scripted suite (tests\Scripted, see CLAUDE.md) is different: it runs the COMMANDS of the add-in, with HVACAI_TEST=1 in the
# environment of its own ZWCAD instances (test mode: dialogs accepted with their defaults, nothing remembered). One instance
# builds the scenario drawings (HVACTESTBUILD), then one instance per scenario opens that DWG, feeds
# tests\Scripted\scenarios\<name>.scr and dumps the model space as JSON; tests\Scripted\compare.py checks the dumps. The
# dumps go to <work folder>\scripted\dump (hand that folder to run-acad-scripted.ps1 -ZwDump for the AutoCAD comparison).
#
# Safety:
# - The runner records the process id of every ZWCAD it starts and only ever stops THAT id (on timeout, or
#   when the instance did not leave by itself after the run). A ZWCAD, acad.exe or EXCEL.EXE of the user
#   is never closed, attached to or sent input.
# - If a dialog blocks the run, the instance is stopped at the timeout and the titles of its windows are
#   printed. No security/trust setting is changed. CAD\MEP_DWT_3.dwg is only copied, never opened.
# - If the per-user autoload key of register-zwcad.ps1 exists, the test instance would load the add-in a
#   second time; the run warns about it (the smoke test then fails "loaded exactly once").
# - Exit code 0 means every suite finished and every check passed; 1 failed checks; 2 a suite did not finish.
param(
    [ValidateSet('smoke', 'writers', 'scripted', 'all', 'duct', 'unit', 'oduriser', 'norm')][string]$Suite = 'all',
    [string]$Scenario = 'all',
    [switch]$StrictKnown,
    [string]$ZwDir = 'D:\ZWCAD',
    [int]$TimeoutSec = 300,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$zwExe = Join-Path $ZwDir 'ZWCAD.exe'
if (-not (Test-Path -LiteralPath $zwExe)) { Write-Error "ZWCAD.exe not found in $ZwDir (use -ZwDir)"; exit 2 }
$buildRoot = Join-Path $env:TEMP 'HvacAiZwTests\build'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $env:TEMP "HvacAiZwTests\$stamp"
New-Item -ItemType Directory -Force $work | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ZwWindows {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static List<string> Titles(int pid) {
        var list = new List<string>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == (uint)pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                if (sb.Length > 0) list.Add(sb.ToString());
            }
            return true; }, IntPtr.Zero);
        return list;
    }
}
'@

# ---------------------------------------------------------------- warnings about the environment
$others = @(Get-Process -Name ZWCAD -ErrorAction SilentlyContinue)
if ($others.Count -gt 0) {
    Write-Output ("NOTE: ZWCAD is already running (pid {0}); it is not touched. Each suite starts its own instance." -f (($others | ForEach-Object Id) -join ','))
}
$autoload = @(Get-ChildItem 'HKCU:\Software\ZWSOFT\ZWCAD\2026' -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.PSPath 'Applications\HvacAiCadAddin' } | Where-Object { Test-Path $_ })
if ($autoload.Count -gt 0) {
    Write-Output "WARNING: the autoload key exists ($($autoload -join '; ')). The test instance loads the add-in from it too, so the smoke test 'loaded exactly once' will fail. Run register-zwcad.ps1 -Unregister to test cleanly."
}

# ---------------------------------------------------------------- build
$projects = [ordered]@{
    addin    = @{ Proj = 'cad_addin\HvacAiCadAddin.Zw.csproj'; Dll = 'cad_addin\bin\Zw\Debug\net48\HvacAiCadAddin.dll' }
    smoke    = @{ Proj = 'tests\Zw\ZwSmokeTests\ZwSmokeTests.csproj'; Dll = "$buildRoot\smoke\bin\Debug\net48\ZwSmokeTests.dll" }
    duct     = @{ Proj = 'tests\Zw\ZwDuctTests\ZwDuctTests.csproj'; Dll = "$buildRoot\duct\bin\Debug\net48\DuctAcadTests.dll" }
    unit     = @{ Proj = 'tests\Zw\ZwUnitTests\ZwUnitTests.csproj'; Dll = "$buildRoot\unit\bin\Debug\net48\UnitAcadTests.dll" }
    oduriser = @{ Proj = 'tests\Zw\ZwOduRiserTests\ZwOduRiserTests.csproj'; Dll = "$buildRoot\oduriser\bin\Debug\net48\OduRiserAcadTests.dll" }
    norm     = @{ Proj = 'tests\Zw\ZwNormTests\ZwNormTests.csproj'; Dll = "$buildRoot\norm\bin\Debug\net48\NormAcadTests.dll" }
    scripted = @{ Proj = 'tests\Zw\ZwScriptedTests\ZwScriptedTests.csproj'; Dll = "$buildRoot\scripted\bin\Debug\net48\ScriptedTests.dll" }
}
$suites = switch ($Suite) { 'writers' { @('duct', 'unit', 'oduriser', 'norm') } 'all' { @('smoke', 'duct', 'unit', 'oduriser', 'norm', 'scripted') } default { @($Suite) } }
$needed = @('addin', 'smoke') + ($suites | Where-Object { $_ -ne 'smoke' })

if (-not $NoBuild) {
    foreach ($name in $needed) {
        Write-Output "building $name"
        $out = & dotnet build (Join-Path $repo $projects[$name].Proj) -c Debug -p:Platform=x64 "-p:ZwDir=$ZwDir" 2>&1
        if ($LASTEXITCODE -ne 0) { $out | Select-Object -Last 15 | ForEach-Object { Write-Output $_ }; Write-Error "build of $name failed"; exit 2 }
    }
}

# ---------------------------------------------------------------- copies: DLLs, drawings
$dllDir = Join-Path $work 'dll'
New-Item -ItemType Directory -Force $dllDir | Out-Null
$dlls = @{}
foreach ($name in $needed) {
    $src = $projects[$name].Dll
    if (-not [System.IO.Path]::IsPathRooted($src)) { $src = Join-Path $repo $src }
    if (-not (Test-Path -LiteralPath $src)) { Write-Error "missing $src (build first)"; exit 2 }
    $target = Join-Path $dllDir (Split-Path $src -Leaf)
    Copy-Item -LiteralPath $src $target -Force
    $dlls[$name] = $target
}
$template = Join-Path $work 'template.dwg'
Copy-Item -LiteralPath (Join-Path $repo 'CAD\MEP_DWT_3.dwg') $template -Force

# ---------------------------------------------------------------- one ZWCAD instance per suite
function Invoke-ZwSuite {
    param([string]$Name, [string[]]$Netload, [string[]]$Commands, [hashtable]$Env, [string]$Drawing = '', [string]$Log, [switch]$NoSummary, [switch]$NoFinish)

    $dir = Join-Path $work $Name
    New-Item -ItemType Directory -Force $dir | Out-Null
    $done = Join-Path $dir 'done.txt'
    Remove-Item $Log, $done -ErrorAction SilentlyContinue

    $lines = @()
    foreach ($dll in $Netload) { $lines += '_.NETLOAD'; $lines += "`"$dll`"" }
    $lines += $Commands
    if (-not $NoFinish) {   # a deferred scenario ends with HVACZWFINISH in its own queued tail
        $lines += 'HVACZWFINISH'
        $lines += '_.QUIT'      # reached only if HVACZWFINISH could not quit
        $lines += '_N'
    }
    $scr = Join-Path $dir 'run.scr'
    Set-Content -Encoding ASCII -Path $scr -Value $lines

    $Env['HVACZW_LOG'] = $Log
    $Env['HVACZW_DONE'] = $done
    $Env['HVACZW_TEMPLATE'] = $template
    $Env['HVACZW_ADDIN'] = $dlls['addin']
    foreach ($key in $Env.Keys) { Set-Item -Path "Env:$key" -Value $Env[$key] }

    $argList = @()
    if ($Drawing) { $argList += "`"$Drawing`"" }
    $argList += '/b'
    $argList += "`"$scr`""
    $before = @(Get-Process -Name ZWCAD -ErrorAction SilentlyContinue | ForEach-Object Id)
    $p = Start-Process -FilePath $zwExe -ArgumentList ($argList -join ' ') -WorkingDirectory $ZwDir -PassThru
    $pidMine = $p.Id
    Write-Host "[$Name] started ZWCAD pid $pidMine ($($argList -join ' '))"

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $doneSeen = $null
    $finished = $false
    $summary = $null
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        if ($p.HasExited) { $finished = $true; break }
        if (Test-Path $done) {
            if (-not $doneSeen) { $doneSeen = [Diagnostics.Stopwatch]::StartNew() }
            elseif ($doneSeen.Elapsed.TotalSeconds -gt 25) { break }   # asked to quit, but did not (the norm suite does this, see CLAUDE.md)
        }
        Start-Sleep -Milliseconds 500
    }
    $timedOut = -not $finished -and -not $doneSeen
    if (-not $p.HasExited) {
        $titles = @([ZwWindows]::Titles($pidMine))
        if ($timedOut) { Write-Host "[$Name] TIMEOUT after $TimeoutSec s; windows of pid ${pidMine}: $($titles -join ' | ')" }
        else {
            Write-Host "[$Name] done, but ZWCAD did not quit; windows: $($titles -join ' | ')"
            $kids = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$pidMine" -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name) pid $($_.ProcessId)" })
            Write-Host "[$Name] child processes of pid ${pidMine}: $($kids -join ', ')"
        }
        Write-Host "[$Name] stopping my own ZWCAD pid $pidMine"
        Stop-Process -Id $pidMine -Force -ErrorAction SilentlyContinue
        $null = $p.WaitForExit(15000)
    }
    $exit = if ($p.HasExited) { $p.ExitCode } else { 'n/a' }
    Write-Host "[$Name] ZWCAD exit code $exit after $([int]$sw.Elapsed.TotalSeconds) s"

    $result = [pscustomobject]@{ Name = $Name; Passed = 0; Failed = 0; Finished = $false; Fails = @(); Notes = @() }
    if ($NoSummary) {
        # Scripted suite: no SUMMARY line; the run counts as finished when HVACZWFINISH ran (the done file).
        $result.Finished = (Test-Path $done)
        return $result
    }
    if (-not (Test-Path $Log)) {
        Write-Host "[$Name] no log written (did the instance start and run the script? exit $exit)"
        return $result
    }
    $text = @(Get-Content $Log -Encoding UTF8)
    $summaryLine = $text | Where-Object { $_ -like 'SUMMARY*' } | Select-Object -Last 1
    $result.Fails = @($text | Where-Object { $_ -like 'FAIL*' })
    $result.Notes = @($text | Where-Object { $_ -like 'INFO*' })
    if ($summaryLine -match 'passed=(\d+) failed=(\d+)') {
        $result.Passed = [int]$Matches[1]; $result.Failed = [int]$Matches[2]; $result.Finished = $true
    } else {
        Write-Host "[$Name] no SUMMARY line; last log lines:"
        $text | Select-Object -Last 8 | ForEach-Object { Write-Host "    $_" }
    }
    $result.Notes | ForEach-Object { Write-Host "[$Name] $_" }
    $result.Fails | ForEach-Object { Write-Host "[$Name] $_" }
    Write-Host "[$Name] $summaryLine   log: $Log"
    return $result
}

$results = @()
foreach ($name in ($suites | Where-Object { $_ -ne 'scripted' })) {
    $log = Join-Path $work "$name\$name.log"
    New-Item -ItemType Directory -Force (Join-Path $work $name) | Out-Null
    switch ($name) {
        'smoke' {
            $results += Invoke-ZwSuite -Name 'smoke' -Netload @($dlls['addin'], $dlls['smoke']) -Commands @('HVACZWSMOKE') -Env @{} -Log $log
        }
        'duct' {
            $drawing = Join-Path $work 'duct\work.dwg'
            Copy-Item -LiteralPath $template $drawing -Force
            $results += Invoke-ZwSuite -Name 'duct' -Netload @($dlls['smoke'], $dlls['duct']) -Commands @('HVACZWUSEWORKDWG', 'DUCTTEST') -Log $log -Env @{
                HVACDUCT_LOG = $log; HVACDUCT_TEMPLATE = $template; HVACZW_WORKDWG = $drawing }
        }
        'unit' {
            $results += Invoke-ZwSuite -Name 'unit' -Netload @($dlls['smoke'], $dlls['unit']) -Commands @('HVACZWMAKEROOMS', 'UNITTEST') -Log $log -Env @{
                HVACUNITS_LOG = $log; HVACUNITS_TEMPLATE = $template }
        }
        'oduriser' {
            $results += Invoke-ZwSuite -Name 'oduriser' -Netload @($dlls['smoke'], $dlls['oduriser']) -Commands @('ODURISERTEST') -Log $log -Env @{
                HVACODU_LOG = $log; HVACODU_TEMPLATE = $template }
        }
        'norm' {
            $results += Invoke-ZwSuite -Name 'norm' -Netload @($dlls['smoke'], $dlls['norm']) -Commands @('NORMTEST', 'DUCTLINETEST') -Log $log -Env @{
                HVACNORM_LOG = $log; HVACNORM_TEMPLATE = $template
                HVACNORM_PYTHON = (Join-Path $repo 'venv\Scripts\python.exe'); HVACNORM_SCRIPT = (Join-Path $repo 'mep_normalize.py') }
        }
    }
}

if ($suites -contains 'scripted') {
    . (Join-Path $repo 'tests\Scripted\ScriptedCommon.ps1')
    $scenarioDir = Join-Path $repo 'tests\Scripted\scenarios'
    $names = Get-ScenarioNames -ScenarioDir $scenarioDir -Wanted $Scenario
    $sWork = Join-Path $work 'scripted'
    $dumpDir = Join-Path $sWork 'dump'
    New-Item -ItemType Directory -Force $sWork, $dumpDir | Out-Null
    $sLog = Join-Path $sWork 'scripted.log'
    $testEnv = @{
        HVACAI_TEST = '1'; HVACAI_TEST_TEMPLATE = $template; HVACAI_TEST_LOG = $sLog; HVACAI_TEST_WORK = $sWork
        HVACTEST_WORK = $sWork; HVACTEST_LOG = $sLog; HVACTEST_CAD = 'zwcad'; HVACTEST_CAD_VERSION = 'ZWCAD 2026'
        HVACTEST_ONLY = ($names -join ',') }
    # Instance 1 builds the scenario drawings; then one instance per scenario.
    $null = Invoke-ZwSuite -Name 'scripted-build' -Netload @($dlls['smoke'], $dlls['scripted']) -Commands @('HVACTESTBUILD') -Env $testEnv -Log (Join-Path $sWork 'build.zwlog') -NoSummary
    $notFinished = @()
    foreach ($name in $names) {
        $dwg = Join-Path $sWork "drawings\$name.dwg"
        if (-not (Test-Path -LiteralPath $dwg)) { Write-Host "[scripted] scenario drawing not built: $name"; $notFinished += $name; continue }
        $body = Get-ScenarioBody -ScenarioDir $scenarioDir -Name $name -Cad 'zwcad'
        $built = New-ScenarioScript -Pre @('HVACTESTINFO', 'HVACTESTTRACE', 'HVACTESTSAFE', '_.ZOOM', '_E') -Body $body -Footer @('HVACTESTRESTORE', 'HVACZWFINISH') -TailPath (Join-Path $sWork "$name.tail.scr")
        $null = Invoke-ZwSuite -Name "s_$name" -Netload @($dlls['addin'], $dlls['smoke'], $dlls['scripted']) -Commands $built.Lines -NoFinish:$built.Deferred -Drawing $dwg -Env $testEnv -Log (Join-Path $sWork "$name.zwlog") -NoSummary
        if (-not (Test-Path (Join-Path $dumpDir "$name.zwcad.json"))) { $notFinished += $name }
    }
    # HVACTESTSAFE sets OSMODE 0 in the user's ZWCAD profile and HVACTESTRESTORE puts it back; a killed instance leaves
    # <work>\osmode.orig behind, and one more instance restores the value from it.
    if (Test-Path (Join-Path $sWork 'osmode.orig')) {
        Write-Host "[scripted] WARNING: OSMODE was not restored by a killed instance; restoring it from osmode.orig"
        $null = Invoke-ZwSuite -Name 'scripted-repair' -Netload @($dlls['smoke'], $dlls['scripted']) -Commands @('HVACTESTRESTORE') -Env $testEnv -Log (Join-Path $sWork 'repair.zwlog') -NoSummary
    }
    foreach ($key in $testEnv.Keys) { Remove-Item "Env:$key" -ErrorAction SilentlyContinue }
    Read-ScenarioLog $sLog | Where-Object { $_ -like 'FAIL*' -or $_ -like 'TESTMODE*' } | ForEach-Object { Write-Host "[scripted] $_" }
    $cmp = Invoke-ScenarioCompare -Repo $repo -DumpDir $dumpDir -Scenarios $names -Cads @('zwcad') -ReportPath (Join-Path $sWork 'report.txt') -Strict:$StrictKnown
    $summary = $cmp.Summary
    $r = [pscustomobject]@{ Name = 'scripted'; Passed = 0; Failed = 0; Finished = ($notFinished.Count -eq 0 -and [bool]$summary); Fails = @(); Notes = @() }
    if ("$summary" -match 'passed=(\d+) failed=(\d+)') { $r.Passed = [int]$Matches[1]; $r.Failed = [int]$Matches[2] }
    if ($notFinished.Count -gt 0) { Write-Host "[scripted] did not finish: $($notFinished -join ', ')" }
    $results += $r
}

# ---------------------------------------------------------------- summary
Write-Output ''
Write-Output 'ZWCAD test summary'
$exitCode = 0
foreach ($r in $results) {
    $state = if (-not $r.Finished) { 'DID NOT FINISH' } elseif ($r.Failed -gt 0) { 'FAIL' } else { 'PASS' }
    Write-Output ('  {0,-9} {1,-14} passed={2} failed={3}' -f $r.Name, $state, $r.Passed, $r.Failed)
    if (-not $r.Finished) { $exitCode = 2 } elseif ($r.Failed -gt 0 -and $exitCode -eq 0) { $exitCode = 1 }
}
$totalPass = ($results | Measure-Object Passed -Sum).Sum
$totalFail = ($results | Measure-Object Failed -Sum).Sum
Write-Output "TOTAL passed=$totalPass failed=$totalFail   work folder: $work"
exit $exitCode
