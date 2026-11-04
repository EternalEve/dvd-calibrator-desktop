#requires -Version 7.0
param([Parameter(Mandatory)][string]$Archive, [string]$ExpectedSha256, [string]$Out)
$ErrorActionPreference = 'Stop'
$Archive = [IO.Path]::GetFullPath($Archive)
if (-not $Out) { $Out = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/portable-check-' + [guid]::NewGuid().ToString('N')) }
$Out = [IO.Path]::GetFullPath($Out)
if (Test-Path -LiteralPath $Out) { throw 'Verification output exists; select a new directory.' }
$hash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ExpectedSha256 -and $ExpectedSha256 -ne $hash) { throw 'Archive SHA256 mismatch.' }
$zip = [IO.Compression.ZipFile]::OpenRead($Archive)
try {
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); [long]$total = 0
    foreach ($entry in $zip.Entries) {
        $path = $entry.FullName.Replace('\', '/')
        if ($path.StartsWith('/') -or $path.Contains(':') -or $path.Split('/') -contains '..' -or -not $paths.Add($path)) { throw 'Unsafe or duplicate archive entry.' }
        $total += $entry.Length
        if ($zip.Entries.Count -gt 5000 -or $entry.Length -gt 500MB -or $total -gt 2GB) { throw 'Archive exceeds verification limits.' }
    }
} finally { $zip.Dispose() }
$relocated = Join-Path $Out '中文 路径/解压目录'
$null = New-Item -ItemType Directory -Path $relocated
[IO.Compression.ZipFile]::ExtractToDirectory($Archive, $relocated)
$roots = @(Get-ChildItem -LiteralPath $relocated -Directory)
if ($roots.Count -ne 1 -or @(Get-ChildItem -LiteralPath $relocated -File).Count -ne 0) { throw 'Expected one package root.' }
$package = $roots[0].FullName
$nestedLayout = Test-Path -LiteralPath (Join-Path $package 'app/package-manifest.json') -PathType Leaf
$app = if ($nestedLayout) { Join-Path $package 'app' } else { $package }
$manifestPath = Join-Path $app 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.recordType -ne 'portable-package' -or $manifest.runtimeIdentifier -ne 'win-x64' -or -not $manifest.selfContained) { throw 'Unsupported package manifest.' }
function Assert-Payload {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $package $file.path))
        if (-not $path.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($path)) { throw 'Unsafe or duplicate manifest path.' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Payload integrity failed: $($file.path)" }
    }
    $actual = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object FullName -NE $manifestPath)
    if ($actual.Count -ne $seen.Count) { throw 'Unlisted payload files.' }
    foreach ($file in $actual) { if (-not $seen.Contains($file.FullName)) { throw 'Unlisted payload path.' } }
}
Assert-Payload
if ($nestedLayout) {
    $rootNames = @(Get-ChildItem -LiteralPath $package -Force | Select-Object -ExpandProperty Name | Sort-Object)
    if (Compare-Object $rootNames (@('app', '使用说明.md', '启动 DVD Calibrator.cmd') | Sort-Object)) { throw 'Unexpected files in package root.' }
    if ($manifest.desktopLauncher -cne '启动 DVD Calibrator.cmd' -or -not @($manifest.files.path).Contains($manifest.desktopLauncher)) { throw 'Missing or invalid desktop launcher.' }
}
$guide = Get-Content -LiteralPath (Join-Path $app '新手教程.html') -Raw
$guideIds = @([regex]::Matches($guide, 'id="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
if (@($guideIds | Select-Object -Unique).Count -ne $guideIds.Count) { throw 'Offline tutorial contains duplicate anchors.' }
foreach ($section in @('materials') + @(1..8 | ForEach-Object { 'step-' + $_ })) {
    if (-not $guide.Contains('id="' + $section + '"')) { throw "Offline tutorial section missing: $section" }
}
if ((Get-FileHash -LiteralPath (Join-Path $app '标定原图/target_slim.png')).Hash.ToLowerInvariant() -ne 'c98ffe87db7cf9c3f132352814e1ce611c44f704de5b7212da4083188257c829') { throw 'Beginner reference image differs from the pinned original.' }
$appPrefix = if ($nestedLayout) { 'app/' } else { '' }
foreach ($executable in @($manifest.desktopExecutable, $manifest.cliExecutable)) {
    if (-not @($manifest.files.path).Contains($executable) -or ($appPrefix + [IO.Path]::GetFileName($executable)) -cne $executable -or -not $executable.EndsWith('.exe')) { throw 'Invalid package executable.' }
}
$emptyDotnet = Join-Path $Out 'empty-dotnet'; $null = New-Item -ItemType Directory -Path $emptyDotnet
function Invoke-Packaged([string]$Executable, [string[]]$Arguments, [int]$Expected = 0) {
    $viaLauncher = $Executable -eq $manifest.desktopLauncher
    if ($viaLauncher) {
        $start = [Diagnostics.ProcessStartInfo]::new("$env:WINDIR\System32\cmd.exe")
        $start.Environment['DVD_CALIBRATOR_TEST_LAUNCHER'] = Join-Path $package $Executable
        $command = '""%DVD_CALIBRATOR_TEST_LAUNCHER%"'
        for ($i = 0; $i -lt $Arguments.Count; $i++) {
            $variable = "DVD_CALIBRATOR_TEST_ARG_$i"
            $start.Environment[$variable] = $Arguments[$i]
            $command += ' "%{0}%"' -f $variable
        }
        $start.Arguments = '/d /s /c ' + $command + '"'
    } else {
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $package $Executable))
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    }
    $start.WorkingDirectory = $Out; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = -not $ViaLauncher; $start.RedirectStandardError = -not $ViaLauncher
    $start.Environment['DOTNET_ROOT'] = $emptyDotnet; $start.Environment['DOTNET_ROOT_X64'] = $emptyDotnet
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'; $start.Environment['DOTNET_ROLL_FORWARD'] = 'Disable'
    $start.Environment['PATH'] = "$env:WINDIR\System32;$env:WINDIR"
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $ViaLauncher) { $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync() }
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Packaged process timed out.' }
        if (-not $ViaLauncher) { $text = $stdout.GetAwaiter().GetResult(); $errorText = $stderr.GetAwaiter().GetResult() }
        if ($process.ExitCode -ne $Expected) { throw "Expected $Expected, got $($process.ExitCode): $text $errorText" }
        return $text
    } finally { $process.Dispose() }
}
$appOutput = Join-Path $Out '桌面 软件自检'
$installationPath = Join-Path $appOutput 'installation-check.json'
$entry = if ($nestedLayout) { $manifest.desktopLauncher } else { $manifest.desktopExecutable }
$null = Invoke-Packaged $entry @('--verify-installation', '--out', $appOutput)
if ($nestedLayout) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath $installationPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path -LiteralPath $installationPath)) { throw 'Desktop launcher did not complete the installation check.' }
}
$installation = Get-Content -LiteralPath $installationPath -Raw | ConvertFrom-Json
if ($installation.status -ne 'passed' -or $installation.wfrRestore -ne 'passed' -or -not $installation.localCoreRuntime -or -not $installation.localWpfRuntime -or $installation.architecture -ne 'X64') { throw 'Bundled runtime or WFR validation failed.' }
$null = Invoke-Packaged $manifest.desktopExecutable @('--verify-installation', '--out', $appOutput) 2
$null = Invoke-Packaged $manifest.desktopExecutable @('--verify-installation') 2
$demo = Join-Path $Out '命令行 示例'
$null = Invoke-Packaged $manifest.cliExecutable @('demo', '--out', $demo)
$null = Invoke-Packaged $manifest.cliExecutable @('demo', '--out', $demo) 3
$inspection = Invoke-Packaged $manifest.cliExecutable @('inspect', '--record', (Join-Path $demo 'generation.json')) | ConvertFrom-Json
if ($inspection.status -ne 'snapshot-valid') { throw 'Packaged CLI snapshot check failed.' }
$null = Invoke-Packaged $manifest.cliExecutable @('predict', '--generated', (Join-Path $demo 'generation.json'), '--actual', (Join-Path $demo 'actual.json'), '--out', (Join-Path $Out 'prediction'))
$session = Join-Path $app 'samples/trace-session.json'
$fit = Join-Path $Out 'cli-fit'; $profile = Join-Path $Out 'cli-profile'
$null = Invoke-Packaged $manifest.cliExecutable @('fit-trace', '--generated', (Join-Path $app 'samples/trace-generation.json'), '--session', $session, '--out', $fit)
$null = Invoke-Packaged $manifest.cliExecutable @('export-profile', '--review', (Join-Path $fit 'fit-review.json'), '--session', $session, '--out', $profile) 2
$null = Invoke-Packaged $manifest.cliExecutable @('export-profile', '--review', (Join-Path $fit 'fit-review.json'), '--session', $session, '--out', $profile, '--confirm-curve', 'true')
$result = Get-Content -LiteralPath (Join-Path $profile 'calibration-profile.json') -Raw | ConvertFrom-Json
if ($result.physicalValidation -ne 'pending' -or -not (Test-Path -LiteralPath (Join-Path $profile 'candidate.discproj'))) { throw 'Packaged profile export failed.' }
$sourceZip = [IO.Compression.ZipFile]::OpenRead((Join-Path $app 'source.zip'))
try {
    foreach ($required in @('LICENSE', 'global.json', 'NuGet.Config', 'src/DvdCalibrator.App/InstallationCheck.cs', 'src/DvdCalibrator.App/MainWindow.Wizard.cs', 'src/DvdCalibrator.App/MainWindow.Guidance.cs', 'docs/guided-workflow.md', 'docs/material-preparation.md', 'docs/beginner-guide.html', 'tools/Render-BeginnerGuide.ps1', 'tools/Publish-Portable.ps1', 'upstream.lock.json', 'third_party/MathNet/mathnet.numerics.5.0.0.nupkg', 'third_party/MathNet/LICENSE.md', 'third_party/pyGPA/geometric_phase_analysis.py', 'third_party/pyGPA/LICENSE', 'src/DvdCalibrator.Core/WindowedFourierRidges.cs', 'fixtures/pygpa-wfr-oracle.json')) {
        if ($null -eq $sourceZip.GetEntry($required)) { throw "Corresponding source missing: $required" }
    }
} finally { $sourceZip.Dispose() }
Assert-Payload
$report = [ordered]@{
    schemaVersion = 1; recordType = 'portable-verification'; status = 'passed'; archiveSha256 = $hash; version = $manifest.version
    relocatedToChineseSpacePath = $true; isolatedDotnetRoot = $true; localCoreRuntime = $installation.localCoreRuntime; localWpfRuntime = $installation.localWpfRuntime
    singleDiscRestore = 'passed'; twoDiscRestore = 'passed'; wfrRestore = 'passed'; finalExport = 'passed'; cliWorkflow = 'passed'; sourceArchive = 'present'; payloadUnchanged = $true
    offlineGuide = 'all-eight-steps-and-materials'; referenceTarget = 'exact-pinned-png'
    desktopLauncher = $(if ($nestedLayout) { 'passed' } else { 'not-present' })
    cleanMachine = 'pending'; nativeInteraction = 'pending'; physicalValidation = 'pending'
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Out 'portable-verification.json') -Encoding utf8NoBOM
Write-Output "Portable verification passed: $Out"
