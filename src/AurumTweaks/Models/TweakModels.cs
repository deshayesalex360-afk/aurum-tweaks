using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AurumTweaks.Models;

/// <summary>
/// Three difficulty tiers. Drives filtering, color coding and confirmation flows.
/// </summary>
public enum TweakTier
{
    Tranquille = 0,
    Avance = 1,
    Extreme = 2
}

/// <summary>
/// How risky a tweak is — for the badge and warning copy.
/// </summary>
public enum RiskLevel
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    HardwareDamage = 4
}

/// <summary>
/// Tweak category. Aligned with the categorisation found in WinUtil/Atlas/FR33THY.
/// </summary>
public enum TweakCategory
{
    PrivacyTelemetry,
    PerformanceMultimedia,
    NetworkLatency,
    Debloat,
    Services,
    UIQualityOfLife,
    PowerBoot,
    Gaming,
    Security,
    Advanced
}

/// <summary>
/// Anti-cheat engines we track per-tweak.
/// </summary>
public enum AntiCheatEngine
{
    Vanguard,
    EasyAntiCheat,
    BattlEye,
    Faceit,
    Ricochet,
    Esea
}

/// <summary>
/// Per-anti-cheat compatibility status for a tweak.
/// </summary>
public enum AntiCheatStatus
{
    Safe,       // green
    Risky,      // yellow
    Banned      // red — tweak will likely get the user banned
}

/// <summary>
/// Operation type a tweak performs.
/// </summary>
public enum OperationType
{
    Registry,
    Service,
    PowerShell,
    Cmd,
    AppX,
    ScheduledTask,
    Bcdedit,
    File
}

/// <summary>
/// Registry value type.
/// </summary>
public enum RegistryValueType
{
    String,
    ExpandString,
    Binary,
    DWord,
    QWord,
    MultiString,
    None
}

/// <summary>
/// One atomic operation inside a tweak. A tweak typically contains multiple operations.
/// Each operation records enough information to also revert it.
/// </summary>
public class TweakOperation
{
    [JsonPropertyName("type")]
    public OperationType Type { get; set; }

    // Registry
    [JsonPropertyName("hive")]
    public string? Hive { get; set; }                  // HKLM, HKCU, HKCR, HKU, HKCC

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }                  // value name

    [JsonPropertyName("valueType")]
    public RegistryValueType ValueType { get; set; } = RegistryValueType.DWord;

    [JsonPropertyName("apply")]
    public string? Apply { get; set; }                 // applied value

    [JsonPropertyName("revert")]
    public string? Revert { get; set; }                // value to restore (null = delete)

    // Service
    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; set; }

    [JsonPropertyName("startupApply")]
    public string? StartupApply { get; set; }          // Disabled / Manual / Automatic / DelayedAuto

    [JsonPropertyName("startupRevert")]
    public string? StartupRevert { get; set; }

    // Script
    [JsonPropertyName("script")]
    public string? Script { get; set; }                // inline command for PowerShell/Cmd/Bcdedit

    [JsonPropertyName("revertScript")]
    public string? RevertScript { get; set; }

    // AppX
    [JsonPropertyName("appxPackage")]
    public string? AppxPackage { get; set; }           // package name pattern

    // Scheduled Task
    [JsonPropertyName("taskPath")]
    public string? TaskPath { get; set; }

    // File operations
    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

/// <summary>
/// Per-anti-cheat status mapping.
/// </summary>
public class AntiCheatMatrix
{
    [JsonPropertyName("vanguard")]
    public AntiCheatStatus Vanguard { get; set; } = AntiCheatStatus.Safe;

    [JsonPropertyName("easyAntiCheat")]
    public AntiCheatStatus EasyAntiCheat { get; set; } = AntiCheatStatus.Safe;

    [JsonPropertyName("battlEye")]
    public AntiCheatStatus BattlEye { get; set; } = AntiCheatStatus.Safe;

    [JsonPropertyName("faceit")]
    public AntiCheatStatus Faceit { get; set; } = AntiCheatStatus.Safe;

    [JsonPropertyName("ricochet")]
    public AntiCheatStatus Ricochet { get; set; } = AntiCheatStatus.Safe;

    [JsonPropertyName("esea")]
    public AntiCheatStatus Esea { get; set; } = AntiCheatStatus.Safe;

    public bool HasAnyConcern => Vanguard != AntiCheatStatus.Safe
                                  || EasyAntiCheat != AntiCheatStatus.Safe
                                  || BattlEye != AntiCheatStatus.Safe
                                  || Faceit != AntiCheatStatus.Safe
                                  || Ricochet != AntiCheatStatus.Safe
                                  || Esea != AntiCheatStatus.Safe;
}

/// <summary>
/// Main Tweak model. Loaded from JSON. ObservableObject so the runtime-state flags below drive the UI live —
/// the per-row "✓ Appliqué" badge and the selection checkbox bind to them and must refresh when detection or
/// an apply/revert changes them after the rows have rendered.
/// </summary>
public partial class Tweak : ObservableObject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public Dictionary<string, string> Name { get; set; } = new();

    [JsonPropertyName("description")]
    public Dictionary<string, string> Description { get; set; } = new();

    [JsonPropertyName("technicalDetails")]
    public Dictionary<string, string> TechnicalDetails { get; set; } = new();

    [JsonPropertyName("tier")]
    public TweakTier Tier { get; set; }

    [JsonPropertyName("category")]
    public TweakCategory Category { get; set; }

    [JsonPropertyName("risk")]
    public RiskLevel Risk { get; set; }

    [JsonPropertyName("reversible")]
    public bool Reversible { get; set; } = true;

    [JsonPropertyName("requiresReboot")]
    public bool RequiresReboot { get; set; }

    [JsonPropertyName("expectedImpact")]
    public string ExpectedImpact { get; set; } = string.Empty;

    [JsonPropertyName("sources")]
    public List<string> Sources { get; set; } = new();

    [JsonPropertyName("antiCheat")]
    public AntiCheatMatrix AntiCheat { get; set; } = new();

    [JsonPropertyName("windowsVersions")]
    public List<string> WindowsVersions { get; set; } = new() { "10", "11" };

    /// <summary>
    /// Optional hardware gating. When null, the tweak applies to every machine
    /// (still subject to <see cref="WindowsVersions"/> and the anti-cheat filter).
    /// Drives the adaptive "recommended for your PC" engine.
    /// </summary>
    [JsonPropertyName("applicability")]
    public TweakApplicability? Applicability { get; set; }

    /// <summary>
    /// Base ranking weight (0-100). Higher = surfaced earlier in the adaptive plan.
    /// Defaults to 50 (neutral). High-value baseline tweaks use 70-90.
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 50;

    [JsonPropertyName("operations")]
    public List<TweakOperation> Operations { get; set; } = new();

    /// <summary>
    /// French, technical "what this changes" disclosure derived from <see cref="Operations"/> — the very data
    /// the engine dispatches on, so it can't drift from what apply/revert actually does. Bound by the Tweaks
    /// card's "Détails techniques" expander so the user sees the real registry keys / services / commands before
    /// applying. Computed on demand (lazy, cheap) and excluded from (de)serialization.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<OperationSummary> OperationSummaries => TweakOperationSummary.Summarize(this);

    [JsonPropertyName("warnings")]
    public Dictionary<string, string> Warnings { get; set; } = new();

    // Runtime state — observable so the Tweaks page reflects changes after rows render (badge, checkbox).
    // [property: JsonIgnore] keeps the generated property out of (de)serialization, as the manual props were.
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isApplied;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSelected;
}

/// <summary>
/// Declares which machines a tweak is relevant to. Every field is optional;
/// an empty/zero field means "no constraint on this axis". The adaptive engine
/// uses this both to <b>hide irrelevant tweaks</b> and to <b>boost the score</b> of
/// tweaks that are specifically tuned for the detected hardware.
/// </summary>
public class TweakApplicability
{
    /// <summary>CPU vendor substrings to match against the raw WMI manufacturer (e.g. "AMD", "Intel"). Empty = any.</summary>
    [JsonPropertyName("cpuVendors")]
    public List<string> CpuVendors { get; set; } = new();

    /// <summary>Specific CPU families this tweak is tuned for (e.g. Ryzen7000X3D). Empty = any.</summary>
    [JsonPropertyName("cpuFamilies")]
    public List<CpuFamily> CpuFamilies { get; set; } = new();

    /// <summary>GPU vendors this tweak targets. Empty = any.</summary>
    [JsonPropertyName("gpuVendors")]
    public List<GpuVendor> GpuVendors { get; set; } = new();

    /// <summary>RAM technologies this tweak targets, e.g. "DDR4", "DDR5". Empty = any.</summary>
    [JsonPropertyName("ramTypes")]
    public List<string> RamTypes { get; set; } = new();

    /// <summary>Minimum installed RAM in GB for the tweak to make sense (0 = no minimum).</summary>
    [JsonPropertyName("minRamGb")]
    public int MinRamGb { get; set; }

    /// <summary>Tweak only makes sense on a desktop (skipped on laptops, e.g. core-parking off).</summary>
    [JsonPropertyName("desktopOnly")]
    public bool DesktopOnly { get; set; }

    /// <summary>Tweak only makes sense on an SSD/NVMe system drive (e.g. disabling Superfetch/Prefetch).</summary>
    [JsonPropertyName("ssdOnly")]
    public bool SsdOnly { get; set; }

    /// <summary>Tweak requires Windows 11 specifically.</summary>
    [JsonPropertyName("requiresWin11")]
    public bool RequiresWin11 { get; set; }

    /// <summary>True when at least one axis narrows this tweak to specific hardware (used for the specificity bonus).</summary>
    [JsonIgnore]
    public bool IsHardwareSpecific =>
        CpuVendors.Count > 0 || CpuFamilies.Count > 0 || GpuVendors.Count > 0
        || RamTypes.Count > 0 || MinRamGb > 0 || DesktopOnly || SsdOnly || RequiresWin11;
}
