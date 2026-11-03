param([string]$Upstream = '.local/upstream/DiscImageStudio')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$upstreamRoot = if ([System.IO.Path]::IsPathRooted($Upstream)) { $Upstream } else { Join-Path $projectRoot $Upstream }
$commit = & git -C $upstreamRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -ne '374c0630326ea61be9adca8d7de3c96f083c00df') { throw 'Upstream checkout does not match upstream.lock.json.' }
$outputRoot = Join-Path $projectRoot 'fixtures'
$bmpPath = Join-Path $outputRoot 'upstream-input.bmp'
# A 4x4, uncompressed 24-bit BMP; left half black, right half white.
$stream = [System.IO.File]::Create($bmpPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([byte[]](66,77)); $writer.Write([int]102); $writer.Write([int]0); $writer.Write([int]54)
    $writer.Write([int]40); $writer.Write([int]4); $writer.Write([int]4)
    $writer.Write([short]1); $writer.Write([short]24); $writer.Write([int]0); $writer.Write([int]48)
    $writer.Write([int]2835); $writer.Write([int]2835); $writer.Write([int]0); $writer.Write([int]0)
    for ($row = 0; $row -lt 4; $row++) { $writer.Write([byte[]](0,0,0,0,0,0,255,255,255,255,255,255)) }
} finally { $writer.Dispose() }
$engine = Join-Path $upstreamRoot 'src/DvdImageSolver/bin/Release/net10.0-windows/DvdImageSolver.dll'
$isoPath = Join-Path $projectRoot '.local/upstream-fixture.iso'
& dotnet $engine solve --image $bmpPath --iso-output $isoPath --total-sectors 16 --fill-sectors 16 --inner-radius-mm 24 --outer-radius-mm 58 --channel-bit-nm 133.33 --pitch-linear 0.02 --pitch-quadratic -0.03 --pitch-cubic 0.015 --spiral-direction cw --algorithm dispersion --fast-output true --fast-parallelism 1
if ($LASTEXITCODE -ne 0) { throw "Upstream generation failed ($LASTEXITCODE)." }
Copy-Item -LiteralPath "$isoPath.json" -Destination (Join-Path $outputRoot 'upstream-fast-fill.iso.json') -Force
Write-Output 'Fixture regenerated from the upstream executable; ISO retained under .local only.'
