<#
.SYNOPSIS
  Publie Aurum Tweaks sur GitHub, en une seule commande, de facon idempotente.

  Fait automatiquement : cree le depot (si absent), pousse main + le tag de version,
  publie la Release avec le ZIP portable + son empreinte SHA-256, et active les
  Discussions (amorce communautaire). Rejouable sans risque.

  PREREQUIS (une seule action humaine, car cela engage VOTRE compte GitHub) :
      & "<chemin-gh>" auth login
  Le script detecte l'absence d'authentification et vous donne la commande exacte.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File build\publish-github.ps1
#>
[CmdletBinding()]
param(
    [string]$Repo = "aurum-tweaks",
    [ValidateSet("public", "private")] [string]$Visibility = "public",
    [string]$Version = "0.1.0",
    [string]$GhPath = ""
)

# Note: on garde ErrorActionPreference par defaut (Continue). Sous Windows PowerShell 5.1,
# forcer "Stop" + appels natifs qui ecrivent sur stderr declenche de faux NativeCommandError.
# On controle donc les erreurs explicitement via $LASTEXITCODE et des "throw" cibles.

$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

function Resolve-Gh {
    param([string]$Hint)
    if ($Hint -and (Test-Path $Hint)) { return (Resolve-Path $Hint).Path }
    $local = Join-Path $env:LOCALAPPDATA 'AurumPublish\gh\bin\gh.exe'
    if (Test-Path $local) { return $local }
    $cmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "gh.exe introuvable. Reexecutez la preparation (telechargement du GitHub CLI portable)."
}

$gh = Resolve-Gh -Hint $GhPath
Write-Host "GitHub CLI : $gh"

# --- L'unique etape humaine : authentification ---
& $gh auth status 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "=== ACTION REQUISE : connexion a GitHub (une seule fois) ===" -ForegroundColor Yellow
    Write-Host "1) Executez la commande ci-dessous, suivez le navigateur qui s'ouvre."
    Write-Host "   Acceptez 'Authenticate Git with your GitHub credentials' quand c'est propose."
    Write-Host "2) Relancez ensuite ce meme script : tout le reste est automatique."
    Write-Host ""
    Write-Host "    & `"$gh`" auth login" -ForegroundColor Cyan
    Write-Host ""
    exit 2
}

$owner = (& $gh api user --jq .login)
$owner = "$owner".Trim()
if (-not $owner) { throw "Impossible de lire le compte GitHub authentifie." }
$slug = "$owner/$Repo"
Write-Host "Compte : $owner   ->   depot cible : $slug"

# --- Depot distant : creer si absent, sinon s'y rattacher, puis pousser ---
& $gh repo view $slug 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Creation du depot $slug et envoi de 'main'..."
    $vis = if ($Visibility -eq "public") { "--public" } else { "--private" }
    & $gh repo create $Repo $vis --source . --remote origin --push
    if ($LASTEXITCODE -ne 0) { throw "Echec de 'gh repo create'." }
}
else {
    Write-Host "Depot $slug deja present."
    $hasOrigin = (git remote) -contains "origin"
    if (-not $hasOrigin) { git remote add origin "https://github.com/$slug.git" }
    git push -u origin main
}
git push origin --tags

# --- Release ---
$tag = "v$Version"
$zip = Join-Path $repoRoot ("dist\AurumTweaks-{0}-win-x64-portable.zip" -f $Version)
$sha = "$zip.sha256"
foreach ($f in @($zip, $sha)) { if (-not (Test-Path $f)) { throw "Asset introuvable : $f" } }

& $gh release view $tag -R $slug 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publication de la Release $tag..."
    $notes = "Version portable autonome (Windows 10/11 x64). Decompresser, puis lancer AurumTweaks.exe en administrateur. Application non signee : voir LISEZ-MOI.txt (avertissement SmartScreen normal). Empreinte SHA-256 fournie pour verifier l'integrite du telechargement."
    & $gh release create $tag $zip $sha -R $slug --title "Aurum Tweaks $Version" --notes $notes
    if ($LASTEXITCODE -ne 0) { throw "Echec de 'gh release create'." }
}
else {
    Write-Host "Release $tag deja presente : mise a jour des assets."
    & $gh release upload $tag $zip $sha -R $slug --clobber
}

# --- Discussions : amorce communautaire (n'echoue pas le script si indisponible) ---
Write-Host "Activation des Discussions..."
& $gh repo edit $slug --enable-discussions 2>$null | Out-Null

Write-Host ""
Write-Host "=== TERMINE ===" -ForegroundColor Green
Write-Host ("Depot       : https://github.com/{0}" -f $slug)
Write-Host ("Release     : https://github.com/{0}/releases/tag/{1}" -f $slug, $tag)
Write-Host ("Telechargt. : https://github.com/{0}/releases/latest" -f $slug)
Write-Host ("Discussions : https://github.com/{0}/discussions" -f $slug)
