# Runs the headless HVACPLACEODU / HVACRISER writer tests in AutoCAD's core console.
#
#   powershell -File tests\OduRiserCoreTests\Acad\run-tests.ps1 -WorkDwg <a drawing without ODU blocks> [-AcadDir <folder>] [-TimeoutSec 600] [-NoBuild]
#
# - The working drawing and CAD\MEP_DWT_3.dwg are COPIED under %TEMP%; the originals are never opened.
# - accoreconsole runs with /isolate and its own profile folder (SECURELOAD / TRUSTEDPATHS writes stay
#   out of the user's AutoCAD registry keys); SECURELOAD is 0 before NETLOAD and 1 before QUIT.
# - Only the accoreconsole process this script starts is ever stopped. acad.exe is never touched.
# - Exit code 0 means every check passed.
param(
    [Parameter(Mandatory = $true)][string]$WorkDwg,
    [string]$AcadDir = 'C:\Program Files\Autodesk\AutoCAD 2024',
    [int]$TimeoutSec = 600,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$acc = Join-Path $AcadDir 'accoreconsole.exe'
$work = Join-Path $env:TEMP 'HvacAcadTests\oduriser'
$iso = Join-Path $env:TEMP 'HvacAcadTests\iso-oduriser'
New-Item -ItemType Directory -Force $work | Out-Null
New-Item -ItemType Directory -Force $iso | Out-Null

if (-not $NoBuild) {
    & dotnet build (Join-Path $repo 'tests\OduRiserCoreTests\Acad\OduRiserAcadTests.csproj') -c Debug -p:Platform=x64 "-p:AcadDir=$AcadDir" | Select-Object -Last 4
    if ($LASTEXITCODE -ne 0) { Write-Error 'build failed'; exit 2 }
}

$builtDll = Join-Path $env:TEMP 'OduRiserAcadTests\bin\x64\Debug\net48\OduRiserAcadTests.dll'
if (-not (Test-Path $builtDll)) { Write-Error "missing $builtDll"; exit 2 }
$dllDir = Join-Path $work 'dll'
New-Item -ItemType Directory -Force $dllDir | Out-Null
Copy-Item $builtDll $dllDir -Force
$dll = Join-Path $dllDir 'OduRiserAcadTests.dll'

$workCopy = Join-Path $work 'work.dwg'
$templateDwg = Join-Path $work 'template.dwg'
Copy-Item $WorkDwg $workCopy -Force
Copy-Item (Join-Path $repo 'CAD\MEP_DWT_3.dwg') $templateDwg -Force

$scr = Join-Path $work 'run.scr'
Set-Content -Encoding ASCII -Path $scr -Value @('SECURELOAD', '0', 'NETLOAD', "`"$dll`"", 'ODURISERTEST', 'SECURELOAD', '1', '_.QUIT', '_Y')

$log = Join-Path $work 'oduriser.log'
Remove-Item $log -ErrorAction SilentlyContinue
$env:HVACODU_LOG = $log
$env:HVACODU_TEMPLATE = $templateDwg

$out = Join-Path $work 'stdout.raw'
$argList = "/isolate hvacoduriser `"$iso`" /i `"$workCopy`" /s `"$scr`" /l en-US"
$p = Start-Process -FilePath $acc -ArgumentList $argList -RedirectStandardOutput $out -PassThru -NoNewWindow
$null = $p.Handle
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    Write-Output "TIMEOUT: stopping my accoreconsole pid $($p.Id)"
    Stop-Process -Id $p.Id -Force
}
Write-Output "accoreconsole exit code: $($p.ExitCode)"

if (-not (Test-Path $log)) { Write-Output 'no log written'; exit 2 }
$lines = Get-Content $log -Encoding UTF8
$lines | Where-Object { $_ -like 'FAIL*' -or $_ -like '  *' } | ForEach-Object { Write-Output $_ }
$summary = $lines | Where-Object { $_ -like 'SUMMARY*' } | Select-Object -Last 1
if (-not $summary) { Write-Output 'no SUMMARY line: the run stopped early (see the log)'; Write-Output "log: $log"; exit 2 }
Write-Output $summary
Write-Output "log: $log"
if ($summary -match 'failed=0$') { exit 0 } else { exit 1 }
