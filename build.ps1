param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts\Build-Release.ps1') -OutputDirectory (Join-Path $PSScriptRoot 'dist') -SkipTests:$SkipTests