using System;
using System.Collections.Generic;
using System.Linq;

namespace AurumTweaks.Models;

/// <summary>
/// System info gathered via WMI / registry / native APIs.
/// Drives BIOS guides, OC presets and tweak compatibility.
/// </summary>
public class HardwareInfo
{
    public string CpuName { get; set; } = "Unknown";
    public string CpuVendor { get; set; } = string.Empty;     // AMD, Intel
    public int CpuCores { get; set; }
    public int CpuThreads { get; set; }                       // active logical processors (NumberOfLogicalProcessors)
    public int CpuMaxThreads { get; set; }                    // silicon max threads (ThreadCount); exceeds CpuThreads when SMT/HT is off
    public string CpuArchitecture { get; set; } = string.Empty;
    public int CpuMaxClockMhz { get; set; }
    public CpuFamily DetectedFamily { get; set; } = CpuFamily.Unknown;

    public string MotherboardManufacturer { get; set; } = "Unknown";
    public string MotherboardModel { get; set; } = "Unknown";
    public BiosVendor DetectedBiosVendor { get; set; } = BiosVendor.Unknown;
    public string BiosVersion { get; set; } = string.Empty;
    public DateTime? BiosReleaseDate { get; set; }
    public string ChipsetName { get; set; } = string.Empty;

    public string GpuPrimary { get; set; } = "Unknown";
    public GpuVendor GpuVendor { get; set; } = GpuVendor.Unknown;
    public string GpuDriverVersion { get; set; } = string.Empty;
    public DateTime? GpuDriverDate { get; set; }

    public long TotalRamBytes { get; set; }
    public string RamSpeedMhz { get; set; } = string.Empty;
    public int RamModuleCount { get; set; }
    public int RamSlotCount { get; set; }                     // total DIMM slots on the board
    public string RamType { get; set; } = string.Empty;       // DDR4, DDR5
    public int RamConfiguredMhz { get; set; }                 // current running speed (MT/s)
    public int RamRatedMhz { get; set; }                      // module's rated/max speed (MT/s)
    public string MemoryChannelSummary { get; set; } = string.Empty;
    public List<MemoryModule> MemoryModules { get; set; } = new();

    public List<StorageDevice> StorageDevices { get; set; } = new();
    public List<DisplayInfo> Displays { get; set; } = new();
    public int MaxRefreshRateHz { get; set; }

    /// <summary>Installed RAM in GiB (computed).</summary>
    public double TotalRamGb => TotalRamBytes / (1024.0 * 1024.0 * 1024.0);

    /// <summary>True when the RAM is running noticeably below its rated speed (EXPO/XMP likely off).</summary>
    public bool RamRunningBelowRated => RamRatedMhz > 0 && RamConfiguredMhz > 0 && RamConfiguredMhz < RamRatedMhz - 100;

    /// <summary>
    /// Installed stick sizes in whole GiB (real sticks only — Win32_PhysicalMemory reports just populated slots),
    /// largest first. Backs both <see cref="RamCapacityMismatched"/> and its human breakdown so the flag and the
    /// displayed sizes can never disagree.
    /// </summary>
    public IReadOnlyList<int> RamModuleCapacitiesGb => MemoryModules
        .Where(m => m.CapacityBytes > 0)
        .Select(m => (int)Math.Round(m.CapacityGb))
        .OrderByDescending(gb => gb)
        .ToList();

    /// <summary>
    /// True when ≥2 sticks are installed but they don't all share the same capacity (e.g. an 8 GB stick added
    /// next to a 16 GB one). Such a kit runs in "flex mode": only the matched portion is dual-channel, the rest
    /// falls back to single-channel, and two different kits frequently won't POST at the rated EXPO/XMP profile.
    /// Capacity is the one per-module field WMI reports reliably, so this never over-claims.
    /// </summary>
    public bool RamCapacityMismatched => RamModuleCapacitiesGb.Distinct().Count() > 1;

    public bool IsLaptop { get; set; }
    public bool SystemDriveIsSsd { get; set; } = true;        // assume SSD unless an HDD is positively detected

    public string OsCaption { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string OsBuild { get; set; } = string.Empty;
    public bool IsWindows11 { get; set; }
    public bool IsLtsc { get; set; }

    public bool VbsRunning { get; set; }
    public bool HvciRunning { get; set; }
    public bool TpmEnabled { get; set; }
    public bool SecureBootEnabled { get; set; }
    public bool ResizableBarEnabled { get; set; }
    public bool VirtualizationEnabled { get; set; }

    // Honest tri-state platform flags: many of these can't always be read reliably
    // from Windows, so we distinguish "detected off" from "couldn't tell".
    public string TpmSpecVersion { get; set; } = string.Empty;
    public TriState TpmStatus { get; set; } = TriState.Unknown;
    public TriState SecureBootStatus { get; set; } = TriState.Unknown;
    public TriState ReBarStatus { get; set; } = TriState.Unknown;
    public TriState VirtualizationStatus { get; set; } = TriState.Unknown;

    public bool VanguardDetected { get; set; }
    public bool FaceItAcDetected { get; set; }
    public bool EacDetected { get; set; }
    public bool BattlEyeDetected { get; set; }

    // ---- Computed helpers used by the BIOS advisor ----

    /// <summary>Age of the installed BIOS in months, or -1 if the release date is unknown.</summary>
    public int BiosAgeMonths => BiosReleaseDate is null
        ? -1
        : (int)((DateTime.Now - BiosReleaseDate.Value).TotalDays / 30.0);

    /// <summary>A BIOS older than ~18 months is worth checking for an AGESA/microcode update.</summary>
    public bool BiosLikelyOutdated => BiosAgeMonths >= 18;

    public bool IsAmd => CpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                         || CpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)
                         || CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);

    // Guard with !IsAmd: AMD names contain "N-Core Processor", and a bare "Core" match
    // would otherwise misclassify every Ryzen as Intel (leaking Intel-only BIOS settings).
    public bool IsIntel => !IsAmd
                           && (CpuVendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                               || CpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                               || CpuName.Contains("Core", StringComparison.OrdinalIgnoreCase));

    public bool IsDdr5 => RamType.Contains("DDR5", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for X3D parts, where Curve Optimizer / PBO behaviour differs.</summary>
    public bool IsX3D => DetectedFamily is CpuFamily.Ryzen5000X3D
        or CpuFamily.Ryzen7000X3D or CpuFamily.Ryzen9000X3D;

    /// <summary>
    /// True only for dual-CCD X3D parts (7900X3D/7950X3D/9900X3D/9950X3D), where one CCD carries the
    /// 3D cache and the other clocks higher. Mono-CCD X3D (7800X3D/9800X3D) aren't concerned. Family
    /// can't tell them apart, so gate on cores: dual-CCD X3D ship with 12–16 cores, mono-CCD with 8.
    /// </summary>
    public bool IsDualCcdX3D => IsX3D && CpuCores >= 12;

    /// <summary>True when the CPU can run more threads than are currently active — i.e. SMT/HT is disabled in the BIOS.</summary>
    public bool SmtCapableButOff => CpuMaxThreads > CpuThreads && CpuThreads > 0 && CpuThreads <= CpuCores;

    /// <summary>The fastest display attached (used for "set Windows to max refresh" advice).</summary>
    public DisplayInfo? PrimaryDisplay => Displays
        .OrderByDescending(d => d.CurrentRefreshHz)
        .ThenByDescending(d => d.Width)
        .FirstOrDefault();
}

/// <summary>One physical RAM stick, as reported by Win32_PhysicalMemory.</summary>
public class MemoryModule
{
    public string Slot { get; set; } = string.Empty;          // DeviceLocator e.g. "DIMM_A2"
    public string BankLabel { get; set; } = string.Empty;     // P0 CHANNEL A ...
    public string Manufacturer { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public long CapacityBytes { get; set; }
    public int ConfiguredMhz { get; set; }
    public int RatedMhz { get; set; }
    public string RamType { get; set; } = string.Empty;
    public double CapacityGb => CapacityBytes / (1024.0 * 1024.0 * 1024.0);
    public string Summary => $"{CapacityGb:0} GB {RamType} @ {ConfiguredMhz} MT/s ({Manufacturer} {PartNumber})".Trim();
}

/// <summary>One physical disk, as reported by MSFT_PhysicalDisk.</summary>
public class StorageDevice
{
    public string Model { get; set; } = string.Empty;
    public string MediaType { get; set; } = "Unknown";        // SSD, HDD, SCM, Unknown
    public string BusType { get; set; } = string.Empty;       // NVMe, SATA, USB, RAID...
    public long SizeBytes { get; set; }
    public double SizeGb => SizeBytes / (1024.0 * 1024.0 * 1024.0);
    public string Summary => $"{Model} — {SizeGb:0} GB {MediaType}/{BusType}".Trim();
}

/// <summary>One attached display, as reported by Win32_VideoController.</summary>
public class DisplayInfo
{
    public string Name { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int CurrentRefreshHz { get; set; }
    public int MaxRefreshHz { get; set; }
    public string Summary => Width > 0 ? $"{Width}×{Height} @ {CurrentRefreshHz} Hz" : Name;
}

/// <summary>Honest three-way flag: we either confirmed on, confirmed off, or couldn't tell.</summary>
public enum TriState
{
    Unknown,
    Yes,
    No
}

public enum CpuFamily
{
    Unknown,
    Ryzen3000,
    Ryzen5000,
    Ryzen5000X3D,
    Ryzen7000,
    Ryzen7000X3D,
    Ryzen9000,
    Ryzen9000X3D,
    IntelCore12,
    IntelCore13,
    IntelCore14,

    // The "Intel Core Ultra" brand as a whole — Meteor Lake (series 1), Arrow Lake & Lunar Lake (series 2).
    // We deliberately do NOT sub-split the dies: the Win32_Processor string only reliably surfaces the
    // "Core Ultra" brand, every downstream rule treats them identically (hybrid P/E-core guidance, generic
    // Intel BIOS cards), and the user-facing label is "Intel Core Ultra". A die-specific enum name would
    // overclaim a precision the detector doesn't actually have.
    IntelCoreUltra
}

public enum BiosVendor
{
    Unknown,
    Asus,
    Msi,
    Gigabyte,
    Asrock,
    Biostar,
    Dell,
    Hp,
    Lenovo
}

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel
}
