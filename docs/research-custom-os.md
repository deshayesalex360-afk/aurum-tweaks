# Recherche OS custom : Atlas, Tiny11, ReviOS, Ghost Spectre, LTSC

## TL;DR — La vérité qui dérange

- Les gains FPS réels entre Windows 11 stock tweaké et Atlas/ReviOS sont **marginaux** (<5%).
- Les vrais gains mesurables : RAM idle (12-13% vs 17%), stabilité CPU au repos.
- Le placebo est massif dans cette communauté.
- **Windows 11 LTSC IoT 2024** officiel Microsoft offre 80% des bénéfices d'Atlas sans aucun risque.
- Pour 90% des users : **Windows 11 Pro + nos tweaks Extrême suffit**.

## Recommandations Aurum Tweaks

### Path 1 (90% des users)
Windows 11 Pro officiel + nos tweaks Extrême. Defender on, Updates on.

### Path 2 (power users)
**Windows 11 IoT Enterprise LTSC 2024** (officiel Microsoft) + nos tweaks.
- Pas de Copilot/Recall/AI
- Anti-cheat parfait
- Support sécurité 10 ans
- Légal via Visual Studio sub ou eval 90j

### Path 3 (eSport hardcore)
Atlas OS v0.5.0 sur Win11 25H2, MAIS en gardant Defender on et Updates on.

### Path 4 (vieux hardware)
Windows 10 LTSC IoT 2021 ou Tiny11 25H2 standard (pas Core).

### À déconseiller
- Ghost Spectre et tout ISO Windows modifié distribué par dev anonyme
- Tiny11 Core en daily driver
- N'importe quel OS custom si tu joues à Valorant/Vanguard ou utilises FACEIT

## Comparatif

| OS | Légal | Gain perf réel | Sécurité | Anti-cheat | Daily driver | Réversible |
|---|---|---|---|---|---|---|
| Win11 Pro + Aurum Extrême | Officiel | Bon | Conservée | OK | Oui | Oui 1 clic |
| Win11 LTSC IoT 2024 | Officiel (VS sub) | Excellent | Officielle Microsoft | OK | Oui | N/A |
| Atlas OS v0.5.0 | Légal (Playbook) | Marginal | Réduite | Probl. Vanguard | Risque | Non (reinstall) |
| ReviOS | Légal (Playbook) | Marginal | Réduite | Probl. Vanguard | Acceptable | Non (reinstall) |
| Tiny11 standard | Légal (build local) | Modéré (vieux PC) | Defender preserve | OK | Oui | N/A |
| Tiny11 Core | Légal (build local) | Élevé | Très réduite | Probl. | **NON** | N/A |
| Ghost Spectre | **Illégal** | Marginal | Inconnue/risquée | Variable | NON | N/A |
| Win10 LTSC IoT 2021 | Officiel (VS sub) | Excellent (vieux PC) | Officielle | OK | Oui | N/A |

Le contenu détaillé est intégré dans `TipsViewModel.cs` et affiché dans l'onglet Conseils.
