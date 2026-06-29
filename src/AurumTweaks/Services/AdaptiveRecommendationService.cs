using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// The "adapté à chaque PC" brain. Cross-references the detected hardware against the
/// full tweak catalogue to produce a ranked, personalized plan plus human-readable
/// hardware insights (RAM speed, BIOS, VBS, X3D, vendor-specific guidance…).
/// </summary>
public sealed class AdaptiveRecommendationService : IAdaptiveRecommendationService
{
    private readonly IHardwareService _hardware;
    private readonly ITweakRepository _repo;

    public AdaptiveRecommendationService(IHardwareService hardware, ITweakRepository repo)
    {
        _hardware = hardware;
        _repo = repo;
    }

    public async Task<AdaptivePlan> BuildPlanAsync(bool strictCompetitive)
    {
        var hw = await _hardware.DetectAsync();
        var all = await _repo.LoadAllAsync();

        var recs = new List<TweakRecommendation>();
        foreach (var t in all)
        {
            if (!IsApplicable(t, hw)) continue;

            bool acConcern = t.AntiCheat.HasAnyConcern && (strictCompetitive || HasActiveAntiCheat(hw));
            bool tuned = t.Applicability?.IsHardwareSpecific == true && MatchesSpecificHardware(t, hw);
            int score = ScoreTweak(t, hw, strictCompetitive, tuned, acConcern);
            bool inDefault = IsInDefaultSet(t, strictCompetitive, acConcern);

            recs.Add(new TweakRecommendation
            {
                Tweak = t,
                Score = score,
                ReasonFr = BuildReason(t, hw, tuned, acConcern, strictCompetitive),
                InDefaultSet = inDefault,
                IsTunedForThisPc = tuned
            });
        }

        var ordered = recs
            .OrderByDescending(r => r.InDefaultSet)
            .ThenByDescending(r => r.IsTunedForThisPc)
            .ThenByDescending(r => r.Score)
            .ToList();

        var insights = BuildInsights(hw, strictCompetitive);

        int potential = 0;
        potential += ordered.Count(r => r.InDefaultSet && !r.Tweak.IsApplied) * 3;
        potential += insights.Count(i => i.Severity == InsightSeverity.Opportunity) * 12;
        if (hw.RamRunningBelowRated) potential += 15;
        potential = Math.Clamp(potential, 0, 100);

        return new AdaptivePlan
        {
            Recommendations = ordered,
            Insights = insights,
            ProfileSummaryFr = BuildProfileSummary(hw),
            RecommendedCount = ordered.Count(r => r.InDefaultSet),
            TotalApplicable = ordered.Count,
            PotentialScore = potential
        };
    }

    // ---------------------------------------------------------------- applicability

    public bool IsApplicable(Tweak tweak, HardwareInfo hw)
    {
        // OS version gate
        if (tweak.WindowsVersions.Count > 0)
        {
            string osTag = hw.IsWindows11 ? "11" : "10";
            if (!tweak.WindowsVersions.Contains(osTag)) return false;
        }

        var a = tweak.Applicability;
        if (a is null) return true;

        if (a.RequiresWin11 && !hw.IsWindows11) return false;
        if (a.DesktopOnly && hw.IsLaptop) return false;
        if (a.SsdOnly && !hw.SystemDriveIsSsd) return false;
        if (a.MinRamGb > 0 && hw.TotalRamGb + 0.5 < a.MinRamGb) return false;

        if (a.CpuVendors.Count > 0 && !a.CpuVendors.Any(v => IsCpuVendor(hw, v))) return false;
        if (a.CpuFamilies.Count > 0 && !a.CpuFamilies.Contains(hw.DetectedFamily)) return false;
        if (a.GpuVendors.Count > 0 && !a.GpuVendors.Contains(hw.GpuVendor)) return false;
        if (a.RamTypes.Count > 0 && !a.RamTypes.Any(r => string.Equals(r, hw.RamType, StringComparison.OrdinalIgnoreCase))) return false;

        return true;
    }

    private static bool MatchesSpecificHardware(Tweak t, HardwareInfo hw)
    {
        var a = t.Applicability;
        if (a is null) return false;
        if (a.GpuVendors.Count > 0 && a.GpuVendors.Contains(hw.GpuVendor)) return true;
        if (a.CpuFamilies.Count > 0 && a.CpuFamilies.Contains(hw.DetectedFamily)) return true;
        if (a.CpuVendors.Count > 0 && a.CpuVendors.Any(v => IsCpuVendor(hw, v))) return true;
        if (a.RamTypes.Count > 0 && a.RamTypes.Any(r => string.Equals(r, hw.RamType, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private static bool IsCpuVendor(HardwareInfo hw, string vendor) =>
        hw.CpuVendor.Contains(vendor, StringComparison.OrdinalIgnoreCase)
        || hw.CpuName.Contains(vendor, StringComparison.OrdinalIgnoreCase);

    private static bool HasActiveAntiCheat(HardwareInfo hw) =>
        hw.VanguardDetected || hw.EacDetected || hw.BattlEyeDetected || hw.FaceItAcDetected;

    // ---------------------------------------------------------------- scoring

    private static int ScoreTweak(Tweak t, HardwareInfo hw, bool strict, bool tuned, bool acConcern)
    {
        int score = t.Priority;

        score += t.Tier switch
        {
            TweakTier.Tranquille => 30,
            TweakTier.Avance => 10,
            TweakTier.Extreme => -20,
            _ => 0
        };

        score += t.Risk switch
        {
            RiskLevel.None => 0,
            RiskLevel.Low => -4,
            RiskLevel.Medium => -15,
            RiskLevel.High => -40,
            RiskLevel.HardwareDamage => -100,
            _ => 0
        };

        if (tuned) score += 18;                 // specifically tuned for this machine
        if (acConcern) score -= 60;             // would jeopardise the user's anti-cheat
        if (t.IsApplied) score -= 25;           // already done — push down

        return score;
    }

    private static bool IsInDefaultSet(Tweak t, bool strict, bool acConcern)
    {
        if (acConcern) return false;
        if (t.Risk >= RiskLevel.High) return false;
        return t.Tier == TweakTier.Tranquille
               || (t.Tier == TweakTier.Avance && t.Risk <= RiskLevel.Low);
    }

    // ---------------------------------------------------------------- reasons

    private static string BuildReason(Tweak t, HardwareInfo hw, bool tuned, bool acConcern, bool strict)
    {
        if (tuned)
        {
            var a = t.Applicability!;
            if (a.GpuVendors.Contains(hw.GpuVendor) && hw.GpuVendor != GpuVendor.Unknown)
                return $"Optimisé pour ton GPU {GpuLabel(hw.GpuVendor)}";
            if (a.CpuFamilies.Contains(hw.DetectedFamily) && IsX3D(hw.DetectedFamily))
                return "Spécifique aux CPU X3D — préserve le cache 3D";
            if (a.CpuFamilies.Contains(hw.DetectedFamily))
                return $"Adapté à ton {FamilyLabel(hw.DetectedFamily)}";
            if (a.CpuVendors.Any(v => IsCpuVendor(hw, v)))
                return $"Adapté aux CPU {(IsCpuVendor(hw, "AMD") ? "AMD" : "Intel")}";
            if (a.RamTypes.Any(r => string.Equals(r, hw.RamType, StringComparison.OrdinalIgnoreCase)))
                return $"Adapté à ta {hw.RamType}";
            if (a.SsdOnly) return "Pertinent car ton disque système est un SSD/NVMe";
        }

        string baseReason = t.Tier switch
        {
            TweakTier.Tranquille => "Sûr, réversible, gain immédiat",
            TweakTier.Avance => "Gain notable, risque maîtrisé",
            TweakTier.Extreme => "Gain maximal, réservé aux initiés",
            _ => "Recommandé"
        };

        if (acConcern)
            baseReason += strict ? " · masqué (mode compétitif)" : " · ⚠ vérifie ton anti-cheat";

        return baseReason;
    }

    // ---------------------------------------------------------------- hardware insights

    private static IReadOnlyList<HardwareInsight> BuildInsights(HardwareInfo hw, bool strict)
    {
        var list = new List<HardwareInsight>();

        // 1. RAM running under its rated speed → biggest free win on most PCs.
        if (hw.RamRunningBelowRated)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Opportunity,
                TitleFr = $"Ta {hw.RamType} tourne à {hw.RamConfiguredMhz} MT/s au lieu de {hw.RamRatedMhz} MT/s",
                DetailFr = "Le profil mémoire (EXPO sur AMD / XMP sur Intel) n'est pas actif. " +
                           "C'est souvent le plus gros gain gratuit : active-le dans le BIOS pour récupérer la vitesse pour laquelle tu as payé.",
                ActionPage = "Bios",
                ActionLabelFr = "Ouvrir le guide BIOS"
            });
        }
        else if (hw.RamRatedMhz > 0)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Info,
                TitleFr = $"Mémoire {hw.RamType} à pleine vitesse ({hw.RamConfiguredMhz} MT/s)",
                DetailFr = "Ton profil mémoire EXPO/XMP semble actif. Parfait."
            });
        }

        // 1b. RAM stability validation — the honest companion to enabling/tightening EXPO/XMP.
        if (hw.RamRatedMhz > 0)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Info,
                TitleFr = "Valide ta RAM après EXPO / XMP",
                DetailFr = "Dès que tu actives l'EXPO/XMP ou que tu resserres des timings, teste la mémoire : « Stabilité RAM » " +
                           "écrit des motifs sur la RAM libre et relit chaque octet (la moindre erreur = profil instable, desserre d'un cran). " +
                           "C'est un screen rapide — pour la certitude, enchaîne avec TestMem5 / Karhu / HCI sur la nuit.",
                ActionPage = "Stability",
                ActionLabelFr = "Tester la stabilité RAM"
            });
        }

        // 1c. Single-channel RAM → ~half the memory bandwidth, a classic « pourquoi mon PC rame » tell
        // (frequent on prebuilts and laptops shipped with one stick). Same single-module signal the channel
        // summary derives, reused — not re-judged. Gated on slot count so we never suggest adding a stick to a
        // board that physically has only one slot (slot count 0 = WMI couldn't tell, still worth flagging).
        if (hw.RamModuleCount == 1 && hw.RamSlotCount != 1)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Opportunity,
                TitleFr = "RAM en single-channel — un seul module détecté",
                DetailFr = "Une seule barrette est installée : la mémoire tourne en single-channel, ce qui divise quasiment par deux " +
                           "la bande passante. Ajouter une barrette identique (même capacité, idéalement le même kit) passe en dual-channel " +
                           "et regagne souvent 10-20 % en jeu et beaucoup plus sur un iGPU. Vérifie d'abord qu'un slot est libre.",
                ActionPage = "MemoryModules",
                ActionLabelFr = "Voir les barrettes mémoire"
            });
        }

        // 1d. Mismatched stick capacities → the kit runs in "flex mode" (only the matched portion is dual-channel,
        // the rest retombe en single-channel) and two different kits often refuse to POST at the rated EXPO/XMP
        // profile. Capacity is the one per-module field WMI reports reliably, so we name the actual sticks. Needs
        // ≥2 sticks of differing size, so it never overlaps the single-channel (1-module) tell. Warning, not
        // Opportunity: it isn't a BIOS toggle you can reclaim — the honest fix is buying a matched kit.
        if (hw.RamCapacityMismatched)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Warning,
                TitleFr = $"Barrettes mémoire dépareillées — {DescribeRamCapacities(hw)}",
                DetailFr = "Tes modules n'ont pas tous la même capacité : la mémoire tourne en « flex mode » — seule la " +
                           "part appairée profite du dual-channel, le reste retombe en single-channel — et deux kits " +
                           "différents refusent souvent de booter au profil EXPO/XMP annoncé. Pour la pleine bande passante " +
                           "et la stabilité, vise un kit unique de barrettes identiques (même capacité, mêmes timings).",
                ActionPage = "MemoryModules",
                ActionLabelFr = "Voir les barrettes mémoire"
            });
        }

        // 2. CPU-family specific guidance
        if (IsX3D(hw.DetectedFamily))
        {
            // Mono-CCD X3D (7800X3D/9800X3D) is one all-cache CCD — there is nothing to "park", so repeating the
            // dual-CCD scheduling advice there would mislead. Dual-CCD X3D (7900X3D/7950X3D/9900X3D/9950X3D)
            // carries the cache on one CCD and higher clocks on the other: a game landing on the wrong CCD is THE
            // classic X3D let-down, so we lead with the placement guidance. We can't read the parking state, so
            // both stay Info (no fabricated "reclaimable %") — only the wording differs by sub-type.
            if (hw.IsDualCcdX3D)
            {
                list.Add(new HardwareInsight
                {
                    Severity = InsightSeverity.Info,
                    TitleFr = "X3D à deux CCD — surveille le placement des jeux",
                    DetailFr = "Ton X3D a deux CCD : un avec le cache 3D (idéal en jeu), l'autre qui monte plus haut en fréquence. " +
                               "Pour que les jeux tournent sur le bon CCD, garde Xbox Game Bar activé et le pilote chipset AMD à jour " +
                               "(c'est ce duo qui pilote le parking de cœurs). Mal placé, un jeu peut perdre 10-20 %. Côté BIOS, " +
                               "privilégie un Curve Optimizer négatif plutôt qu'un OC fréquence.",
                    ActionPage = "Bios",
                    ActionLabelFr = "Réglages Curve Optimizer"
                });
            }
            else
            {
                list.Add(new HardwareInsight
                {
                    Severity = InsightSeverity.Info,
                    TitleFr = "CPU X3D détecté — cache 3D prioritaire",
                    DetailFr = "X3D mono-CCD : tous les cœurs partagent le cache 3D, donc aucun placement de CCD à gérer. Le cache " +
                               "fait gagner plus que la fréquence : évite l'OC fréquence agressif et privilégie un Curve Optimizer négatif.",
                    ActionPage = "Bios",
                    ActionLabelFr = "Réglages Curve Optimizer"
                });
            }
        }
        else if (hw.DetectedFamily is CpuFamily.IntelCore12 or CpuFamily.IntelCore13 or CpuFamily.IntelCore14 or CpuFamily.IntelCoreUltra)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Info,
                TitleFr = "Architecture hybride P-core / E-core",
                DetailFr = "Laisse le Thread Director gérer le placement des threads : évite de forcer l'affinité CPU manuellement. " +
                           "Assure-toi que Windows est à jour (le scheduler hybride s'améliore à chaque version).",
            });
        }
        else if (hw.DetectedFamily is CpuFamily.Ryzen5000 or CpuFamily.Ryzen7000 or CpuFamily.Ryzen9000)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Opportunity,
                TitleFr = "Ryzen détecté — PBO + Curve Optimizer",
                DetailFr = "Active PBO et un Curve Optimizer négatif (-15 à -30 selon le silicium) pour plus de fréquence à température égale. " +
                           "Surveille VSOC (≤ 1,30 V) pour la longévité.",
                ActionPage = "Bios",
                ActionLabelFr = "Guide PBO / Curve"
            });
        }

        // 2b. CPU stability validation — the honest companion to the PBO / Curve Optimizer / undervolt advice above.
        if (IsX3D(hw.DetectedFamily)
            || hw.DetectedFamily is CpuFamily.Ryzen5000 or CpuFamily.Ryzen7000 or CpuFamily.Ryzen9000
            || hw.DetectedFamily is CpuFamily.IntelCore12 or CpuFamily.IntelCore13 or CpuFamily.IntelCore14 or CpuFamily.IntelCoreUltra)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Info,
                TitleFr = "Valide ton undervolt / OC CPU",
                DetailFr = "Après un Curve Optimizer négatif, un undervolt ou un PBO poussé, vérifie que le CPU calcule juste sous charge : " +
                           "« Stabilité CPU » charge tous les cœurs et compare chaque lot à une référence (une erreur = trop agressif, remonte le vcore ou réduis l'offset). " +
                           "Reste un screen rapide — pour du 24/7, enchaîne avec CoreCycler / Prime95 / OCCT.",
                ActionPage = "CpuStability",
                ActionLabelFr = "Tester la stabilité CPU"
            });
        }

        // 2c. SMT / Hyper-Threading off → half the logical threads gone. The detector only fires when the
        // silicon genuinely exposes more threads than are active (guarded so a partially-parked CPU isn't
        // misread). Disabling SMT is a deliberate latency choice for some competitive players, so we state the
        // fact and the trade-off without judging it — mirrors the EXPO-off tell, never asserts a mistake.
        if (hw.SmtCapableButOff)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Opportunity,
                TitleFr = $"SMT / Hyper-Threading désactivé — {hw.CpuThreads} threads actifs sur {hw.CpuMaxThreads}",
                DetailFr = $"Ton CPU peut exécuter {hw.CpuMaxThreads} threads mais seulement {hw.CpuThreads} sont actifs : le SMT (AMD) / " +
                           "Hyper-Threading (Intel) est coupé dans le BIOS. Si ce n'est pas volontaire, le réactiver redonne tout le débit " +
                           "multi-thread (compilation, rendu, encodage, machines virtuelles). Certains le coupent exprès pour gagner en " +
                           "régularité en jeu compétitif — dans ce cas, c'est un choix assumé, garde-le.",
                ActionPage = "Bios",
                ActionLabelFr = "Ouvrir le guide BIOS"
            });
        }

        // 2d. Outdated BIOS → AGESA (AMD) / microcode (Intel) fixes left on the table. The release date is the
        // real firmware build date and we only fire past ~18 months, so this never over-claims. For Intel
        // 13/14th gen we escalate to a Warning: those parts need the 2024 microcode (0x12B) to avoid the
        // documented voltage-degradation, and a BIOS this old is exactly where it may still be missing.
        if (hw.BiosLikelyOutdated)
        {
            bool intelRaptor = hw.DetectedFamily is CpuFamily.IntelCore13 or CpuFamily.IntelCore14;
            list.Add(new HardwareInsight
            {
                Severity = intelRaptor ? InsightSeverity.Warning : InsightSeverity.Opportunity,
                TitleFr = $"BIOS daté de ~{hw.BiosAgeMonths} mois — une mise à jour est conseillée",
                DetailFr = intelRaptor
                    ? "Les Intel 13/14e gen (surtout i7/i9 K) ont besoin du microcode 0x12B, diffusé par mise à jour BIOS en 2024, " +
                      "pour éviter la dégradation par sur-tension. Un BIOS de cet âge peut ne pas l'inclure : mets-le à jour et applique " +
                      "le profil « Intel Default Settings ». C'est un correctif de stabilité à long terme, pas une simple option."
                    : "Un BIOS plus récent apporte les correctifs AGESA (AMD) / microcode (Intel) : meilleure compatibilité et stabilité " +
                      "mémoire (EXPO/XMP), support des derniers CPU et correctifs critiques. Lis le changelog, puis flashe via BIOS " +
                      "FlashBack / EZ Flash — jamais pendant un orage.",
                ActionPage = "Bios",
                ActionLabelFr = "Guide mise à jour BIOS"
            });
        }

        // 3. GPU vendor guidance
        switch (hw.GpuVendor)
        {
            case GpuVendor.Nvidia:
                list.Add(new HardwareInsight
                {
                    Severity = InsightSeverity.Opportunity,
                    TitleFr = "GPU NVIDIA — Low Latency + Prefer Max Performance",
                    DetailFr = "Dans le panneau NVIDIA / NVPI : mode faible latence « Ultra », gestion de l'alimentation « Privilégier perf max ». " +
                               "Aurum peut aussi forcer PowerMizer via les tweaks GPU.",
                    ActionPage = "Drivers",
                    ActionLabelFr = "Pilotes & NVPI"
                });
                break;
            case GpuVendor.Amd:
                list.Add(new HardwareInsight
                {
                    Severity = InsightSeverity.Opportunity,
                    TitleFr = "GPU AMD Radeon — Anti-Lag + ULPS",
                    DetailFr = "Active Radeon Anti-Lag et désactive ULPS (économie d'énergie qui cause du micro-stutter). " +
                               "Aurum propose le tweak « Désactiver ULPS » côté AMD.",
                    ActionPage = "Tweaks",
                    ActionLabelFr = "Voir les tweaks GPU"
                });
                break;
        }

        // 4. Resizable BAR reminder for modern GPUs (we can't always read its BIOS state).
        if (hw.GpuVendor is GpuVendor.Nvidia or GpuVendor.Amd && !hw.ResizableBarEnabled)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Opportunity,
                TitleFr = "Resizable BAR — à vérifier dans le BIOS",
                DetailFr = "Resizable BAR (Smart Access Memory côté AMD) peut donner +5-10 % en jeu sur les GPU récents. " +
                           "Vérifie qu'il est activé (nécessite CSM désactivé / boot UEFI).",
                ActionPage = "Bios",
                ActionLabelFr = "Vérifier dans le BIOS"
            });
        }

        // 5. VBS / HVCI cost for gamers
        if (hw.VbsRunning)
        {
            bool acForcesIt = strict || HasActiveAntiCheat(hw);
            list.Add(new HardwareInsight
            {
                Severity = acForcesIt ? InsightSeverity.Info : InsightSeverity.Opportunity,
                TitleFr = "VBS / Sécurité basée sur la virtualisation est actif",
                DetailFr = acForcesIt
                    ? "VBS coûte 5-10 % de perf CPU mais certains anti-cheats (Vanguard, FACEIT) l'exigent. On le laisse actif pour rester safe."
                    : "VBS coûte typiquement 5-10 % de perf CPU en jeu. Tu peux le désactiver (tweak Extrême) si aucun anti-cheat ne l'impose.",
                ActionPage = "Tweaks",
                ActionLabelFr = "Voir le tweak VBS"
            });
        }

        // 6. RAM capacity guidance
        if (hw.TotalRamGb > 0 && hw.TotalRamGb < 12)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Warning,
                TitleFr = $"Seulement {hw.TotalRamGb:F0} Go de RAM",
                DetailFr = "C'est juste pour le jeu moderne. NE désactive PAS le fichier d'échange (pagefile) et évite les tweaks mémoire agressifs. " +
                           "Un upgrade vers 16-32 Go serait le meilleur gain."
            });
        }
        else if (hw.TotalRamGb >= 32)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Info,
                TitleFr = $"{hw.TotalRamGb:F0} Go de RAM — confortable",
                DetailFr = "Tu peux te permettre les tweaks mémoire avancés (cache, pagefile fixe). Aurum les débloque automatiquement."
            });
        }

        // 7. Laptop power caveats
        if (hw.IsLaptop)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Warning,
                TitleFr = "PC portable détecté",
                DetailFr = "Les tweaks « desktop » (désactiver le core parking, plan Performances Ultimes permanent, USB selective suspend off) " +
                           "sont masqués ou modérés pour préserver l'autonomie et les températures."
            });
        }

        // 8. Storage
        if (!hw.SystemDriveIsSsd)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Warning,
                TitleFr = "Disque système HDD détecté",
                DetailFr = "Le passage à un SSD NVMe est, de loin, l'upgrade le plus sensible (boot, chargements, réactivité). " +
                           "Les tweaks « SSD » (Superfetch/Prefetch off) sont désactivés pour ne pas pénaliser un disque mécanique."
            });
        }

        // 9. LTSC / already-lean OS
        if (hw.IsLtsc)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Info,
                TitleFr = "Windows LTSC / IoT détecté",
                DetailFr = "Déjà épuré : beaucoup de tweaks de debloat (Recall, Copilot, apps Store) ne s'appliquent pas. On se concentre sur la perf pure."
            });
        }

        // 10. Windows 11 AI surface
        if (hw.IsWindows11 && !hw.IsLtsc)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Opportunity,
                TitleFr = "Windows 11 — Recall / Copilot / widgets",
                DetailFr = "Désactive Recall, Copilot et les widgets pour récupérer RAM, CPU idle et de la confidentialité. Inclus dans le set recommandé.",
                ActionPage = "Tweaks",
                ActionLabelFr = "Voir les tweaks Windows 11"
            });
        }

        // 11. Consumer Windows 10 past end-of-support. Win10 (Home/Pro) stopped receiving free security
        // updates on 14 Oct 2025 — a fixed historical fact, so this fires regardless of the system clock and
        // never over-claims. LTSC is excluded (it owns #9): IoT/Enterprise LTSC keep a much longer lifecycle.
        // A security matter → Warning. No ActionPage on purpose: no in-app action upgrades the OS, and the
        // detail says Aurum still optimises Win10 fully, so this informs without nagging or implying a capability.
        else if (!hw.IsWindows11 && !hw.IsLtsc)
        {
            list.Add(new HardwareInsight
            {
                Severity = InsightSeverity.Warning,
                TitleFr = "Windows 10 — fin du support depuis octobre 2025",
                DetailFr = "Windows 10 grand public ne reçoit plus de mises à jour de sécurité depuis le 14 octobre 2025. " +
                           "Le programme ESU (gratuit en liant un compte Microsoft, ou payant) prolonge les correctifs jusqu'en " +
                           "octobre 2026 ; au-delà, plus aucun patch de sécurité. Pour rester protégé durablement, envisage " +
                           "Windows 11 si ton matériel est compatible. Aurum continue d'optimiser Windows 10 normalement."
            });
        }

        return list
            .OrderByDescending(i => i.Severity)
            .ToList();
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsX3D(CpuFamily f) =>
        f is CpuFamily.Ryzen5000X3D or CpuFamily.Ryzen7000X3D or CpuFamily.Ryzen9000X3D;

    private static string FamilyLabel(CpuFamily f) => f switch
    {
        CpuFamily.Ryzen3000 => "Ryzen 3000",
        CpuFamily.Ryzen5000 => "Ryzen 5000",
        CpuFamily.Ryzen5000X3D => "Ryzen 5000 X3D",
        CpuFamily.Ryzen7000 => "Ryzen 7000",
        CpuFamily.Ryzen7000X3D => "Ryzen 7000 X3D",
        CpuFamily.Ryzen9000 => "Ryzen 9000",
        CpuFamily.Ryzen9000X3D => "Ryzen 9000 X3D",
        CpuFamily.IntelCore12 => "Intel 12e gen",
        CpuFamily.IntelCore13 => "Intel 13e gen",
        CpuFamily.IntelCore14 => "Intel 14e gen",
        CpuFamily.IntelCoreUltra => "Intel Core Ultra",
        _ => "CPU"
    };

    private static string GpuLabel(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD Radeon",
        GpuVendor.Intel => "Intel Arc",
        _ => "GPU"
    };

    // Human breakdown of the installed stick sizes, largest first — e.g. "16 + 8 Go" or "16 + 8 + 8 Go".
    // Reuses the model's GiB-rounded projection so the displayed sizes always match the mismatch flag.
    private static string DescribeRamCapacities(HardwareInfo hw) =>
        string.Join(" + ", hw.RamModuleCapacitiesGb) + " Go";

    private static string BuildProfileSummary(HardwareInfo hw)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(hw.CpuName) && hw.CpuName != "Unknown") parts.Add(hw.CpuName);
        if (!string.IsNullOrWhiteSpace(hw.GpuPrimary) && hw.GpuPrimary != "Unknown") parts.Add(hw.GpuPrimary);
        if (hw.TotalRamGb > 0)
        {
            var ram = $"{hw.RamType} {hw.TotalRamGb:F0} Go".Trim();
            if (hw.RamConfiguredMhz > 0) ram += $" @ {hw.RamConfiguredMhz} MT/s";
            parts.Add(ram);
        }
        if (!string.IsNullOrWhiteSpace(hw.OsCaption)) parts.Add(hw.OsCaption.Replace("Microsoft ", ""));
        if (hw.IsLaptop) parts.Add("Portable");
        return string.Join("  ·  ", parts);
    }
}
