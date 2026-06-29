using System;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Tests for the pure frame-time metrics engine. Zero hardware — deterministic arithmetic over a list
/// of frame times, so we can assert exact values (constant-FPS streams), the documented "lows"
/// conventions, filtering of garbage samples, and the ordering invariants that must always hold.
/// </summary>
public class FrameTimeAnalyzerTests
{
    [Fact]
    public void Empty_Or_Null_Yields_EmptyStats()
    {
        Assert.Equal(0, FrameTimeAnalyzer.Compute(Array.Empty<double>()).FrameCount);
        Assert.Equal(0, FrameTimeAnalyzer.Compute(null!).FrameCount);
    }

    [Fact]
    public void ConstantStream_HasFlatFpsEverywhere()
    {
        // 100 frames at exactly 16.6667 ms ⇒ a flat 60 FPS line.
        var frames = Enumerable.Repeat(1000.0 / 60.0, 100).ToList();
        var s = FrameTimeAnalyzer.Compute(frames);

        Assert.Equal(100, s.FrameCount);
        Assert.Equal(60.0, s.AvgFps, 3);
        Assert.Equal(60.0, s.MinFps, 3);
        Assert.Equal(60.0, s.MaxFps, 3);
        Assert.Equal(60.0, s.P1LowFps, 3);
        Assert.Equal(60.0, s.P01LowFps, 3);
        Assert.Equal(0.0, s.StutterPct, 6);
        Assert.Equal(0.0, s.StdDevMs, 6);
        Assert.Equal(0.0, s.ConsecutiveDeltaMs, 6);   // a flat line has no frame-to-frame jitter
        Assert.Equal(100 * (1000.0 / 60.0) / 1000.0, s.DurationSec, 6);
    }

    [Fact]
    public void ConsecutiveDelta_IsZero_ForAFlatStream_AndASingleFrame()
    {
        Assert.Equal(0.0, FrameTimeAnalyzer.Compute(Enumerable.Repeat(16.0, 50).ToList()).ConsecutiveDeltaMs, 6);
        Assert.Equal(0.0, FrameTimeAnalyzer.Compute(new[] { 16.0 }).ConsecutiveDeltaMs, 6);
    }

    [Fact]
    public void ConsecutiveDelta_IsTheMeanAbsoluteStepBetweenFrames()
    {
        // |14-10| + |12-14| + |20-12| = 4 + 2 + 8 = 14, averaged over the 3 steps ⇒ 4.6667 ms.
        var s = FrameTimeAnalyzer.Compute(new[] { 10.0, 14.0, 12.0, 20.0 });
        Assert.Equal(14.0 / 3.0, s.ConsecutiveDeltaMs, 6);
    }

    // The load-bearing test: it's why this metric exists. The same MULTISET of frame times in two different
    // orders is byte-identical under every order-independent stat (avg / std-dev / percentiles / stutter),
    // yet a smooth ramp and a zig-zag feel nothing alike — only the consecutive-delta metric tells them apart.
    [Fact]
    public void ConsecutiveDelta_SeesFrameOrder_WhereEveryOtherMetricIsBlind()
    {
        var smooth = FrameTimeAnalyzer.Compute(new[] { 10.0, 20.0, 30.0, 40.0 }); // steps 10,10,10 ⇒ 10
        var jagged = FrameTimeAnalyzer.Compute(new[] { 10.0, 40.0, 20.0, 30.0 }); // steps 30,20,10 ⇒ 20

        Assert.Equal(smooth.AvgFps, jagged.AvgFps, 9);
        Assert.Equal(smooth.StdDevMs, jagged.StdDevMs, 9);
        Assert.Equal(smooth.P1LowFps, jagged.P1LowFps, 9);
        Assert.Equal(smooth.StutterPct, jagged.StutterPct, 9);

        Assert.Equal(10.0, smooth.ConsecutiveDeltaMs, 6);
        Assert.Equal(20.0, jagged.ConsecutiveDeltaMs, 6);
        Assert.True(jagged.ConsecutiveDeltaMs > smooth.ConsecutiveDeltaMs, "the jagged run must read as less smooth");
    }

    [Fact]
    public void Discards_NonPositive_NaN_And_Infinity()
    {
        double ft = 1000.0 / 60.0;
        var s = FrameTimeAnalyzer.Compute(new[] { ft, -1, 0, double.NaN, double.PositiveInfinity, ft });
        Assert.Equal(2, s.FrameCount);          // only the two valid 16.67 ms frames survive
        Assert.Equal(60.0, s.AvgFps, 3);
    }

    [Fact]
    public void AvgFps_IsInverseOfMeanFrameTime()
    {
        var s = FrameTimeAnalyzer.Compute(new[] { 10.0, 20.0, 30.0, 40.0 });
        Assert.Equal(25.0, s.AvgFrameTimeMs, 6);          // mean of 10..40
        Assert.Equal(1000.0 / 25.0, s.AvgFps, 6);         // 40 fps
    }

    [Fact]
    public void OrderingInvariants_AlwaysHold()
    {
        var rng = new Random(123);
        var frames = Enumerable.Range(0, 5000).Select(_ => 5.0 + rng.NextDouble() * 25.0).ToList();
        var s = FrameTimeAnalyzer.Compute(frames);

        Assert.True(s.MaxFps >= s.AvgFps, "max ≥ avg");
        Assert.True(s.AvgFps >= s.MinFps, "avg ≥ min");
        Assert.True(s.P1LowFps >= s.MinFps, "1% low ≥ absolute min");
        Assert.True(s.P1LowFps <= s.MaxFps, "1% low ≤ max");
        Assert.True(s.P01LowFps <= s.P1LowFps + 1e-9, "0.1% low ≤ 1% low (p99.9 ≥ p99)");
        Assert.True(s.P999FrameTimeMs >= s.P99FrameTimeMs, "p99.9 frame time ≥ p99");
    }

    [Fact]
    public void Lows_AreConsistentWithTheirPercentileFrameTimes()
    {
        var rng = new Random(7);
        var frames = Enumerable.Range(0, 2000).Select(_ => 8.0 + rng.NextDouble() * 10.0).ToList();
        var s = FrameTimeAnalyzer.Compute(frames);

        Assert.Equal(1000.0 / s.P99FrameTimeMs, s.P1LowFps, 6);
        Assert.Equal(1000.0 / s.P999FrameTimeMs, s.P01LowFps, 6);
    }

    [Fact]
    public void Stutter_CountsFramesOverTwiceMedian()
    {
        // nine 10 ms frames + one 100 ms spike ⇒ median 10, threshold 20, one stutter ⇒ 10%.
        var frames = Enumerable.Repeat(10.0, 9).Append(100.0).ToList();
        var s = FrameTimeAnalyzer.Compute(frames);
        Assert.Equal(10.0, s.MedianFrameTimeMs, 6);
        Assert.Equal(10.0, s.StutterPct, 6);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(50, 30)]
    [InlineData(100, 50)]
    [InlineData(25, 20)]
    [InlineData(75, 40)]
    public void Percentile_InterpolatesOnSortedData(double p, double expected)
    {
        var sorted = new[] { 10.0, 20.0, 30.0, 40.0, 50.0 };
        Assert.Equal(expected, FrameTimeAnalyzer.Percentile(sorted, p), 6);
    }

    // The tail-low sufficiency guard shared by the single-run paste and the A/B comparer — one threshold, so the
    // two surfaces can never disagree on when « 1% / 0,1% low » rest on too few frames.
    [Theory]
    [InlineData(0, false)]       // no data is an absence, not a "thin" run
    [InlineData(1, true)]
    [InlineData(999, true)]
    [InlineData(1000, false)]    // at the floor the tail lows are trustworthy
    [InlineData(5000, false)]
    public void TailLowsAreThin_FlagsRunsBelowTheThousandFrameFloor(int frames, bool expectedThin)
        => Assert.Equal(expectedThin, FrameSampleAdequacy.TailLowsAreThin(frames));
}
