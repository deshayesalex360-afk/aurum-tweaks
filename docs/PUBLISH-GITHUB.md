# Publier Aurum Tweaks sur GitHub Releases

État local déjà préparé (rien à refaire) :

- dépôt git initialisé, branche `main`, commit initial créé ;
- tag de version **`v0.1.0`** posé ;
- artefacts prêts dans `dist/` (ignorés par git, à téléverser comme *release assets*) :
  - `AurumTweaks-0.1.0-win-x64-portable.zip` (≈ 66 Mo, autonome)
  - `AurumTweaks-0.1.0-win-x64-portable.zip.sha256` (empreinte d'intégrité)

> Le ZIP n'est **pas** poussé dans le dépôt : les binaires se publient en *Release*, pas dans l'historique git. C'est voulu.

Il reste une seule chose qui dépend de **votre compte GitHub** : créer le dépôt distant, pousser, et publier la Release. Deux routes — choisissez-en une.

---

## Route A — GitHub CLI (la plus automatisée, recommandée)

```powershell
winget install --id GitHub.cli -e
# Fermez puis rouvrez le terminal pour que "gh" soit reconnu, puis :
gh auth login
gh repo create aurum-tweaks --public --source . --remote origin --push
gh release create v0.1.0 "dist/AurumTweaks-0.1.0-win-x64-portable.zip" "dist/AurumTweaks-0.1.0-win-x64-portable.zip.sha256" --title "Aurum Tweaks 0.1.0" --notes "Version portable autonome (Windows 10/11 x64). Decompresser, lancer AurumTweaks.exe en administrateur. Application non signee : voir LISEZ-MOI.txt. Empreinte SHA-256 fournie."
```

- `--public` rend la Release téléchargeable sans connexion. Mettez `--private` si vous préférez tester d'abord (vous seul pourrez télécharger).
- Le lien de téléchargement stable sera : `https://github.com/<votre-compte>/aurum-tweaks/releases/latest`.

---

## Route B — Web + git (sans installer gh)

1. Sur https://github.com/new : créez un dépôt **vide** nommé `aurum-tweaks` (ne cochez **aucun** README / .gitignore / licence). Copiez l'URL HTTPS proposée.
2. Dans ce dossier :

   ```powershell
   git remote add origin https://github.com/<votre-compte>/aurum-tweaks.git
   git push -u origin main
   git push origin v0.1.0
   ```

   (Git Credential Manager ouvre le navigateur pour la connexion GitHub au premier push.)
3. Sur la page du dépôt → **Releases** → **Draft a new release** → choisissez le tag existant **`v0.1.0`** → glissez-déposez les **deux** fichiers de `dist/` comme *assets* → **Publish release**.

---

## Mettre à jour plus tard (v0.1.1, etc.)

```powershell
# 1) bump <Version> dans src/AurumTweaks/AurumTweaks.csproj
# 2) republier l'artefact
dotnet publish src/AurumTweaks/AurumTweaks.csproj -c Release -r win-x64 --self-contained true --nologo
# 3) re-zipper dist/ (voir l'historique de commandes), recalculer le .sha256
# 4) git commit, nouveau tag, nouvelle Release
git tag -a v0.1.1 -m "Aurum Tweaks 0.1.1"
git push origin v0.1.1
gh release create v0.1.1 dist\AurumTweaks-0.1.1-win-x64-portable.zip dist\AurumTweaks-0.1.1-win-x64-portable.zip.sha256 --title "Aurum Tweaks 0.1.1" --notes "..."
```

## Pour aller plus loin

- **Signature de code** (supprime l'avertissement SmartScreen) : nécessite un certificat OV/EV — voir `build/sign.ps1`, prêt à l'emploi dès qu'un certificat est disponible.
- **Installeur .exe** : `build/installer/AurumTweaks.iss` (Inno Setup) emballe ce même publish self-contained.
- **Site communautaire** (discussions, partage de résultats, évolution guidée par les retours) : prévu pour plus tard ; GitHub Discussions peut servir d'amorce immédiate et gratuite sur ce même dépôt.
