param(
    [string]$ModsDirectory,
    # Part IDs that P5 pushes 1 unit into every 60 ticks (single-player test only). Empty = P5 off.
    [string[]]$P5Parts = @()
)
# Installs the test-only belt probe as its own local mod (Mods\CrewlessLogisticsBeltProbe). Never touches the
# Crewless Logistics package, settings.rules or the game folder. Enable + trust it in the in-game Mods list yourself.
$ErrorActionPreference = 'Stop'
if (-not $ModsDirectory) {
    $profiles = @(Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE 'Saved Games\Cosmoteer') -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Mods') -PathType Container })
    if ($profiles.Count -ne 1) { throw "Cannot select one Cosmoteer profile ($($profiles.Count) candidates); pass -ModsDirectory." }
    $ModsDirectory = Join-Path $profiles[0].FullName 'Mods'
}
# The real package ships the same belt DLL; two copies would double-patch the game.
$realMod = Get-ChildItem -LiteralPath $ModsDirectory -Filter 'mod.rules' -Depth 1 -File -ErrorAction SilentlyContinue | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '(?m)^ID\s*=\s*morgott\.crewless_logistics\s*$' }
if ($realMod) { throw "Refusing: Crewless Logistics is installed ($($realMod[0].DirectoryName)); it already carries the belt DLL. Remove it before installing the probe." }
$project = Join-Path $PSScriptRoot '..\CrewlessLogistics.Belts.csproj'
dotnet build $project --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
$dll = Join-Path $PSScriptRoot '..\bin\Release\CrewlessLogistics.Belts.dll'
$target = Join-Path ([IO.Path]::GetFullPath($ModsDirectory)) 'CrewlessLogisticsBeltProbe'
if (Test-Path -LiteralPath $target) {
    $manifest = Join-Path $target 'mod.rules'
    if (-not (Test-Path -LiteralPath $manifest) -or (Get-Content -LiteralPath $manifest -Raw) -notmatch '(?m)^ID\s*=\s*morgott\.crewless_logistics\.belt_probe\s*$') {
        throw "Refusing to overwrite an unrelated directory: $target"
    }
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -LiteralPath $dll, (Join-Path $PSScriptRoot 'mod.rules') -Destination $target -Force
$p5 = Join-Path $target 'p5_parts.txt'
if ($P5Parts.Count -gt 0) { Set-Content -LiteralPath $p5 -Value $P5Parts } elseif (Test-Path -LiteralPath $p5) { Remove-Item -LiteralPath $p5 }
foreach ($name in 'CrewlessLogistics.Belts.dll', 'mod.rules') {
    $src = if ($name -eq 'mod.rules') { Join-Path $PSScriptRoot $name } else { $dll }
    if ((Get-FileHash -LiteralPath $src).Hash -ne (Get-FileHash -LiteralPath (Join-Path $target $name)).Hash) { throw "Copy mismatch: $name" }
}
Write-Output "Installed $target (P5: $(if ($P5Parts.Count) { $P5Parts -join ', ' } else { 'off' }))"
Write-Output 'Next: in-game Mods list -> enable "Crewless Logistics belt probe", trust its library (every rebuild changes the DLL MVID), restart.'
