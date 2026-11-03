param([string]$Upstream = '.local/upstream/DiscImageStudio', [string]$Out = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$upstreamRoot = if ([System.IO.Path]::IsPathRooted($Upstream)) { $Upstream } else { Join-Path $projectRoot $Upstream }
$commit = & git -C $upstreamRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -ne '374c0630326ea61be9adca8d7de3c96f083c00df') { throw 'Upstream checkout differs from upstream.lock.json.' }
$changedSources = & git -C $upstreamRoot status --porcelain -- src/DiscImageStudio.Core src/DvdImageSolver
if ($LASTEXITCODE -ne 0 -or $changedSources) { throw 'Upstream engine/Core source has local changes; contract probe requires the pinned source.' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local/dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$engine = Join-Path $upstreamRoot 'src/DvdImageSolver/bin/Release/net10.0-windows/DvdImageSolver.dll'
if (-not (Test-Path -LiteralPath $engine)) { throw 'Build the pinned upstream Windows engine first; see docs/upstream-target-contract.md.' }
$outputPath = if ($Out) { $Out } else { Join-Path $projectRoot ('artifacts/upstream-target-' + [Guid]::NewGuid().ToString('N')) }
& dotnet run --project (Join-Path $PSScriptRoot 'UpstreamContractProbe') -c Release -- --engine $engine --out $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Actual upstream contract verification failed.' }
Write-Output "Verified output: $outputPath"
