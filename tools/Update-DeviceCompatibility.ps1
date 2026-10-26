param(
    [string]$GameRoot = 'D:\Steam\steamapps\common\Cosmoteer',
    [switch]$Install,
    [string]$ModsDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $ModsDirectory) {
    # Default: the single Cosmoteer profile under %USERPROFILE%\Saved Games\Cosmoteer\<SteamID>\Mods.
    $profiles = @(Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE 'Saved Games\Cosmoteer') -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Mods') -PathType Container })
    if ($profiles.Count -ne 1) { throw "Cannot select one Cosmoteer profile ($($profiles.Count) candidates); pass -ModsDirectory." }
    $ModsDirectory = Join-Path $profiles[0].FullName 'Mods'
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Discovery and generation run outside the game. The installed package remains
# ordinary native rules/assets; no discovery DLL is installed or loaded by it.
& (Join-Path $projectRoot 'prototypes\whole-cell-native\Run-Probe.ps1') -GameRoot $GameRoot -Catalog -ModsDirectory $ModsDirectory
& (Join-Path $PSScriptRoot 'Generate-PerimeterPorts.ps1') -SelfTest
& (Join-Path $PSScriptRoot 'Generate-ConsumerCoverage.ps1') -GameRoot $GameRoot
& (Join-Path $projectRoot 'prototypes\whole-cell-native\Run-Probe.ps1') -GameRoot $GameRoot -ModsDirectory $ModsDirectory
if ($Install) {
    & (Join-Path $projectRoot 'Install-Mod.ps1') -ModsDirectory $ModsDirectory
} else {
    Write-Output 'Compatibility generated and verified. Use -Install to copy the verified package into the game profile.'
}
