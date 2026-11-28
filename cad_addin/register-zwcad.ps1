# Registers the ZWCAD build of the add-in for autoload (per user), or removes the registration.
#
#   powershell -File cad_addin\register-zwcad.ps1 [-Dll <path>] [-Language en-US] [-Unregister]
#
# Writes HKCU\Software\ZWSOFT\ZWCAD\2026\<Language>\Applications\HvacAiCadAddin:
#   LOADER = the DLL, LOADCTRLS = 2 (load at startup), MANAGED = 1, DESCRIPTION.
# Nothing under HKLM is touched. The commands appear the next time ZWCAD starts.
#
# ZWCAD locks a loaded DLL until it exits, so a rebuild fails while ZWCAD runs with this key set.
# Point -Dll at a copy (for example under %LOCALAPPDATA%\HvacAiCad\zw) if you want to keep building.
# tests\Zw\run-zw-tests.ps1 warns when this key exists, because its test instance would then
# autoload the same add-in as well as the copy it NETLOADs.
param(
    [string]$Dll = '',
    [string]$Language = 'en-US',
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$key = "HKCU:\Software\ZWSOFT\ZWCAD\2026\$Language\Applications\HvacAiCadAddin"

if ($Unregister) {
    if (Test-Path $key) {
        Remove-Item $key -Recurse -Force
        Write-Output "removed $key"
    } else {
        Write-Output "nothing to remove ($key does not exist)"
    }
    exit 0
}

if (-not $Dll) { $Dll = Join-Path $repo 'cad_addin\bin\Zw\Debug\net48\HvacAiCadAddin.dll' }
if (-not (Test-Path -LiteralPath $Dll)) {
    Write-Error "DLL not found: $Dll. Build it first: dotnet build cad_addin\HvacAiCadAddin.Zw.csproj -p:Configuration=Debug -p:Platform=x64"
    exit 2
}
$Dll = (Resolve-Path -LiteralPath $Dll).Path

New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name 'LOADER' -Value $Dll -PropertyType String -Force | Out-Null
New-ItemProperty -Path $key -Name 'LOADCTRLS' -Value 2 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name 'MANAGED' -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name 'DESCRIPTION' -Value 'hvac_ai MEP design assistant' -PropertyType String -Force | Out-Null
Write-Output "registered $Dll"
Write-Output "key: $key (load at ZWCAD startup)"
