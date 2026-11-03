param([switch]$Quick, [switch]$SkipBuild, [string]$Out = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local/dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$outputPath = if ($Out) { $Out } else { Join-Path $projectRoot ('artifacts/simulation-' + [Guid]::NewGuid().ToString('N')) }
$suite = if ($Quick) { 'quick' } else { 'full' }
$runArguments = @('run', '--project', (Join-Path $PSScriptRoot 'SimulationMatrix'), '-c', 'Release')
if ($SkipBuild) { $runArguments += '--no-build' }
$runArguments += @('--', '--out', $outputPath, '--suite', $suite)
& dotnet @runArguments
if ($LASTEXITCODE -ne 0) { throw "Simulation matrix failed; inspect $outputPath/report.html and matrix.json (exit $LASTEXITCODE)." }
