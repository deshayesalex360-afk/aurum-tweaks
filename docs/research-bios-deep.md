# Recherche BIOS : guide approfondi Ryzen + NVIDIA

Document de référence pour le contenu de l'onglet BIOS.

## Top 10 settings haut impact

| Setting | Impact | Risque |
|---|---|---|
| EXPO/DOCP/XMP | +20% mémoire | Très bas |
| Curve Optimizer per-core | +5-10% CPU + thermals | Bas |
| FCLK 1900-2100 | +5-8% jeux | Bas |
| ReBAR + Above 4G | +0-15% jeux | Aucun |
| Secondary timings tightening | +5-10% jeux | Bas-modéré |
| GPU undervolt curve | +0-3% perf, -25% conso | Aucun |
| VBS off (si pas anti-cheat strict) | +3-10% FPS | Sécurité réduite |
| NVIDIA Reflex + Low Latency Ultra | -20-40ms latence | Aucun |
| PBO + Boost Override | +3-5% multi | Thermal |
| CSM Disabled (UEFI pur) | Permet ReBAR/SB | Aucun |

## Avertissements critiques

- **VSOC ≤ 1.30V sur AM5** : historique burn 2023 sur Ryzen 7950X3D
- **Ryzen X3D restrictions** : 5800X3D pas de PBO, 7800X3D CO -30 only, 9800X3D full support
- **DRAM Calculator** : abandonné, utiliser Buildzoid / Veii / ZenTimings / Hydra
- **CoreCycler** : outil incontournable pour validation Curve Optimizer per-core
- **GDM / Power Down / MCR** : settings RAM les plus oubliés à fort impact

## Variantes vendeurs

ASUS ROG/Strix, MSI MEG/MAG, Gigabyte AORUS, ASRock Taichi — chaque vendor utilise des noms différents pour les mêmes settings. Mapping intégré dans BiosViewModel.cs.

## Outils validation

- **TestMem5 (TM5)** anta777 Extreme/Absolut — référence RAM stability
- **HCI Memtest** 800-1000% coverage
- **Karhu RAMTest** (payant) — détecte erreurs subtiles
- **CoreCycler** (sp00n GitHub) — Curve Optimizer per-core
- **OCCT / Prime95 Small FFT** — CPU+RAM stress
- **y-cruncher** — math intensif

## Référence content : sections détaillées

Le contenu détaillé du BIOS (RAM timings, PBO, VSOC, IOMMU, ReBAR, etc.) est intégré directement dans `BiosViewModel.LoadBiosSettings()` avec mapping par vendor.

Voir aussi le BiosSetting model dans `Models/BiosModels.cs` pour la structure de données.
