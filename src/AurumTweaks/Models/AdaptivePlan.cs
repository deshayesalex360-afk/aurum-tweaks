using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>
/// How loud a hardware insight is in the UI.
/// </summary>
public enum InsightSeverity
{
    Info = 0,         // neutral, informational (gold whisper)
    Opportunity = 1,  // free performance the user is leaving on the table (gold)
    Warning = 2       // something to be careful about (amber/red)
}

/// <summary>
/// A single ranked tweak recommendation produced by the adaptive engine for THIS machine.
/// </summary>
public sealed class TweakRecommendation
{
    public required Tweak Tweak { get; init; }

    /// <summary>Localized display name, filled by the view-model from the localization service.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Final ranking score after hardware/tier/risk/anti-cheat weighting.</summary>
    public int Score { get; init; }

    /// <summary>Localized (FR) one-line justification of why this is recommended for this PC.</summary>
    public string ReasonFr { get; init; } = string.Empty;

    /// <summary>True when this tweak is part of the safe one-click "apply recommended" set.</summary>
    public bool InDefaultSet { get; init; }

    /// <summary>True when the tweak is specifically tuned for the detected hardware (vendor/family match).</summary>
    public bool IsTunedForThisPc { get; init; }
}

/// <summary>
/// A hardware-derived observation that may not map to a single tweak (BIOS, RAM speed, VBS, etc.).
/// These are what make the experience feel "adapté à ton PC".
/// </summary>
public sealed class HardwareInsight
{
    public string TitleFr { get; init; } = string.Empty;
    public string DetailFr { get; init; } = string.Empty;
    public InsightSeverity Severity { get; init; } = InsightSeverity.Info;

    /// <summary>Optional navigation target (page key) for a "Fix it" action, e.g. "Bios", "Tweaks".</summary>
    public string? ActionPage { get; init; }

    /// <summary>Optional label for the action button.</summary>
    public string? ActionLabelFr { get; init; }
}

/// <summary>
/// The complete personalized optimization plan for the detected machine.
/// </summary>
public sealed class AdaptivePlan
{
    public IReadOnlyList<TweakRecommendation> Recommendations { get; init; } = new List<TweakRecommendation>();
    public IReadOnlyList<HardwareInsight> Insights { get; init; } = new List<HardwareInsight>();

    /// <summary>One-line profile, e.g. "AMD Ryzen 7 7800X3D · RTX 4080 · DDR5 32 Go · Windows 11".</summary>
    public string ProfileSummaryFr { get; init; } = string.Empty;

    /// <summary>Tweaks in the safe one-click default set.</summary>
    public int RecommendedCount { get; init; }

    /// <summary>Tweaks that apply to this machine at all (after hardware filtering).</summary>
    public int TotalApplicable { get; init; }

    /// <summary>Estimated, qualitative performance headroom score 0-100 (how much is left to gain).</summary>
    public int PotentialScore { get; init; }
}
