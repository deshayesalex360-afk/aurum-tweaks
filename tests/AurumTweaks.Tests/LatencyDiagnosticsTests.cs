using System.Collections.Generic;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  « Latence système (DPC/ISR) » pure-core tests. The kernel sampling (NtQuerySystemInformation) is mechanical I/O
//  glue and gets no flaky integration test; what we pin is the load math, the errors-first verdict, the fr-FR
//  formatting and the report aggregation — the honesty-bearing logic. The load-bearing invariant: a counter that
//  appears to move backwards across the window can NEVER fabricate a negative or a >100 % reading, and "no data"
//  must surface as Unknown, never a cheerful "Low".
// ─────────────────────────────────────────────────────────────────────────────

public class ProcessorLoadMathTests
{
    private static CpuTimes T(long idle, long kernel, long user, long dpc, long intr, long count) =>
        new(idle, kernel, user, dpc, intr, count);

    [Fact]
    public void Compute_BasicWindow_DerivesBusyDpcInterruptAndRate()
    {
        // kernel Δ=80 (incl. idle 50, dpc 10, intr 5), user Δ=20 ⇒ total=100
        var a = T(0, 0, 0, 0, 0, 0);
        var b = T(50, 80, 20, 10, 5, 1000);

        var load = ProcessorLoadMath.Compute(0, a, b, 2.0);

        Assert.Equal(0, load.CpuIndex);
        Assert.Equal(50.0, load.BusyPercent, 6);        // (100 − idle 50) / 100
        Assert.Equal(10.0, load.DpcPercent, 6);         // 10 / 100
        Assert.Equal(5.0, load.InterruptPercent, 6);    // 5 / 100
        Assert.Equal(500.0, load.InterruptsPerSecond, 6); // 1000 interrupts / 2 s
    }

    [Fact]
    public void Compute_CountersGoBackwards_NeverFabricatesNegativeLoad()
    {
        // Every "b" counter is LOWER than "a" — a wrap/reset. All deltas must clamp to 0, not go negative.
        var a = T(100, 100, 100, 100, 100, 100);
        var b = T(0, 0, 0, 0, 0, 0);

        var load = ProcessorLoadMath.Compute(7, a, b, 2.0);

        Assert.Equal(0.0, load.BusyPercent, 6);
        Assert.Equal(0.0, load.DpcPercent, 6);
        Assert.Equal(0.0, load.InterruptPercent, 6);
        Assert.Equal(0.0, load.InterruptsPerSecond, 6);
    }

    [Fact]
    public void Compute_ZeroTotalTime_YieldsZeroPercentsNoDivideByZero()
    {
        var same = T(0, 0, 0, 0, 0, 0);
        var load = ProcessorLoadMath.Compute(0, same, same, 2.0);

        Assert.Equal(0.0, load.BusyPercent, 6);
        Assert.Equal(0.0, load.DpcPercent, 6);
        Assert.Equal(0.0, load.InterruptPercent, 6);
    }

    [Fact]
    public void Compute_IdleExceedsBusy_BusyClampsToZeroNotNegative()
    {
        // total=10 but idle Δ=50 (a nonsensical reading) ⇒ (10 − 50) would be negative; must clamp to 0.
        var a = T(0, 0, 0, 0, 0, 0);
        var b = T(50, 10, 0, 0, 0, 0);

        var load = ProcessorLoadMath.Compute(0, a, b, 2.0);

        Assert.Equal(0.0, load.BusyPercent, 6);
    }

    [Fact]
    public void Compute_DpcExceedsTotal_ClampsToHundredPercent()
    {
        // total=10, dpc Δ=50 ⇒ 500 % would be absurd; must clamp to 100.
        var a = T(0, 0, 0, 0, 0, 0);
        var b = T(0, 10, 0, 50, 0, 0);

        var load = ProcessorLoadMath.Compute(0, a, b, 2.0);

        Assert.Equal(100.0, load.DpcPercent, 6);
    }

    [Fact]
    public void Compute_ZeroElapsed_RateIsZero()
    {
        var a = T(0, 0, 0, 0, 0, 0);
        var b = T(0, 100, 0, 0, 0, 5000);

        var load = ProcessorLoadMath.Compute(0, a, b, 0.0);

        Assert.Equal(0.0, load.InterruptsPerSecond, 6);
    }

    [Fact]
    public void Compute_RateScalesWithElapsedSeconds()
    {
        var a = T(0, 0, 0, 0, 0, 0);
        var b = T(0, 100, 0, 0, 0, 3000);

        var load = ProcessorLoadMath.Compute(0, a, b, 1.5);

        Assert.Equal(2000.0, load.InterruptsPerSecond, 6); // 3000 / 1.5
    }
}

public class LatencyVerdictTests
{
    [Fact]
    public void Evaluate_QueryFailed_IsUnknown_EvenWithZeros()
    {
        var v = LatencyVerdict.Evaluate(0, 0, queryOk: false);
        Assert.Equal(LatencyLevel.Unknown, v.Level);
    }

    [Fact]
    public void Evaluate_QueryFailed_IsUnknown_EvenWithHighLoad()
    {
        // Errors-first: a failed read can't be reported as "High" just because stale numbers look bad.
        var v = LatencyVerdict.Evaluate(50, 50, queryOk: false);
        Assert.Equal(LatencyLevel.Unknown, v.Level);
    }

    [Theory]
    [InlineData(20, 0, LatencyLevel.High)]    // dpc drives it
    [InlineData(0, 20, LatencyLevel.High)]    // interrupt drives it (worst = max)
    [InlineData(15, 0, LatencyLevel.High)]    // boundary: ≥ 15
    [InlineData(14.9, 0, LatencyLevel.Moderate)]
    [InlineData(10, 3, LatencyLevel.Moderate)]
    [InlineData(5, 0, LatencyLevel.Moderate)] // boundary: ≥ 5
    [InlineData(4.9, 0, LatencyLevel.Low)]
    [InlineData(0, 0, LatencyLevel.Low)]
    public void Evaluate_WorstOfDpcAndInterrupt_DrivesLevel(double dpc, double intr, LatencyLevel expected)
    {
        var v = LatencyVerdict.Evaluate(dpc, intr, queryOk: true);
        Assert.Equal(expected, v.Level);
    }

    [Fact]
    public void Evaluate_High_HandsOffToRealTracerNotAFabricatedDriver()
    {
        var v = LatencyVerdict.Evaluate(20, 0, queryOk: true);
        Assert.Contains("LatencyMon", v.Message);
        Assert.Contains("DPC Latency Checker", v.Message);
    }

    [Fact]
    public void Evaluate_EveryLevel_HasNonEmptyMessage()
    {
        Assert.False(string.IsNullOrWhiteSpace(LatencyVerdict.Evaluate(0, 0, false).Message));
        Assert.False(string.IsNullOrWhiteSpace(LatencyVerdict.Evaluate(0, 0, true).Message));
        Assert.False(string.IsNullOrWhiteSpace(LatencyVerdict.Evaluate(8, 0, true).Message));
        Assert.False(string.IsNullOrWhiteSpace(LatencyVerdict.Evaluate(20, 0, true).Message));
    }

    [Fact]
    public void Thresholds_AreOrdered()
    {
        Assert.True(LatencyVerdict.ModeratePercent < LatencyVerdict.HighPercent);
    }

    [Theory]
    [InlineData(LatencyLevel.Low, "Faible")]
    [InlineData(LatencyLevel.Moderate, "Modérée")]
    [InlineData(LatencyLevel.High, "Élevée")]
    [InlineData(LatencyLevel.Unknown, "Inconnue")]
    public void Label_MapsEachLevelToFrench(LatencyLevel level, string expected) =>
        Assert.Equal(expected, LatencyVerdict.Label(level));
}

public class LatencyFormatTests
{
    [Fact]
    public void Percent_UsesFrenchDecimalComma()
    {
        Assert.Equal("6,2 %", LatencyFormat.Percent(6.2));
        Assert.Equal("50,0 %", LatencyFormat.Percent(50));
    }

    [Fact]
    public void Percent_ClampsToZeroToHundred()
    {
        Assert.Equal("100,0 %", LatencyFormat.Percent(150));
        Assert.Equal("0,0 %", LatencyFormat.Percent(-5));
    }

    [Fact]
    public void Rate_IsRoundedIntegerWithSuffix_NoGroupSeparator()
    {
        Assert.Equal("500/s", LatencyFormat.Rate(500));
        Assert.Equal("12001/s", LatencyFormat.Rate(12000.7));
        Assert.Equal("1234567/s", LatencyFormat.Rate(1234567)); // invariant: no thousands separator
    }

    [Fact]
    public void Rate_NegativeClampsToZero()
    {
        Assert.Equal("0/s", LatencyFormat.Rate(-10));
    }
}

public class ProcessorLoadDisplayTests
{
    [Fact]
    public void DisplayProperties_AreFormattedForTheFrenchUi()
    {
        var load = new ProcessorLoad(3, BusyPercent: 40, DpcPercent: 6.2, InterruptPercent: 1.1, InterruptsPerSecond: 12000);

        Assert.Equal("CPU 3", load.CpuLabel);
        Assert.Equal("40,0 %", load.BusyDisplay);
        Assert.Equal("6,2 %", load.DpcDisplay);
        Assert.Equal("1,1 %", load.InterruptDisplay);
        Assert.Equal("12000/s", load.InterruptRateDisplay);
    }

    [Fact]
    public void SummaryDisplay_MentionsEveryMeasuredFacet()
    {
        var load = new ProcessorLoad(3, BusyPercent: 40, DpcPercent: 6.2, InterruptPercent: 1.1, InterruptsPerSecond: 12000);
        var s = load.SummaryDisplay;

        Assert.Contains("CPU 3", s);
        Assert.Contains("DPC", s);
        Assert.Contains("ISR", s);
        Assert.Contains("occupation", s);
        Assert.Contains("6,2 %", s);
    }
}

public class LatencyReportTests
{
    private static LatencyReport ThreeCoreReport() => new(
        new List<ProcessorLoad>
        {
            new(0, BusyPercent: 40, DpcPercent: 6,  InterruptPercent: 2, InterruptsPerSecond: 1000),
            new(1, BusyPercent: 30, DpcPercent: 12, InterruptPercent: 1, InterruptsPerSecond: 2000),
            new(2, BusyPercent: 20, DpcPercent: 3,  InterruptPercent: 8, InterruptsPerSecond: 500),
        },
        MeasurementSeconds: 2.0, QueryOk: true);

    [Fact]
    public void Aggregates_MaxAvgAndTotalAcrossCores()
    {
        var r = ThreeCoreReport();

        Assert.Equal(3, r.CpuCount);
        Assert.Equal(12.0, r.MaxDpcPercent, 6);
        Assert.Equal(8.0, r.MaxInterruptPercent, 6);
        Assert.Equal(7.0, r.AvgDpcPercent, 6);            // (6+12+3)/3
        Assert.Equal(3.6667, r.AvgInterruptPercent, 4);   // (2+1+8)/3
        Assert.Equal(3500.0, r.TotalInterruptsPerSecond, 6);
    }

    [Fact]
    public void WorstDpcCpu_IsTheCoreCarryingThePeakDpc()
    {
        var r = ThreeCoreReport();
        Assert.Equal(1, r.WorstDpcCpu);
        Assert.Equal("Pic DPC sur CPU 1 (12,0 %)", r.WorstDpcDisplay);
    }

    [Fact]
    public void Verdict_UsesTheWorstCoreAndIsModerateHere()
    {
        var r = ThreeCoreReport();   // worst = max(12, 8) = 12 ⇒ Moderate
        Assert.Equal(LatencyLevel.Moderate, r.Verdict.Level);
    }

    [Fact]
    public void Headline_QueryOk_ReportsCoreCountAndWindow()
    {
        var r = ThreeCoreReport();
        Assert.Equal("3 cœurs logiques mesurés sur 2 s", r.Headline);
    }

    [Fact]
    public void Headline_FormatsFractionalSecondsInFrench()
    {
        var r = new LatencyReport(new List<ProcessorLoad> { new(0, 0, 0, 0, 0) }, MeasurementSeconds: 1.5, QueryOk: true);
        Assert.Equal("1 cœurs logiques mesurés sur 1,5 s", r.Headline);
    }

    [Fact]
    public void EmptyFailedReport_IsHonestlyUnknown()
    {
        var r = new LatencyReport(new List<ProcessorLoad>(), MeasurementSeconds: 2.0, QueryOk: false);

        Assert.Equal(0, r.CpuCount);
        Assert.Equal(-1, r.WorstDpcCpu);
        Assert.Equal("—", r.WorstDpcDisplay);
        Assert.Equal("Mesure impossible", r.Headline);
        Assert.Equal(LatencyLevel.Unknown, r.Verdict.Level);
    }

    [Fact]
    public void Verdict_RespectsQueryOkFlag_IndependentOfData()
    {
        // Same (empty) data, but QueryOk true would evaluate to Low while false must be Unknown.
        var failed = new LatencyReport(new List<ProcessorLoad>(), 2.0, QueryOk: false);
        Assert.Equal(LatencyLevel.Unknown, failed.Verdict.Level);
    }
}

/// <summary>
/// Pins <see cref="LatencyTextReport"/> — the shareable DPC/ISR paste. Honesty contract: a failed measurement prints
/// « Mesure impossible » (never a fabricated zero-load sheet), the « how much, not which driver » caveat survives into
/// the paste, and the verdict label is the shared <see cref="LatencyVerdict.Label"/>. Numbers are LatencyFormat-fixed to
/// fr-FR, so the asserted decimals are locale-independent.
/// </summary>
public class LatencyTextReportTests
{
    private static readonly System.DateTime When = new(2026, 6, 21, 14, 30, 0, System.DateTimeKind.Utc);

    private static LatencyReport Report(bool queryOk, params ProcessorLoad[] cores) =>
        new(cores, MeasurementSeconds: 2.0, QueryOk: queryOk);

    [Fact]
    public void Header_AlwaysCarriesTitle()
    {
        var text = LatencyTextReport.Render(Report(true, new ProcessorLoad(0, 30, 1.0, 0.5, 8000)), When);
        Assert.Contains("Aurum Tweaks — Latence système (DPC/ISR)", text);
    }

    [Fact]
    public void QueryFailed_SaysMeasurementImpossible_AndOmitsVerdictAndPerCore()
    {
        var text = LatencyTextReport.Render(Report(false), When);
        Assert.Contains("Mesure impossible", text);
        Assert.DoesNotContain("VERDICT", text);
        Assert.DoesNotContain("DÉTAIL PAR CŒUR", text);
    }

    [Fact]
    public void LowLoad_RendersFaibleVerdict_SynthesisAndPerCore()
    {
        var text = LatencyTextReport.Render(Report(true, new ProcessorLoad(0, 30, 1.0, 0.5, 8000)), When);
        Assert.Contains("VERDICT : Faible", text);
        Assert.Contains("SYNTHÈSE", text);
        Assert.Contains("DPC max", text);
        Assert.Contains("1,0 %", text);                 // fr-FR comma, deterministic across machine locales
        Assert.Contains("Pic DPC sur CPU 0", text);
        Assert.Contains("DÉTAIL PAR CŒUR LOGIQUE", text);
        Assert.Contains("CPU 0", text);
    }

    [Fact]
    public void HighLoad_RendersElevatedVerdict_WithRealTracerHandoff()
    {
        var text = LatencyTextReport.Render(Report(true, new ProcessorLoad(3, 60, 20.0, 2.0, 30000)), When);
        Assert.Contains("VERDICT : Élevée", text);
        Assert.Contains("LatencyMon", text);            // hands off to a real tracer, never names a fabricated driver
        Assert.Contains("Pic DPC sur CPU 3", text);
    }

    [Fact]
    public void Footer_KeepsTheHowMuchNotWhichDriverHonestyLine()
    {
        var text = LatencyTextReport.Render(Report(true, new ProcessorLoad(0, 30, 1.0, 0.5, 8000)), When);
        Assert.Contains("pas QUEL pilote", text);
        Assert.Contains("jamais envoyé", text);
    }

    [Fact]
    public void EveryCore_GetsItsOwnLine()
    {
        var text = LatencyTextReport.Render(Report(true,
            new ProcessorLoad(0, 30, 1.0, 0.5, 8000),
            new ProcessorLoad(1, 25, 2.0, 0.3, 6000),
            new ProcessorLoad(2, 20, 0.5, 0.1, 4000)), When);
        Assert.Contains("CPU 0", text);
        Assert.Contains("CPU 1", text);
        Assert.Contains("CPU 2", text);
    }

    // (ok, current, default-coarse, best-precise) in 100-ns units; defaults to an honest 0.5 ms / 15.6 ms reading.
    private static TimerResolutionReading Timer(bool ok = true, uint current = 5000, uint min = 156250, uint max = 5000) =>
        new(ok, current, min, max);

    [Fact]
    public void TimerSection_AppearsWhenTheReadingIsOk()
    {
        var text = LatencyTextReport.Render(Report(true, new ProcessorLoad(0, 30, 1.0, 0.5, 8000)), When, Timer());
        Assert.Contains("MINUTEUR SYSTÈME", text);
        Assert.Contains("Résolution actuelle", text);
        Assert.Contains("0,5 ms", text);                 // fr-FR, deterministic
        Assert.Contains("Lecture seule", text);          // the read-only / per-process honesty line survives
    }

    [Fact]
    public void TimerSection_OmittedWhenNoReadingPassed()
        // The companion section is optional — a report without a timer reading simply doesn't carry it.
        => Assert.DoesNotContain("MINUTEUR SYSTÈME",
            LatencyTextReport.Render(Report(true, new ProcessorLoad(0, 30, 1.0, 0.5, 8000)), When));

    [Fact]
    public void TimerSection_OmittedWhenTheReadingFailed()
        // A failed timer query is never fabricated into a section — it's simply absent.
        => Assert.DoesNotContain("MINUTEUR SYSTÈME",
            LatencyTextReport.Render(Report(true, new ProcessorLoad(0, 30, 1.0, 0.5, 8000)), When, Timer(ok: false)));
}
