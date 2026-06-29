using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Turns the static <see cref="BiosCatalog"/> into a per-PC report: filters settings to the
/// detected CPU/platform, reads back whatever state Windows can tell us, picks the right vendor
/// menu path, and ranks the list so the real "do this now" actions float to the top.
/// </summary>
public sealed class BiosAdvisorService : IBiosAdvisorService
{
    private static readonly CpuFamily[] AmdFamilies =
    {
        CpuFamily.Ryzen3000, CpuFamily.Ryzen5000, CpuFamily.Ryzen5000X3D,
        CpuFamily.Ryzen7000, CpuFamily.Ryzen7000X3D, CpuFamily.Ryzen9000, CpuFamily.Ryzen9000X3D
    };

    private static readonly CpuFamily[] IntelFamilies =
    {
        CpuFamily.IntelCore12, CpuFamily.IntelCore13, CpuFamily.IntelCore14, CpuFamily.IntelCoreUltra
    };

    public BiosAdvisorReport BuildReport(HardwareInfo hw, TweakTier tier)
    {
        var report = new BiosAdvisorReport
        {
            PlatformSummary = BuildPlatformSummary(hw)
        };

        foreach (var setting in BiosCatalog.All())
        {
            if (!IsApplicable(setting, hw))
                continue;

            var (state, detected) = DetectState(setting, hw);

            var rec = new BiosRecommendation
            {
                Setting = setting,
                State = state,
                DetectedStateText = detected,
                VendorPath = PickVendorPath(setting, hw.DetectedBiosVendor),
                VendorAlias = PickVendorAlias(setting, hw.DetectedBiosVendor),
                TierRecommendation = setting.Recommendations.TryGetValue(tier, out var r)
                    ? r
                    : setting.Recommendations.Values.FirstOrDefault() ?? string.Empty,
                Priority = ComputePriority(state, setting.Risk)
            };

            report.Recommendations.Add(rec);
        }

        // Rank: ActionNeeded first, then critical-safety Verify, then the rest; ties by name.
        report.Recommendations = report.Recommendations
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Category)
            .ThenBy(r => r.Name)
            .ToList();

        report.ActionNeededCount = report.Recommendations.Count(r => r.State == BiosCheckState.ActionNeeded);
        report.VerifyCount = report.Recommendations.Count(r => r.State == BiosCheckState.Verify);
        report.OptimalCount = report.Recommendations.Count(r => r.State == BiosCheckState.Optimal);

        return report;
    }

    /// <summary>A setting applies if it's universal (no compat list) or matches the detected CPU/platform.</summary>
    private static bool IsApplicable(BiosSetting s, HardwareInfo hw)
    {
        // Dual-CCD X3D cache routing only exists on parts with two CCDs (7900X3D/7950X3D/9900X3D/
        // 9950X3D). Family alone can't separate those from mono-CCD X3D (7800X3D, 8 cores), and the
        // broad AMD fallback below would otherwise leak this card onto every Ryzen — so gate it on
        // the core count.
        if (s.Id == "x3d-ccd-prefer-cache")
            return hw.IsDualCcdX3D;

        if (s.Compatibility.Count == 0)
            return true;

        if (s.Compatibility.Contains(hw.DetectedFamily))
            return true;

        // Family is sometimes Unknown even when the vendor is clear — fall back to AMD/Intel buckets.
        if (hw.IsAmd && s.Compatibility.Any(f => AmdFamilies.Contains(f)))
            return true;

        if (hw.IsIntel && s.Compatibility.Any(f => IntelFamilies.Contains(f)))
            return true;

        return false;
    }

    /// <summary>Map a setting to whatever we could read back from Windows. Honest about the unknowns.</summary>
    private static (BiosCheckState state, string detected) DetectState(BiosSetting s, HardwareInfo hw)
    {
        switch (s.Id)
        {
            case "expo-xmp":
            case "xmp-intel":
                if (hw.RamRunningBelowRated)
                    return (BiosCheckState.ActionNeeded,
                        $"RAM à {hw.RamConfiguredMhz} MT/s alors qu'elle est notée {hw.RamRatedMhz} MT/s → profil mémoire (EXPO/XMP) probablement DÉSACTIVÉ.");
                if (hw.RamRatedMhz > 0 && hw.RamConfiguredMhz > 0)
                    return (BiosCheckState.Optimal,
                        $"RAM à {hw.RamConfiguredMhz} MT/s (= vitesse notée) → profil déjà actif.");
                return (BiosCheckState.Verify,
                    $"RAM à {hw.RamConfiguredMhz} MT/s — vitesse notée illisible, vérifie que le profil est activé.");

            case "secure-boot":
                return FromTriState(hw.SecureBootStatus,
                    onYes: "Secure Boot est ACTIVÉ.",
                    onNo: "Secure Boot est DÉSACTIVÉ → requis par Vanguard / FACEIT / Windows 11 strict.",
                    onUnknown: "État Secure Boot illisible depuis Windows.");

            case "ftpm-enabled":
            case "ptt-enabled":
                return FromTriState(hw.TpmStatus,
                    onYes: $"TPM détecté actif{(string.IsNullOrEmpty(hw.TpmSpecVersion) ? "" : $" (v{hw.TpmSpecVersion})")}.",
                    onNo: "Aucun TPM actif détecté → requis par les anti-cheats modernes et Windows 11.",
                    onUnknown: "État TPM illisible depuis Windows.");

            case "rebar-above4g":
                return FromTriState(hw.ReBarStatus,
                    onYes: "Resizable BAR est ACTIVÉ.",
                    onNo: "Resizable BAR est DÉSACTIVÉ → gain gaming possible une fois activé.",
                    onUnknown: "État Resizable BAR illisible — à vérifier dans le BIOS / GPU-Z.");

            case "virtualization":
                return hw.VirtualizationStatus switch
                {
                    TriState.Yes => (BiosCheckState.Optimal, "Virtualisation matérielle ACTIVÉE."),
                    TriState.No => (BiosCheckState.Verify, "Virtualisation DÉSACTIVÉE — l'activer si tu utilises VM/WSL/Docker."),
                    _ => (BiosCheckState.Verify, "État virtualisation illisible depuis Windows.")
                };

            case "bios-update":
                if (hw.BiosAgeMonths < 0)
                    return (BiosCheckState.Verify, "Date du BIOS inconnue — compare ta version avec celle du site du fabricant.");
                if (hw.BiosLikelyOutdated)
                    return (BiosCheckState.ActionNeeded,
                        $"BIOS daté de ~{hw.BiosAgeMonths} mois → un AGESA/microcode plus récent apporte stabilité et correctifs.");
                return (BiosCheckState.Optimal, $"BIOS récent (~{hw.BiosAgeMonths} mois).");

            case "vsoc-cap":
                // Can't read VSOC from Windows, but on Ryzen 7000/9000 it's a safety-critical check.
                return (BiosCheckState.Verify,
                    "Impossible de lire VSOC depuis Windows — VÉRIFIE qu'il reste ≤ 1.30V (sécurité hardware).");

            case "smt-control":
                // SMT/HT is readable: a CPU with SMT on reports threads == 2× physical cores.
                if (hw.CpuCores <= 0 || hw.CpuThreads <= 0)
                    return (BiosCheckState.Verify, "Topologie CPU illisible — vérifie SMT dans le BIOS.");
                if (hw.CpuThreads >= hw.CpuCores * 2)
                    return (BiosCheckState.Optimal,
                        $"SMT ACTIVÉ ({hw.CpuCores} cœurs / {hw.CpuThreads} threads).");
                if (hw.SmtCapableButOff)
                    return (BiosCheckState.Verify,
                        $"SMT DÉSACTIVÉ : {hw.CpuThreads} threads actifs alors que ton CPU peut en faire {hw.CpuMaxThreads} ({hw.CpuCores} cœurs) — réactive-le pour le multi-thread, sauf choix délibéré.");
                return (BiosCheckState.Verify,
                    $"SMT semble DÉSACTIVÉ ({hw.CpuCores} cœurs / {hw.CpuThreads} threads) — réactive-le pour le multi-thread, sauf choix délibéré.");

            case "ecore-htt-intel":
                // Hybrid topology: we can show the core/thread count but can't split E-cores vs HT from Windows.
                if (hw.CpuCores > 0 && hw.CpuThreads > 0)
                    return (BiosCheckState.Verify,
                        $"{hw.CpuCores} cœurs / {hw.CpuThreads} threads détectés — vérifie l'état des E-cores et de l'Hyper-Threading dans le BIOS.");
                return (BiosCheckState.Verify, string.Empty);

            case "vmd-controller":
                // VMD/RAID is the safety-critical one: flipping it after install = no-boot.
                var nvme = hw.StorageDevices.Count(d => d.BusType.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
                var raid = hw.StorageDevices.Any(d => d.BusType.Contains("RAID", StringComparison.OrdinalIgnoreCase));
                if (raid)
                    return (BiosCheckState.Verify,
                        "Un disque en mode RAID/VMD est détecté — ne change ce réglage qu'avec le pilote Intel RST prêt (risque de no-boot).");
                if (nvme > 0)
                    return (BiosCheckState.Verify,
                        $"{nvme} NVMe détecté(s) en mode standard — ne touche à VMD qu'AVANT une réinstallation de Windows.");
                return (BiosCheckState.Verify, string.Empty);

            default:
                return (BiosCheckState.Verify, string.Empty);
        }
    }

    private static (BiosCheckState, string) FromTriState(TriState t, string onYes, string onNo, string onUnknown) => t switch
    {
        TriState.Yes => (BiosCheckState.Optimal, onYes),
        TriState.No => (BiosCheckState.ActionNeeded, onNo),
        _ => (BiosCheckState.Verify, onUnknown)
    };

    /// <summary>Higher floats to the top. ActionNeeded dominates; hardware-damage safety checks rank high.</summary>
    private static int ComputePriority(BiosCheckState state, RiskLevel risk)
    {
        int stateWeight = state switch
        {
            BiosCheckState.ActionNeeded => 1000,
            BiosCheckState.Verify => 500,
            BiosCheckState.Optimal => 200,
            _ => 100
        };

        int riskWeight = risk switch
        {
            RiskLevel.HardwareDamage => 300,
            RiskLevel.High => 200,
            RiskLevel.Medium => 100,
            RiskLevel.Low => 50,
            _ => 0
        };

        return stateWeight + riskWeight;
    }

    private static string PickVendorPath(BiosSetting s, BiosVendor v)
    {
        if (s.VendorPaths.TryGetValue(v, out var p) && !string.IsNullOrWhiteSpace(p))
            return p;

        // OEM/unknown boards (Dell/HP/Lenovo/Biostar): give the ASUS path as a labelled reference.
        if (s.VendorPaths.TryGetValue(BiosVendor.Asus, out var asus) && !string.IsNullOrWhiteSpace(asus))
            return $"(réf. ASUS) {asus}";

        return s.VendorPaths.Values.FirstOrDefault() ?? "Voir le manuel de la carte mère";
    }

    private static string PickVendorAlias(BiosSetting s, BiosVendor v)
    {
        if (s.VendorAliases.TryGetValue(v, out var a) && !string.IsNullOrWhiteSpace(a))
            return a;
        return s.VendorAliases.Values.FirstOrDefault() ?? string.Empty;
    }

    private static string BuildPlatformSummary(HardwareInfo hw)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(hw.CpuName) && hw.CpuName != "Unknown")
            parts.Add(hw.CpuName);

        if (!string.IsNullOrWhiteSpace(hw.ChipsetName))
            parts.Add(hw.ChipsetName);
        else if (!string.IsNullOrWhiteSpace(hw.MotherboardModel) && hw.MotherboardModel != "Unknown")
            parts.Add(hw.MotherboardModel);

        if (hw.DetectedBiosVendor != BiosVendor.Unknown)
        {
            var bios = hw.DetectedBiosVendor.ToString();
            if (!string.IsNullOrWhiteSpace(hw.BiosVersion))
                bios += $" {hw.BiosVersion}";
            if (hw.BiosAgeMonths >= 0)
                bios += $" (~{hw.BiosAgeMonths} mois)";
            parts.Add($"BIOS {bios}");
        }

        if (hw.TotalRamGb > 0)
        {
            var ram = $"{hw.TotalRamGb:0} GB {hw.RamType}".Trim();
            if (hw.RamConfiguredMhz > 0)
                ram += $"-{hw.RamConfiguredMhz}";
            parts.Add(ram);
        }

        return string.Join("  ·  ", parts);
    }
}
