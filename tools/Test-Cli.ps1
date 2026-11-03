$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $projectRoot 'src/DvdCalibrator.Cli/bin/Release/net10.0/DvdCalibrator.Cli.dll'
$runRoot = Join-Path $projectRoot ('artifacts/cli-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
function Invoke-Cli([int]$Expected, [string[]]$Arguments) {
    $output = & dotnet $cli @Arguments 2>&1
    if ($LASTEXITCODE -ne $Expected) { throw "Expected exit $Expected, got $LASTEXITCODE. $output" }
    return ($output -join "`n")
}
$demo = Join-Path $runRoot 'demo'
$null = Invoke-Cli 0 @('demo', '--out', $demo)
$snapshot = Join-Path $demo 'generation.json'
$actual = Join-Path $demo 'actual.json'
$hashBefore = (Get-FileHash -LiteralPath $snapshot).Hash
$null = Invoke-Cli 3 @('demo', '--out', $demo)
if ((Get-FileHash -LiteralPath $snapshot).Hash -ne $hashBefore) { throw 'Existing output changed.' }
$inspect = Invoke-Cli 0 @('inspect', '--record', $snapshot)
if (($inspect | ConvertFrom-Json).status -ne 'snapshot-valid') { throw 'Snapshot inspection failed.' }
$null = Invoke-Cli 0 @('predict', '--generated', $snapshot, '--actual', $actual, '--out', (Join-Path $runRoot 'predict'))
$prediction = Get-Content -LiteralPath (Join-Path $runRoot 'predict/prediction.json') -Raw | ConvertFrom-Json
if ($prediction.curve.Count -ne 2049 -or $prediction.physicalValidation -ne 'pending') { throw 'Prediction contract mismatch.' }
[xml]$svg = Get-Content -LiteralPath (Join-Path $runRoot 'predict/prediction.svg') -Raw
if ($svg.DocumentElement.LocalName -ne 'svg') { throw 'Invalid SVG.' }
$null = Invoke-Cli 0 @('import-discimage', '--record', (Join-Path $projectRoot 'fixtures/upstream-fast-fill.iso.json'), '--out', (Join-Path $runRoot 'import'))
$imported = Get-Content -LiteralPath (Join-Path $runRoot 'import/generation-record.json') -Raw | ConvertFrom-Json
if ($imported.qualityStatus -ne 'record-incomplete') { throw 'Incomplete source was certified.' }
if ((Get-FileHash (Join-Path $runRoot 'import/source.json')).Hash.ToLowerInvariant() -ne $imported.sourceSha256) { throw 'Source hash mismatch.' }
$frozen = Join-Path $runRoot 'frozen'
$null = Invoke-Cli 0 @('freeze-discimage', '--record', (Join-Path $projectRoot 'fixtures/upstream-fast-fill.iso.json'), '--image', (Join-Path $projectRoot 'fixtures/upstream-input.bmp'), '--media', 'DVD-R', '--confirmed-sectors', '16', '--generator-version', '1.3.6', '--generator-commit', '374c0630326ea61be9adca8d7de3c96f083c00df', '--out', $frozen)
$external = Invoke-Cli 0 @('inspect', '--record', (Join-Path $frozen 'generation.json'))
if (($external | ConvertFrom-Json).status -ne 'external-snapshot-valid') { throw 'External snapshot roundtrip failed.' }
$null = Invoke-Cli 2 @('predict', '--generated', $snapshot, '--actual', $actual, '--out', (Join-Path $runRoot 'bad'), '--samples', '1')
$null = Invoke-Cli 2 @('predict', '--generated', $snapshot, '--actual', $actual, '--out', (Join-Path $runRoot 'bad'), '--rotation-deg', 'NaN')
$null = Invoke-Cli 2 @('inspect', '--record', $snapshot, '--record', $snapshot)
$null = Invoke-Cli 2 @('inspect', '--record')
$null = Invoke-Cli 3 @('inspect', '--record', (Join-Path $runRoot 'not-found.json'))
$tampered = Get-Content -LiteralPath $snapshot -Raw | ConvertFrom-Json
$tampered.generated.innerRadiusMm = 24.1
$tampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $runRoot 'tampered.json') -Encoding utf8
$null = Invoke-Cli 2 @('inspect', '--record', (Join-Path $runRoot 'tampered.json'))
Write-Output "PASS CLI demo / prediction / import / overwrite / invalid-input checks ($runRoot)"
