param(
    [string]$GameRoot = 'D:\Steam\steamapps\common\Cosmoteer',
    [switch]$Catalog,
    [switch]$ValidateCandidate,
    [string]$ModsDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runtimeRoot = Join-Path $projectRoot 'artifacts\whole-cell-native\runtime'
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
$gameBin = Join-Path $GameRoot 'Bin'
if (-not (Test-Path -LiteralPath (Join-Path $gameBin 'Cosmoteer.dll'))) { throw 'Game assembly not found' }
dotnet build (Join-Path $PSScriptRoot 'NativeProbe.csproj') --configuration Release --nologo --output $runtimeRoot
if ($LASTEXITCODE -ne 0) { throw "Probe build failed: $LASTEXITCODE" }
# The test host uses the game's bundled runtime and assemblies in an isolated output folder.
# Never install a loader, change the game directory, or attach to a running game process.
$excluded = @('winmm.dll', 'AVRT.dll', 'ModLoader.dll', 'EML_Helper.dll', '0Harmony.dll')
foreach ($library in Get-ChildItem -LiteralPath $gameBin -Filter '*.dll' -File) {
    if ($library.Name -in $excluded) { continue }
    $destination = Join-Path $runtimeRoot $library.Name
    if (-not (Test-Path -LiteralPath $destination) -or (Get-Item -LiteralPath $destination).Length -ne $library.Length -or (Get-Item -LiteralPath $destination).LastWriteTimeUtc -ne $library.LastWriteTimeUtc) {
        Copy-Item -LiteralPath $library.FullName -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $gameBin 'Cosmoteer.runtimeconfig.json') -Destination (Join-Path $runtimeRoot 'CrewlessLogistics.NativeProbe.runtimeconfig.json')
Copy-Item -LiteralPath (Join-Path $gameBin 'Cosmoteer.deps.json') -Destination (Join-Path $runtimeRoot 'CrewlessLogistics.NativeProbe.deps.json')
$probeArguments = @($GameRoot, $projectRoot)
if ($Catalog) { $probeArguments += '--catalog' }
if ($ValidateCandidate) { $probeArguments += '--validate-candidate' }
if ($ModsDirectory) { $probeArguments += '--mods', $ModsDirectory }
& (Join-Path $runtimeRoot 'CrewlessLogistics.NativeProbe.exe') @probeArguments
if ($LASTEXITCODE -ne 0) { throw "Native probe failed: $LASTEXITCODE" }
