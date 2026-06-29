# Aurum Tweaks

> L'optimiseur PC premium tout-en-un — BIOS, Windows, Overclocking, Gaming.

Aurum Tweaks combine en une seule app Windows native (WPF, .NET 8) l'esprit des meilleurs
optimisateurs PC du marché : **Hone**, **MSI Afterburner**, **ExitLag**, **Razer Cortex**,
ainsi que le savoir-faire de la communauté FR/EN du tweaking : FR33THY, Atlas OS, Chris Titus,
marvingdt/opti-pc, NAIKO, Mopti, leStripeZ, et beaucoup d'autres.

## Identité visuelle

**Style Linear.app + marbre signature.** Aurum Tweaks utilise une esthétique inspirée de
[linear.app](https://linear.app) — palette ultra-sombre `#08080A` de base, bordures à peine
visibles, typographie tight (`Segoe UI Variable`), géométrie sharp (corners 5–6 px),
accent or `#D4AF37` — combinée avec une **signature de marbre procédural** qui apparaît sur :

- Le **splash screen** au lancement (seed `2026`)
- Le **logo de la sidebar** dans toutes les pages (seed `888`)
- Le **hero du Dashboard** (seed `404`)
- Le **header des Conseils** (seed `747`)
- Le **panneau latéral du Welcome wizard** (seed `111`)

Le marbre est généré **procéduralement** à partir d'un seed déterministe via
`Controls/MarbleSurface.cs` — bruit de valeur multi-octave + ondes sinusoïdales pour les
veines + grain subtil + relief directionnel. Chaque seed produit un slab unique mais
reproductible — quand l'utilisateur voit ce marbre noir-or aux veines particulières,
il sait immédiatement qu'il regarde Aurum Tweaks.

## Vision

- **Trois niveaux d'optimisation** : Tranquille (safe), Avancé (gains solides), Extrême (compétitif hardcore)
- **Focus BIOS** : c'est là que se cachent les plus gros gains. Auto-détection mobo/CPU + guides ultra spécifiques.
- **Auto-apply transactional** : restore point Windows + revert per-tweak. Aucun PC cassé.
- **Anti-cheat matrix** : compatibilité Vanguard/FACEIT/EAC/BattlEye documentée par tweak.
- **i18n** dès le départ : FR-FR par défaut, EN-US prêt, architecture .resx.

## Sections de l'app

| Section | Contenu |
|---|---|
| Tableau de bord | Marble hero + détection hardware + métriques live (CPU/GPU/RAM/temps) |
| Tweaks Windows | Catalogue JSON-driven, 3 niveaux, filtres tier/catégorie/AC, badges colorés par tier |
| **BIOS** | Auto-détect mobo/CPU + checklist interactive + presets RAM (DDR4/DDR5) |
| Overclocking | Sliders GPU + auto-OC + tests de stabilité + abstraction `IGpuOcService` (NVAPI/ADL-ready) |
| Gaming | Game Mode + détection auto jeux (Steam/Epic/Riot/Battle.net/...) + opti réseau |
| Pilotes | DDU + NVCleanstall + NVIDIA Profile Inspector |
| Monitoring | LibreHardwareMonitor live + sparkline charts 90s (CPU/GPU/Temps/RAM) |
| Profils | 6 presets intégrés (Stock, Tranquille, Compétitif sécurisé, Gaming, Streaming, Extrême) |
| Conseils | Marble header + comparatif honnête OS + hardware mods |
| Paramètres | Langue (FR/EN), point de restauration auto, mode AC strict, télémétrie opt-in, persistence localappdata |

## Tech stack

- **WPF .NET 8** (Windows-only native, deepest Windows integration)
- **MVVM** via CommunityToolkit.Mvvm 8.3
- **DI** via Microsoft.Extensions.DependencyInjection
- **Logs** via Serilog (file + console)
- **Hardware** via LibreHardwareMonitor + System.Management (WMI)
- **Marbre procédural** via WriteableBitmap + value noise (code maison, sans dépendance)
- **i18n** via .resx (fr-FR par défaut, en-US prêt)
- **Tweaks** chargés depuis JSON dans `src/AurumTweaks/Tweaks/{tranquille,advanced,extreme}/`
- **Persistence** settings + profils → `%LOCALAPPDATA%\AurumTweaks\`

## Build & run

Prérequis : .NET 8 SDK, Windows 10/11 x64.

```powershell
# Compiler
dotnet build

# Lancer (admin requis via app.manifest)
dotnet run --project src\AurumTweaks
```

L'app demande UAC admin au lancement (requis pour modifier registre, services, BIOS-side).
Au premier lancement, un Welcome wizard apparaît avec le marbre signature en panneau latéral.

## Architecture

```
src/AurumTweaks/
├── App.xaml(.cs)                     # Bootstrap + DI + splash + welcome
├── MainWindow.xaml(.cs)              # Shell + marble logo sidebar + Linear chrome
├── app.manifest                      # UAC requireAdministrator
├── appsettings.json                  # Paths, CDN URLs, defaults
├── Controls/
│   ├── MarbleSurface.cs              # Procedural marble brand mark (value noise)
│   └── Sparkline.cs                  # Lightweight live-data line chart
├── Resources/
│   ├── Themes/AurumDarkGold.xaml     # Linear-inspired palette + marble accents
│   └── Styles/                       # Cards (Linear-flat), Buttons, NavButton, Toggles, Scrollbars
├── Localization/
│   ├── Strings.resx                  # neutral
│   ├── Strings.fr.resx               # FR
│   └── Strings.en.resx               # EN
├── Models/                           # Tweak / Profile / HardwareInfo / Bios / Tips models
├── Services/
│   ├── Interfaces.cs                 # All service contracts
│   ├── TweakRepository.cs            # JSON loader
│   ├── TweakService.cs               # Apply/Revert orchestrator (transactional)
│   ├── RegistryService.cs            # Registry HKLM/HKCU
│   ├── ServiceManagerService.cs      # Service start types
│   ├── RestorePointService.cs        # Windows System Restore
│   ├── HardwareService.cs            # WMI detection + AC detection
│   ├── MonitoringService.cs          # LibreHardwareMonitor polling
│   ├── ProfileService.cs             # Profiles + built-in presets
│   ├── LocalizationService.cs        # i18n
│   ├── NavigationService.cs          # Page-key navigation
│   ├── GameDetectionService.cs       # Multi-launcher scan
│   ├── NetworkOptiService.cs         # Ping/jitter/loss
│   ├── AppSettingsStore.cs           # Settings persistence (localappdata)
│   └── GpuOcService.cs               # GPU OC abstraction (NVAPI/ADL-ready)
├── ViewModels/                       # 11 ViewModels (Main + 10 sections)
├── Views/                            # 10 UserControl views + SplashWindow + WelcomeWindow
├── Converters/                       # Bool/String/Tier/Risk/AC converters
└── Tweaks/                           # JSON tweak database (~40 tweaks)
    ├── tranquille/                   # safe and 100% reversible
    ├── advanced/                     # intermediate
    └── extreme/                      # high-impact + anti-cheat warnings
```

## État actuel — v0.1.0 early preview

**Fonctionnel** :
- Squelette WPF .NET 8 complet, build verte
- **Signature marble** procédurale appliquée sur 5 surfaces clés
- Splash screen + Welcome wizard first-launch
- Détection hardware via WMI (CPU/Mobo/GPU/RAM/OS/AC engines)
- Monitoring live via LibreHardwareMonitor + sparkline 90s
- Système JSON de tweaks avec apply/revert (registry, services, AppX, BCDEdit, PowerShell)
- Catalogue : ~40 tweaks issus de la recherche FR/EN
- BIOS : 9 settings documentés ASUS/MSI/Gigabyte/ASRock + 4 presets RAM
- Tips : 6 OS comparés + 4 hardware mods recommandés
- Détection jeux : Steam, Epic, Riot, Battle.net, EA, Ubisoft, GOG
- Point de restauration Windows automatique avant batch d'apply
- Settings persistence + first-launch welcome
- i18n complet FR + EN
- TweaksView avec badges tier colorés + indicateur AC risk

**Roadmap court terme** :
- NVAPI/ADL native pour OC GPU effectif (abstraction prête, scaffolding en place)
- Stress test runner intégré (Heaven/OCCT auto)
- Network multipath routing (WFP driver)
- FPS overlay AC-safe (PresentMon-based)
- CDN updates pour catalogue de tweaks
- Cloud sync des profils

## Documentation

- [docs/research-fr-scene.md](docs/research-fr-scene.md) — synthèse scène FR du tweaking
- [docs/research-bios-deep.md](docs/research-bios-deep.md) — guide BIOS Ryzen + NVIDIA
- [docs/research-custom-os.md](docs/research-custom-os.md) — comparatif OS custom honnête
- [docs/research-competitive-apps.md](docs/research-competitive-apps.md) — analyse Hone/Afterburner/ExitLag/Cortex

## Crédits

Aurum Tweaks s'inspire et s'appuie sur le travail des communautés du tweaking PC :

- **FR33THY** — Ultimate Windows Optimization Guide
- **Chris Titus Tech** — WinUtil (architecture JSON)
- **Atlas OS team** — catégorisation 8-axes, playbook format
- **hellzerg** — Optimizer (i18n)
- **builtbybel** — Privatezilla (mode Analyze)
- **NTDEV** — Tiny11
- **marvingdt** — opti-pc.github.io
- **MSI / Unwinder** — Afterburner / RTSS gold standard
- **Mopti, NAIKO, GouashTweaks, Thomy, leStripeZ** — scène FR
- **Buildzoid, Veii, der8auer** — hardcore overclocking guidance

## Licence

À déterminer (recommandé : MIT pour la base + freemium pour fonctions Pro). Pour l'instant : code source privé en preview.

---

**Aurum Tweaks** · Premium PC Optimizer · `v0.1.0` · 2026 · Reconnaissable par sa marbrure noir-or signature
