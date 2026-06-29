using System.Collections.Generic;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// The full BIOS settings knowledge base. Kept type-safe in C# (rather than JSON) because
/// the entries use enum-keyed dictionaries and per-CPU compatibility lists. The advisor
/// service filters and ranks these against the detected hardware.
/// </summary>
public static class BiosCatalog
{
    private static readonly CpuFamily[] AllAmd =
    {
        CpuFamily.Ryzen3000, CpuFamily.Ryzen5000, CpuFamily.Ryzen5000X3D,
        CpuFamily.Ryzen7000, CpuFamily.Ryzen7000X3D, CpuFamily.Ryzen9000, CpuFamily.Ryzen9000X3D
    };

    private static readonly CpuFamily[] AllIntel =
    {
        CpuFamily.IntelCore12, CpuFamily.IntelCore13, CpuFamily.IntelCore14, CpuFamily.IntelCoreUltra
    };

    public static List<BiosSetting> All()
    {
        var list = new List<BiosSetting>();

        // ============================== RAM (AMD) ==============================
        list.Add(new BiosSetting
        {
            Id = "expo-xmp",
            Name = "EXPO / DOCP / XMP",
            Category = "RAM",
            Description = "Active le profil mémoire validé par le fabricant. Sans ça, ta RAM tourne en JEDEC (DDR4-2133 / DDR5-4800) au lieu de sa fréquence annoncée.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > Ai Overclock Tuner",
                [BiosVendor.Msi] = "OC > Advanced DRAM Configuration > A-XMP",
                [BiosVendor.Gigabyte] = "Tweaker > Extreme Memory Profile (X.M.P)",
                [BiosVendor.Asrock] = "OC Tweaker > DRAM Configuration > Load XMP Setting"
            },
            VendorAliases =
            {
                [BiosVendor.Asus] = "DOCP (AM4) / EXPO (AM5)",
                [BiosVendor.Msi] = "A-XMP / EXPO",
                [BiosVendor.Gigabyte] = "XMP / EXPO",
                [BiosVendor.Asrock] = "XMP / EXPO"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Activer Profile 1 (laisser tout en Auto)",
                [TweakTier.Avance] = "EXPO + ajuster FCLK/UCLK manuellement",
                [TweakTier.Extreme] = "EXPO comme base + tighten timings manuellement"
            },
            ExpectedGain = "+15 à 25% perf jeux, +30% min FPS, +40% bande passante mémoire",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily>(AllAmd),
            ValidationTool = "TestMem5 anta777 Extreme 1h+"
        });

        list.Add(new BiosSetting
        {
            Id = "fclk-ratio",
            Name = "FCLK / UCLK / MCLK ratio 1:1",
            Category = "RAM",
            Description = "Le ratio sacré pour Ryzen. AM4 = 1800 MHz (DDR4-3600). AM5 = 2000-2100 MHz (FCLK découplé). Au-delà, ratio 2:1 forcé = +1000 ns latence.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > FCLK Frequency",
                [BiosVendor.Msi] = "OC > Infinity Fabric Frequency and Dividers (FCLK)",
                [BiosVendor.Gigabyte] = "Tweaker > Advanced CPU Settings > Infinity Fabric Frequency",
                [BiosVendor.Asrock] = "OC Tweaker > Infinity Fabric Frequency"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Auto (= MCLK/2 sur AM4)",
                [TweakTier.Avance] = "AM4: 1800 MHz. AM5: 2000 MHz",
                [TweakTier.Extreme] = "AM4: 1900-2000 MHz. AM5: 2100-2133 MHz"
            },
            ExpectedGain = "AM4 1600 → 1800: +5-8% jeux compétitifs. AM5 1:1 vs 2:1: +15-25% min FPS",
            Risk = RiskLevel.Medium,
            Compatibility = new List<CpuFamily> { CpuFamily.Ryzen3000, CpuFamily.Ryzen5000, CpuFamily.Ryzen5000X3D, CpuFamily.Ryzen7000, CpuFamily.Ryzen7000X3D, CpuFamily.Ryzen9000, CpuFamily.Ryzen9000X3D },
            ValidationTool = "« Stabilité RAM » (intégré) pour un 1er screen, puis OCCT memory / TestMem5 — WHEA 18/19 = FCLK trop haut",
            Notes = "Sweet spot Ryzen 7000: DDR5-6000 CL30 EXPO + FCLK 2000"
        });

        list.Add(new BiosSetting
        {
            Id = "memory-context-restore",
            Name = "Memory Context Restore (MCR)",
            Category = "RAM",
            Description = "Permet de skip l'entraînement mémoire au boot. Boot 30-60s plus rapide (très utile sur AM5). Mais peut casser un OC mémoire serré.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD Overclocking > DDR Options > Memory Context Restore",
                [BiosVendor.Msi] = "OC > Advanced DRAM Configuration > Memory Context Restore",
                [BiosVendor.Gigabyte] = "Tweaker > Advanced Memory Settings > Memory Context Restore",
                [BiosVendor.Asrock] = "OC Tweaker > DRAM Configuration > Memory Context Restore"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled",
                [TweakTier.Avance] = "Enabled si EXPO standard, Disabled si OC manuel serré",
                [TweakTier.Extreme] = "Disabled pendant validation, Enabled une fois stable"
            },
            ExpectedGain = "Boot 30-60s plus rapide",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily> { CpuFamily.Ryzen7000, CpuFamily.Ryzen7000X3D, CpuFamily.Ryzen9000, CpuFamily.Ryzen9000X3D }
        });

        list.Add(new BiosSetting
        {
            Id = "dram-power-down",
            Name = "DRAM Power Down Mode = Disabled",
            Category = "RAM",
            Description = "En charge, le 'Power Down Enable' met les rangs mémoire en veille entre deux accès pour gagner quelques milliwatts, au prix d'un peu de latence. Le désactiver récupère un poil de latence mémoire — utile en jeu compétitif, négligeable en bureautique.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > DRAM Power Down Enable",
                [BiosVendor.Msi] = "OC > Advanced DRAM Configuration > Power Down Enable",
                [BiosVendor.Gigabyte] = "Tweaker > Advanced Memory Settings > Power Down Enable",
                [BiosVendor.Asrock] = "OC Tweaker > DRAM Configuration > Power Down Enable"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Auto",
                [TweakTier.Avance] = "Disabled",
                [TweakTier.Extreme] = "Disabled"
            },
            ExpectedGain = "-1 à -2 ns de latence mémoire",
            Risk = RiskLevel.Low
        });

        list.Add(new BiosSetting
        {
            Id = "nitro-mode",
            Name = "DDR5 Nitro Mode (AM5, AGESA récent)",
            Category = "RAM",
            Description = "Presets d'entraînement mémoire agressifs introduits par AGESA 1.2.0.2+ sur AM5. Aide à stabiliser des fréquences/timings plus serrés (DDR5-6400+ en 1:1). À réserver à un OC mémoire validé — s'il est trop agressif, le PC peut refuser de booter (Clear CMOS pour récupérer).",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > AMD Overclocking > DDR Options > Nitro Mode",
                [BiosVendor.Msi] = "OC > Advanced DRAM Configuration > Nitro Mode",
                [BiosVendor.Gigabyte] = "Tweaker > Advanced Memory Settings > Nitro Mode",
                [BiosVendor.Asrock] = "OC Tweaker > DRAM Configuration > Nitro Mode"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Laisser Auto",
                [TweakTier.Avance] = "Auto / Nitro 1 si tu pushes DDR5-6400+",
                [TweakTier.Extreme] = "Nitro RX/TX tuné + validation TestMem5"
            },
            ExpectedGain = "Stabilité DDR5-6400+ en FCLK 1:1",
            Risk = RiskLevel.Medium,
            Compatibility = new List<CpuFamily> { CpuFamily.Ryzen7000, CpuFamily.Ryzen7000X3D, CpuFamily.Ryzen9000, CpuFamily.Ryzen9000X3D },
            ValidationTool = "« Stabilité RAM » (intégré), puis TestMem5 anta777 Extreme",
            Notes = "Clear CMOS si le PC ne boote plus après un réglage trop serré."
        });

        // ============================== CPU (AMD) ==============================
        list.Add(new BiosSetting
        {
            Id = "pbo-curve-optimizer",
            Name = "Curve Optimizer (PBO)",
            Category = "CPU",
            Description = "Applique un offset à la courbe Voltage/Frequency. Offset négatif = moins de voltage à la même fréquence = moins de chaleur + boost plus haut plus longtemps. AUCUN risque hardware.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD Overclocking > Precision Boost Overdrive > Curve Optimizer",
                [BiosVendor.Msi] = "OC > Advanced CPU Configuration > AMD Overclocking > Curve Optimizer",
                [BiosVendor.Gigabyte] = "Tweaker > Advanced CPU Settings > Precision Boost Overdrive > Curve Optimizer",
                [BiosVendor.Asrock] = "OC Tweaker > AMD Overclocking > Curve Optimizer"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Laisser sur Auto",
                [TweakTier.Avance] = "All-core -15 (Ryzen 5000) ou -20 (Ryzen 7000/9000)",
                [TweakTier.Extreme] = "Per-core tuné via CoreCycler (jusqu'à -30 / -40 selon CPU)"
            },
            ExpectedGain = "-15 à -20°C Cinebench, +3-5% multi, +100-200 MHz boost effectif",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily> { CpuFamily.Ryzen5000, CpuFamily.Ryzen5000X3D, CpuFamily.Ryzen7000, CpuFamily.Ryzen7000X3D, CpuFamily.Ryzen9000, CpuFamily.Ryzen9000X3D },
            ValidationTool = "« Stabilité CPU » (intégré) pour un 1er screen rapide, puis CoreCycler 6→60 min/cœur + Prime95 Small FFT",
            Notes = "ATTENTION: 5800X3D = PBO désactivé / 7800X3D = CO -30 only / 9800X3D = full support"
        });

        list.Add(new BiosSetting
        {
            Id = "pbo-limits",
            Name = "PBO Limits (PPT / TDC / EDC)",
            Category = "CPU",
            Description = "Relève les plafonds de puissance et de courant du CPU pour tenir le boost plus longtemps en charge multi-cœur. À coupler avec un bon refroidissement. Sans danger sur non-X3D.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD Overclocking > PBO > PBO Limits = Manual",
                [BiosVendor.Msi] = "OC > AMD Overclocking > PBO > Limits",
                [BiosVendor.Gigabyte] = "Tweaker > PBO > Advanced Limits",
                [BiosVendor.Asrock] = "OC Tweaker > AMD Overclocking > PBO Limits"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Auto",
                [TweakTier.Avance] = "Motherboard / PPT élevé si refroidissement correct",
                [TweakTier.Extreme] = "Manuel selon limite thermique (ex. 7950X: 200/130/180)"
            },
            ExpectedGain = "+3-8% multi-thread soutenu",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily> { CpuFamily.Ryzen5000, CpuFamily.Ryzen7000, CpuFamily.Ryzen9000 },
            Notes = "Inutile sur X3D (verrouillé). Surveiller les températures."
        });

        list.Add(new BiosSetting
        {
            Id = "vsoc-cap",
            Name = "VSOC ≤ 1.30V (CRITIQUE Ryzen 7000+)",
            Category = "CPU",
            Description = "En avril 2023, des Ryzen 7000X3D ont brûlé physiquement à cause de VSOC trop élevé. AMD a imposé un cap firmware VSOC ≤ 1.30V via AGESA 1.0.0.7. À NE JAMAIS dépasser sur Ryzen 7000/9000 X3D.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > VDDCR SOC Voltage",
                [BiosVendor.Msi] = "OC > CPU SOC Voltage",
                [BiosVendor.Gigabyte] = "Tweaker > SOC Voltage",
                [BiosVendor.Asrock] = "OC Tweaker > Voltage Configuration > VDDCR_SOC"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Auto",
                [TweakTier.Avance] = "≤ 1.25V",
                [TweakTier.Extreme] = "≤ 1.30V IMPÉRATIF"
            },
            ExpectedGain = "Stabilité OC RAM + FCLK + sécurité hardware",
            Risk = RiskLevel.HardwareDamage,
            Compatibility = new List<CpuFamily> { CpuFamily.Ryzen7000, CpuFamily.Ryzen7000X3D, CpuFamily.Ryzen9000, CpuFamily.Ryzen9000X3D },
            Notes = "Historique 2023: incident burn confirmé sur 7950X3D + Asus X670E"
        });

        list.Add(new BiosSetting
        {
            Id = "power-supply-idle-control",
            Name = "Power Supply Idle Control = Typical",
            Category = "CPU",
            Description = "Sur Ryzen, le mode 'Low Current Idle' peut provoquer des reboots/freezes au repos (PC qui s'éteint la nuit, déconnexions USB). Le passer en 'Typical Current Idle' corrige ça.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD CBS > Power Supply Idle Control",
                [BiosVendor.Msi] = "OC > Advanced CPU Configuration > Power Supply Idle Control",
                [BiosVendor.Gigabyte] = "Settings > AMD CBS > Power Supply Idle Control",
                [BiosVendor.Asrock] = "Advanced > AMD CBS > Power Supply Idle Control"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Typical Current Idle (si reboots au repos)",
                [TweakTier.Avance] = "Typical Current Idle",
                [TweakTier.Extreme] = "Typical Current Idle"
            },
            ExpectedGain = "Fin des reboots/freezes au repos",
            Risk = RiskLevel.None,
            Compatibility = new List<CpuFamily>(AllAmd)
        });

        list.Add(new BiosSetting
        {
            Id = "ftpm-enabled",
            Name = "fTPM Enabled (avec BIOS récent)",
            Category = "Security",
            Description = "Sur AM4 entre 2021-2022, fTPM causait des freezes 1-3s toutes les 30min. Fix officiel via AGESA 1.2.0.7+ (déplace données vers RAM). Mise à jour BIOS + fTPM Enabled = OK.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD fTPM Configuration > TPM Device Selection > Firmware TPM",
                [BiosVendor.Msi] = "Security > Trusted Computing > AMD fTPM",
                [BiosVendor.Gigabyte] = "Settings > Miscellaneous > AMD CPU fTPM",
                [BiosVendor.Asrock] = "Security > AMD fTPM Switch"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled",
                [TweakTier.Avance] = "Enabled (BIOS récent obligatoire)",
                [TweakTier.Extreme] = "Enabled — requis Windows 11 strict + Vanguard"
            },
            ExpectedGain = "Compatibilité anti-cheat moderne",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily>(AllAmd)
        });

        list.Add(new BiosSetting
        {
            Id = "core-performance-boost",
            Name = "Core Performance Boost (CPB)",
            Category = "CPU",
            Description = "L'interrupteur maître du Turbo AMD. Activé, le CPU monte au-dessus de sa fréquence de base (boost). Le désactiver fige le CPU à sa fréquence de base : utile uniquement pour un OC all-core fixe ou pour traquer une instabilité — sinon, à laisser activé.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > Core Performance Boost",
                [BiosVendor.Msi] = "OC > CPU Features > Core Performance Boost",
                [BiosVendor.Gigabyte] = "Tweaker > Advanced CPU Settings > Core Performance Boost",
                [BiosVendor.Asrock] = "OC Tweaker > Core Performance Boost"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled (Auto)",
                [TweakTier.Avance] = "Enabled",
                [TweakTier.Extreme] = "Enabled (sauf si OC all-core fixe)"
            },
            ExpectedGain = "Boost mono-cœur (perte ~10-20% si désactivé)",
            Risk = RiskLevel.None,
            Compatibility = new List<CpuFamily>(AllAmd)
        });

        list.Add(new BiosSetting
        {
            Id = "global-cstate-control",
            Name = "Global C-State Control = Enabled",
            Category = "CPU",
            Description = "Gère les états de veille profonds du CPU. Doit rester activé : requis pour le bon fonctionnement de Curve Optimizer, du boost agressif et d'une conso/température basse au repos. Ne le désactive que pour diagnostiquer un rare souci USB/audio.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD CBS > CPU Common Options > Global C-state Control",
                [BiosVendor.Msi] = "OC > Advanced CPU Configuration > Global C-State Control",
                [BiosVendor.Gigabyte] = "Settings > AMD CBS > Global C-state Control",
                [BiosVendor.Asrock] = "Advanced > AMD CBS > Global C-state Control"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled (Auto)",
                [TweakTier.Avance] = "Enabled",
                [TweakTier.Extreme] = "Enabled (requis pour Curve Optimizer)"
            },
            ExpectedGain = "Repos plus frais + boost/CO corrects",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily>(AllAmd)
        });

        list.Add(new BiosSetting
        {
            Id = "pbo-max-boost-override",
            Name = "PBO Max Boost Clock Override (+MHz)",
            Category = "CPU",
            Description = "Relève le plafond de fréquence boost de PBO, de +0 à +200 MHz (pas de 25). À coupler à un Curve Optimizer négatif, qui crée la marge thermique pour réellement atteindre ces MHz. Gain surtout en charge légère / mono-cœur.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD Overclocking > PBO > Max CPU Boost Clock Override",
                [BiosVendor.Msi] = "OC > AMD Overclocking > Max CPU Boost Clock Override",
                [BiosVendor.Gigabyte] = "Tweaker > PBO > Max CPU Boost Clock Override",
                [BiosVendor.Asrock] = "OC Tweaker > AMD Overclocking > Max CPU Boost Clock Override"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Auto (0)",
                [TweakTier.Avance] = "+100 à +150 MHz avec CO négatif",
                [TweakTier.Extreme] = "+200 MHz, vérifier le boost réel (HWiNFO effective clocks)"
            },
            ExpectedGain = "+50-150 MHz boost effectif (1T)",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily> { CpuFamily.Ryzen5000, CpuFamily.Ryzen7000, CpuFamily.Ryzen7000X3D, CpuFamily.Ryzen9000, CpuFamily.Ryzen9000X3D },
            ValidationTool = "HWiNFO (effective clocks) + CoreCycler",
            Notes = "Sur X3D, l'effet est plafonné par les limites thermiques/voltage du cache."
        });

        list.Add(new BiosSetting
        {
            Id = "cppc-preferred-cores",
            Name = "CPPC + Preferred Cores = Enabled",
            Category = "CPU",
            Description = "Permet à Windows de connaître les 'meilleurs cœurs' (les plus rapides) et d'y placer les tâches lourdes en priorité. Essentiel pour le boost mono-cœur et, sur les X3D à 2 CCD, pour router les jeux vers le bon CCD.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD CBS > CPPC + CPPC Preferred Cores",
                [BiosVendor.Msi] = "OC > Advanced CPU Configuration > CPPC / CPPC Preferred Cores",
                [BiosVendor.Gigabyte] = "Settings > AMD CBS > CPPC",
                [BiosVendor.Asrock] = "Advanced > AMD CBS > CPPC"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled (Auto)",
                [TweakTier.Avance] = "Enabled",
                [TweakTier.Extreme] = "Enabled"
            },
            ExpectedGain = "Meilleur boost 1T + scheduling correct",
            Risk = RiskLevel.None,
            Compatibility = new List<CpuFamily>(AllAmd),
            Notes = "Requiert le pilote chipset AMD installé côté Windows."
        });

        list.Add(new BiosSetting
        {
            Id = "smt-control",
            Name = "SMT (multithreading) = Enabled",
            Category = "CPU",
            Description = "Le SMT (2 threads par cœur, l'équivalent de l'Hyper-Threading) booste fortement le multi-thread. À laisser activé. Quelques joueurs compétitifs le testent désactivé pour réduire la variance de latence sur des CPU à beaucoup de cœurs — gain marginal et très dépendant du jeu.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD CBS > CPU Common Options > SMT Control",
                [BiosVendor.Msi] = "OC > Advanced CPU Configuration > SMT Control",
                [BiosVendor.Gigabyte] = "Settings > AMD CBS > SMT Control",
                [BiosVendor.Asrock] = "Advanced > AMD CBS > SMT Control"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled (Auto)",
                [TweakTier.Avance] = "Enabled",
                [TweakTier.Extreme] = "Enabled (tester Disabled seulement sur CPU 12c+ et jeu CPU-bound)"
            },
            ExpectedGain = "+30-50% multi-thread (activé)",
            Risk = RiskLevel.None,
            Compatibility = new List<CpuFamily>(AllAmd)
        });

        list.Add(new BiosSetting
        {
            Id = "x3d-ccd-prefer-cache",
            Name = "CCD / Prefer Cache (X3D à 2 CCD)",
            Category = "CPU",
            Description = "Sur les Ryzen X3D à deux CCD (7900X3D/7950X3D/9900X3D/9950X3D), un CCD porte le cache 3D (idéal jeux) et l'autre monte plus haut en fréquence (idéal apps). Le BIOS permet de privilégier le CCD cache, ou de désactiver le CCD fréquence pour les jeux purs. Sans effet sur les X3D mono-CCD (7800X3D/9800X3D).",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD CBS > Core Control / CCD Control",
                [BiosVendor.Msi] = "OC > Advanced CPU Configuration > Downcore / CCD Control",
                [BiosVendor.Gigabyte] = "Settings > AMD CBS > CCD Control",
                [BiosVendor.Asrock] = "Advanced > AMD CBS > CCD Control"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Auto + Xbox Game Bar (méthode officielle AMD)",
                [TweakTier.Avance] = "Prefer Cache via pilote chipset, ou CPPC Preferred Cores",
                [TweakTier.Extreme] = "Désactiver le CCD fréquence pour le jeu pur (réactiver pour le multi-thread)"
            },
            ExpectedGain = "Évite que le jeu tourne sur le mauvais CCD (jusqu'à +10-15% FPS)",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily> { CpuFamily.Ryzen7000X3D, CpuFamily.Ryzen9000X3D },
            Notes = "Méthode officielle = pilote chipset AMD + détection de jeu via Xbox Game Bar. Mono-CCD (7800X3D/9800X3D) : non concerné."
        });

        // ============================== RAM / CPU (Intel) ==============================
        list.Add(new BiosSetting
        {
            Id = "xmp-intel",
            Name = "XMP (profil mémoire Intel)",
            Category = "RAM",
            Description = "Active le profil XMP de tes barrettes. Sans XMP, la RAM tourne en JEDEC (DDR4-2133 / DDR5-4800) au lieu de sa vitesse notée. Le gain #1 le plus simple sur plateforme Intel.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > Ai Overclock Tuner > XMP I/II",
                [BiosVendor.Msi] = "OC > Extreme Memory Profile (XMP)",
                [BiosVendor.Gigabyte] = "Tweaker > Extreme Memory Profile (X.M.P)",
                [BiosVendor.Asrock] = "OC Tweaker > Load XMP Setting"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Activer XMP Profile 1",
                [TweakTier.Avance] = "XMP + Gear 1 si DDR5 ≤ 6400 (sinon Gear 2)",
                [TweakTier.Extreme] = "XMP comme base + tighten timings + tune VCCSA/VDDQ"
            },
            ExpectedGain = "+10 à 20% perf jeux, +25-30% min FPS",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily>(AllIntel),
            ValidationTool = "TestMem5 anta777 / Karhu RAM Test"
        });

        list.Add(new BiosSetting
        {
            Id = "intel-default-settings",
            Name = "Intel Default Settings + microcode 0x12B (13/14e gen)",
            Category = "CPU",
            Description = "Suite à l'instabilité des Core i9 13/14e gen (dégradation par sur-tension), Intel impose un profil 'Intel Default Settings' (Performance) et le microcode 0x12B. CRITIQUE pour la stabilité long terme. Mets le BIOS à jour AVANT.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > Load Intel Default Settings (Performance)",
                [BiosVendor.Msi] = "OC > CPU Cooler Tuning > Intel Default / Boxed Cooler",
                [BiosVendor.Gigabyte] = "Tweaker > Intel Default Profile",
                [BiosVendor.Asrock] = "OC Tweaker > Intel Default Settings"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Intel Default (Performance) + BIOS à jour",
                [TweakTier.Avance] = "Intel Default puis undervolt léger (CEP on)",
                [TweakTier.Extreme] = "Default comme socle stable, puis tuning loadline prudent"
            },
            ExpectedGain = "Stabilité (évite la dégradation irréversible du CPU)",
            Risk = RiskLevel.Medium,
            Compatibility = new List<CpuFamily> { CpuFamily.IntelCore13, CpuFamily.IntelCore14 },
            Notes = "Concernait surtout i7/i9 K. BIOS avec microcode 0x12B requis (mi-2024+)."
        });

        list.Add(new BiosSetting
        {
            Id = "power-limits-intel",
            Name = "Power Limits PL1 / PL2 + ICCMax",
            Category = "CPU",
            Description = "Définit l'enveloppe de puissance du CPU. Sur carte Z, PL1=PL2 'illimité' maximise les perfs (si refroidissement OK) ; sur petit refroidisseur, fixer aux specs Intel évite le throttle thermique brutal.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > Long/Short Duration Package Power Limit",
                [BiosVendor.Msi] = "OC > CPU Specifications > Long/Short Duration Power Limit",
                [BiosVendor.Gigabyte] = "Tweaker > Advanced CPU > Power Limits",
                [BiosVendor.Asrock] = "OC Tweaker > Long/Short Duration Power Limit"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Laisser le profil Intel Default",
                [TweakTier.Avance] = "PL selon refroidissement (ex. i7 = 253W si AIO 280)",
                [TweakTier.Extreme] = "PL élevés + ICCMax adapté, surveiller temps"
            },
            ExpectedGain = "+5-15% multi soutenu (si thermiques le permettent)",
            Risk = RiskLevel.Medium,
            Compatibility = new List<CpuFamily>(AllIntel)
        });

        list.Add(new BiosSetting
        {
            Id = "intel-undervolt-loadline",
            Name = "Undervolt (Loadline / SVID / CEP)",
            Category = "CPU",
            Description = "Réduit la tension CPU pour baisser températures et conso sans perdre de fréquence. Sur Intel récent, garder IA CEP activé et undervolter via Loadline Calibration + AC/DC Loadline plutôt que offset négatif brut.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > Digi+ VRM > Loadline Calibration + AC/DC LL",
                [BiosVendor.Msi] = "OC > CPU Loadline Calibration Control + CPU Lite Load",
                [BiosVendor.Gigabyte] = "Tweaker > CPU Vcore Loadline Calibration",
                [BiosVendor.Asrock] = "OC Tweaker > Loadline Calibration"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Laisser Auto",
                [TweakTier.Avance] = "MSI: CPU Lite Load Mode 7-9. Asus: LLC 4-5 + AC LL bas",
                [TweakTier.Extreme] = "Tuning fin testé sous OCCT/y-cruncher"
            },
            ExpectedGain = "-5 à -15°C, conso réduite, boost plus stable",
            Risk = RiskLevel.Medium,
            Compatibility = new List<CpuFamily>(AllIntel),
            ValidationTool = "« Stabilité CPU » (intégré) en 1er screen, puis OCCT CPU + y-cruncher (erreurs = undervolt trop agressif)"
        });

        list.Add(new BiosSetting
        {
            Id = "ptt-enabled",
            Name = "Intel PTT (TPM firmware) Enabled",
            Category = "Security",
            Description = "L'équivalent Intel du fTPM. Fournit le TPM 2.0 requis par Windows 11 strict, Vanguard, FACEIT et l'EA AntiCheat moderne — sans module matériel.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > PCH-FW Configuration > PTT",
                [BiosVendor.Msi] = "Security > Trusted Computing > TPM Device = PTT",
                [BiosVendor.Gigabyte] = "Settings > Miscellaneous > Intel Platform Trust Technology",
                [BiosVendor.Asrock] = "Security > Intel Platform Trust Technology"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled",
                [TweakTier.Avance] = "Enabled",
                [TweakTier.Extreme] = "Enabled — requis Windows 11 strict + anti-cheats modernes"
            },
            ExpectedGain = "Compatibilité anti-cheat / Windows 11",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily>(AllIntel)
        });

        list.Add(new BiosSetting
        {
            Id = "gear-mode-intel",
            Name = "Gear Mode (Intel DDR5/DDR4)",
            Category = "RAM",
            Description = "Le 'Gear' définit le ratio entre le contrôleur mémoire (IMC) et la RAM. Gear 1 = latence la plus basse (idéal jeux) mais limité en fréquence ; Gear 2 permet des fréquences plus hautes au prix d'un peu de latence. Sur DDR5, Gear 2 est souvent obligatoire au-delà de ~6400.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > DRAM Frequency / Gear Mode",
                [BiosVendor.Msi] = "OC > DRAM Gear Mode",
                [BiosVendor.Gigabyte] = "Tweaker > Advanced Memory Settings > Gear Mode",
                [BiosVendor.Asrock] = "OC Tweaker > Gear Mode"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Auto",
                [TweakTier.Avance] = "Gear 1 si DDR5 ≤ 6400, sinon Gear 2",
                [TweakTier.Extreme] = "Gear 1 le plus haut possible (DDR4 ~3866/4000, DDR5 ~6400), sinon Gear 2 plus haut"
            },
            ExpectedGain = "Gear 1 vs 2 : -5 à -8 ns de latence",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily>(AllIntel)
        });

        list.Add(new BiosSetting
        {
            Id = "ecore-htt-intel",
            Name = "E-Cores / Hyper-Threading (Intel hybride)",
            Category = "CPU",
            Description = "Les Core 12-14e gen mélangent P-cores (perf) et E-cores (efficience). Garder les deux activés est le meilleur choix global. Désactiver l'Hyper-Threading peut baisser les températures (et la dégradation sur i9 13/14e gen) ; désactiver les E-cores n'aide quasiment aucun jeu moderne — le Thread Director gère bien le placement.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > Active Efficient Cores + Hyper-Threading",
                [BiosVendor.Msi] = "OC > CPU Features > Active E-Cores / Hyper-Threading",
                [BiosVendor.Gigabyte] = "Tweaker > Advanced CPU Settings > CPU Cores / Hyper-Threading",
                [BiosVendor.Asrock] = "Advanced > CPU Configuration > E-Core / Hyper Threading"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Tout activé (Auto)",
                [TweakTier.Avance] = "Tout activé ; HT off envisageable sur i9 K pour les températures",
                [TweakTier.Extreme] = "E-cores on ; tester HT off sous charge selon le jeu"
            },
            ExpectedGain = "Stabilité thermique (HT off) ou multi-thread (tout on)",
            Risk = RiskLevel.Low,
            Compatibility = new List<CpuFamily> { CpuFamily.IntelCore12, CpuFamily.IntelCore13, CpuFamily.IntelCore14 }
        });

        list.Add(new BiosSetting
        {
            Id = "intel-apo",
            Name = "Intel APO (Application Performance Optimizer)",
            Category = "CPU",
            Description = "Optimisation de scheduling par jeu introduite sur Core 14e gen (et quelques 13e). Peut offrir de gros gains FPS sur les titres supportés. Nécessite l'option activée dans le BIOS + l'application Intel APO côté Windows.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > Intel Application Optimization (APO)",
                [BiosVendor.Msi] = "OC > Intel APO",
                [BiosVendor.Gigabyte] = "Settings > Intel APO",
                [BiosVendor.Asrock] = "Advanced > Intel APO"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled si proposé",
                [TweakTier.Avance] = "Enabled + application Intel APO installée",
                [TweakTier.Extreme] = "Enabled (vérifier le support jeu par jeu)"
            },
            ExpectedGain = "+0 à +30% FPS sur les jeux supportés",
            Risk = RiskLevel.None,
            Compatibility = new List<CpuFamily> { CpuFamily.IntelCore14 },
            Notes = "Liste de jeux supportés limitée ; surtout 14e gen."
        });

        // ============================== Universal Platform / Boot ==============================
        list.Add(new BiosSetting
        {
            Id = "rebar-above4g",
            Name = "Resizable BAR + Above 4G Decoding",
            Category = "Platform",
            Description = "Permet au CPU d'accéder à toute la VRAM en une fois. Above 4G Decoding doit être activé AVANT que ReBAR apparaisse. CSM doit aussi être Disabled.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > PCI Subsystem Settings > Re-Size BAR Support + Above 4G Decoding",
                [BiosVendor.Msi] = "Settings > Advanced > PCI Subsystem Settings > Re-Size BAR Support",
                [BiosVendor.Gigabyte] = "Settings > IO Ports > Re-Size BAR Support",
                [BiosVendor.Asrock] = "Advanced > PCI Configuration > Above 4G Decoding + Re-Size BAR"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled",
                [TweakTier.Avance] = "Enabled",
                [TweakTier.Extreme] = "Enabled"
            },
            ExpectedGain = "0% à 15% perf gaming selon jeu",
            Risk = RiskLevel.None
        });

        list.Add(new BiosSetting
        {
            Id = "csm-disabled",
            Name = "CSM Disabled (UEFI pur)",
            Category = "Boot",
            Description = "Désactive le Compatibility Support Module (legacy BIOS). Requis pour Resizable BAR, Secure Boot et Windows 11. Aucune raison de l'avoir activé sur un PC moderne.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Boot > CSM (Compatibility Support Module) > Launch CSM",
                [BiosVendor.Msi] = "Settings > Boot > CSM",
                [BiosVendor.Gigabyte] = "Boot > CSM Support",
                [BiosVendor.Asrock] = "Boot > CSM"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Disabled",
                [TweakTier.Avance] = "Disabled",
                [TweakTier.Extreme] = "Disabled"
            },
            ExpectedGain = "Permet d'activer ReBAR + Secure Boot",
            Risk = RiskLevel.None,
            Notes = "Si Windows est installé en MBR/Legacy, convertir en GPT (mbr2gpt) AVANT de désactiver CSM."
        });

        list.Add(new BiosSetting
        {
            Id = "secure-boot",
            Name = "Secure Boot Enabled",
            Category = "Security",
            Description = "Vérifie les signatures bootloaders au démarrage. Requis par Windows 11 strict, Riot Vanguard, FACEIT AC, EA AntiCheat moderne, ESEA.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Boot > Secure Boot > OS Type > Windows UEFI Mode",
                [BiosVendor.Msi] = "Settings > Boot > Secure Boot",
                [BiosVendor.Gigabyte] = "BIOS > Secure Boot",
                [BiosVendor.Asrock] = "Security > Secure Boot"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled",
                [TweakTier.Avance] = "Enabled",
                [TweakTier.Extreme] = "Enabled (sauf dual-boot Linux non-signé)"
            },
            ExpectedGain = "Compatibilité anti-cheat strict",
            Risk = RiskLevel.None
        });

        list.Add(new BiosSetting
        {
            Id = "virtualization",
            Name = "Virtualisation (SVM / VT-x + VT-d/IOMMU)",
            Category = "Platform",
            Description = "Active la virtualisation matérielle. Requise pour WSL2, Docker, machines virtuelles, l'émulation Android et le sandboxing. À noter : certains joueurs la désactivent pour gagner quelques µs de latence (VBS off), au choix.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > CPU Configuration > SVM Mode (AMD) / Intel VT (Intel)",
                [BiosVendor.Msi] = "OC > CPU Features > SVM Mode / Intel Virtualization Tech",
                [BiosVendor.Gigabyte] = "Tweaker/Settings > SVM Mode / VT-d",
                [BiosVendor.Asrock] = "Advanced > CPU Configuration > SVM / Intel Virtualization"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Enabled (compatibilité maximale)",
                [TweakTier.Avance] = "Enabled si tu utilises VM/WSL, sinon au choix",
                [TweakTier.Extreme] = "Au choix : Disabled possible pour latence (si pas de VM/VBS)"
            },
            ExpectedGain = "Compatibilité VM/WSL/Docker",
            Risk = RiskLevel.None
        });

        list.Add(new BiosSetting
        {
            Id = "fast-boot",
            Name = "Fast Boot",
            Category = "Boot",
            Description = "Saute une partie des initialisations matérielles au démarrage pour booter plus vite. Inconvénient : il faut parfois maintenir Suppr/F2 plus tôt pour rentrer dans le BIOS.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Boot > Fast Boot",
                [BiosVendor.Msi] = "Settings > Boot > Fast Boot",
                [BiosVendor.Gigabyte] = "Boot > Fast Boot",
                [BiosVendor.Asrock] = "Boot > Fast Boot"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Au choix (Enabled = boot plus rapide)",
                [TweakTier.Avance] = "Enabled",
                [TweakTier.Extreme] = "Enabled (utiliser 'reboot to UEFI' depuis Windows pour rentrer)"
            },
            ExpectedGain = "Démarrage quelques secondes plus rapide",
            Risk = RiskLevel.Low
        });

        list.Add(new BiosSetting
        {
            Id = "bios-update",
            Name = "Mettre le BIOS à jour (AGESA / microcode)",
            Category = "Platform",
            Description = "Un BIOS à jour apporte les correctifs AGESA (AMD) / microcode (Intel) : stabilité mémoire, support des nouveaux CPU, fix fTPM, et surtout les correctifs de sécurité/stabilité critiques. Utilise BIOS FlashBack / EZ Flash, jamais pendant un orage.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "EZ Flash 3 (dans le BIOS) ou USB BIOS FlashBack",
                [BiosVendor.Msi] = "M-Flash (dans le BIOS) ou Flash BIOS Button",
                [BiosVendor.Gigabyte] = "Q-Flash (dans le BIOS) ou Q-Flash Plus",
                [BiosVendor.Asrock] = "Instant Flash (dans le BIOS) ou BIOS Flashback"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Mettre à jour vers la dernière version stable",
                [TweakTier.Avance] = "Dernière version stable (lire le changelog AGESA/microcode)",
                [TweakTier.Extreme] = "Dernière version (X3D/13-14e gen: indispensable)"
            },
            ExpectedGain = "Stabilité, sécurité, support CPU/RAM",
            Risk = RiskLevel.Medium,
            Notes = "Sauvegarde tes réglages OC avant : un flash réinitialise le BIOS."
        });

        list.Add(new BiosSetting
        {
            Id = "fan-curve",
            Name = "Courbe de ventilation (Q-Fan / Smart Fan)",
            Category = "Fan",
            Description = "Une courbe de ventilation adaptée garde le CPU au frais en charge (donc boost plus haut/longtemps) tout en restant silencieux au repos. Base la courbe sur la température du CPU, pas de la carte mère.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Monitor / Q-Fan Control (ou touche F6)",
                [BiosVendor.Msi] = "Hardware Monitor",
                [BiosVendor.Gigabyte] = "Smart Fan 6",
                [BiosVendor.Asrock] = "H/W Monitor > FAN-Tastic Tuning"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Profil 'Standard' / Silent",
                [TweakTier.Avance] = "Courbe perso : 30% jusqu'à 50°C → 100% à 80°C",
                [TweakTier.Extreme] = "Courbe agressive sur capteur CPU (Tctl/Tdie)"
            },
            ExpectedGain = "Boost soutenu + silence au repos",
            Risk = RiskLevel.None
        });

        // ============================== Storage ==============================
        list.Add(new BiosSetting
        {
            Id = "vmd-controller",
            Name = "Intel VMD / RST (NVMe) — AHCI propre",
            Category = "Storage",
            Description = "Intel VMD (Volume Management Device) regroupe les NVMe pour le RAID/RST. Pour un disque NVMe unique, le laisser sur Auto/Disabled donne une installation Windows plus simple, sans pilote spécifique. ATTENTION : changer ce réglage APRÈS l'installation de Windows empêche le boot (INACCESSIBLE_BOOT_DEVICE) tant que le pilote n'est pas adapté.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > VMD setup menu > Enable VMD controller",
                [BiosVendor.Msi] = "Settings > Advanced > VMD / Intel RST",
                [BiosVendor.Gigabyte] = "Settings > IO Ports > VMD setup menu",
                [BiosVendor.Asrock] = "Advanced > Intel(R) VMD Technology"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Ne pas y toucher après l'installation de Windows",
                [TweakTier.Avance] = "Disabled pour un NVMe simple (à régler AVANT l'install)",
                [TweakTier.Extreme] = "Enabled seulement pour du RAID NVMe / Optane"
            },
            ExpectedGain = "Installation propre + parfois meilleures latences NVMe",
            Risk = RiskLevel.Medium,
            Compatibility = new List<CpuFamily>(AllIntel),
            Notes = "Ne JAMAIS changer après coup sans préparer le pilote — risque de no-boot."
        });

        // ============================== Platform / Power ==============================
        list.Add(new BiosSetting
        {
            Id = "pcie-link-speed",
            Name = "PCIe Link Speed (Gen GPU / NVMe)",
            Category = "Platform",
            Description = "Force la génération PCIe du slot GPU et des M.2. En 'Auto', la carte négocie le maximum (Gen4/Gen5). Utile à fixer en cas d'instabilité (riser, câble, slot partagé) : redescendre d'une génération corrige souvent les écrans noirs / NVMe qui disparaît, sans perte réelle de FPS.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > PCIe Configuration > PCIEX16 Link Speed",
                [BiosVendor.Msi] = "Settings > Advanced > PCI Subsystem Settings > PCIe Gen",
                [BiosVendor.Gigabyte] = "Settings > IO Ports > PCIe Slot Configuration",
                [BiosVendor.Asrock] = "Advanced > Chipset Configuration > PCIe Link Speed"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Auto",
                [TweakTier.Avance] = "Auto (Gen4/Gen5)",
                [TweakTier.Extreme] = "Forcer Gen4 si instabilité en Gen5 (riser/câble)"
            },
            ExpectedGain = "Stabilité PCIe (0% de FPS perdu en baissant d'une gen)",
            Risk = RiskLevel.Low
        });

        list.Add(new BiosSetting
        {
            Id = "erp-ready",
            Name = "ErP Ready / Deep Sleep",
            Category = "Platform",
            Description = "ErP (EuP) coupe quasiment toute l'alimentation à l'arrêt du PC : conso en veille proche de 0 W, mais plus de RGB allumé, plus de charge USB ni de réveil clavier / Wake-on-LAN PC éteint. Choix conso vs confort.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > APM Configuration > ErP Ready",
                [BiosVendor.Msi] = "Settings > Advanced > Power Management Setup > ErP Ready",
                [BiosVendor.Gigabyte] = "Settings > Platform Power > ErP",
                [BiosVendor.Asrock] = "Advanced > ACPI Configuration > Deep Sleep"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Disabled (confort : USB/RGB/Wake actifs)",
                [TweakTier.Avance] = "Au choix",
                [TweakTier.Extreme] = "Enabled si tu veux ~0 W en veille"
            },
            ExpectedGain = "Conso en veille quasi nulle (si Enabled)",
            Risk = RiskLevel.None
        });

        list.Add(new BiosSetting
        {
            Id = "spread-spectrum",
            Name = "Spread Spectrum = Disabled",
            Category = "Platform",
            Description = "Le Spread Spectrum module légèrement les horloges pour passer les normes d'émissions électromagnétiques (EMI). Le désactiver donne des horloges plus stables et précises, utile pour un OC (BCLK) propre. Impact réel minime sur un PC standard.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Ai Tweaker > CPU Spread Spectrum",
                [BiosVendor.Msi] = "OC > Spread Spectrum",
                [BiosVendor.Gigabyte] = "Tweaker > Advanced CPU Settings > Spread Spectrum",
                [BiosVendor.Asrock] = "OC Tweaker > Spread Spectrum"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Auto",
                [TweakTier.Avance] = "Disabled",
                [TweakTier.Extreme] = "Disabled (OC BCLK stable)"
            },
            ExpectedGain = "Horloges plus stables pour l'OC",
            Risk = RiskLevel.Low
        });

        list.Add(new BiosSetting
        {
            Id = "thermal-throttle-limit",
            Name = "Limite de température CPU (Throttle / TjMax)",
            Category = "Fan",
            Description = "Plafonne la température à laquelle le CPU réduit ses fréquences. La baisser (ex. 85°C au lieu de 95°C) sacrifie un peu de boost en charge lourde mais réduit le bruit et préserve la longévité — recommandé pour les Ryzen X3D et les i9 qui chauffent.",
            VendorPaths =
            {
                [BiosVendor.Asus] = "Advanced > AMD Overclocking > PBO > Thermal Throttle Limit  (Intel : Ai Tweaker > CPU Core Temperature Limit)",
                [BiosVendor.Msi] = "OC > AMD Overclocking > Platform Thermal Throttle Limit",
                [BiosVendor.Gigabyte] = "Tweaker > PBO > Thermal Throttle Limit",
                [BiosVendor.Asrock] = "OC Tweaker > AMD Overclocking > Thermal Throttle Limit"
            },
            Recommendations =
            {
                [TweakTier.Tranquille] = "Auto (95°C par défaut)",
                [TweakTier.Avance] = "85-90°C (silence + longévité)",
                [TweakTier.Extreme] = "80-85°C sur X3D, sinon selon le refroidissement"
            },
            ExpectedGain = "Moins de bruit/chaleur, longévité (boost légèrement réduit)",
            Risk = RiskLevel.Low,
            Notes = "AMD : via PBO. Intel : 'CPU Core Temperature Limit' / Tj Max offset."
        });

        return list;
    }
}
