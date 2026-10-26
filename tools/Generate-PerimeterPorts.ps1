param(
    [switch]$SelfTest,
    [string]$ReportPath = (Join-Path $PSScriptRoot '..\artifacts\perimeter-edge-validation.json')
)
# Native geometry reference: Cosmoteer.Ships.Parts.PartRules constructor builds
# ProhibitLeft/Right/Above/Below rectangles from Size, not PhysicalRect.
# Port locations use physical boundary cells; neighbors inside normal Size are
# conservatively excluded because barrels/exhaust may reserve nonphysical cells.
function Get-CrewlessLogisticsMetadataField {
    param($Object,[string]$Name,$Default=$null)
    if($null -eq $Object){return $Default}
    if($Object -is [System.Collections.IDictionary]) {
        if($Object.Contains($Name)){return $Object[$Name]}
    } else {
        $property=$Object.PSObject.Properties[$Name]
        if($null -ne $property){return $property.Value}
    }
    return $Default
}
function Test-CrewlessLogisticsCellInRect {
    param([int[]]$Cell,[int[]]$Rect)
    return $Cell[0] -ge $Rect[0] -and $Cell[1] -ge $Rect[1] -and $Cell[0] -lt ($Rect[0]+$Rect[2]) -and $Cell[1] -lt ($Rect[1]+$Rect[3])
}
function Get-CrewlessLogisticsPerimeterEdges {
    [CmdletBinding()]
    param([Parameter(Mandatory,ValueFromPipeline)]$Part)
    process {
        $size=@(Get-CrewlessLogisticsMetadataField $Part 'Size')
        if($size.Count -ne 2 -or [int]$size[0] -le 0 -or [int]$size[1] -le 0){throw 'Part.Size must contain two positive cell dimensions.'}
        $width=[int]$size[0];$height=[int]$size[1]
        $baseRect=[int[]]@(0,0,$width,$height)
        $physical=@(Get-CrewlessLogisticsMetadataField $Part 'PhysicalRect')
        if($physical.Count -eq 0 -or ($physical.Count -eq 1 -and $null -eq $physical[0])){$physical=$baseRect}
        if($physical.Count -ne 4 -or [int]$physical[2] -le 0 -or [int]$physical[3] -le 0){throw 'Part.PhysicalRect must be null or [x,y,width,height] with positive dimensions.'}
        $physical=[int[]]$physical
        if($physical[0] -lt 0 -or $physical[1] -lt 0 -or ($physical[0]+$physical[2]) -gt $width -or ($physical[1]+$physical[3]) -gt $height){throw 'Part.PhysicalRect must be contained within Part.Size, as required by native PartRules.'}
        $blockedCategories=@(Get-CrewlessLogisticsMetadataField $Part 'CableProhibitedBy' @('default'))
        $rectangles=[System.Collections.Generic.List[object]]::new()
        foreach($entry in @(Get-CrewlessLogisticsMetadataField $Part 'ProhibitRects' @())) {
            if($null -eq $entry){continue}
            $rectangle=@(Get-CrewlessLogisticsMetadataField $entry 'Rect')
            if($rectangle.Count -ne 4){throw 'Each ProhibitRects entry must contain Rect=[x,y,width,height].'}
            if([int]$rectangle[2] -lt 0 -or [int]$rectangle[3] -lt 0){throw 'ProhibitRects dimensions cannot be negative.'}
            $category=[string](Get-CrewlessLogisticsMetadataField $entry 'Category')
            $rectangles.Add([pscustomobject]@{Category=$category;Rect=[int[]]$rectangle})
        }
        $sides=Get-CrewlessLogisticsMetadataField $Part 'ProhibitSides'
        $prohibits=@(Get-CrewlessLogisticsMetadataField $Part 'Prohibits' @())
        foreach($side in @('Left','Right','Above','Below')) {
            $distance=[int](Get-CrewlessLogisticsMetadataField $sides $side 0)
            if($distance -le 0){continue}
            if($prohibits.Count -eq 0){throw "Prohibit$side used but Prohibits wasn't specified, matching native PartRules validation."}
            $rectangle=switch($side){
                'Left'  { [int[]]@(-$distance,0,$distance,$height) }
                'Right' { [int[]]@($width,0,$distance,$height) }
                'Above' { [int[]]@(0,-$distance,$width,$distance) }
                'Below' { [int[]]@(0,$height,$width,$distance) }
            }
            foreach($category in $prohibits){$rectangles.Add([pscustomobject]@{Category=[string]$category;Rect=$rectangle})}
        }
        $lastX=$physical[0]+$physical[2]-1;$lastY=$physical[1]+$physical[3]-1
        $candidates=[System.Collections.Generic.List[object]]::new()
        for($x=$physical[0];$x -le $lastX;$x++) {
            $candidates.Add([pscustomobject]@{Location=[int[]]@($x,$physical[1]);Direction='Up';Neighbor=[int[]]@($x,($physical[1]-1))})
            $candidates.Add([pscustomobject]@{Location=[int[]]@($x,$lastY);Direction='Down';Neighbor=[int[]]@($x,($lastY+1))})
        }
        for($y=$physical[1];$y -le $lastY;$y++) {
            $candidates.Add([pscustomobject]@{Location=[int[]]@($physical[0],$y);Direction='Left';Neighbor=[int[]]@(($physical[0]-1),$y)})
            $candidates.Add([pscustomobject]@{Location=[int[]]@($lastX,$y);Direction='Right';Neighbor=[int[]]@(($lastX+1),$y)})
        }
        foreach($edge in $candidates) {
            if(Test-CrewlessLogisticsCellInRect $edge.Neighbor $baseRect){continue}
            $blocked=$false
            foreach($entry in $rectangles) {
                if($blockedCategories -contains $entry.Category -and (Test-CrewlessLogisticsCellInRect $edge.Neighbor $entry.Rect)){$blocked=$true;break}
            }
            if(-not $blocked){$edge}
        }
    }
}
function Invoke-CrewlessLogisticsPerimeterSelfTest {
    param([string]$Path)
    $checks=0;$cases=[System.Collections.Generic.List[object]]::new()
    function Assert-Edge([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message};$script:pgPerimeterChecks++}
    function Edge-Key($Edge){return ($Edge.Location -join ',')+'/'+$Edge.Direction+'/'+($Edge.Neighbor -join ',')}
    function Transform-Cell([int[]]$Cell,[int[]]$Size,[int]$Rotation,[bool]$Flip){
        $x=$Cell[0];$y=$Cell[1];if($Flip){$x=$Size[0]-1-$x}
        switch($Rotation){0{return [int[]]@($x,$y)};1{return [int[]]@(($Size[1]-1-$y),$x)};2{return [int[]]@(($Size[0]-1-$x),($Size[1]-1-$y))};3{return [int[]]@($y,($Size[0]-1-$x))}}
    }
    function Transform-Rect([int[]]$Rect,[int[]]$Size,[int]$Rotation,[bool]$Flip){
        $points=@(foreach($x in @($Rect[0],($Rect[0]+$Rect[2]-1))){foreach($y in @($Rect[1],($Rect[1]+$Rect[3]-1))){,@(Transform-Cell @($x,$y) $Size $Rotation $Flip)}})
        $xs=@($points|ForEach-Object{$_[0]});$ys=@($points|ForEach-Object{$_[1]})
        $left=($xs|Measure-Object -Minimum).Minimum;$top=($ys|Measure-Object -Minimum).Minimum
        return [int[]]@($left,$top,(($xs|Measure-Object -Maximum).Maximum-$left+1),(($ys|Measure-Object -Maximum).Maximum-$top+1))
    }
    $script:pgPerimeterChecks=0
    $square=[pscustomobject]@{Size=@(2,2);PhysicalRect=$null;ProhibitRects=@();ProhibitSides=@{};Prohibits=@('default')}
    $laser=[pscustomobject]@{Size=@(1,3);PhysicalRect=@(0,1,1,2);ProhibitRects=@();ProhibitSides=@{Above=1};Prohibits=@('default')}
    $thruster=[pscustomobject]@{Size=@(2,3);PhysicalRect=@(0,0,2,2);ProhibitRects=@();ProhibitSides=@{Below=7};Prohibits=@('default')}
    $custom=[pscustomobject]@{Size=@(3,2);PhysicalRect=$null;ProhibitRects=@(@{Category='default';Rect=@(-1,0,1,1)},@{Category='special';Rect=@(3,0,1,2)});ProhibitSides=@{};Prohibits=@('default')}
    foreach($case in @(@{Name='square';Part=$square;Expected=8},@{Name='small laser';Part=$laser;Expected=5},@{Name='thruster';Part=$thruster;Expected=6},@{Name='category-filtered custom part';Part=$custom;Expected=9})) {
        $edges=@(Get-CrewlessLogisticsPerimeterEdges $case.Part)
        Assert-Edge ($edges.Count -eq $case.Expected) "Unexpected perimeter count for $($case.Name): $($edges.Count)"
        Assert-Edge (@($edges|ForEach-Object {Edge-Key $_}|Sort-Object -Unique).Count -eq $edges.Count) 'Duplicate perimeter edges were emitted.'
        foreach($edge in $edges){Assert-Edge (-not(Test-CrewlessLogisticsCellInRect $edge.Neighbor @(0,0,$case.Part.Size[0],$case.Part.Size[1]))) 'An emitted edge enters reserved normal-size cells.'}
        $rectangles=@($case.Part.ProhibitRects)
        if($case.Name -eq 'small laser'){$rectangles+=@{Category='default';Rect=@(0,-1,1,1)}}
        if($case.Name -eq 'thruster'){$rectangles+=@{Category='default';Rect=@(0,3,2,7)}}
        for($rotation=0;$rotation -lt 4;$rotation++){foreach($flip in @($false,$true)) {
            $size=if($rotation%2){@($case.Part.Size[1],$case.Part.Size[0])}else{@($case.Part.Size[0],$case.Part.Size[1])}
            $physical=if($null -ne $case.Part.PhysicalRect){Transform-Rect $case.Part.PhysicalRect $case.Part.Size $rotation $flip}else{$null}
            $transformedRects=@(foreach($entry in $rectangles){[pscustomobject]@{Category=$entry.Category;Rect=(Transform-Rect $entry.Rect $case.Part.Size $rotation $flip)}})
            $transformed=[pscustomobject]@{Size=$size;PhysicalRect=$physical;ProhibitRects=$transformedRects;ProhibitSides=@{};Prohibits=@('default')}
            $actual=@(Get-CrewlessLogisticsPerimeterEdges $transformed|ForEach-Object{Edge-Key $_}|Sort-Object)
            $expected=@(foreach($edge in $edges){
                $location=Transform-Cell $edge.Location $case.Part.Size $rotation $flip;$neighbor=Transform-Cell $edge.Neighbor $case.Part.Size $rotation $flip
                $dx=$neighbor[0]-$location[0];$dy=$neighbor[1]-$location[1]
                $direction=if($dx -eq -1){'Left'}elseif($dx -eq 1){'Right'}elseif($dy -eq -1){'Up'}else{'Down'}
                Edge-Key ([pscustomobject]@{Location=$location;Direction=$direction;Neighbor=$neighbor})
            })|Sort-Object
            Assert-Edge (($actual -join ';') -eq ($expected -join ';')) "Rotation/mirroring changed perimeter decisions for $($case.Name), rot=$rotation flip=$flip"
        }}
        $cases.Add([pscustomobject]@{Name=$case.Name;Edges=$edges.Count;TransformCases=8;ActualEdges=$edges})
    }
    $onlySpecial=[pscustomobject]@{Size=@(2,2);ProhibitRects=@(@{Category='special';Rect=@(0,-1,2,1)});CableProhibitedBy=@('special')}
    Assert-Edge (@(Get-CrewlessLogisticsPerimeterEdges $onlySpecial).Count -eq 6) 'Cable-specific prohibition categories were ignored.'
    $ignoreSpecial=[pscustomobject]@{Size=@(2,2);ProhibitRects=$onlySpecial.ProhibitRects;CableProhibitedBy=@()}
    Assert-Edge (@(Get-CrewlessLogisticsPerimeterEdges $ignoreSpecial).Count -eq 8) 'An explicit empty cable prohibition set must preserve all edges.'
    $sideBlock=[pscustomobject]@{Size=@(2,2);ProhibitSides=@{Left=1;Right=2;Above=3;Below=4};Prohibits=@('default')}
    Assert-Edge (@(Get-CrewlessLogisticsPerimeterEdges $sideBlock).Count -eq 0) 'Directional prohibition rectangles were not built from normal Size.'
    foreach($invalid in @(@{Size=@(2,2);PhysicalRect=@(1,0,2,2)},@{Size=@(2,2);ProhibitSides=@{Above=1}})) {
        $rejected=$false
        try{Get-CrewlessLogisticsPerimeterEdges $invalid|Out-Null}catch{$rejected=$true}
        Assert-Edge $rejected 'Invalid geometry or missing native Prohibits declaration was accepted.'
    }
    $report=[pscustomobject]@{VerifiedAtUtc=[DateTimeOffset]::UtcNow;Checks=$script:pgPerimeterChecks;Cases=$cases;NativeEvidence='PartRules constructor: directional prohibition rectangles use Size; IsCellProhibited matches category and IntRect.Contains; PhysicalRect must lie within Size.';ConservativeBoundary='Adjacent cells inside normal Size remain excluded even when outside PhysicalRect.';Limitations='Pure coordinate regression; custom colliders and other neighboring-part build restrictions require native blueprint validation.'}
    $absolute=[IO.Path]::GetFullPath($Path);$parent=[IO.Path]::GetDirectoryName($absolute)
    if(-not(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent -Force|Out-Null}
    $report|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $absolute -Encoding utf8
    Write-Output "PASS: perimeter edges; checks=$($script:pgPerimeterChecks); transformed cases=32; report=$absolute"
    Remove-Variable -Scope Script -Name pgPerimeterChecks
}
if($SelfTest){Invoke-CrewlessLogisticsPerimeterSelfTest -Path $ReportPath}


