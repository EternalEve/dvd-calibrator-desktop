param([Parameter(Mandatory)][string]$RunRoot)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $projectRoot 'src/DvdCalibrator.Cli/bin/Release/net10.0/DvdCalibrator.Cli.dll'
$referenceRun = Join-Path $RunRoot 'core/reference-contract'
$photoRun = Join-Path $RunRoot 'photo'
$referencePlan = Join-Path $referenceRun 'target-job/fine-target-plan.json'
$referenceGeneration = Join-Path $referenceRun 'frozen/generation.json'
$fineGeneration = Join-Path $photoRun 'fine-input-generation.json'
$fineSession = Join-Path $photoRun 'fine-input-session.json'
foreach ($inputPath in @($referencePlan, $referenceGeneration, $fineGeneration, $fineSession)) {
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Missing regression input: $inputPath. Run tools/Test.ps1 first." }
}
$cliOutput = Join-Path $RunRoot 'cli'
$null = New-Item -ItemType Directory -Path $cliOutput
function Invoke-Cli([int]$Expected, [string[]]$Arguments) {
    $output = & dotnet $cli @Arguments 2>&1
    if ($LASTEXITCODE -ne $Expected) { throw "Expected exit $Expected, got $LASTEXITCODE. $output" }
    return ($output -join "`n")
}
$demo = Join-Path $cliOutput 'demo'
$null = Invoke-Cli 0 @('demo', '--out', $demo)
$snapshot = Join-Path $demo 'generation.json'
$actual = Join-Path $demo 'actual.json'
$hashBefore = (Get-FileHash -LiteralPath $snapshot).Hash
$null = Invoke-Cli 3 @('demo', '--out', $demo)
if ((Get-FileHash -LiteralPath $snapshot).Hash -ne $hashBefore) { throw 'Existing output changed.' }
$inspect = Invoke-Cli 0 @('inspect', '--record', $snapshot)
if (($inspect | ConvertFrom-Json).status -ne 'snapshot-valid') { throw 'Snapshot inspection failed.' }
$null = Invoke-Cli 0 @('predict', '--generated', $snapshot, '--actual', $actual, '--out', (Join-Path $cliOutput 'predict'))
$prediction = Get-Content -LiteralPath (Join-Path $cliOutput 'predict/prediction.json') -Raw | ConvertFrom-Json
if ($prediction.curve.Count -ne 2049 -or $prediction.physicalValidation -ne 'pending') { throw 'Prediction contract mismatch.' }
[xml]$svg = Get-Content -LiteralPath (Join-Path $cliOutput 'predict/prediction.svg') -Raw
if ($svg.DocumentElement.LocalName -ne 'svg') { throw 'Invalid SVG.' }
$null = Invoke-Cli 0 @('import-discimage', '--record', (Join-Path $projectRoot 'fixtures/upstream-fast-fill.iso.json'), '--out', (Join-Path $cliOutput 'import'))
$imported = Get-Content -LiteralPath (Join-Path $cliOutput 'import/generation-record.json') -Raw | ConvertFrom-Json
if ($imported.qualityStatus -ne 'record-incomplete') { throw 'Incomplete source was certified.' }
if ((Get-FileHash (Join-Path $cliOutput 'import/source.json')).Hash.ToLowerInvariant() -ne $imported.sourceSha256) { throw 'Source hash mismatch.' }
$frozen = Join-Path $cliOutput 'frozen'
$null = Invoke-Cli 0 @('freeze-discimage', '--record', (Join-Path $projectRoot 'fixtures/upstream-fast-fill.iso.json'), '--image', (Join-Path $projectRoot 'fixtures/upstream-input.bmp'), '--media', 'DVD-R', '--confirmed-sectors', '16', '--generator-version', '1.3.6', '--generator-commit', '374c0630326ea61be9adca8d7de3c96f083c00df', '--out', $frozen)
$external = Invoke-Cli 0 @('inspect', '--record', (Join-Path $frozen 'generation.json'))
if (($external | ConvertFrom-Json).status -ne 'external-snapshot-valid') { throw 'External snapshot roundtrip failed.' }
$null = Invoke-Cli 2 @('predict', '--generated', $snapshot, '--actual', $actual, '--out', (Join-Path $cliOutput 'bad'), '--samples', '1')
$null = Invoke-Cli 2 @('predict', '--generated', $snapshot, '--actual', $actual, '--out', (Join-Path $cliOutput 'bad'), '--rotation-deg', 'NaN')
$null = Invoke-Cli 2 @('inspect', '--record', $snapshot, '--record', $snapshot)
$null = Invoke-Cli 2 @('inspect', '--record')
$null = Invoke-Cli 3 @('inspect', '--record', (Join-Path $cliOutput 'not-found.json'))
$tampered = Get-Content -LiteralPath $snapshot -Raw | ConvertFrom-Json
$tampered.generated.innerRadiusMm = 24.1
$tampered | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $cliOutput 'tampered.json') -Encoding utf8
$null = Invoke-Cli 2 @('inspect', '--record', (Join-Path $cliOutput 'tampered.json'))
$rectifyFolder = Join-Path $cliOutput 'rectify'
$null = Invoke-Cli 0 @('rectify', '--session', (Join-Path $projectRoot 'fixtures/photo-session-v2.synthetic.json'), '--out', $rectifyFolder)
$rectification = Get-Content -LiteralPath (Join-Path $rectifyFolder 'photo-session.json') -Raw | ConvertFrom-Json
if ($null -eq $rectification.rectification -or $null -ne $rectification.confirmedRectificationSha256) { throw 'CLI rectification confirmation semantics failed.' }
$null = Invoke-Cli 3 @('rectify', '--session', (Join-Path $projectRoot 'fixtures/photo-session-v2.synthetic.json'), '--out', $rectifyFolder)
$fitFolder = Join-Path $cliOutput 'fit'
$fitSession = Join-Path $projectRoot 'fixtures/trace-fit-session.synthetic.json'
$fitGeneration = Join-Path $projectRoot 'fixtures/trace-fit-generation.synthetic.json'
$null = Invoke-Cli 0 @('fit-trace', '--generated', $fitGeneration, '--session', $fitSession, '--out', $fitFolder)
$reviewPath = Join-Path $fitFolder 'fit-review.json'
$profileFolder = Join-Path $cliOutput 'profile'
$null = Invoke-Cli 2 @('export-profile', '--review', $reviewPath, '--session', $fitSession, '--out', $profileFolder)
$null = Invoke-Cli 0 @('export-profile', '--review', $reviewPath, '--session', $fitSession, '--out', $profileFolder, '--confirm-curve', 'true')
$profile = Get-Content -LiteralPath (Join-Path $profileFolder 'calibration-profile.json') -Raw | ConvertFrom-Json
if ($profile.physicalValidation -ne 'pending' -or -not (Test-Path -LiteralPath (Join-Path $profileFolder 'candidate.discproj'))) { throw 'Profile export contract failed.' }
$null = Invoke-Cli 3 @('export-profile', '--review', $reviewPath, '--session', $fitSession, '--out', $profileFolder, '--confirm-curve', 'true')
$null = Invoke-Cli 0 @('verify-fine-generation', '--plan', $referencePlan, '--generated', $referenceGeneration)
$null = Invoke-Cli 2 @('verify-fine-generation', '--plan', $referencePlan, '--generated', (Join-Path $frozen 'generation.json'))
$inspection = Invoke-Cli 0 @('inspect', '--record', $fineGeneration)
if (($inspection | ConvertFrom-Json).status -ne 'simulation-only') { throw 'Reference simulation was presented as actual output.' }
$fineFit = Join-Path $cliOutput 'fine-fit'
$null = Invoke-Cli 0 @('fit-trace', '--generated', $fineGeneration, '--session', $fineSession, '--out', $fineFit)
$null = Invoke-Cli 0 @('export-profile', '--review', (Join-Path $fineFit 'fit-review.json'), '--session', $fineSession, '--out', (Join-Path $cliOutput 'fine-profile'), '--confirm-curve', 'true')
$null = Invoke-Cli 2 @('fit-trace', '--generated', $fitGeneration, '--session', $fineSession, '--out', (Join-Path $cliOutput 'wrong-g0-fine-fit'))
Write-Output "PASS CLI generation / photo / G1 fine-fit / target verification / profile / failure-path checks ($cliOutput)"
