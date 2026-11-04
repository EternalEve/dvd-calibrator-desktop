param([switch]$SkipBuild, [switch]$SkipSimulationMatrix, [string]$Out)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runRoot = if ($Out) { [IO.Path]::GetFullPath($Out, (Get-Location).ProviderPath) } else {
    Join-Path $projectRoot ('artifacts/regression-' + [guid]::NewGuid().ToString('N'))
}
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local/dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    if (-not $SkipBuild) {
        & dotnet build DvdCalibrator.slnx -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    $null = New-Item -ItemType Directory -Path $runRoot
    Write-Output "Regression output: $runRoot"
    & dotnet run --project tests/DvdCalibrator.Tests -c Release --no-build -- --out (Join-Path $runRoot 'core')
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
    & dotnet run --project tests/DvdCalibrator.Imaging.Tests -c Release --no-build -- --out (Join-Path $runRoot 'photo')
    if ($LASTEXITCODE -ne 0) { throw 'Windows imaging checks failed.' }
    & (Join-Path $PSScriptRoot 'Test-Cli.ps1') -RunRoot $runRoot
    if (-not $SkipSimulationMatrix) {
        & (Join-Path $PSScriptRoot 'Test-SimulationMatrix.ps1') -SkipBuild -Out (Join-Path $runRoot 'simulation')
    }
} finally { Pop-Location }
