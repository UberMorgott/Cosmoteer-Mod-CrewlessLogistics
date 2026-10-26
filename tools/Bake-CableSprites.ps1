param(
    [string]$GameRoot = 'D:\Steam\steamapps\common\Cosmoteer',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\mod\assets'),
    # Optional: render artifacts\baked-vs-shader-preview.png from the native
    # appearance export (written by prototypes\whole-cell-native\Run-Probe.ps1).
    [switch]$Compare,
    [string]$AppearancePath = (Join-Path $PSScriptRoot '..\artifacts\cable-appearance-native.json'),
    [string]$ComparisonPath = (Join-Path $PSScriptRoot '..\artifacts\baked-vs-shader-preview.png')
)
# Bakes the cable look into plain PNGs for native sprite layers. Mechanical
# per-pixel recolour/mask extraction of the vanilla pipe PNGs only; the pixel
# math is the former cable_material/cable_glow shader evaluated at texel centers.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('CrewlessLogisticsCableBake' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
public static class CrewlessLogisticsCableBake {
    // Lamp RGB stores emission/(LampScale*alpha); the rules VertexColor restores LampScale.
    public const float LampScale = 4f;
    static float Clamp(float x)=>Math.Max(0,Math.Min(1,x));
    static float Smooth(float a,float b,float x){float t=Clamp((x-a)/(b-a));return t*t*(3-2*t);}
    static (float r,float g,float b) Pixel(byte[] src,int w,int h,int x,int y){int i=(Math.Max(0,Math.Min(h-1,y))*w+Math.Max(0,Math.Min(w-1,x)))*4;return(src[i]/255f,src[i+1]/255f,src[i+2]/255f);}
    static float Saturation((float r,float g,float b)c){float hi=Math.Max(c.r,Math.Max(c.g,c.b)),lo=Math.Min(c.r,Math.Min(c.g,c.b));return(hi-lo)/Math.Max(hi,.0001f);}
    static float Metal(byte[] src,int w,int h,int x,int y){var c=Pixel(src,w,h,x,y);return(1-Smooth(.20f,.35f,Saturation(c)))*Smooth(.09f,.13f,c.r*.30f+c.g*.59f+c.b*.11f);}
    static float Rect(float x,float y,float cx,float cy,float hx,float hy){
        float radius=Math.Min(2.5f,Math.Min(hx,hy));
        float dx=Math.Abs(x-cx)-(hx-radius),dy=Math.Abs(y-cy)-(hy-radius);
        return(float)Math.Sqrt(Math.Max(dx,0)*Math.Max(dx,0)+Math.Max(dy,0)*Math.Max(dy,0))+Math.Min(Math.Max(dx,dy),0)-radius;
    }
    // Native grille silhouette (signed distance) and rib axis per source pixel.
    static void Lamp(byte[] src,int w,int h,out float[] distances,out float[] axes){
        if(w!=64||h!=64)throw new ArgumentException("Native lamp masks require original 64x64 sprites.");
        bool corner=Metal(src,w,h,31,31)<.5f,top=Metal(src,w,h,31,21)>=.5f,bottom=Metal(src,w,h,31,39)>=.5f;
        bool right=top&&Metal(src,w,h,40,31)>=.5f,left=top&&Metal(src,w,h,23,31)>=.5f;
        float a=top?22:28,b=(top||bottom)?42:36;distances=new float[w*h];axes=new float[w*h];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){
            float px=x+.5f,py=y+.5f,d,axis;
            if(corner){d=Rect((px+py-79.5f)*.70710678f,(px-py)*.70710678f,0,0,2.4f,5.5f);axis=(x-y)*.70710678f;}
            else{
                float main=Rect(px,py,32,(a+b)*.5f,2.8f,(b-a)*.5f),side=64;
                if(right)side=Math.Min(side,Rect(px,py,37,32,5,2.8f));if(left)side=Math.Min(side,Rect(px,py,27,32,5,2.8f));
                d=Math.Min(main,side);axis=side<=main?px-32:py-32;
            }
            distances[y*w+x]=d;axes[y*w+x]=axis;
        }
    }
    static float Bars(float axis){float period=(axis+2)/4,separation=Math.Abs((period-(float)Math.Floor(period))*4-2);return 1-Smooth(.65f,1,separation);}
    static (float r,float g,float b) Blue((float r,float g,float b)c){
        float warm=Clamp((c.r-Math.Max(c.g,c.b)-.025f)*12),steel=Clamp((c.r*.30f+c.g*.59f+c.b*.11f)*1.15f),t=Smooth(.30f,.60f,Saturation(c));
        return(c.r*(1-warm)+(steel*(1-t)+c.b*.45f*t)*warm,c.g*(1-warm)+(steel*(1-t)+c.g*.65f*t)*warm,c.b*(1-warm)+(steel*(1-t)+c.r*.62f*t)*warm);
    }
    static float Copper((float r,float g,float b)c)=>Smooth(.12f,.20f,c.r-Math.Max(c.g,c.b))*Smooth(.35f,.50f,Saturation(c));
    static byte B(float v)=>(byte)Math.Round(Clamp(v)*255);
    // Body: navy/silver casing, old axial highlight erased, dark glass under silver ribs.
    public static byte[] Body(byte[]src,int w,int h){
        Lamp(src,w,h,out var d,out var axes);var result=new byte[src.Length];int[,] pairs={{3,0},{0,3},{2,2},{2,-2}};
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){
            int i=y*w+x;var original=Pixel(src,w,h,x,y);var casing=original;float confidence=0;
            for(int k=0;k<4;k++){int ox=pairs[k,0],oy=pairs[k,1];var a=Pixel(src,w,h,x+ox,y+oy);var b=Pixel(src,w,h,x-ox,y-oy);float score=Math.Min(Copper(a),Copper(b));if(score>confidence){confidence=score;casing=((a.r+b.r)*.5f,(a.g+b.g)*.5f,(a.b+b.b)*.5f);}}
            float stripe=confidence*(1-Smooth(.40f,.50f,Saturation(original)))*Smooth(.13f,.25f,original.r*.30f+original.g*.59f+original.b*.11f)*Smooth(1,2,d[i])*Smooth(6,8,Math.Min(Math.Min(x+.5f,y+.5f),Math.Min(63.5f-x,63.5f-y)));
            var c=Blue(original);var borrowed=Blue(casing);
            c=(c.r*(1-stripe)+borrowed.r*stripe,c.g*(1-stripe)+borrowed.g*stripe,c.b*(1-stripe)+borrowed.b*stripe);
            float glass=1-Smooth(-.35f,.55f,d[i]),inset=Clamp(-d[i]/2.8f);
            var dark=(r:.010f*(1-inset)+.025f*inset,g:.070f*(1-inset)+.18f*inset,b:.025f*(1-inset)+.065f*inset);
            c=(c.r*(1-glass)+dark.r*glass,c.g*(1-glass)+dark.g*glass,c.b*(1-glass)+dark.b*glass);
            float bevel=.5f+.5f*(float)Math.Cos(axes[i]*1.57079633f),rib=Bars(axes[i])*glass;
            c=(c.r*(1-rib)+(.12f+(.38f-.12f)*bevel)*rib,c.g*(1-rib)+(.17f+(.44f-.17f)*bevel)*rib,c.b*(1-rib)+(.19f+(.45f-.19f)*bevel)*rib);
            result[i*4]=B(c.r);result[i*4+1]=B(c.g);result[i*4+2]=B(c.b);result[i*4+3]=src[i*4+3];
        }
        return result;
    }
    // Lamp: green light through the gaps between opaque ribs plus a <=4px casing halo.
    public static byte[] Light(byte[]src,int w,int h){
        Lamp(src,w,h,out var distances,out var axes);var dst=new byte[src.Length];
        for(int i=0;i<distances.Length;i++){
            float d=distances[i],a=src[i*4+3]/255f,glass=1-Smooth(-.35f,.55f,d),opaque=Bars(axes[i])*glass;
            float core=(1-Smooth(-.50f,.75f,d))*(1-opaque)*a,center=Clamp(-d/2.4f);
            float halo=(float)Math.Exp(-Math.Max(d,0)*Math.Max(d,0)/4)*(1-Smooth(2.5f,4,d))*(1-glass)*(1-opaque)*a;
            float alpha=Math.Max(core,halo),divisor=Math.Max(alpha,.0001f)*LampScale;
            float r=(.055f+(.20f-.055f)*center)*core+.025f*halo;
            float g=(.65f+(1.65f-.65f)*center)*core+.45f*halo;
            float b=(.045f+(.12f-.045f)*center)*core+.030f*halo;
            dst[i*4]=B(r/divisor);dst[i*4+1]=B(g/divisor);dst[i*4+2]=B(b/divisor);dst[i*4+3]=B(alpha);
        }
        return dst;
    }
    public static void SwapRedBlue(byte[] p){for(int i=0;i<p.Length;i+=4){byte t=p[i];p[i]=p[i+2];p[i+2]=t;}}
    // Additive light composition (vanilla lights_add: dst += rgb*VertexColor*alpha).
    public static void Additive(byte[]dst,byte[]src,int stride,int width,int height,float scale){
        for(int y=0;y<height;y++)for(int x=0;x<width;x++){
            int i=y*stride+x*4;float alpha=src[i+3]/255f;
            if(alpha<=0)continue;
            for(int c=0;c<3;c++)dst[i+c]=(byte)Math.Min(255,Math.Round(dst[i+c]+src[i+c]*alpha*scale));
            dst[i+3]=Math.Max(dst[i+3],src[i+3]);
        }
    }
}
'@
}

function Read-Rgba([string]$path, [ref]$w, [ref]$h) {
    $bitmap = [Drawing.Bitmap]::new($path)
    try {
        $w.Value = $bitmap.Width; $h.Value = $bitmap.Height
        $data = $bitmap.LockBits([Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height), [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $bytes = [byte[]]::new($bitmap.Width * $bitmap.Height * 4)
        try { for ($y = 0; $y -lt $bitmap.Height; $y++) { [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0, $y * $data.Stride), $bytes, $y * $bitmap.Width * 4, $bitmap.Width * 4) } }
        finally { $bitmap.UnlockBits($data) }
    } finally { $bitmap.Dispose() }
    [CrewlessLogisticsCableBake]::SwapRedBlue($bytes)
    return ,$bytes
}
function ConvertTo-Bitmap([byte[]]$rgba, [int]$w, [int]$h) {
    $bgra = [byte[]]$rgba.Clone(); [CrewlessLogisticsCableBake]::SwapRedBlue($bgra)
    $bitmap = [Drawing.Bitmap]::new($w, $h, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $data = $bitmap.LockBits([Drawing.Rectangle]::new(0, 0, $w, $h), [Drawing.Imaging.ImageLockMode]::WriteOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try { for ($y = 0; $y -lt $h; $y++) { [Runtime.InteropServices.Marshal]::Copy($bgra, $y * $w * 4, [IntPtr]::Add($data.Scan0, $y * $data.Stride), $w * 4) } }
    finally { $bitmap.UnlockBits($data) }
    return $bitmap
}
$terran = Join-Path $GameRoot 'Data\ships\terran'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$damage = @('', '_33', '_66')
# Baked asset name -> vanilla source; also used to map rules-resolved files back.
$bake = [ordered]@{}
foreach ($shape in 'hub','stub','straight','corner','3way','4way') {
    foreach ($suffix in $damage) {
        $source = Join-Path $terran "heat_pipe_adaptive\floor_$shape$suffix.png"
        $bake["cable_body_$shape$suffix.png"] = @{ Source = $source; Kind = 'body' }
        $bake["cable_lamp_$shape$suffix.png"] = @{ Source = $source; Kind = 'lamp' }
    }
}
foreach ($suffix in $damage) {
    $source = Join-Path $terran "heat_pipe_crossing\doodads$suffix.png"
    $bake["cable_crossing_body$suffix.png"] = @{ Source = $source; Kind = 'body' }
    $bake["cable_crossing_lamp$suffix.png"] = @{ Source = $source; Kind = 'lamp' }
}

function Get-Baked([string]$name) {
    $entry = $bake[$name]
    $w = 0; $h = 0
    $rgba = Read-Rgba $entry.Source ([ref]$w) ([ref]$h)
    if ($entry.Kind -eq 'body') { return ,[CrewlessLogisticsCableBake]::Body($rgba, $w, $h) }
    return ,[CrewlessLogisticsCableBake]::Light($rgba, $w, $h)
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($name in $bake.Keys) {
    $bitmap = ConvertTo-Bitmap (Get-Baked $name) 64 64; $bitmap.Save((Join-Path $output $name), [Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
}
Write-Output "Baked $($bake.Count) cable sprites into $output"
if (-not $Compare) { return }

# ---- Comparison: former CPU shader emulation vs composited baked layers ----
$document = Get-Content -LiteralPath $AppearancePath -Raw | ConvertFrom-Json
$vanillaToBaked = @{}
foreach ($name in $bake.Keys) { if ($name -like 'cable_body_*' -or $name -like 'cable_lamp_*') { $vanillaToBaked[$bake[$name].Kind + '|' + [IO.Path]::GetFullPath($bake[$name].Source)] = $name } }
function Resolve-Art($primitive, [bool]$baked) {
    $file = [IO.Path]::GetFullPath($primitive.File)
    $kind = if ($primitive.Component -eq 'CableGlow') { 'lamp' } else { 'body' }
    $leaf = [IO.Path]::GetFileName($file)
    if ($bake.Contains($leaf)) { $name = $leaf } else { $name = $vanillaToBaked[$kind + '|' + $file] }
    if (-not $name) { throw "No baked asset for $kind $file" }
    if ($baked) { return [Drawing.Bitmap]::new((Join-Path $output $name)) }
    # Former path: the original shader CPU emulation applied to the vanilla sprite.
    $w = 0; $h = 0; $rgba = Read-Rgba $bake[$name].Source ([ref]$w) ([ref]$h)
    $old = if ($kind -eq 'body') { [CrewlessLogisticsGrooveMaskPreview]::ShadeBody($rgba, $w, $h, $false) } else { [CrewlessLogisticsGrooveMaskPreview]::Shade($rgba, $w, $h) }
    return ConvertTo-Bitmap $old $w $h
}
# Independent reference: the shader emulation exactly as shipped in 0.3.0.
if (-not ('CrewlessLogisticsGrooveMaskPreview' -as [type])) {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $legacy = (git -C $repo show '634af3e:tools/Render-CablePreview.ps1') -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Reference shader emulation (634af3e) is unavailable' }
    $start = $legacy.IndexOf("@'") + 2; $end = $legacy.IndexOf("'@", $start)
    Add-Type -TypeDefinition $legacy.Substring($start, $end - $start)
}
function Draw-Tile([Drawing.Bitmap]$canvas, [single]$cx, [single]$cy, [single]$scale, [int]$rotation, [string[]]$connections, [bool]$lit, [bool]$baked) {
    $state = 0
    foreach ($side in 'Up','Right','Down','Left') { $state = $state * 2; if ($connections -contains $side) { $state++ } }
    $parts = @($document.styles.armored | Where-Object { $_.DamageLevel -eq 0 -and $_.Component -in 'CableBody','CableGlow' -and $_.Gate -match "Situation_$state$" })
    foreach ($component in 'CableBody','CableGlow') {
        if ($component -eq 'CableGlow' -and -not $lit) { continue }
        $primitive = $parts | Where-Object Component -eq $component | Select-Object -First 1
        if (-not $primitive) { throw "Missing $component for state $state" }
        $image = Resolve-Art $primitive $baked
        $turns = (([int]$primitive.UVRotation % 4) + 4) % 4
        $image.RotateFlip([Drawing.RotateFlipType]$turns)
        if ($primitive.MirrorU) { $image.RotateFlip([Drawing.RotateFlipType]::RotateNoneFlipX) }
        if ($primitive.MirrorV) { $image.RotateFlip([Drawing.RotateFlipType]::RotateNoneFlipY) }
        $layerCanvas = if ($component -eq 'CableGlow') { [Drawing.Bitmap]::new($canvas.Width, $canvas.Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb) } else { $canvas }
        $g = [Drawing.Graphics]::FromImage($layerCanvas)
        $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
        $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
        $g.TranslateTransform($cx, $cy); $g.RotateTransform([single]($rotation * 90))
        $attributes = [Drawing.Imaging.ImageAttributes]::new(); $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $half = [int][Math]::Round($scale / 2)
        $g.DrawImage($image, [Drawing.Rectangle]::new(-$half, -$half, [int]$scale, [int]$scale), 0, 0, 64, 64, [Drawing.GraphicsUnit]::Pixel, $attributes)
        $g.Dispose(); $attributes.Dispose(); $image.Dispose()
        if ($component -eq 'CableGlow') {
            # Baked lamps carry their HDR scale in the rules VertexColor; the former
            # CPU emulation used the same quarter-emission encoding.
            $lightScale = if ($baked) { [single]$primitive.Color[0] } else { [CrewlessLogisticsCableBake]::LampScale }
            $bounds = [Drawing.Rectangle]::new(0, 0, $canvas.Width, $canvas.Height)
            $a = $canvas.LockBits($bounds, [Drawing.Imaging.ImageLockMode]::ReadWrite, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $b = $layerCanvas.LockBits($bounds, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $dst = [byte[]]::new($a.Stride * $canvas.Height); $src = [byte[]]::new($b.Stride * $canvas.Height)
                [Runtime.InteropServices.Marshal]::Copy($a.Scan0, $dst, 0, $dst.Length)
                [Runtime.InteropServices.Marshal]::Copy($b.Scan0, $src, 0, $src.Length)
                [CrewlessLogisticsCableBake]::Additive($dst, $src, $a.Stride, $canvas.Width, $canvas.Height, $lightScale)
                [Runtime.InteropServices.Marshal]::Copy($dst, 0, $a.Scan0, $dst.Length)
            } finally { $canvas.UnlockBits($a); $layerCanvas.UnlockBits($b) }
            $layerCanvas.Dispose()
        }
    }
}
$cases = @(
    @{ Name = 'T 0'; Rotation = 0; Connections = @('Up','Right','Down') }
    @{ Name = 'T 90'; Rotation = 1; Connections = @('Up','Right','Down') }
    @{ Name = 'T 180'; Rotation = 2; Connections = @('Up','Right','Down') }
    @{ Name = 'T 270'; Rotation = 3; Connections = @('Up','Right','Down') }
    @{ Name = '4-way'; Rotation = 0; Connections = @('Up','Right','Down','Left') }
    @{ Name = 'Straight'; Rotation = 0; Connections = @('Up','Down') }
    @{ Name = 'Corner'; Rotation = 0; Connections = @('Right','Down') }
)
$tile = 160; $pad = 20; $columns = $cases.Count
$rows = @(@{ Label = 'shader, lit'; Lit = $true; Baked = $false }, @{ Label = 'baked, lit'; Lit = $true; Baked = $true },
          @{ Label = 'shader, unlit'; Lit = $false; Baked = $false }, @{ Label = 'baked, unlit'; Lit = $false; Baked = $true })
$width = $pad + $columns * ($tile + $pad); $rowHeight = $tile + 40; $top = 50
$sheet = [Drawing.Bitmap]::new($width, $top + $rows.Count * $rowHeight + 30, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
$sg = [Drawing.Graphics]::FromImage($sheet); $sg.Clear([Drawing.Color]::FromArgb(18, 23, 29))
$font = [Drawing.Font]::new('Segoe UI', 11); $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(229, 238, 245))
$sg.DrawString('CABLE: FORMER SHADER CPU EMULATION vs BAKED NATIVE SPRITES (lights_add x VertexColor)', $font, $brush, $pad, 14)
$maxDiff = 0; $sumDiff = 0.0; $samples = 0
for ($r = 0; $r -lt $rows.Count; $r += 2) {
    foreach ($c in 0..($columns - 1)) {
        $case = $cases[$c]; $tiles = @()
        foreach ($variant in $rows[$r], $rows[$r + 1]) {
            $bitmap = [Drawing.Bitmap]::new($tile, $tile, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $tg = [Drawing.Graphics]::FromImage($bitmap); $tg.Clear([Drawing.Color]::FromArgb(18, 23, 29)); $tg.Dispose()
            Draw-Tile $bitmap ($tile / 2) ($tile / 2) $tile $case.Rotation $case.Connections $variant.Lit $variant.Baked
            $tiles += $bitmap
        }
        $bounds = [Drawing.Rectangle]::new(0, 0, $tile, $tile)
        $pixels = foreach ($bitmap in $tiles) {
            $data = $bitmap.LockBits($bounds, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $bytes = [byte[]]::new($data.Stride * $tile); [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length); $bitmap.UnlockBits($data); ,$bytes
        }
        for ($i = 0; $i -lt $pixels[0].Length; $i++) {
            if (($i % 4) -eq 3) { continue }
            $d = [Math]::Abs([int]$pixels[0][$i] - [int]$pixels[1][$i]); if ($d -gt $maxDiff) { $maxDiff = $d }; $sumDiff += $d; $samples++
        }
        foreach ($k in 0, 1) {
            $y = $top + ($r + $k) * $rowHeight; $x = $pad + $c * ($tile + $pad)
            $sg.DrawImage($tiles[$k], $x, $y + 22, $tile, $tile)
            $sg.DrawString(($case.Name + ' / ' + $rows[$r + $k].Label), $font, $brush, $x, $y)
            $tiles[$k].Dispose()
        }
    }
}
$mean = $sumDiff / $samples
$sg.DrawString(("Per-channel RGB diff (0-255) over {0} samples: max={1}, mean={2:N4}" -f $samples, $maxDiff, $mean), $font, $brush, $pad, $sheet.Height - 26)
$sheet.Save([IO.Path]::GetFullPath($ComparisonPath), [Drawing.Imaging.ImageFormat]::Png)
$sg.Dispose(); $sheet.Dispose(); $font.Dispose(); $brush.Dispose()
Write-Output ("Comparison {0}: max={1} mean={2:N4} (per-channel RGB, {3} samples)" -f [IO.Path]::GetFullPath($ComparisonPath), $maxDiff, $mean, $samples)
