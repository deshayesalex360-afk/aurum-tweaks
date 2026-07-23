using System.Collections.Generic;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure GPU-stability classification. The load-bearing honesty rule: NEVER certify "stable" on a
/// run it couldn't verify (TDR-probe failure or too-few samples → Indeterminate). Priority is
/// Crashed &gt; Hung &gt; Throttling &gt; Stable, and thresholds are explicit (no fabricated precision).
/// </summary>
public class GpuStabilityVerdictTests
{
    private static List<GpuStabilitySample> Steady(int n, double clock = 2800, double temp = 70, double usage = 99)
    {
        var list = new List<GpuStabilitySample>();
        // Vary slightly so the freeze detector does NOT fire on a healthy run.
        for (int i = 0; i < n; i++) list.Add(new GpuStabilitySample(clock + (i % 3), temp + (i % 2) * 0.3, usage - (i % 2)));
        return list;
    }

    [Fact]
    public void TdrEvent_AlwaysWins_EvenWithHealthyTelemetry()
        => Assert.Equal(GpuStabilityResult.Crashed,
            GpuStabilityVerdict.Classify(Steady(20), tdrEventObserved: true, tdrProbeFailed: false));

    [Fact]
    public void HealthyRun_IsStable()
        => Assert.Equal(GpuStabilityResult.Stable,
            GpuStabilityVerdict.Classify(Steady(20), tdrEventObserved: false, tdrProbeFailed: false));

    [Fact]
    public void FrozenTelemetry_IsHung()
    {
        // 8 identical samples under load = freeze → hang suspect.
        var s = new List<GpuStabilitySample>();
        for (int i = 0; i < 8; i++) s.Add(new GpuStabilitySample(2800, 72, 100));
        Assert.Equal(GpuStabilityResult.Hung,
            GpuStabilityVerdict.Classify(s, tdrEventObserved: false, tdrProbeFailed: false));
    }

    [Fact]
    public void ClockSagUnderLoad_IsThrottling_AndClassifiedThermalWhenTempPegged()
    {
        var s = new List<GpuStabilitySample>();
        for (int i = 0; i < 5; i++) s.Add(new GpuStabilitySample(2900 + i, 60 + i * 0.2, 99));   // baseline high clock
        for (int i = 0; i < 8; i++) s.Add(new GpuStabilitySample(2300 + i, 86 + i * 0.1, 98));   // clock sagged, temp pegged
        Assert.Equal(GpuStabilityResult.Throttling,
            GpuStabilityVerdict.Classify(s, tdrEventObserved: false, tdrProbeFailed: false));
        Assert.True(GpuStabilityVerdict.IsThermalThrottle(s));                                    // temp >= 84 → thermal
    }

    [Fact]
    public void PowerThrottle_ClockSagWithoutHeat_IsThrottling_ButNotThermal()
    {
        var s = new List<GpuStabilitySample>();
        for (int i = 0; i < 5; i++) s.Add(new GpuStabilitySample(2900 + i, 55, 99));
        for (int i = 0; i < 8; i++) s.Add(new GpuStabilitySample(2300 + i, 62, 98));   // clock down, temp cool
        Assert.Equal(GpuStabilityResult.Throttling,
            GpuStabilityVerdict.Classify(s, tdrEventObserved: false, tdrProbeFailed: false));
        Assert.False(GpuStabilityVerdict.IsThermalThrottle(s));
    }

    [Fact]
    public void ProbeFailed_NeverReportsStable_EvenOnHealthyLookingData()
        => Assert.Equal(GpuStabilityResult.Indeterminate,
            GpuStabilityVerdict.Classify(Steady(20), tdrEventObserved: false, tdrProbeFailed: true));

    [Fact]
    public void TooFewSamples_IsIndeterminate_NotAFalsePass()
        => Assert.Equal(GpuStabilityResult.Indeterminate,
            GpuStabilityVerdict.Classify(new List<GpuStabilitySample>(), tdrEventObserved: false, tdrProbeFailed: false));

    [Fact]
    public void Describe_NamesExactlyWhatWasObserved()
    {
        Assert.Contains("reset du driver", GpuStabilityVerdict.Describe(GpuStabilityResult.Crashed, Steady(5)));
        Assert.Contains("Indéterminé", GpuStabilityVerdict.Describe(GpuStabilityResult.Indeterminate, Steady(5)));
        Assert.Contains("Stable", GpuStabilityVerdict.Describe(GpuStabilityResult.Stable, Steady(5)));
    }
}
