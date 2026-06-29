using System.Collections.ObjectModel;
using AurumTweaks.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AurumTweaks.ViewModels;

public partial class TipsViewModel : ObservableObject
{
    public ObservableCollection<OsRecommendation> OperatingSystems { get; } = new();
    public ObservableCollection<HardwareModTip> HardwareMods { get; } = new();

    [ObservableProperty] private string _verdict =
        "Pour 90% des users : Windows 11 Pro officiel + tweaks Extrême d'Aurum suffit. "
        + "Pour les users avancés : Windows 11 IoT Enterprise LTSC 2024 (officiel Microsoft, sans Copilot/Recall, support 10 ans).";

    public TipsViewModel()
    {
        OperatingSystems.Add(new OsRecommendation
        {
            Name = "Windows 11 Pro + Aurum Extrême",
            Tagline = "Notre recommandation pour 90% des users",
            Description = "Windows officiel + nos tweaks Extrême. Réversible, sûr, anti-cheat compatible.",
            TargetAudience = "Tout le monde, gamers compétitifs, créateurs",
            Performance = "Très bonne (≈80% des gains d'Atlas, en restant officiel)",
            SecurityLevel = "Pleine (Defender + Updates + recovery)",
            IsOfficial = true,
            IsLegalToRedistribute = true,
            Pros = { "Réversible 1 clic", "Anti-cheat OK (Vanguard, EAC, BattlEye, FACEIT)", "Support Microsoft", "Compatibilité 100%", "Updates sécurité" },
            Cons = { "Bloatware par défaut (mais on le retire)", "Copilot / Recall présents (mais désactivables)" },
            OfficialUrl = "https://www.microsoft.com/software-download/windows11",
            RecommendationVerdict = "Recommandation par défaut."
        });

        OperatingSystems.Add(new OsRecommendation
        {
            Name = "Windows 11 IoT Enterprise LTSC 2024",
            Tagline = "Le meilleur Windows officiel pour les power users",
            Description = "Édition Long-Term Servicing Channel officielle. Pas de Copilot/Recall/AI by design. Support sécurité 10 ans.",
            TargetAudience = "Power users, gamers sérieux, anti-AI/Recall",
            Performance = "Excellente (idle RAM -25%, processes -50%, boot rapide)",
            SecurityLevel = "Officielle Microsoft, anti-cheat parfait",
            IsOfficial = true,
            IsLegalToRedistribute = false,
            Pros = { "Officiel Microsoft", "PAS de Copilot/Recall/AI", "Support 10 ans", "TPM/Secure Boot optionnels", "Background processes 60-80 vs 120-150", "Anti-cheat compatibility parfaite" },
            Cons = { "Distribution: Visual Studio sub ou trial 90j", "Pas de feature updates pendant 10 ans", "Pas de Microsoft Store par défaut" },
            OfficialUrl = "https://learn.microsoft.com/windows/iot/iot-enterprise/",
            RecommendationVerdict = "LE choix pour les power users qui veulent rester officiels."
        });

        OperatingSystems.Add(new OsRecommendation
        {
            Name = "Atlas OS v0.5.0",
            Tagline = "Hardcore performance, hardcore risque",
            Description = "Playbook AME Wizard qui modifie ton install Windows officielle. Désactive VBS/HVCI, retire Defender (optionnel), Copilot, Recall, Widgets.",
            TargetAudience = "eSport competitive hardcore, privacy radical",
            Performance = "Marginale vs Windows tweaké (RAM idle -5%, FPS gain <5% en moyenne)",
            SecurityLevel = "Réduite (togglable)",
            AntiCheat = new AntiCheatMatrix
            {
                Vanguard = AntiCheatStatus.Banned,
                EasyAntiCheat = AntiCheatStatus.Risky,
                BattlEye = AntiCheatStatus.Risky,
                Faceit = AntiCheatStatus.Risky
            },
            IsOfficial = false,
            IsLegalToRedistribute = true,
            Pros = { "Open source", "Playbook AME Wizard (légal)", "Communauté active", "Atlas Toolbox" },
            Cons = { "Vanguard incompatible par défaut (HVCI off)", "Non réversible sans reinstall", "Gains marketing inflated vs réalité", "Defender off par défaut", "Author original a quitté en 2024" },
            OfficialUrl = "https://atlasos.net/",
            RecommendationVerdict = "Justifié uniquement si tu acceptes les risques anti-cheat et tu sais ce que tu fais."
        });

        OperatingSystems.Add(new OsRecommendation
        {
            Name = "Tiny11 (NTDEV)",
            Tagline = "Pour vieux PC ou VMs légères",
            Description = "Script PowerShell open source qui retire 57 packages AppX d'un ISO Windows 11 officiel. Tu cookies ton propre ISO.",
            TargetAudience = "Vieux hardware, netbooks, VMs de test, sub-4GB RAM",
            Performance = "Bon gain sur hardware limité, négligeable sur moderne",
            SecurityLevel = "Defender PRESERVE (standard), absent (Core)",
            IsOfficial = false,
            IsLegalToRedistribute = false,
            Pros = { "Script légal et open source", "TPM bypass intégré (vieux hardware)", "Defender préservé en mode standard" },
            Cons = { "Variant Core = pas d'updates ni Defender (jamais en daily)", "Feature upgrades parfois cassés", "ISO redistribués = zone grise légale" },
            OfficialUrl = "https://github.com/ntdevlabs/tiny11builder",
            RecommendationVerdict = "OK pour vieux hardware en mode standard. JAMAIS Core en daily."
        });

        OperatingSystems.Add(new OsRecommendation
        {
            Name = "ReviOS",
            Tagline = "Atlas pour les gens raisonnables",
            Description = "Playbook AME Wizard, balance perf/compat. Plus prudent qu'Atlas, Revision Tool conviviale.",
            TargetAudience = "Daily driver gaming + work, anti-AI",
            Performance = "Similaire à Atlas mais plus stable au quotidien",
            SecurityLevel = "Réduite (Defender + VBS off)",
            AntiCheat = new AntiCheatMatrix
            {
                Vanguard = AntiCheatStatus.Banned,
                EasyAntiCheat = AntiCheatStatus.Risky,
                BattlEye = AntiCheatStatus.Risky,
                Faceit = AntiCheatStatus.Risky
            },
            IsOfficial = false,
            IsLegalToRedistribute = true,
            Pros = { "Plus stable qu'Atlas", "Revision Tool GUI conviviale", "Playbook AME légal" },
            Cons = { "Xbox achievements cassés", "HVCI off → Vanguard incompatible", "Non réversible sans reinstall" },
            OfficialUrl = "https://revi.cc/",
            RecommendationVerdict = "Bon compromis si tu veux Atlas avec moins de risques."
        });

        OperatingSystems.Add(new OsRecommendation
        {
            Name = "Ghost Spectre",
            Tagline = "À NE PAS UTILISER",
            Description = "ISO Windows modifié distribué par un dev anonyme. Cumule tous les pires aspects : illégal, non auditable, sécurité cassée.",
            TargetAudience = "Personne (selon notre recommandation)",
            Performance = "Claims inflated, jamais reproduits indépendamment",
            SecurityLevel = "Inconnue / suspecte",
            IsOfficial = false,
            IsLegalToRedistribute = false,
            Pros = { },
            Cons = { "Redistribution ISO Windows = illégale", "Dev anonyme", "Pas d'audit indépendant possible", "Activation pirate incluse souvent", "Malware risk persistant", "Microsoft Q&A: 'Never safe to install Windows 11 in an unauthorized manner'" },
            OfficialUrl = string.Empty,
            RecommendationVerdict = "DÉCONSEILLÉ FERMEMENT. Préfère Tiny11 ou Atlas si tu veux ce style."
        });

        HardwareMods.Add(new HardwareModTip
        {
            Title = "Passer à DDR5-6000 CL30 sur Ryzen 7000+",
            Category = "RAM",
            Description = "Le sweet spot absolu sur AM5. Plus rapide que DDR5-6400 CL32 et infiniment plus que DDR5-5200 stock.",
            ExpectedGain = "+10 à 20% perf jeux, latence -25%",
            PriceRange = "90-130€ pour 2x16GB Hynix A-die",
            Risk = RiskLevel.None,
            Recommendations = { "G.Skill Flare X5 EXPO", "Kingston Fury Beast EXPO", "Corsair Vengeance EXPO" }
        });

        HardwareMods.Add(new HardwareModTip
        {
            Title = "NVMe Gen 4 1TB+ système",
            Category = "Storage",
            Description = "Réduit chargements jeux + DirectStorage prêt. Gen 4 suffit (Gen 5 = chaleur excessive pour gain marginal).",
            ExpectedGain = "Boot 8s, chargements -30%",
            PriceRange = "70-120€ pour 1TB Gen 4 quality",
            Risk = RiskLevel.None,
            Recommendations = { "Samsung 990 Pro", "WD Black SN850X", "Crucial T500", "Kingston KC3000" }
        });

        HardwareMods.Add(new HardwareModTip
        {
            Title = "AIO 360mm ou Air Cooler haut de gamme",
            Category = "Cooling",
            Description = "Permet à PBO + Curve Optimizer d'exploiter le boost à fond sans throttling thermique.",
            ExpectedGain = "+100-300 MHz boost effectif soutenu",
            PriceRange = "70-180€",
            Risk = RiskLevel.None,
            Recommendations = { "Noctua NH-D15 G2", "Thermalright Peerless Assassin 120", "Arctic Liquid Freezer III 360", "Be Quiet! Pure Loop 2 360" }
        });

        HardwareMods.Add(new HardwareModTip
        {
            Title = "PSU 80+ Gold/Platinum dimensionné",
            Category = "PSU",
            Description = "Évite les drops voltage en pic. Sur RTX 4090/5090 + Ryzen 9 X3D : 1000W minimum recommandé.",
            ExpectedGain = "Stabilité totale, pas de protect shutdown sur pics",
            PriceRange = "120-250€",
            Risk = RiskLevel.None,
            Recommendations = { "Seasonic Vertex GX/PX", "Corsair RM1000x", "be quiet! Straight Power 12" }
        });
    }
}
