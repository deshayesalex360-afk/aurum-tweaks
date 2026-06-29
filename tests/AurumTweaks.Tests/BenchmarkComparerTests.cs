using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the honesty contract of the before/after benchmark comparison (<see cref="BenchmarkComparer.Compare"/>) —
/// the feature that proves whether a tweak actually moved the numbers. The load-bearing assertions are the
/// anti-snake-oil ones: a drop in FPS is reported as a regression (never buffed into a win), a "lower is better"
/// metric (stutter / std-dev) counts an increase as a regression, a zero baseline yields 0 % (never NaN/∞), and
/// runs that aren't a clean A/B (different process, short capture, lopsided durations) carry an honest caveat
/// rather than being passed off as authoritative.
/// </summary>
public class BenchmarkComparerTests
{
    private static BenchmarkResult Run(
        double avgFps, double p1 = 0, double p01 = 0, double stutter = 0, double std = 0,
        double consec = 0, string process = "game", double durationSec = 20, int frames = 1200)
        => new()
        {
            TargetProcess = process,
            Stats = new FrameTimeStats
            {
                FrameCount = frames,
                DurationSec = durationSec,
                AvgFps = avgFps,
                P1LowFps = p1,
                P01LowFps = p01,
                StutterPct = stutter,
                StdDevMs = std,
                ConsecutiveDeltaMs = consec,
            }
        };

    private static MetricDelta Metric(BenchmarkComparison c, string label) => c.Metrics.Single(m => m.Label == label);

    // ---- The headline (average FPS): correct sign, exact delta and percent ----

    [Fact]
    public void Compare_AverageFpsUp_IsAnImprovement_WithExactDeltaAndPercent()
    {
        var c = BenchmarkComparer.Compare(Run(avgFps: 100), Run(avgFps: 120));

        Assert.Equal("FPS moyen", c.Headline.Label);
        Assert.Equal(100, c.Headline.Before, 3);
        Assert.Equal(120, c.Headline.After, 3);
        Assert.Equal(20, c.Headline.Delta, 3);
        Assert.Equal(20, c.Headline.PercentChange, 3);   // (120-100)/100
        Assert.True(c.Headline.Improved);
        Assert.False(c.Headline.Regressed);
    }

    // ---- The anti-snake-oil pin: a regression is reported AS a regression ----

    [Fact]
    public void Compare_AverageFpsDown_IsHonestlyARegression_NotBuffedIntoAWin()
    {
        var c = BenchmarkComparer.Compare(Run(avgFps: 120), Run(avgFps: 96));

        Assert.Equal(-24, c.Headline.Delta, 3);
        Assert.Equal(-20, c.Headline.PercentChange, 3);
        Assert.False(c.Headline.Improved);
        Assert.True(c.Headline.Regressed);
    }

    // ---- "Lower is better" metrics invert the meaning of the sign ----

    [Fact]
    public void Compare_StutterDown_CountsAsImprovement_BecauseLowerIsBetter()
    {
        var c = BenchmarkComparer.Compare(Run(avgFps: 100, stutter: 6.0), Run(avgFps: 100, stutter: 2.0));
        var stutter = Metric(c, "Stutter");

        Assert.False(stutter.HigherIsBetter);
        Assert.Equal(-4.0, stutter.Delta, 3);
        Assert.True(stutter.Improved);
        Assert.False(stutter.Regressed);
    }

    [Fact]
    public void Compare_StdDevUp_IsRegression_BecauseLowerIsBetter()
    {
        var c = BenchmarkComparer.Compare(Run(avgFps: 100, std: 1.0), Run(avgFps: 100, std: 2.5));
        var std = Metric(c, "Écart-type");

        Assert.False(std.HigherIsBetter);
        Assert.True(std.Regressed);
        Assert.False(std.Improved);
    }

    [Fact]
    public void Compare_FrameToFrameVariationDown_CountsAsImprovement_BecauseLowerIsBetter()
    {
        var c = BenchmarkComparer.Compare(Run(avgFps: 100, consec: 4.0), Run(avgFps: 100, consec: 1.5));
        var v = Metric(c, "Var. img-à-img");

        Assert.False(v.HigherIsBetter);
        Assert.Equal(-2.5, v.Delta, 3);
        Assert.True(v.Improved);
        Assert.False(v.Regressed);
    }

    // ---- No fabricated number: a zero baseline yields 0 %, never NaN/∞ ----

    [Fact]
    public void Compare_ZeroBaselineAvgFps_PercentIsZero_NeverNaNOrInfinity()
    {
        var c = BenchmarkComparer.Compare(Run(avgFps: 0), Run(avgFps: 60));

        Assert.Equal(0, c.Headline.PercentChange);
        Assert.False(double.IsNaN(c.Headline.PercentChange));
        Assert.False(double.IsInfinity(c.Headline.PercentChange));
        Assert.True(c.Headline.Improved);              // the +60 FPS delta is still a real improvement
    }

    // ---- Honest comparability caveats ----

    [Fact]
    public void Compare_DifferentProcesses_AddsIndicativeCaveat()
    {
        var c = BenchmarkComparer.Compare(Run(avgFps: 100, process: "valorant"), Run(avgFps: 110, process: "cs2"));
        Assert.Contains(c.Caveats, n => n.Contains("process différents"));
    }

    [Fact]
    public void Compare_SameProcess_CleanRuns_HaveNoCaveats()
    {
        var c = BenchmarkComparer.Compare(
            Run(avgFps: 100, process: "game", durationSec: 30),
            Run(avgFps: 110, process: "game", durationSec: 30));
        Assert.Empty(c.Caveats);
    }

    [Fact]
    public void Compare_ShortRun_WarnsItIsLessReliable()
    {
        var c = BenchmarkComparer.Compare(
            Run(avgFps: 100, durationSec: 30),
            Run(avgFps: 110, durationSec: 3));
        Assert.Contains(c.Caveats, n => n.Contains("courte"));
    }

    [Fact]
    public void Compare_LopsidedDurations_WarnAboutComparability()
    {
        // 30 s vs 10 s: neither is "short" (≥5 s), but the 3× imbalance makes the lows non-comparable.
        var c = BenchmarkComparer.Compare(
            Run(avgFps: 100, durationSec: 30),
            Run(avgFps: 110, durationSec: 10));
        Assert.Contains(c.Caveats, n => n.Contains("Durées très différentes"));
    }

    [Fact]
    public void Compare_EnoughSecondsButFrameCapped_WarnsTheTailLowsRestOnTooFewFrames()
    {
        // Both runs clear the duration guards (30 s), but one is frame-capped to 800 frames — 0,1% of 800 is
        // < 1 frame, so its « 0,1% low » is essentially a single sample. Duration can't see this; the frame count can.
        var c = BenchmarkComparer.Compare(
            Run(avgFps: 100, durationSec: 30, frames: 800),
            Run(avgFps: 110, durationSec: 30, frames: 1500));
        Assert.Contains(c.Caveats, n => n.Contains("Peu d'images") && n.Contains("800"));
    }

    [Fact]
    public void Compare_FewFrames_ButRunAlreadyShort_DoesNotDoubleHedge()
    {
        // A 3 s, 200-frame capture is already flagged « courte »; the frame-count note would only say the same
        // thing a second way, so it is suppressed — one honest hedge, not a pile-on.
        var c = BenchmarkComparer.Compare(
            Run(avgFps: 100, durationSec: 30, frames: 1500),
            Run(avgFps: 110, durationSec: 3, frames: 200));
        Assert.Contains(c.Caveats, n => n.Contains("courte"));
        Assert.DoesNotContain(c.Caveats, n => n.Contains("Peu d'images"));
    }

    [Fact]
    public void Compare_VeryDifferentFrameCounts_AtComparableDuration_WarnsSampleSizesDiffer()
    {
        // Same 30 s on the clock, but 5000 vs 1500 frames = a frame-rate cap that moved between runs. The averages
        // stay fair; the tail lows now compare a 5000-sample tail against a 1500-sample one — disclosed, not hidden.
        var c = BenchmarkComparer.Compare(
            Run(avgFps: 100, durationSec: 30, frames: 5000),
            Run(avgFps: 110, durationSec: 30, frames: 1500));
        Assert.Contains(c.Caveats, n => n.Contains("Nombre d'images très différent"));
    }

    [Fact]
    public void Compare_FrameCountGap_Suppressed_WhenDurationsAlreadyDiverged()
    {
        // 30 s vs 10 s already earns the « durées très différentes » note; the frame-count gap it implies would be a
        // redundant second hedge, so only the duration note is raised.
        var c = BenchmarkComparer.Compare(
            Run(avgFps: 100, durationSec: 30, frames: 5000),
            Run(avgFps: 110, durationSec: 10, frames: 1200));
        Assert.Contains(c.Caveats, n => n.Contains("Durées très différentes"));
        Assert.DoesNotContain(c.Caveats, n => n.Contains("Nombre d'images très différent"));
    }

    // ---- The metric set is exactly the five secondary metrics, in order ----

    [Fact]
    public void Compare_SecondaryMetrics_AreTheFiveExpected_InOrder()
    {
        var c = BenchmarkComparer.Compare(Run(avgFps: 100), Run(avgFps: 110));
        Assert.Equal(new[] { "1% low", "0,1% low", "Stutter", "Écart-type", "Var. img-à-img" },
                     c.Metrics.Select(m => m.Label).ToArray());
    }
}
