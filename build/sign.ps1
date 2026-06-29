<#
    sign.ps1 - honest Authenticode signing wrapper for Aurum Tweaks.

    This script signs an artifact ONLY when you give it a real code-signing certificate. It never fabricates a
    "signed" or "trusted" state: with no certificate it stops and tells you plainly that the artifact is UNSIGNED
    (Windows SmartScreen / UAC will show "Unknown Publisher"). After signing it runs `signtool verify` and reports
    the ACTUAL result - a self-signed test cert that does not chain to a trusted root is reported as untrusted,
    not glossed over. Honesty mandate: no fake safety indicators, ever.

    Provide a certificate in exactly one of three ways:
      -PfxPath <file> [-PfxPassword <pwd>]   sign with a PFX/P12 file
      -Thumbprint <sha1>                      sign with a cert already in the user/machine store
      (nothing)                               => honest no-op: reports UNSIGNED and exits non-zero

    Examples:
      powershell -ExecutionPolicy Bypass -File build\sign.ps1 -PfxPath C:\certs\aurum.pfx -PfxPassword (Read-Host -AsSecureString)
      powershell -ExecutionPolicy Bypass -File build\sign.ps1 -Thumbprint 1A2B3C... -File .\publish\AurumTweaks.exe,.\dist\AurumTweaks-Setup.exe

    To obtain a trusted certificate you need an OV or EV code-signing certificate from a CA (DigiCert, Sectigo,
    SSL.com, etc.). EV certs clear SmartScreen reputation immediately; OV certs build reputation over time.

    NOTE: this file is deliberately ASCII-only so Windows PowerShell 5.1 parses it correctly without a UTF-8 BOM.
#>

[CmdletBinding()]
param(
    # Artifact(s) to sign. Defaults to the Release exe; pass several (exe + installer) as a comma-separated list.
    [string[]] $File = @("$PSScriptRoot\..\src\AurumTweaks\bin\Release\net8.0-windows\AurumTweaks.exe"),
    [string]   $PfxPath,
    [securestring] $PfxPassword,
    [string]   $Thumbprint,
    [string]   $TimestampUrl = 'http://timestamp.digicert.com',
    [string]   $Description   = 'Aurum Tweaks',
    [string]   $Url
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-SignTool {
    # Prefer the newest x64 signtool from an installed Windows 10/11 SDK; fall back to PATH.
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $kits) {
        $hit = Get-ChildItem -Path $kits -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match '\\x64\\' } |
               Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $onPath = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

# --- locate signtool -------------------------------------------------------
$signtool = Resolve-SignTool
if (-not $signtool) {
    Write-Host 'ERROR: signtool.exe not found. Install the Windows 10/11 SDK (Signing Tools feature).' -ForegroundColor Red
    exit 2
}

# --- check the artifacts actually exist ------------------------------------
$targets = @()
foreach ($f in $File) {
    $resolved = Resolve-Path -Path $f -ErrorAction SilentlyContinue
    if (-not $resolved) {
        Write-Host ("ERROR: artifact not found: {0}" -f $f) -ForegroundColor Red
        Write-Host '       Build/publish it first (e.g. dotnet publish -c Release -r win-x64).' -ForegroundColor Yellow
        exit 2
    }
    $targets += $resolved.Path
}

# --- honest no-op when no certificate is supplied --------------------------
$hasPfx   = -not [string]::IsNullOrWhiteSpace($PfxPath)
$hasThumb = -not [string]::IsNullOrWhiteSpace($Thumbprint)
if (-not $hasPfx -and -not $hasThumb) {
    Write-Host ''
    Write-Host 'No code-signing certificate supplied - nothing was signed.' -ForegroundColor Yellow
    Write-Host 'The artifact(s) remain UNSIGNED. Windows will display "Unknown Publisher" in UAC/SmartScreen:' -ForegroundColor Yellow
    foreach ($t in $targets) { Write-Host ("  - {0}" -f $t) }
    Write-Host 'Supply -PfxPath (+ -PfxPassword) or -Thumbprint to sign for real.' -ForegroundColor Yellow
    exit 1   # non-zero: this is NOT a successful signing, and we refuse to pretend it is
}
if ($hasPfx -and $hasThumb) {
    Write-Host 'ERROR: specify either -PfxPath or -Thumbprint, not both.' -ForegroundColor Red
    exit 2
}

# --- build the signtool argument list --------------------------------------
$common = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', $Description)
if (-not [string]::IsNullOrWhiteSpace($Url)) { $common += @('/du', $Url) }
if ($hasPfx) {
    $common += @('/f', (Resolve-Path $PfxPath).Path)
    if ($PfxPassword) {
        $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword))
        $common += @('/p', $plain)
    }
} else {
    $common += @('/sha1', $Thumbprint)
}

# --- sign each target, then VERIFY and report the real outcome -------------
$allOk = $true
foreach ($t in $targets) {
    Write-Host ("Signing: {0}" -f $t) -ForegroundColor Cyan
    & $signtool @common $t
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("  signtool sign FAILED (exit {0}) - left unsigned." -f $LASTEXITCODE) -ForegroundColor Red
        $allOk = $false
        continue
    }
    # /pa = default authentication policy; this only passes if the signature chains to a TRUSTED root.
    & $signtool verify /pa /v $t | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host '  signed and verified against a trusted root.' -ForegroundColor Green
    } else {
        Write-Host '  signed, but verification did NOT chain to a trusted root (e.g. a self-signed/test cert).' -ForegroundColor Yellow
        Write-Host '  This file will still show as untrusted to end users until a CA-issued cert is used.' -ForegroundColor Yellow
        $allOk = $false
    }
}

if ($allOk) { exit 0 } else { exit 1 }
