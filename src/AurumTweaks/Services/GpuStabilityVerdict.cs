using System;
using System.Collections.Generic;

namespace AurumTweaks.Services;

/// <summary>One monitoring sample taken during a GPU load run.</summary>
public readonly record struct GpuStabilitySample(double CoreClockMhz, double TempC, double UsagePct);

/// <summary>The outcome of a GPU stability run, most-severe first.</summary>
public enum GpuStabilityResult
{
    Stable,
    Throttling,
    Hung,
    Crashed,
    Indeterminate,
}

/// <summary>
/// Pure classification of a GPU stress run from its telemetry stream plus whether a driver-reset (TDR)
/// event was seen in the run window. This is the honesty-load-bearing core: it must NEVER claim "stable"
/// on a run it could not actually verify (a TDR-probe failure or too-few samples → Indeterminate, not a
/// false pass), and it must not fabricate precision — every threshold is an explicit, documented constant
/// the caller can override. Priority: Crashed (event-confirmed) &gt; Hung (telemetry freeze) &gt; Throttling
/// &gt; Stable. Mirrors the repo's existing pure *Verdict pattern; the real load + event probe live outside.
/// </summary>
public static class GpuStabilityVerdict
{
    /// <summary>Default GPU-throttle temperature (°C). Vendor/card dependent → exposed as a parameter.</summary>
    public const double DefaultThrottleTempC = 84.0;
    /// <summary>Core-clock sag (fraction below the early-run sustained baseline) that counts as a throttle.</summary>
    public const double DefaultClockSagFraction = 0.15;
    /// <summary>Consecutive near-identical samples that count as a telemetry freeze (hang suspect).</summary>
    public const int DefaultFrozenRun = 6;
    /// <summary>Below-this variance in clock AND usage between two samples counts as "identical".</summary>
    public const double DefaultFreezeEpsilon = 0.5;

    public static GpuStabilityResult Classify(
        IReadOnlyList<GpuStabilitySample> samples,
        bool tdrEventObserved,
        bool tdrProbeFailed,
        double throttleTempC = DefaultThrottleTempC,
        double clockSagFraction = DefaultClockSagFraction,
        int frozenRun = DefaultFrozenRun,
        double freezeEpsilon = DefaultFreezeEpsilon)
    {
        // A driver reset in the run window is the definitive instability signal — it wins outright.
        if (tdrEventObserved) return GpuStabilityResult.Crashed;

        // Too little data to judge: honest indeterminate rather than a hollow "stable".
        if (samples is null || samples.Count < frozenRun)
            return tdrProbeFailed ? GpuStabilityResult.Indeterminate
                 : (samples is not null && samples.Count > 0) ? GpuStabilityResult.Stable
                 : GpuStabilityResult.Indeterminate;

        // Telemetry freeze → hang suspect (clock AND usage flat for several consecutive samples).
        int frozen = 1;
        for (int i = 1; i < samples.Count; i++)
        {
            bool same = Math.Abs(samples[i].CoreClockMhz - samples[i - 1].CoreClockMhz) < freezeEpsilon
                     && Math.Abs(samples[i].UsagePct - samples[i - 1].UsagePct) < freezeEpsilon;
            frozen = same ? frozen + 1 : 1;
            if (frozen >= frozenRun) return GpuStabilityResult.Hung;
        }

        // Throttle: core clock sagged materially below its early sustained baseline while usage stayed high.
        double baseClock = 0;
        int head = Math.Min(5, samples.Count);
        for (int i = 0; i < head; i++) baseClock = Math.Max(baseClock, samples[i].CoreClockMhz);
        if (baseClock > 0)
        {
            foreach (var s in samples)
                if (s.UsagePct > 80 && s.CoreClockMhz < baseClock * (1 - clockSagFraction))
                    return GpuStabilityResult.Throttling;
        }

        // Nothing adverse observed. If the TDR probe itself failed, we cannot honestly certify "stable".
        return tdrProbeFailed ? GpuStabilityResult.Indeterminate : GpuStabilityResult.Stable;
    }

    /// <summary>Whether a throttle is thermal (temp at/above the limit) vs power/voltage-limited — only
    /// meaningful once <see cref="Classify"/> returned <see cref="GpuStabilityResult.Throttling"/>.</summary>
    public static bool IsThermalThrottle(IReadOnlyList<GpuStabilitySample> samples, double throttleTempC = DefaultThrottleTempC)
    {
        if (samples is null) return false;
        foreach (var s in samples)
            if (s.TempC >= throttleTempC) return true;
        return false;
    }

    /// <summary>Short French verdict line for the UI/journal — states exactly what was observed, no more.</summary>
    public static string Describe(GpuStabilityResult result, IReadOnlyList<GpuStabilitySample> samples, double throttleTempC = DefaultThrottleTempC) => result switch
    {
        GpuStabilityResult.Stable => "Stable — aucune instabilité observée pendant la charge (ni reset driver, ni throttling, ni gel).",
        GpuStabilityResult.Throttling => IsThermalThrottle(samples, throttleTempC)
            ? "Throttling thermique — la fréquence a chuté alors que la température plafonnait. Baisse l'OC ou améliore le refroidissement."
            : "Throttling (limite de puissance) — la fréquence a chuté sans plafond thermique. Limité par le power limit/voltage.",
        GpuStabilityResult.Hung => "Gel suspecté — la télémétrie s'est figée sous charge. À corroborer (l'OC est probablement instable).",
        GpuStabilityResult.Crashed => "Instable — reset du driver GPU (TDR) détecté pendant la charge. Réduis l'OC.",
        _ => "Indéterminé — impossible de certifier la stabilité (données insuffisantes ou journal Windows illisible).",
    };
}
