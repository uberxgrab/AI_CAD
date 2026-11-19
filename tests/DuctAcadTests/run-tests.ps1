# Runs the headless duct tests in AutoCAD's core console on COPIES of CAD\MEP_DWT_3.dwg.
#
#   powershell -File tests\DuctAcadTests\run-tests.ps1 [-TimeoutSec 600] [-NoBuild]
#
# - Everything is built and copied under %TEMP%; the original template is never opened.
# - accoreconsole runs with /isolate and its own profile folder, so SECURELOAD and TRUSTEDPATHS
#   writes never reach the registry keys the user's AutoCAD uses. The script still sets SECURELOAD
#   to 0 before NETLOAD and back to 1 before QUIT.
# - Only the accoreconsole process this script starts is ever stopped. acad.exe is never touched.
# - Exit code 0 means every check passed.
param(
    [int]$TimeoutSec = 600,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$acc = 'C:\Program Files\Autodesk\AutoCAD 2023\accoreconsole.exe'
$work = Join-Path $env:TEMP 'HvacAcadTests\duct'
$iso = Join-Path $env:TEMP 'HvacAcadTests\iso-duct'
New-Item -ItemType Directory -Force $work | Out-Null
New-Item -ItemType Directory -Force $iso | Out-Null

if (-not $NoBuild) {
    & dotnet build (Join-Path $repo 'tests\DuctAcadTests\DuctAcadTests.csproj') -c Debug -p:Platform=x64 | Select-Object -Last 4
    if ($LASTEXITCODE -ne 0) { Write-Error 'build failed'; exit 2 }
}

$builtDll = Join-Path $env:TEMP 'DuctAcadTests\bin\x64\Debug\net48\DuctAcadTests.dll'
if (-not (Test-Path $builtDll)) { Write-Error "missing $builtDll"; exit 2 }
$dllDir = Join-Path $work 'dll'
New-Item -ItemType Directory -Force $dllDir | Out-Null
Copy-Item $builtDll $dllDir -Force
$dll = Join-Path $dllDir 'DuctAcadTests.dll'

# Two copies: the drawing the commands edit, and the template they import from.
$workDwg = Join-Path $work 'work.dwg'
$templateDwg = Join-Path $work 'template.dwg'
Copy-Item (Join-Path $repo 'CAD\MEP_DWT_3.dwg') $workDwg -Force
Copy-Item (Join-Path $repo 'CAD\MEP_DWT_3.dwg') $templateDwg -Force

$scr = Join-Path $work 'run.scr'
Set-Content -Encoding ASCII -Path $scr -Value @('SECURELOAD', '0', 'NETLOAD', "`"$dll`"", 'DUCTTEST', 'SECURELOAD', '1', '_.QUIT', '_Y')

$log = Join-Path $work 'duct.log'
Remove-Item $log -ErrorAction SilentlyContinue
$env:HVACDUCT_LOG = $log
$env:HVACDUCT_TEMPLATE = $templateDwg

$out = Join-Path $work 'stdout.raw'
$argList = "/isolate hvacduct `"$iso`" /i `"$workDwg`" /s `"$scr`" /l en-US"
$p = Start-Process -FilePath $acc -ArgumentList $argList -RedirectStandardOutput $out -PassThru -NoNewWindow
$null = $p.Handle
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    Write-Output "TIMEOUT: stopping my accoreconsole pid $($p.Id)"
    Stop-Process -Id $p.Id -Force
}
Write-Output "accoreconsole exit code: $($p.ExitCode)"

if (-not (Test-Path $log)) { Write-Output 'no log written'; exit 2 }
$lines = Get-Content $log -Encoding UTF8
$lines | Where-Object { $_ -like 'FAIL*' -or $_ -like '==*' -or $_ -like '  *' } | ForEach-Object { Write-Output $_ }
$summary = $lines | Where-Object { $_ -like 'SUMMARY*' } | Select-Object -Last 1
if (-not $summary) { Write-Output 'no SUMMARY line: the run stopped early (see the log)'; Write-Output "log: $log"; exit 2 }
Write-Output $summary
Write-Output "log: $log"
if ($summary -match 'failed=0$') { exit 0 } else { exit 1 }
