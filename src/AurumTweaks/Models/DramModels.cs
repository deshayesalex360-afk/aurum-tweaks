using System;
using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>DDR generation. Timing math differs (tCK, secondary shapes), so it is an explicit input.</summary>
public enum RamGeneration
{
    Ddr4,
    Ddr5
}

/// <summary>
/// Memory IC family. Windows cannot read this reliably (it lives in the SPD / on the dies), so the
/// user picks it — ideally after identifying chips with Thaiphoon Burner. Drives how tight the
/// suggested timings can go: a Samsung B-die tolerates far lower tCL than a Hynix CJR.
/// </summary>
public enum MemoryIc
{
    Unknown,
    // DDR4
    SamsungBDie,
    SamsungCDie,
    MicronRevE,
    MicronRevB,
    HynixCJR,
    HynixDJR,
    NanyaOther,
    // DDR5
    HynixADie,
    HynixMDie,
    SamsungDdr5,
    MicronDdr5
}

/// <summary>Single- vs dual-rank: dual-rank refreshes longer (higher tRFC) and clocks lower.</summary>
public enum MemoryRank
{
    SingleRank,
    DualRank
}

/// <summary>How aggressive the suggested set is. Safe ≈ daily/JEDEC-ish, Extreme ≈ benching.</summary>
public enum TimingPreset
{
    Safe,
    Fast,
    Extreme
}

/// <summary>Everything the calculator needs. A plain value object so it is trivially testable.</summary>
public sealed record DramCalculatorInput(
    RamGeneration Generation,
    MemoryIc Ic,
    int DataRateMtps,
    MemoryRank Rank,
    TimingPreset Preset);

/// <summary>
/// A computed set of suggested timings plus the honest context numbers (cycle time, true latency).
/// This is a <b>starting point to load and validate</b>, never a guaranteed-stable profile.
/// </summary>
public sealed record DramTimingSet
{
    // ---- Frequency context (rigorous, not a guess) ----
    public int DataRateMtps { get; init; }
    public double IoClockMhz { get; init; }     // memory I/O clock = MT/s ÷ 2
    public double CycleTimeNs { get; init; }     // tCK = 2000 / MT/s
    public double TrueLatencyNs { get; init; }   // tCL × tCK — the headline number

    // ---- Primary timings ----
    public int CasLatency { get; init; }   // tCL
    public int Trcd { get; init; }
    public int Trp { get; init; }
    public int Tras { get; init; }
    public int Trc { get; init; }

    // ---- Key secondaries ----
    public int Trfc { get; init; }
    public double TrfcNs { get; init; }
    public int TrrdS { get; init; }
    public int TrrdL { get; init; }
    public int Tfaw { get; init; }
    public int Twr { get; init; }
    public int Twtrs { get; init; }
    public int Twtrl { get; init; }
    public int Trtp { get; init; }
    public int Tcwl { get; init; }
    public int Trefi { get; init; }
    public string CommandRate { get; init; } = string.Empty;

    // ---- Display helpers ----
    public string PrimarySummary { get; init; } = string.Empty;   // "16-16-16-38"
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>Shared option lists + French labels so the VM and any UI stay consistent.</summary>
public static class DramOptions
{
    public static IReadOnlyList<MemoryIc> IcsFor(RamGeneration g) => g == RamGeneration.Ddr4
        ? new[]
        {
            MemoryIc.SamsungBDie, MemoryIc.HynixDJR, MemoryIc.HynixCJR, MemoryIc.MicronRevB,
            MemoryIc.MicronRevE, MemoryIc.SamsungCDie, MemoryIc.NanyaOther, MemoryIc.Unknown
        }
        : new[]
        {
            MemoryIc.HynixADie, MemoryIc.HynixMDie, MemoryIc.SamsungDdr5, MemoryIc.MicronDdr5, MemoryIc.Unknown
        };

    public static string Label(MemoryIc ic) => ic switch
    {
        MemoryIc.SamsungBDie => "Samsung B-die (top OC)",
        MemoryIc.SamsungCDie => "Samsung C-die",
        MemoryIc.MicronRevE  => "Micron Rev.E",
        MemoryIc.MicronRevB  => "Micron Rev.B",
        MemoryIc.HynixCJR    => "SK Hynix CJR",
        MemoryIc.HynixDJR    => "SK Hynix DJR",
        MemoryIc.NanyaOther  => "Nanya / autre",
        MemoryIc.HynixADie   => "SK Hynix A-die (top OC)",
        MemoryIc.HynixMDie   => "SK Hynix M-die",
        MemoryIc.SamsungDdr5 => "Samsung DDR5",
        MemoryIc.MicronDdr5  => "Micron DDR5",
        _                    => "Inconnu (prudent)"
    };

    public static string Label(RamGeneration g) => g == RamGeneration.Ddr4 ? "DDR4" : "DDR5";

    public static string Label(MemoryRank r) => r == MemoryRank.SingleRank ? "Single-rank" : "Dual-rank";

    public static string Label(TimingPreset p) => p switch
    {
        TimingPreset.Safe    => "Safe (quotidien)",
        TimingPreset.Extreme => "Extrême (bench)",
        _                    => "Fast (équilibré)"
    };
}
