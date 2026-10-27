param(
    [string]$GameRoot = 'D:\Steam\steamapps\common\Cosmoteer',
    [string]$ModLoaderDir = 'D:\Steam\steamapps\workshop\content\799600\3577650065'
)
# Headless belt probes P1/P3 on the game's own runtime (same approach as prototypes\whole-cell-native\Run-Probe.ps1).
# Never installs anything, never touches the game folder or a running game.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runtimeRoot = Join-Path $projectRoot 'artifacts\belt-probes\runtime'
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
$gameBin = Join-Path $GameRoot 'Bin'
dotnet build (Join-Path $PSScriptRoot 'BeltProbes.csproj') --configuration Release --nologo --output $runtimeRoot
if ($LASTEXITCODE -ne 0) { throw "Probe build failed: $LASTEXITCODE" }
dotnet build (Join-Path $PSScriptRoot '..\CrewlessLogistics.Belts.csproj') --configuration Release --nologo --output $runtimeRoot
if ($LASTEXITCODE -ne 0) { throw "Mod build failed: $LASTEXITCODE" }
$excluded = @('winmm.dll', 'AVRT.dll', 'ModLoader.dll', 'EML_Helper.dll', '0Harmony.dll')
foreach ($library in Get-ChildItem -LiteralPath $gameBin -Filter '*.dll' -File) {
    if ($library.Name -in $excluded) { continue }
    $destination = Join-Path $runtimeRoot $library.Name
    if (-not (Test-Path -LiteralPath $destination) -or (Get-Item -LiteralPath $destination).Length -ne $library.Length -or (Get-Item -LiteralPath $destination).LastWriteTimeUtc -ne $library.LastWriteTimeUtc) {
        Copy-Item -LiteralPath $library.FullName -Destination $destination
    }
}
# The loader's own Harmony build (the only one it accepts in game).
Copy-Item -LiteralPath (Join-Path $ModLoaderDir '0Harmony.dll') -Destination $runtimeRoot -Force
Copy-Item -LiteralPath (Join-Path $gameBin 'Cosmoteer.runtimeconfig.json') -Destination (Join-Path $runtimeRoot 'CrewlessLogistics.BeltProbes.runtimeconfig.json')
# Self-contained game runtime: the host needs a deps.json to locate coreclr; mod DLL and Harmony load via LoadFrom.
Copy-Item -LiteralPath (Join-Path $gameBin 'Cosmoteer.deps.json') -Destination (Join-Path $runtimeRoot 'CrewlessLogistics.BeltProbes.deps.json')
& (Join-Path $runtimeRoot 'CrewlessLogistics.BeltProbes.exe')
if ($LASTEXITCODE -ne 0) { throw "Belt probes failed: $LASTEXITCODE" }
