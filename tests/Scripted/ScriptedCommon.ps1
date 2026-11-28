# Shared by run-acad-scripted.ps1 and tests\Zw\run-zw-tests.ps1 (-Suite scripted).

# The scenario names: every tests\Scripted\scenarios\<name>.scr, or the comma-separated list asked for.
function Get-ScenarioNames {
    param([string]$ScenarioDir, [string]$Wanted)
    $all = @(Get-ChildItem -LiteralPath $ScenarioDir -Filter '*.scr' | ForEach-Object { $_.BaseName } | Sort-Object)
    if (-not $Wanted -or $Wanted -eq 'all') { return $all }
    $names = @($Wanted -split '[,;]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    foreach ($n in $names) { if ($all -notcontains $n) { throw "unknown scenario '$n'; known: $($all -join ', ')" } }
    return $names
}

# The script body of one scenario: tests\Scripted\scenarios\<name>.scr. A <name>.<cad>.scr beside it replaces it for
# that CAD (only when a real prompt difference needs it). Blank lines are meaningful (Enter); lines that start with
# a semicolon are comments (AutoCAD and ZWCAD both skip them).
function Get-ScenarioBody {
    param([string]$ScenarioDir, [string]$Name, [string]$Cad)
    $own = Join-Path $ScenarioDir "$Name.$Cad.scr"
    $path = if (Test-Path -LiteralPath $own) { $own } else { Join-Path $ScenarioDir "$Name.scr" }
    return @(Get-Content -LiteralPath $path -Encoding ASCII)
}

# Reads the log lines of a run (UTF-8). Returns the lines.
function Read-ScenarioLog {
    param([string]$Log)
    if (-not (Test-Path -LiteralPath $Log)) { return @() }
    return @(Get-Content -LiteralPath $Log -Encoding UTF8)
}

# Runs tests\Scripted\compare.py (repo venv): each dump against its expectations, and the CADs against each other.
# Known failures (tests\Scripted\known-failures.json) are reported as KNOWN and do not fail the run unless -Strict.
# Returns the exit code, the SUMMARY line and every output line.
function Invoke-ScenarioCompare {
    param([string]$Repo, [string]$DumpDir, [string[]]$Scenarios, [string[]]$Cads, [string]$ReportPath, [switch]$Strict)
    $python = Join-Path $Repo 'venv\Scripts\python.exe'
    $script = Join-Path $Repo 'tests\Scripted\compare.py'
    $expected = Join-Path $Repo 'tests\Scripted\expected'
    $known = Join-Path $Repo 'tests\Scripted\known-failures.json'
    $cmdArgs = @('-I', $script, '--expected', $expected, '--dumps', $DumpDir, '--scenarios', ($Scenarios -join ','), '--cads', ($Cads -join ','), '--report', $ReportPath, '--known', $known)
    if ($Strict) { $cmdArgs += '--strict' }
    $lines = @(& $python @cmdArgs)
    $code = $LASTEXITCODE
    $lines | ForEach-Object { Write-Host $_ }
    $summary = $lines | Where-Object { "$_" -like 'SUMMARY*' } | Select-Object -Last 1
    return [pscustomobject]@{ ExitCode = $code; Summary = "$summary"; Lines = $lines }
}

# Builds the script lines of one scenario. $Pre is what runs before the body (info, trace, zoom), $Footer what
# ends the run (quit). A body line ";;DEFER" splits the body: the lines after it, then HVACTESTDUMP and the footer,
# go to $TailPath and run as typed input once the command before the marker has ended and the script has ended
# (see HVACTESTDEFER). HVACSTRETCH needs this: it queues native STRETCH with SendStringToExecute, and a CAD runs a
# queued string only after the script has ended, so the second point and everything after it must be queued behind
# that string. Without the marker the whole script runs in order and ends with the dump and the footer.
function New-ScenarioScript {
    param([string[]]$Pre, [string[]]$Body, [string[]]$Footer, [string]$TailPath)
    $at = [Array]::IndexOf($Body, ';;DEFER')
    if ($at -lt 0) {
        return [pscustomobject]@{ Lines = @($Pre + $Body + @('HVACTESTDUMP') + $Footer); Deferred = $false }
    }
    [string[]]$before = if ($at -gt 0) { @($Body[0..($at - 1)]) } else { @() }
    [string[]]$after = if ($at + 1 -lt $Body.Count) { @($Body[($at + 1)..($Body.Count - 1)]) } else { @() }
    Set-Content -Encoding ASCII -Path $TailPath -Value @($after + @('HVACTESTDUMP') + $Footer)
    return [pscustomobject]@{ Lines = @($Pre + @('HVACTESTDEFER', $TailPath) + $before); Deferred = $true }
}
