param([string]$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$report = Get-Content -LiteralPath (Join-Path $ProjectRoot 'artifacts\whole-cell-native\cable-placement-validation.json') -Raw | ConvertFrom-Json
$pictures = @($report.nativeShapes | ForEach-Object { [pscustomobject]@{Name=[IO.Path]::GetFileNameWithoutExtension($_); File=$_} })
$pictures += @($report.fixedParts | ForEach-Object { [pscustomobject]@{Name=$_.name; File=$_.file} })
$canvas = [Drawing.Bitmap]::new(1120,600,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($canvas)
$title = [Drawing.Font]::new('Segoe UI',17)
$label = [Drawing.Font]::new('Segoe UI',10)
$brush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
try {
    $graphics.Clear([Drawing.Color]::FromArgb(22,27,32))
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $graphics.DrawString('PLACEMENT PREVIEW SPRITES - NATIVE RULES EXPORT',$title,$brush,24,18)
    $graphics.DrawString('Six native adaptive blueprint shapes and both fixed cable previews. CPU display; no live game renderer.',$label,$brush,24,60)
    for($i=0;$i -lt $pictures.Count;$i++) {
        $x=40+270*($i%4);$y=110+230*[Math]::Floor($i/4)
        $texture=[Drawing.Bitmap]::FromFile($pictures[$i].File)
        try { $graphics.DrawImage($texture,[Drawing.Rectangle]::new($x,$y,160,160)) } finally { $texture.Dispose() }
        $graphics.DrawString($pictures[$i].Name,$label,$brush,$x,($y+169))
    }
    $path=Join-Path $ProjectRoot 'artifacts\cable-placement-preview.png'
    $canvas.Save($path,[Drawing.Imaging.ImageFormat]::Png)
    Write-Output "Native placement sprite preview: $path"
} finally {
    $graphics.Dispose();$canvas.Dispose();$title.Dispose();$label.Dispose();$brush.Dispose()
}
