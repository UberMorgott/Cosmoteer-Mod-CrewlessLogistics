param(
    [string]$ModsDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $ModsDirectory) {
    # Default: the single Cosmoteer profile under %USERPROFILE%\Saved Games\Cosmoteer\<SteamID>\Mods.
    $profiles = @(Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE 'Saved Games\Cosmoteer') -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Mods') -PathType Container })
    if ($profiles.Count -ne 1) { throw "Cannot select one Cosmoteer profile ($($profiles.Count) candidates); pass -ModsDirectory." }
    $ModsDirectory = Join-Path $profiles[0].FullName 'Mods'
}
$sourceDirectory = Join-Path $PSScriptRoot 'mod'
$targetDirectory = Join-Path ([IO.Path]::GetFullPath($ModsDirectory)) 'CrewlessLogistics'
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -File -Recurse)
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'mod.rules'))) { throw 'Source manifest not found' }
if ($sourceFiles | Where-Object Extension -in @('.dll', '.exe', '.cs', '.ps1')) { throw 'Gameplay package must contain rules/assets/documentation only' }
if (-not (Test-Path -LiteralPath $ModsDirectory -PathType Container)) { throw "Mods directory not found: $ModsDirectory" }
if (Test-Path -LiteralPath $targetDirectory) {
    $manifest = Join-Path $targetDirectory 'mod.rules'
    if (-not (Test-Path -LiteralPath $manifest) -or (Get-Content -LiteralPath $manifest -Raw) -notmatch '(?m)^ID\s*=\s*morgott\.crewless_logistics\s*$') {
        throw "Refusing to overwrite an unrelated directory: $targetDirectory"
    }
    if (Get-ChildItem -LiteralPath $targetDirectory -Filter '*.dll' -Recurse -File) { throw 'Unexpected DLL in existing target; inspect before updating' }
    $backupDirectory = Join-Path $PSScriptRoot ('artifacts\mod-backups\CrewlessLogistics-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    Copy-Item -LiteralPath $targetDirectory -Destination $backupDirectory -Recurse
    Write-Output "Previous package backed up: $backupDirectory"
}
New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
# Pre-0.3.3 package folder (Mods\Powergrid, ID morgott.powergrid): back up, then remove it,
# otherwise both packages load and the legacy part OtherIDs collide.
$legacyDirectory = Join-Path ([IO.Path]::GetFullPath($ModsDirectory)) 'Powergrid'
if (Test-Path -LiteralPath $legacyDirectory) {
    $legacyManifest = Join-Path $legacyDirectory 'mod.rules'
    if (-not (Test-Path -LiteralPath $legacyManifest) -or (Get-Content -LiteralPath $legacyManifest -Raw) -notmatch '(?m)^ID\s*=\s*morgott\.powergrid\s*$') {
        throw "Refusing to remove an unrelated directory: $legacyDirectory"
    }
    $legacyBackup = Join-Path $PSScriptRoot ('artifacts\mod-backups\Powergrid-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    New-Item -ItemType Directory -Path $legacyBackup -Force | Out-Null
    Copy-Item -LiteralPath $legacyDirectory -Destination $legacyBackup -Recurse
    # Keep a Workshop item link (hidden .workshop file) if the old folder was ever uploaded.
    $legacyWorkshop = Join-Path $legacyDirectory '.workshop'
    if ((Test-Path -LiteralPath $legacyWorkshop) -and -not (Test-Path -LiteralPath (Join-Path $targetDirectory '.workshop'))) {
        Copy-Item -LiteralPath $legacyWorkshop -Destination $targetDirectory -Force
    }
    Remove-Item -LiteralPath $legacyDirectory -Recurse -Force
    Write-Output "Legacy Mods\Powergrid backed up to $legacyBackup and removed"
}
foreach ($sourceFile in $sourceFiles) {
    $relativePath = [IO.Path]::GetRelativePath($sourceDirectory, $sourceFile.FullName)
    $destination = Join-Path $targetDirectory $relativePath
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination -Force
    if ((Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
        throw "Installed file hash mismatch: $relativePath"
    }
}
# Mirror: files from older packages would otherwise still load (backup above).
$packaged = $sourceFiles | ForEach-Object { [IO.Path]::GetRelativePath($sourceDirectory, $_.FullName) }
# .workshop (written by the in-game uploader) holds the Workshop item ID; never remove it.
$stale = @(Get-ChildItem -LiteralPath $targetDirectory -File -Recurse | Where-Object { $_.Name -ne '.workshop' -and [IO.Path]::GetRelativePath($targetDirectory, $_.FullName) -notin $packaged })
$stale | Remove-Item -Force
Get-ChildItem -LiteralPath $targetDirectory -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending | Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } | Remove-Item
Write-Output "Installed $($sourceFiles.Count) verified files, removed $($stale.Count) stale: $targetDirectory"
Write-Output 'Enable Crewless Logistics in the game Mods list. Game settings, running processes and saves were not changed.'
