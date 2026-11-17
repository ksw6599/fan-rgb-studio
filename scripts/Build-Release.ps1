param(
    [string]$OutputDirectory = '',
    [string]$ArtifactDirectory = '',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDir 'release-build\app' }
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $projectDir 'release-build\artifacts' }
$outputDir = [IO.Path]::GetFullPath($OutputDirectory)
$artifactDir = [IO.Path]::GetFullPath($ArtifactDirectory)
if ($outputDir -eq $projectDir -or $outputDir.StartsWith($artifactDir + [IO.Path]::DirectorySeparatorChar)) { throw 'Choose separate application and artifact directories.' }
$sdk = Join-Path $env:USERPROFILE '.codex\tools\dotnet8\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
& (Join-Path $PSScriptRoot 'Fetch-Dependencies.ps1')
& $sdk restore (Join-Path $projectDir 'FanRgbStudio.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
& $sdk publish (Join-Path $projectDir 'FanRgbStudio.csproj') -c Release --no-restore -p:DebugType=None -p:DebugSymbols=false -o $outputDir
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
New-Item -ItemType Directory -Path (Join-Path $outputDir 'tools\OpenRGB'),(Join-Path $outputDir 'licenses'),(Join-Path $outputDir 'docs'),$artifactDir -Force | Out-Null
Copy-Item -Path (Join-Path $projectDir 'tools\OpenRGB\OpenRGB Windows 64-bit\*') -Destination (Join-Path $outputDir 'tools\OpenRGB') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectDir 'tools\PawnIO_setup.exe'),(Join-Path $projectDir 'tools\Enable-Autostart.ps1') -Destination (Join-Path $outputDir 'tools') -Force
Copy-Item -LiteralPath (Join-Path $projectDir 'README.md'),(Join-Path $projectDir 'THIRD-PARTY-NOTICES.txt'),(Join-Path $projectDir 'LICENSE') -Destination $outputDir -Force
Get-ChildItem -LiteralPath $projectDir -Filter '*.html' -File | Copy-Item -Destination $outputDir -Force
Copy-Item -Path (Join-Path $projectDir 'licenses\*') -Destination (Join-Path $outputDir 'licenses') -Force
foreach ($name in 'GETTING_STARTED.md','UPDATING.md','HARDWARE.md') {
    Copy-Item -LiteralPath (Join-Path $projectDir ('docs\' + $name)) -Destination (Join-Path $outputDir 'docs') -Force
}
if (-not $SkipTests) {
    & $sdk (Join-Path $outputDir 'FanRgbStudio.dll') --self-test --with-engine --output (Join-Path $outputDir 'self-test-results.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Self-tests failed; no release archive was created.' }
    Get-Content -LiteralPath (Join-Path $outputDir 'self-test-results.txt') | Select-Object -Last 1 | Write-Output
}
[xml]$project = Get-Content -LiteralPath (Join-Path $projectDir 'FanRgbStudio.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$zipPath = Join-Path $artifactDir 'FanRgbStudio-win-x64.zip'
Compress-Archive -Path (Join-Path $outputDir '*') -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $artifactDir 'SHA256SUMS.txt'), "$hash  FanRgbStudio-win-x64.zip`n", [Text.Encoding]::ASCII)
Write-Output "Version: $version"
Write-Output "Release: $zipPath"
