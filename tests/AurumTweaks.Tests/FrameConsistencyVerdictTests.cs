using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="FrameConsistencyVerdict"/> — the benchmark page's frame-time REGULARITY verdict (the missing twin of
/// <see cref="LatencyVerdict"/>; every other diagnostic in the app already ships one). Honesty contract:
/// <list type="bullet">
/// <item>errors-first — no usable run, or fewer than <see cref="FrameConsistencyVerdict.MinFrames"/> frames (too few for
/// a real « 1% low »), yields Unknown, never a fabricated « Régulière »;</item>
/// <item>the WORSE of two scale-free signals drives the level — the 1%-low/average ratio AND the stutter rate — so an
/// otherwise-tight run is still flagged when isolated stutters pile up, and a stutter-driven verdict never claims the
/// lows decoupled;</item>
/// <item>it judges SMOOTHNESS only: a smooth 45 fps scores « Régulière » exactly like a smooth 240 fps — the verdict
/// never reads the FPS level, which depends on the game and the monitor.</item>
/// </list>
/// Pure arithmetic over a <see cref="FrameTimeStats"/>, so it is a deterministic value table.
/// </summary>
public class FrameConsistencyVerdictTests
{
    private static FrameTimeStats Stats(int frames = 1200, double avgFps = 120, double p1Low = 110, double stutterPct = 0.3)
        => new() { FrameCount = frames, AvgFps = avgFps, P1LowFps = p1Low, StutterPct = stutterPct };

    // --- Errors-first: never pronounce off a sample that can't support a verdict ---

    [Fact]
    public void NullStats_AreUnknown_NotAFabricatedVerdict()
    {
        var v = FrameConsistencyVerdict.Evaluate(null!);
        Assert.Equal(FrameConsistencyLevel.Unknown, v.Level);
        Assert.Equal(0, v.LowToAvgRatio);
    }

    [Fact]
    public void NoFrames_AreUnknown()
        => Assert.Equal(FrameConsistencyLevel.Unknown, FrameConsistencyVerdict.Evaluate(FrameTimeStats.Empty).Level);

    [Fact]
    public void ZeroAverageFps_IsUnknown_NeverDividesIntoAFakeRatio()
        => Assert.Equal(FrameConsistencyLevel.Unknown, FrameConsistencyVerdict.Evaluate(Stats(avgFps: 0, p1Low: 0)).Level);

    [Fact]
    public void TooFewFrames_DeclineToPronounce_NamingTheCount()
    {
        var v = FrameConsistencyVerdict.Evaluate(Stats(frames: 50));   // < MinFrames, otherwise a perfect run
        Assert.Equal(FrameConsistencyLevel.Unknown, v.Level);
        Assert.Contains("Trop peu de frames", v.Message);
        Assert.Contains("50", v.Message);
        Assert.Equal("Indéterminée", v.Label);
    }

    [Fact]
    public void AtTheFrameFloor_AVerdictIsAllowed()
        => Assert.NotEqual(FrameConsistencyLevel.Unknown,
            FrameConsistencyVerdict.Evaluate(Stats(frames: FrameConsistencyVerdict.MinFrames)).Level);

    // --- The ratio drives the level (stutter held low so it doesn't escalate) ---

    [Theory]
    [InlineData(100, 85, FrameConsistencyLevel.Smooth)]      // ratio 0.85 → exactly at the smooth threshold (inclusive)
    [InlineData(100, 84.9, FrameConsistencyLevel.Moderate)]  // just under → correct
    [InlineData(100, 70, FrameConsistencyLevel.Moderate)]    // ratio 0.70 → exactly at the moderate threshold (inclusive)
    [InlineData(100, 69.9, FrameConsistencyLevel.Choppy)]    // just under → choppy
    [InlineData(120, 110, FrameConsistencyLevel.Smooth)]     // 0.917 → comfortably smooth
    [InlineData(120, 50, FrameConsistencyLevel.Choppy)]      // 0.417 → the lows fall away from the mean
    public void Ratio_DrivesTheLevel(double avgFps, double p1Low, FrameConsistencyLevel expected)
        => Assert.Equal(expected, FrameConsistencyVerdict.Evaluate(Stats(avgFps: avgFps, p1Low: p1Low, stutterPct: 0.2)).Level);

    // --- The stutter rate drives the level (ratio held high so only stutter can escalate) ---

    [Theory]
    [InlineData(0.9, FrameConsistencyLevel.Smooth)]    // < 1 % → rare
    [InlineData(1.0, FrameConsistencyLevel.Moderate)]  // 1 % is NOT « rare » (strict <) → escalates a tight run
    [InlineData(4.9, FrameConsistencyLevel.Moderate)]
    [InlineData(5.0, FrameConsistencyLevel.Choppy)]    // ≥ 5 % of frames over 2× median → choppy on its own
    public void Stutter_EscalatesEvenWhenTheRatioIsExcellent(double stutterPct, FrameConsistencyLevel expected)
        => Assert.Equal(expected, FrameConsistencyVerdict.Evaluate(Stats(avgFps: 100, p1Low: 95, stutterPct: stutterPct)).Level);

    [Fact]
    public void WorstOfTheTwoSignalsWins_AndDoesNotClaimTheLowsDecoupled_WhenItsStutterDriven()
    {
        // Lows hug the mean (0.96, would be Smooth) but 8 % of frames stutter → overall Choppy. The message must not
        // assert the 1% low « décroche nettement » (it doesn't); the « et/ou … saccades » phrasing stays honest.
        var v = FrameConsistencyVerdict.Evaluate(Stats(avgFps: 120, p1Low: 115, stutterPct: 8.0));
        Assert.Equal(FrameConsistencyLevel.Choppy, v.Level);
        Assert.Contains("saccades", v.Message);
    }

    // --- Ratio reporting / clamp ---

    [Fact]
    public void Ratio_IsReported()
        => Assert.Equal(0.917, FrameConsistencyVerdict.Evaluate(Stats(avgFps: 120, p1Low: 110)).LowToAvgRatio, 3);

    [Fact]
    public void LowAboveAverage_IsClampedToOne_NotShownAsBetterThanTheMean()
    {
        // A heavy-tail artefact can put the 1% low above the average; clamp to 1.0 so the message reads « 100 % », not
        // a nonsensical « 110 % du FPS moyen ». Still Smooth.
        var v = FrameConsistencyVerdict.Evaluate(Stats(avgFps: 100, p1Low: 110, stutterPct: 0.2));
        Assert.Equal(FrameConsistencyLevel.Smooth, v.Level);
        Assert.Equal(1.0, v.LowToAvgRatio, 3);
        Assert.Contains("100 %", v.Message);
    }

    // --- Honesty boundary: regularity, NOT FPS adequacy ---

    [Fact]
    public void Smooth_SaysSoIsNotAnFpsJudgement()
        => Assert.Contains("ne juge pas le niveau de FPS",
            FrameConsistencyVerdict.Evaluate(Stats(avgFps: 120, p1Low: 110)).Message);

    [Fact]
    public void SameSmoothness_ScoresTheSame_RegardlessOfTheFpsLevel()
    {
        // A smooth 45 fps and a smooth 240 fps both hold the same lows-to-mean ratio → same level. The verdict is blind
        // to the absolute FPS on purpose.
        var slow = FrameConsistencyVerdict.Evaluate(Stats(avgFps: 45, p1Low: 41, stutterPct: 0.2));
        var fast = FrameConsistencyVerdict.Evaluate(Stats(avgFps: 240, p1Low: 218, stutterPct: 0.2));
        Assert.Equal(FrameConsistencyLevel.Smooth, slow.Level);
        Assert.Equal(fast.Level, slow.Level);
    }

    // --- Label mapping (shared by the badge and the report) ---

    [Theory]
    [InlineData(FrameConsistencyLevel.Smooth, "Régulière")]
    [InlineData(FrameConsistencyLevel.Moderate, "Correcte")]
    [InlineData(FrameConsistencyLevel.Choppy, "Irrégulière")]
    [InlineData(FrameConsistencyLevel.Unknown, "Indéterminée")]
    public void Label_MapsEachLevel(FrameConsistencyLevel level, string expected)
        => Assert.Equal(expected, FrameConsistencyVerdict.Label(level));
}
