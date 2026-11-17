$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$toolsDir = Join-Path $projectDir 'tools'
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

function Get-VerifiedFile([string]$Url, [string]$Destination, [string]$Sha256) {
    if (Test-Path -LiteralPath $Destination) {
        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Sha256) { return }
        throw "Existing dependency has an unexpected hash: $Destination"
    }
    Invoke-WebRequest -Uri $Url -OutFile ($Destination + '.download')
    if ((Get-FileHash -LiteralPath ($Destination + '.download') -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Downloaded dependency has an unexpected SHA256: $Url"
    }
    Move-Item -LiteralPath ($Destination + '.download') -Destination $Destination
}

$archive = Join-Path $toolsDir 'openrgb.zip'
Get-VerifiedFile 'https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.1/OpenRGB_1.1_Windows_64_8e16de2.zip' $archive '63291726C6FC84EB479EF2EE752E97E1187F6CE9C49FD27EEE40DED588A8172A'
$engineDir = Join-Path $toolsDir 'OpenRGB'
Expand-Archive -LiteralPath $archive -DestinationPath $engineDir -Force
if (-not (Test-Path -LiteralPath (Join-Path $engineDir 'OpenRGB Windows 64-bit\OpenRGB.exe'))) { throw 'OpenRGB archive layout changed.' }

$driver = Join-Path $toolsDir 'PawnIO_setup.exe'
Get-VerifiedFile 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe' $driver '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
$signature = Get-AuthenticodeSignature -LiteralPath $driver
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'namazso') { throw 'PawnIO publisher signature verification failed.' }
Write-Output 'Pinned OpenRGB and PawnIO dependencies verified. No driver was installed.'
