#requires -Version 7.0
param([string]$Out, [string]$RuntimeSource, [switch]$SkipChecks)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$runtimeVersion = '10.0.7'
$name = "DvdCalibrator-$version-win-x64"
if (-not $Out) { $Out = Join-Path $projectRoot ('artifacts/portable-' + [guid]::NewGuid().ToString('N')) }
$Out = [IO.Path]::GetFullPath($Out)
if (Test-Path -LiteralPath $Out) { throw 'Output exists; select a new directory.' }
$work = Join-Path $projectRoot ('.local/portable-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work $name
$null = New-Item -ItemType Directory -Path $payload
$packages = Join-Path $projectRoot '.local/nuget'
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local/dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    foreach ($project in @('App', 'Cli')) {
        $publish = Join-Path $work $project
        $publishArgs = @('publish', "src/DvdCalibrator.$project", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            "-p:RuntimeFrameworkVersion=$runtimeVersion", "-p:RestorePackagesPath=$packages",
            '-p:NuGetAudit=false', '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-o', $publish, '--nologo')
        if ($RuntimeSource) { $publishArgs += @('--source', (Join-Path $projectRoot 'third_party/MathNet'), '--source', $RuntimeSource) }
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "$project publish failed. Runtime packs may need an explicit -RuntimeSource on first use." }
        foreach ($file in Get-ChildItem -LiteralPath $publish -Recurse -File) {
            $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
            $destination = Join-Path $payload $relative
            if (Test-Path -LiteralPath $destination) {
                if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                    # WPF replaces the Core runtime's WindowsBase facade with its implementation.
                    $desktopWindowsBase = Join-Path $packages "microsoft.windowsdesktop.app.runtime.win-x64/$runtimeVersion/runtimes/win-x64/lib/net10.0/WindowsBase.dll"
                    $coreWindowsBase = Join-Path $packages "microsoft.netcore.app.runtime.win-x64/$runtimeVersion/runtimes/win-x64/lib/net10.0/WindowsBase.dll"
                    if ($project -ne 'Cli' -or $relative -ne 'WindowsBase.dll' -or
                        (Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $desktopWindowsBase).Hash -or
                        (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $coreWindowsBase).Hash) { throw "Conflicting publish files: $relative" }
                }
            } else {
                $null = New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent)
                Copy-Item -LiteralPath $file.FullName -Destination $destination
            }
        }
    }
    foreach ($required in @('DvdCalibrator.App.exe', 'DvdCalibrator.Cli.exe', 'MathNet.Numerics.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll', 'PresentationFramework.Fluent.dll', 'wpfgfx_cor3.dll', '新手教程.html', '标定原图/target_slim.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Missing payload file: $required" }
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $payload 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Destination $payload
    foreach ($dependency in @('MathNet', 'pyGPA')) {
        $licenseFolder = Join-Path $payload "licenses/$dependency"
        $null = New-Item -ItemType Directory -Path $licenseFolder -Force
        $licenseName = if ($dependency -eq 'MathNet') { 'LICENSE.md' } else { 'LICENSE' }
        Copy-Item -LiteralPath (Join-Path $projectRoot "third_party/$dependency/$licenseName") -Destination $licenseFolder
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/portable-quickstart.md') -Destination (Join-Path $payload '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/guided-workflow.md') -Destination (Join-Path $payload '分步教程.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/material-preparation.md') -Destination (Join-Path $payload '生成记录说明.md')
    foreach ($pack in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
        $packRoot = Join-Path $packages "$pack/$runtimeVersion"
        $legal = @(Get-ChildItem -LiteralPath $packRoot -Recurse -File | Where-Object Name -Match '^(LICENSE|THIRD.PARTY.NOTICES|ThirdPartyNotices)(\..*)?$')
        if ($legal.Count -eq 0) { throw "Runtime license missing: $pack" }
        foreach ($file in $legal) {
            $destination = Join-Path $payload ("licenses/dotnet/$pack/" + [IO.Path]::GetRelativePath($packRoot, $file.FullName))
            $null = New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent)
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    $samples = Join-Path $payload 'samples'; $null = New-Item -ItemType Directory -Path $samples
    Copy-Item -LiteralPath (Join-Path $projectRoot 'fixtures/trace-fit-generation.synthetic.json') -Destination (Join-Path $samples 'trace-generation.json')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'fixtures/trace-fit-session.synthetic.json') -Destination (Join-Path $samples 'trace-session.json')
    # Include the complete current source, including uncommitted edits, rather than a stale HEAD archive.
    $source = Join-Path $work 'source'; $null = New-Item -ItemType Directory -Path $source
    if (Test-Path -LiteralPath (Join-Path $projectRoot '.git')) {
        $sourceFiles = @(& git -c core.quotepath=false ls-files --cached --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate corresponding source.' }
        $commit = & git rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read source revision.' }
        $dirtySource = [bool](& git status --porcelain)
    } else {
        $sourceSnapshot = Get-Content -LiteralPath (Join-Path $projectRoot 'source-files.json') -Raw | ConvertFrom-Json
        $sourceFiles = @($sourceSnapshot.files); $commit = $sourceSnapshot.commit; $dirtySource = $null
    }
    foreach ($relative in $sourceFiles) {
        $original = [IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
        if (-not $original.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source path escapes project.' }
        if (-not (Test-Path -LiteralPath $original -PathType Leaf)) { continue }
        if ((Get-Item -LiteralPath $original).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source symlink is not supported: $relative" }
        $destination = Join-Path $source $relative
        $null = New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent)
        Copy-Item -LiteralPath $original -Destination $destination
    }
    [ordered]@{ schemaVersion = 1; commit = "$commit"; files = $sourceFiles } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $source 'source-files.json') -Encoding utf8NoBOM
    [IO.Compression.ZipFile]::CreateFromDirectory($source, (Join-Path $payload 'source.zip'), [IO.Compression.CompressionLevel]::Optimal, $false)
    $files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = [ordered]@{
        schemaVersion = 1; recordType = 'portable-package'; version = "$version"; runtimeIdentifier = 'win-x64'; selfContained = $true
        runtimeVersion = $runtimeVersion; desktopExecutable = 'DvdCalibrator.App.exe'; cliExecutable = 'DvdCalibrator.Cli.exe'
        source = [ordered]@{ commit = "$commit"; workingTreeChanges = $dirtySource; archive = 'source.zip' }
        nativeInteraction = 'pending'; physicalValidation = 'pending'; files = $files
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $payload 'package-manifest.json') -Encoding utf8NoBOM
    $null = New-Item -ItemType Directory -Path $Out
    $archive = Join-Path $Out ($name + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ascii
    if (-not $SkipChecks) { & (Join-Path $PSScriptRoot 'Test-Portable.ps1') -Archive $archive -ExpectedSha256 $hash -Out (Join-Path $Out 'verification') }
    Write-Output "Portable archive: $archive"
    Write-Output "SHA256: $hash"
} finally { Pop-Location }
