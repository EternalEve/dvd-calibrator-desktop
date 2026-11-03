param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local/dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    if (-not $SkipBuild) {
        & dotnet build DvdCalibrator.slnx -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    & dotnet run --project tests/DvdCalibrator.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
    & dotnet run --project tests/DvdCalibrator.Imaging.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Windows imaging checks failed.' }
    & (Join-Path $PSScriptRoot 'Test-Cli.ps1')
} finally { Pop-Location }
