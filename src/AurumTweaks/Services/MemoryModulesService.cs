using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Honest three-way read of whether the installed memory is running at its rated speed.
/// We deliberately do NOT claim "XMP/EXPO is active": Windows (Win32_PhysicalMemory) exposes
/// only a "configured" and a "rated" MT/s figure, and the rated value is frequently the JEDEC
/// base — not the kit's XMP/EXPO rating — so the profile state can't be asserted as fact. The
/// most we can say truthfully is "running at / below the rated speed Windows reports".
/// </summary>
public enum MemoryProfileStatus
{
    Unknown,         // configured or rated speed not reported → can't compare
    AtRatedSpeed,    // configured ≈ rated → memory is at the speed Windows calls rated
    BelowRatedSpeed  // configured noticeably < rated → XMP/EXPO profile probably inactive
}

/// <summary>
/// Pure speed verdict. The 100 MT/s margin mirrors <see cref="HardwareInfo.RamRunningBelowRated"/>
/// on purpose, so this page, the dashboard and the BIOS advisor all agree on when memory counts as
/// "below rated" — there is one threshold, not three.
/// </summary>
public static class MemorySpeedAssessment
{
    public const int MarginMhz = 100;

    public static MemoryProfileStatus Classify(int configuredMhz, int ratedMhz)
    {
        if (configuredMhz <= 0 || ratedMhz <= 0) return MemoryProfileStatus.Unknown;
        return configuredMhz < ratedMhz - MarginMhz
            ? MemoryProfileStatus.BelowRatedSpeed
            : MemoryProfileStatus.AtRatedSpeed;
    }
}

/// <summary>
/// Channel-mode hint inferred from the populated-module count. Windows can't read the actual
/// channel mode, so this is explicitly a probability ("probable"), never asserted as fact.
/// </summary>
public enum MemoryChannelHint
{
    Unknown,
    Single,
    DualLikely,
    Asymmetric
}

public static class MemoryChannelInference
{
    public static MemoryChannelHint Infer(int moduleCount)
    {
        if (moduleCount <= 0) return MemoryChannelHint.Unknown;
        if (moduleCount == 1) return MemoryChannelHint.Single;
        return moduleCount % 2 == 0 ? MemoryChannelHint.DualLikely : MemoryChannelHint.Asymmetric;
    }

    public static string Describe(MemoryChannelHint hint) => hint switch
    {
        MemoryChannelHint.Single => "Canal simple",
        MemoryChannelHint.DualLikely => "Double canal probable",
        MemoryChannelHint.Asymmetric => "Configuration asymétrique (nombre impair de barrettes)",
        _ => "Indéterminé"
    };
}

/// <summary>One module as shown on the page: French display strings over a <see cref="MemoryModule"/>.</summary>
public record MemoryModuleRow(MemoryModule Module)
{
    public string Slot => string.IsNullOrWhiteSpace(Module.Slot) ? "Slot ?" : Module.Slot;

    public string Identity
    {
        get
        {
            var id = $"{Module.Manufacturer} {Module.PartNumber}".Trim();
            return string.IsNullOrWhiteSpace(id) ? "Fabricant inconnu" : id;
        }
    }

    public string BankLabel => Module.BankLabel;
    public bool HasBankLabel => !string.IsNullOrWhiteSpace(Module.BankLabel);

    // Never fabricate a "0 Go" for a stick whose capacity Windows didn't report.
    public string Capacity => Module.CapacityBytes > 0 ? ByteSize.Format(Module.CapacityBytes) : "—";
    public string Type => string.IsNullOrWhiteSpace(Module.RamType) ? "—" : Module.RamType;

    public string SpeedDisplay
    {
        get
        {
            if (Module.ConfiguredMhz <= 0) return "—";
            return Module.RatedMhz > 0 && Module.RatedMhz != Module.ConfiguredMhz
                ? $"{Module.ConfiguredMhz} MT/s · nominal {Module.RatedMhz} MT/s"
                : $"{Module.ConfiguredMhz} MT/s";
        }
    }

    public bool BelowRated =>
        MemorySpeedAssessment.Classify(Module.ConfiguredMhz, Module.RatedMhz) == MemoryProfileStatus.BelowRatedSpeed;
}

/// <summary>
/// Aggregated, display-ready view of the physical memory layout. Built purely from a
/// <see cref="HardwareInfo"/> (no I/O) so the totals, free-slot count and the XMP/EXPO verdict
/// can be pinned by tests.
/// </summary>
public record MemoryModulesReport(
    IReadOnlyList<MemoryModuleRow> Modules,
    int SlotCount,
    string RamType,
    int ConfiguredMhz,
    int RatedMhz,
    long TotalBytes)
{
    public int ModuleCount => Modules.Count;
    public bool HasModules => ModuleCount > 0;
    public bool HasSlotInfo => SlotCount > 0;

    // Clamp so a board reporting fewer slots than sticks (some OEM firmwares) never shows a negative count.
    public int FreeSlots => SlotCount > ModuleCount ? SlotCount - ModuleCount : 0;

    public MemoryChannelHint ChannelHint => MemoryChannelInference.Infer(ModuleCount);
    public string ChannelDisplay => MemoryChannelInference.Describe(ChannelHint);

    public MemoryProfileStatus ProfileStatus => HasModules
        ? MemorySpeedAssessment.Classify(ConfiguredMhz, RatedMhz)
        : MemoryProfileStatus.Unknown;

    public bool ProfileWarn => HasModules && ProfileStatus == MemoryProfileStatus.BelowRatedSpeed;
    public bool ProfileOk => HasModules && ProfileStatus == MemoryProfileStatus.AtRatedSpeed;
    public bool ProfileUnknown => !ProfileWarn && !ProfileOk;

    public string TypeDisplay => string.IsNullOrWhiteSpace(RamType) ? "—" : RamType;
    public string TotalDisplay => HasModules && TotalBytes > 0 ? ByteSize.Format(TotalBytes) : "—";
    public string SpeedDisplay => ConfiguredMhz > 0 ? $"{ConfiguredMhz} MT/s" : "—";
    public string RatedDisplay => RatedMhz > 0 ? $"{RatedMhz} MT/s" : "—";

    public string SlotsDisplay => HasSlotInfo
        ? $"{ModuleCount} / {SlotCount} ({FreeSlots} libre{(FreeSlots > 1 ? "s" : string.Empty)})"
        : (HasModules ? ModuleCount.ToString() : "—");

    public string ProfileHeadline => !HasModules
        ? "Aucune barrette détectée"
        : ProfileStatus switch
        {
            MemoryProfileStatus.BelowRatedSpeed => "Mémoire sous sa vitesse nominale — profil XMP/EXPO probablement inactif",
            MemoryProfileStatus.AtRatedSpeed => "Mémoire à sa vitesse nominale rapportée",
            _ => "Vitesse nominale indéterminée"
        };

    public string ProfileDetail => !HasModules
        ? "Windows n'a renvoyé aucun module physique. L'application doit être élevée (administrateur) pour interroger le SPD, ou WMI est indisponible sur cette machine."
        : ProfileStatus switch
        {
            MemoryProfileStatus.BelowRatedSpeed =>
                $"La vitesse configurée ({ConfiguredMhz} MT/s) est inférieure à la vitesse nominale rapportée ({RatedMhz} MT/s). Activez le profil XMP/EXPO dans le BIOS pour viser la vitesse nominale. Indicatif : Windows peut aussi sous-estimer la vitesse nominale réelle du kit.",
            MemoryProfileStatus.AtRatedSpeed =>
                $"La vitesse configurée ({ConfiguredMhz} MT/s) correspond à la vitesse nominale rapportée par Windows. Attention : Windows ne lit pas toujours la vitesse XMP/EXPO maximale d'un kit — si le vôtre est noté plus rapide, confirmez le profil dans le BIOS.",
            _ =>
                "Windows n'expose pas un couple vitesse configurée / nominale fiable pour ces modules. Vérifiez le SPD/XMP dans le BIOS pour connaître la vitesse réelle."
        };

    public static MemoryModulesReport From(HardwareInfo info)
    {
        var rows = info.MemoryModules.Select(m => new MemoryModuleRow(m)).ToList();
        return new MemoryModulesReport(
            rows,
            info.RamSlotCount,
            info.RamType,
            info.RamConfiguredMhz,
            info.RamRatedMhz,
            info.TotalRamBytes);
    }
}

/// <summary>
/// Read-only front-end over <see cref="IHardwareService"/>: turns the already-collected
/// Win32_PhysicalMemory data into a display-ready <see cref="MemoryModulesReport"/>. No new WMI
/// surface is added — this mirrors the ServiceControl-over-ServiceManager front-end pattern.
/// </summary>
public sealed class MemoryModulesService : IMemoryModulesService
{
    private readonly IHardwareService _hardware;

    public MemoryModulesService(IHardwareService hardware) => _hardware = hardware;

    public async Task<MemoryModulesReport> GetReportAsync()
    {
        // DetectAsync re-queries WMI on every call (no caching), so this is a genuine live re-read.
        var info = await _hardware.DetectAsync();
        return MemoryModulesReport.From(info);
    }
}
