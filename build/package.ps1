<#
    package.ps1 - reproducible portable-ZIP build for Aurum Tweaks.

    Produces the shippable self-contained win-x64 portable ZIP + its SHA-256 sidecar - exactly the assets
    publish-github.ps1 uploads to the GitHub Release. Signing is WIRED IN but gated on a certificate:
    pass -PfxPath (+ -PfxPassword) or -Thumbprint to sign the exe (via sign.ps1) BEFORE zipping; with no
    cert the exe stays honestly UNSIGNED (the ZIP is still produced and the console says so plainly).
    No fake "signed" state, ever - the certificate is the one manual step this pipeline cannot invent.

    Examples:
      powershell -ExecutionPolicy Bypass -File build\package.ps1
      powershell -ExecutionPolicy Bypass -File build\package.ps1 -Thumbprint 1A2B3C...
      powershell -ExecutionPolicy Bypass -File build\package.ps1 -PfxPath C:\certs\aurum.pfx -PfxPassword (Read-Host -AsSecureString)

    ASCII-only (Windows PowerShell 5.1 parses it without a UTF-8 BOM).
#>
[CmdletBinding()]
param(
    [string]       $Version,                 # defaults to <Version> read from the csproj
    [string]       $Configuration = 'Release',
    [string]       $PfxPath,
    [securestring] $PfxPassword,
    [string]       $Thumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo    = (Resolve-Path "$PSScriptRoot\..").Path
$csproj  = Join-Path $repo 'src\AurumTweaks\AurumTweaks.csproj'
$distDir = Join-Path $repo 'dist'

# --- resolve version from the csproj when not supplied ---------------------
if ([string]::IsNullOrWhiteSpace($Version)) {
    try {
        $xml = [xml](Get-Content $csproj)
        $Version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    } catch { $Version = $null }
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '0.1.0' }
}
Write-Host ("Packaging Aurum Tweaks {0} (win-x64, self-contained, {1})" -f $Version, $Configuration) -ForegroundColor Cyan

# --- publish self-contained win-x64 ----------------------------------------
$publishDir = Join-Path $repo ("src\AurumTweaks\bin\{0}\net8.0-windows\win-x64\publish" -f $Configuration)
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish $csproj -c $Configuration -r win-x64 --self-contained true /p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $publishDir 'AurumTweaks.exe'
if (-not (Test-Path $exe)) { throw "published exe not found: $exe" }

# --- sign the exe (wired in, gated on a certificate) -----------------------
$signed  = $false
$hasCert = (-not [string]::IsNullOrWhiteSpace($PfxPath)) -or (-not [string]::IsNullOrWhiteSpace($Thumbprint))
if ($hasCert) {
    $signArgs = @{ File = @($exe) }
    if (-not [string]::IsNullOrWhiteSpace($PfxPath))    { $signArgs.PfxPath = $PfxPath }
    if ($PfxPassword)                                   { $signArgs.PfxPassword = $PfxPassword }
    if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) { $signArgs.Thumbprint = $Thumbprint }
    & (Join-Path $PSScriptRoot 'sign.ps1') @signArgs
    if ($LASTEXITCODE -eq 0) {
        $signed = $true
    } else {
        Write-Host 'WARNING: signing did not chain to a trusted root (see sign.ps1 output). Continuing; the exe is effectively unsigned.' -ForegroundColor Yellow
    }
} else {
    Write-Host 'No certificate supplied - the exe will be UNSIGNED (Unknown Publisher / SmartScreen warning is expected).' -ForegroundColor Yellow
}

# --- optional LISEZ-MOI (copied verbatim if present; never generated here so this script stays ASCII) ---
$readme = Join-Path $PSScriptRoot 'LISEZ-MOI.txt'
if (Test-Path $readme) { Copy-Item $readme (Join-Path $publishDir 'LISEZ-MOI.txt') -Force }

# --- zip + sha256 ----------------------------------------------------------
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }
$zip = Join-Path $distDir ("AurumTweaks-{0}-win-x64-portable.zip" -f $Version)
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLower()
"$hash  $([System.IO.Path]::GetFileName($zip))" | Out-File -FilePath "$zip.sha256" -Encoding ascii -NoNewline

Write-Host ''
Write-Host ("Portable ZIP : {0}" -f $zip) -ForegroundColor Green
Write-Host ("SHA-256      : {0}" -f $hash) -ForegroundColor Green
if ($signed) {
    Write-Host 'Signed       : yes (verified against a trusted root above).' -ForegroundColor Green
} else {
    Write-Host 'Signed       : NO - unsigned. Supply -PfxPath or -Thumbprint with a real OV/EV cert to sign.' -ForegroundColor Yellow
}
