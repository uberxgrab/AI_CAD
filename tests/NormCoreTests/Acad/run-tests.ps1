# Runs the headless HVACNORM test in AutoCAD's core console: blank drawing, snapshot, mep_normalize.py,
# planner, layer import from the template, NormWriter, then the HVACQTO core on the result.
#
#   powershell -File tests\NormCoreTests\Acad\run-tests.ps1 [-AcadDir <folder>] [-TimeoutSec 600] [-NoBuild]
#   ... -RealDwg <drawing> [-Duct]   runs NORMREAL (or HVACNORM then DUCTLINEREAL) on a COPY of that drawing instead (proposed map, all fixes,
#                            takeoff lines in the log; the copy is never saved)
#
# - CAD\MEP_DWT_3.dwg is COPIED under %TEMP%; the original is never opened. No drawing is saved.
# - accoreconsole runs with /isolate and its own profile folder (SECURELOAD / TRUSTEDPATHS writes stay
#   out of the user's AutoCAD registry keys); SECURELOAD is 0 before NETLOAD and 1 before QUIT.
# - Only the accoreconsole process this script starts is ever stopped. acad.exe is never touched.
# - Exit code 0 means every check passed.
param(
    [string]$AcadDir = 'C:\Program Files\Autodesk\AutoCAD 2024',
    [string]$RealDwg = '',
    [int]$TimeoutSec = 600,
    [switch]$NoBuild,
    [switch]$Duct
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$acc = Join-Path $AcadDir 'accoreconsole.exe'
$work = Join-Path $env:TEMP 'HvacAcadTests\norm'
$iso = Join-Path $env:TEMP 'HvacAcadTests\iso-norm'
New-Item -ItemType Directory -Force $work | Out-Null
New-Item -ItemType Directory -Force $iso | Out-Null

if (-not $NoBuild) {
    & dotnet build (Join-Path $repo 'tests\NormCoreTests\Acad\NormAcadTests.csproj') -c Debug -p:Platform=x64 "-p:AcadDir=$AcadDir" | Select-Object -Last 4
    if ($LASTEXITCODE -ne 0) { Write-Error 'build failed'; exit 2 }
}

$builtDll = Join-Path $env:TEMP 'NormAcadTests\bin\x64\Debug\net48\NormAcadTests.dll'
if (-not (Test-Path $builtDll)) { Write-Error "missing $builtDll"; exit 2 }
$dllDir = Join-Path $work 'dll'
New-Item -ItemType Directory -Force $dllDir | Out-Null
Copy-Item $builtDll $dllDir -Force
$dll = Join-Path $dllDir 'NormAcadTests.dll'

$templateDwg = Join-Path $work 'template.dwg'
Copy-Item (Join-Path $repo 'CAD\MEP_DWT_3.dwg') $templateDwg -Force

$scr = Join-Path $work 'run.scr'
$commands = @('NORMTEST', 'DUCTLINETEST')
$open = ''
if ($RealDwg) {
    $realCopy = Join-Path $work 'real.dwg'
    Copy-Item -LiteralPath $RealDwg $realCopy -Force
    $commands = @($(if ($Duct) { 'DUCTLINEREAL' } else { 'NORMREAL' }))
    $open = "/i `"$realCopy`" "
}
Set-Content -Encoding ASCII -Path $scr -Value (@('SECURELOAD', '0', 'NETLOAD', "`"$dll`"") + $commands + @('SECURELOAD', '1', '_.QUIT', '_Y'))

$log = Join-Path $work 'norm.log'
Remove-Item $log -ErrorAction SilentlyContinue
$env:HVACNORM_LOG = $log
$env:HVACNORM_TEMPLATE = $templateDwg
$env:HVACNORM_PYTHON = Join-Path $repo 'venv\Scripts\python.exe'
$env:HVACNORM_SCRIPT = Join-Path $repo 'mep_normalize.py'

$out = Join-Path $work 'stdout.raw'
$argList = "/isolate hvacnorm `"$iso`" $open/s `"$scr`" /l en-US"
$p = Start-Process -FilePath $acc -ArgumentList $argList -RedirectStandardOutput $out -PassThru -NoNewWindow
$null = $p.Handle
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    Write-Output "TIMEOUT: stopping my accoreconsole pid $($p.Id)"
    Stop-Process -Id $p.Id -Force
}
Write-Output "accoreconsole exit code: $($p.ExitCode)"

if (-not (Test-Path $log)) { Write-Output 'no log written'; exit 2 }
$lines = Get-Content $log -Encoding UTF8
$lines | Where-Object { $_ -like 'FAIL*' -or $_ -like 'written*' -or $_ -like 'ductline*' -or $_ -like 'ducts:*' -or $_ -like 'fittings:*' -or ($RealDwg -and $_ -notlike 'PASS*' -and $_ -notlike 'SUMMARY*') } | ForEach-Object { Write-Output $_ }
$summary = $lines | Where-Object { $_ -like 'SUMMARY*' } | Select-Object -Last 1
if (-not $summary) { Write-Output 'no SUMMARY line: the run stopped early (see the log)'; Write-Output "log: $log"; exit 2 }
Write-Output $summary
Write-Output "log: $log"
if ($summary -match 'failed=0$') { exit 0 } else { exit 1 }
