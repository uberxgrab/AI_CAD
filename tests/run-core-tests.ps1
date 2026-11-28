# Runs every AutoCAD-free C# core test project (they run under en-US, vi-VN and tr-TR) and summarises.
# These cores are shared by the AutoCAD and the ZWCAD build of the add-in, so this is the part of the
# test suite that does not depend on either CAD.
#
#   powershell -File tests\run-core-tests.ps1 [-Only Duct,Norm]
#
# Exit code 0 = every project exited 0 and reported no failures.
param(
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projects = [ordered]@{
    Pipe    = 'tests\PipeCoreTests\PipeCoreTests.csproj'
    Duct    = 'tests\DuctCoreTests\DuctCoreTests.csproj'
    Qto     = 'tests\HvacQtoCoreTests\HvacQtoCoreTests.csproj'
    Rooms   = 'tests\RoomsCoreTests\RoomsCoreTests.csproj'
    Load    = 'tests\LoadCoreTests\LoadCoreTests.csproj'
    Unit    = 'tests\UnitCoreTests\UnitCoreTests.csproj'
    Route   = 'tests\RouteCoreTests\RouteCoreTests.csproj'
    OduRiser = 'tests\OduRiserCoreTests\OduRiserCoreTests.csproj'
    Norm    = 'tests\NormCoreTests\NormCoreTests.csproj'
}

$rows = @()
$exitCode = 0
foreach ($name in $projects.Keys) {
    if ($Only.Count -gt 0 -and $Only -notcontains $name) { continue }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & dotnet run --project (Join-Path $repo $projects[$name]) -c Debug 2>&1
    $code = $LASTEXITCODE
    $total = $out | Where-Object { $_ -match '^TOTAL: (\d+) passed, (\d+) failed' } | Select-Object -Last 1
    $passed = 0; $failed = 0; $ok = $false
    if ($total -match '^TOTAL: (\d+) passed, (\d+) failed') { $passed = [int]$Matches[1]; $failed = [int]$Matches[2]; $ok = $true }
    $state = if ($code -ne 0 -or -not $ok -or $failed -gt 0) { 'FAIL' } else { 'PASS' }
    if ($state -eq 'FAIL') {
        $exitCode = 1
        $out | Where-Object { $_ -match 'FAIL|error' } | Select-Object -First 15 | ForEach-Object { Write-Output "  [$name] $_" }
        if (-not $ok) { $out | Select-Object -Last 6 | ForEach-Object { Write-Output "  [$name] $_" } }
    }
    $rows += [pscustomobject]@{ Project = $name; State = $state; Passed = $passed; Failed = $failed; Exit = $code; Seconds = [int]$sw.Elapsed.TotalSeconds }
}

Write-Output ''
$rows | Format-Table -AutoSize | Out-String | ForEach-Object { Write-Output $_.TrimEnd() }
Write-Output ("TOTAL: {0} passed, {1} failed in {2} projects" -f ($rows | Measure-Object Passed -Sum).Sum, ($rows | Measure-Object Failed -Sum).Sum, $rows.Count)
exit $exitCode
