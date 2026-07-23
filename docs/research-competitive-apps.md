# Recherche concurrentielle & positionnement d'Aurum Tweaks

> **Vérifié contre le code le 2026-07-23** (build v0.1.0). Ce document décrit
> l'état **réel** d'Aurum — les fonctions non implémentées sont listées à part,
> jamais comptées comme faites. Les capacités des concurrents reposent sur leurs
> usages publics connus (voir *Sources*) et peuvent évoluer ; à recouper avant
> toute décision. Le mandat d'honnêteté de l'app s'applique aussi à ce document :
> **épingler une valeur fausse dans la matrice serait en soi une violation.**

Positionnement en une phrase : les concurrents sont des **spécialistes** (un pour
l'OC, un pour le debloat, un pour le réseau, un pour les processus) ; Aurum tente
d'**unifier** avec un parti-pris de réversibilité et de transparence. C'est sa
vraie force — et sa vraie limite, car chaque spécialiste mature le bat dans son
créneau.

## Matrice de positionnement (état réel v0.1.0)

Légende : **OUI** = implémenté et fonctionnel · **partiel** = présent mais limité ·
**NON** = absent.

| Capacité | **Aurum Tweaks** | Hone | Optimizer | MSI AB + RTSS | WinUtil | Razer Cortex |
|---|---|---|---|---|---|---|
| Tweaks système / debloat | **OUI — 3 paliers, réversibles** | partiel (gaming, cap free) | OUI (privacy/debloat fort) | NON | OUI (référence) | partiel (léger) |
| Overclocking GPU | **OUI — natif NVAPI + ADLX, sans voltage** | NON | NON | OUI (référence, + voltage) | NON | NON |
| Contrôle ventilateur | **partiel — % manuel NVIDIA ; AMD → Adrenalin** | NON | NON | OUI (courbe complète) | NON | NON |
| Test de stabilité intégré | **OUI — GPU (D3D11) + CPU + RAM** | NON | NON | via Kombustor (séparé) | NON | NON |
| Monitoring capteurs temps réel | **OUI (LibreHardwareMonitor)** | partiel | NON | OSD via RTSS/HWiNFO | NON | partiel (FPS) |
| Gestion des processus | **partiel — priorité/affinité + règles opt-in** | partiel | NON | NON | NON | partiel (boost en jeu) |
| Détection de jeux | **partiel — scan multi-launcher (pas d'auto-apply)** | partiel | NON | NON | NON | OUI (référence) |
| Réseau / DNS / latence | **OUI — diagnostics + tweaks + DNS + DPC/ISR** | partiel | partiel (hosts) | NON | partiel | NON |
| Routage réseau (ping) | **NON — non implémenté** | NON | NON | NON | NON | NON |
| OSD / overlay en jeu | **NON — pas d'overlay** | NON | NON | OUI (RTSS) | NON | partiel |
| Réversibilité / restauration | **OUI — apply↔revert + restore point + snapshots** | partiel | partiel (restore point) | partiel (profils) | partiel | partiel (auto-restore) |
| Journal & page transparence | **OUI — journal + Transparence + preuve avant/après** | NON | partiel (code ouvert) | NON | partiel (code ouvert) | NON |
| Risque anti-triche par tweak | **OUI — matrice de risque par tweak + détection live** | NON | NON | N/A | NON | NON |
| Modèle (prix) | **Gratuit, sans pub** | Payant (abo) | Gratuit | Gratuit | Gratuit | Gratuit |
| Signé / confiance établie | **NON — non signé, v0.1.0** | OUI | partiel (open source) | OUI (15 ans) | OUI | OUI |
| Open source | **OUI (GitHub public)** | NON | OUI | NON | OUI | NON |
| Langue | **partiel — FR (EN partiel)** | OUI (multi) | OUI (multi) | OUI (multi) | partiel | OUI (multi) |

## Détail des capacités GPU (le point le plus scruté)

Toutes vérifiées dans le code, avec les limites honnêtes affichées dans l'app :

- **NVIDIA — offsets core / mémoire** (NVAPI `SetPstates20`, `NvApi.cs:278`) :
  appliqués sur toute carte pilotable. Delta **clampé par le driver**, **volatile**
  (remis à zéro au reboot) et — contrairement au power/temp — **non confirmé par
  relecture** ; le code et l'UI le disent explicitement.
- **NVIDIA — power limit & cible température** (`NvApi.cs:393`/`:517`) : interface
  *community-documented* (non documentée par NVIDIA), appliquée **uniquement** sur
  les cartes dont la fenêtre min/def/max se lit de façon plausible, **chaque écriture
  confirmée par relecture** ou signalée en échec. Sinon → renvoi honnête à Afterburner.
- **AMD — power limit + fréquence GPU max + fréquence mémoire max** (ADLX officielle,
  `AdlxApi.cs:281/396/505`) : gated par les flags `IsSupported…` par-GPU + cohérence,
  valeurs sur l'échelle d'Adrenalin (round-trip), **confirmées par relecture**. Min-freq,
  voltage et cartes anciennes (Tuning1) → laissés à Adrenalin, jamais à moitié faits.
- **Ventilateur NVIDIA** (`NvApi.cs:669`, `GpuFanSafety.cs`) : % manuel réel, relecture
  confirmée, **plancher de sécurité 20 %** appliqué deux fois. Volatile.
- **Test de stabilité** : charge **GPU D3D11 matérielle** (jamais CPU/WARP ; sinon renvoi
  honnête à FurMark/OCCT), + tests **CPU** et **RAM**. Détection de reset driver (TDR) lue
  dans le journal d'événements Windows ; verdict **Indéterminé** (jamais un faux « stable »)
  si trop peu d'échantillons.
- **Auto-retour de sécurité** (`GpuOcAutoRevert.cs`) : compte à rebours (15 s par défaut)
  armé **uniquement** quand un axe capable de crasher a changé (offsets core/mem ou
  fréquences AMD) ; « Conserver » annule.
- **Voltage : JAMAIS appliqué** — confirmé (aucun `TrySetVoltage`/`SetVoltage` dans tout
  `src`). Le curseur de voltage est une **référence** à reporter dans Afterburner/Adrenalin,
  pas un bouton mort.

## Les spécialistes (hors matrice) — là où ils dominent

- **MSI Afterburner + RivaTuner/RTSS** — la référence OC : **voltage**, **éditeur de courbe
  ventilateur complet**, OSD matériel (RTSS), limiteur de framerate, multi-vendeur. Aurum
  n'a ni voltage, ni courbe ventilateur, ni overlay (choix + jeunesse).
- **ExitLag** — vrai **routage réseau** multipath (chemins privés optimisés) pouvant réduire
  ping/jitter/perte. Catégorie qu'Aurum **ne touche pas** (et, par honnêteté, ne devrait pas
  prétendre toucher).
- **Process Lasso (Bitsum)** — **ProBalance** : dé-priorisation dynamique et permanente des
  tâches de fond, règles persistantes, automatisation des plans d'alim. Bien au-delà des
  règles opt-in d'Aurum.
- **HWiNFO** — monitoring de **référence** : couverture de capteurs la plus large et la plus
  fiable, journalisation, flux mémoire partagée vers les overlays. Plus profond que la base
  LibreHardwareMonitor d'Aurum.
- **O&O ShutUp10++** — spécialiste **confidentialité** Windows : longue liste curée avec
  presets recommandés, explication par réglage, undo + point de restauration.

## Différenciateurs réels d'Aurum (aujourd'hui, vérifiés)

1. **Vrai tout-en-un** — réunit ce qui demande d'ordinaire 4–5 outils (tweaks + OC GPU +
   monitoring + réseau + processus + maintenance).
2. **Mandat d'honnêteté** — aucun bouton mort, aucune métrique inventée, aucun faux badge
   « sûr » ; une page **Transparence** dédiée qui liste ce que l'app *ne fait pas*. Rare dans
   un marché d'« optimiseurs » qui sur-promettent.
3. **Réversibilité de premier ordre** — apply ↔ revert = inverses, point de restauration
   avant chaque lot, journal des modifications, instantanés système.
4. **OC GPU natif confirmé par relecture** — chaque écriture power/temp/fréquence/ventilo
   vérifiée et per-carte ; dégradation en renvoi honnête plutôt qu'en faux contrôle ;
   jamais de voltage/vBIOS/driver noyau.
5. **Risque anti-triche par tweak** — modèle `AntiCheatMatrix` (Vanguard/EAC/BattlEye/Faceit/
   Ricochet/ESEA → Safe/Risky/Banned) attaché à chaque tweak, affiché en badge « AC RISK »
   et injecté dans le score adaptatif avec détection live de l'anti-triche présent. *(Ce n'est
   pas une « matrice publique » autonome — c'est une donnée de risque intégrée, plus utile.)*
6. **Gratuit, sans pub, open source** — face à Hone (payant) et aux suites « cleaner » chargées
   de pub.

## Limites honnêtes actuelles

- **Non signé** (SmartScreen), **v0.1.0**, communauté naissante, UI surtout en français.
- **Chemins d'écriture GPU** confirmés par relecture *dans le code* et validés en **lecture**
  sur une RTX 4080 SUPER, mais les **écritures ne sont pas encore exécutées sur matériel réel** ;
  la vérification a lieu au premier « Appliquer ».
- **Pas d'éditeur de courbe ventilateur** (un helper de courbe existe mais l'action câblée est
  le % manuel) ; **ventilateur AMD** renvoyé à Adrenalin.
- **Gestion des processus** = priorité manuelle (Normal/Au-dessus/Haute uniquement) + affinité
  + règles persistantes **opt-in** via tâche planifiée visible. **Pas** de ProBalance dynamique.
- Monitoring moins profond que HWiNFO ; OC moins mature qu'Afterburner (pas de voltage — choix).

## Non implémenté (roadmap — ne pas vendre comme présent)

- **OSD / overlay en jeu.** *(Le benchmark mesure les frame-times via ETW/DXGI **sans injecter
  aucune DLL** — c'est une mesure, pas un overlay.)*
- **Routage réseau (WFP/WinDivert/multipath).** `NetworkOptiService` le désavoue explicitement ;
  le réseau se limite aux diagnostics (ping/jitter/perte, traceroute lecture seule, benchmark DNS)
  et aux tweaks registre + page DNS.
- **Auto-apply de profils GPU par jeu.** Fondation seulement (`GameOcBinding`/`GameOcMatching`/
  `GameOcBindingStore`) : **aucun consommateur** dans l'app, pas de watcher lancement/sortie.
  La détection de jeux, elle, fonctionne (scan).
- Signature de code, sync cloud des profils, éditeur de courbe ventilateur, ventilateur AMD natif.

## Inventaire complet des pages (41)

Tableau de bord · Tweaks Windows · BIOS · Calculatrice timings RAM · Stabilité mémoire ·
Stabilité CPU · Overclocking GPU · Gaming · Démarrage · Alimentation · Tâches planifiées ·
Applications préinstallées · Nettoyage disque · Santé des disques · Priorité & affinité ·
Latence DPC/ISR · Services Windows · Effets visuels · Mémoire vive · Confidentialité ·
Optimisations jeu · Points de restauration · Veille & hibernation · Barrettes mémoire ·
Cartes réseau · Mémoire virtuelle · Son · Windows Update · Affichage · Serveurs DNS ·
Benchmark frame-times · Pilotes · Périphériques · Monitoring temps réel · Profils ·
Journal des modifications · Instantanés système · Conseils · Transparence & confiance ·
Licence · Paramètres.

## Sources principales

- Hone — https://hone.gg
- Optimizer (hellzerg) — https://github.com/hellzerg/optimizer
- MSI Afterburner / RivaTuner RTSS — Guru3D
- Chris Titus Tech WinUtil — https://github.com/ChrisTitusTech/winutil
- Razer Cortex — https://www.razer.com/cortex
- ExitLag — https://www.exitlag.com/how-it-works
- Process Lasso (Bitsum) — https://bitsum.com
- HWiNFO — https://www.hwinfo.com
- O&O ShutUp10++ — https://www.oo-software.com/en/shutup10
