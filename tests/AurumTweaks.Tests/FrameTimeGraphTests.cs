using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="FrameTimeGraph"/> — the pure geometry behind the benchmark page's frame-time plot. The honesty
/// properties: the axis is truthful (0 ms at the bottom, the run's max at the top, spikes UP, clamped never off-canvas),
/// a degenerate run draws NOTHING rather than a fabricated line, and — the load-bearing one — a single-frame spike
/// SURVIVES downsampling. A long run has more frames than the plot has pixels, so frames are bucketed; if each bucket
/// were averaged, the very stutters the plot exists to show would vanish into the baseline. The min/max envelope keeps
/// them, and this test proves it.
/// </summary>
public class FrameTimeGraphTests
{
    // --- Y mapping: linear onto a 0 → yMax axis, baseline at the bottom, spikes up, clamped ---

    [Theory]
    [InlineData(0, 10, 200, 200)]    // 0 ms → baseline (bottom edge)
    [InlineData(10, 10, 200, 0)]     // == yMax → top edge
    [InlineData(5, 10, 200, 100)]    // half → middle
    [InlineData(2.5, 10, 200, 150)]
    [InlineData(20, 10, 200, 0)]     // above the axis → clamped to the top, never off-canvas
    public void Y_MapsFrameTimeOntoTheAxis_SpikesUp(double ms, double yMax, double height, double expected)
        => Assert.Equal(expected, FrameTimeGraph.Y(ms, yMax, height), 9);

    [Fact]
    public void Y_NonPositiveAxis_IsTheBaseline_NeverDividesByZero()
        => Assert.Equal(200, FrameTimeGraph.Y(16, 0, 200));

    // --- Degenerate inputs draw nothing (an empty plot, never an invented line) ---

    [Fact]
    public void NoUsableInput_DrawsNothing()
    {
        Assert.Empty(FrameTimeGraph.BuildEnvelope(Array.Empty<double>(), 10));
        Assert.Empty(FrameTimeGraph.BuildEnvelope(null!, 10));
        Assert.Empty(FrameTimeGraph.BuildEnvelope(new[] { 8.0, 9.0 }, 0));   // no axis → nothing to scale onto
    }

    [Fact]
    public void SingleFrame_IsCentred_AtItsMappedHeight()
    {
        var p = Assert.Single(FrameTimeGraph.BuildEnvelope(new[] { 10.0 }, 10, width: 1000, height: 200));
        Assert.Equal(500, p.X);   // centred — a lone frame has no span
        Assert.Equal(0, p.Y);     // == yMax → top
    }

    // --- Sparse (frames ≤ columns): one point per frame, left-to-right, mapped correctly ---

    [Fact]
    public void Sparse_OnePointPerFrame_EvenlySpacedAcrossTheWidth()
    {
        var pts = FrameTimeGraph.BuildEnvelope(new[] { 5.0, 10.0 }, 10, width: 1000, height: 200);
        Assert.Equal(2, pts.Count);
        Assert.Equal(new GraphPoint(0, 100), pts[0]);      // 5 ms → middle, at the left edge
        Assert.Equal(new GraphPoint(1000, 0), pts[1]);     // 10 ms → top, at the right edge
    }

    // --- Dense (frames > columns): the spike-preserving envelope ---

    [Fact]
    public void Dense_SingleFrameSpike_IsPreserved_NotAveragedAway()
    {
        var frames = Enumerable.Repeat(8.0, 5000).ToList();
        frames[2500] = 100.0;   // one huge hitch buried in an otherwise flat 8 ms run

        var pts = FrameTimeGraph.BuildEnvelope(frames, 100.0, width: 1000, height: 200);

        Assert.NotEmpty(pts);
        Assert.True(pts.Count <= 2 * 1000);     // bounded work no matter how many frames were captured
        Assert.Contains(pts, p => p.Y == 0);    // the 100 ms spike reaches the very top — kept, never hidden
        Assert.Contains(pts, p => p.Y > 150);   // the 8 ms baseline band is drawn low, as it should be
    }

    [Fact]
    public void AllPoints_StayWithinTheViewport_AndRunLeftToRight()
    {
        var frames = Enumerable.Range(0, 4000).Select(i => 8.0 + i % 7).ToList();   // > width → dense
        var pts = FrameTimeGraph.BuildEnvelope(frames, 20.0, width: 1000, height: 200);

        Assert.All(pts, p =>
        {
            Assert.InRange(p.X, 0, 1000 + 1e-6);
            Assert.InRange(p.Y, 0, 200);
        });
        for (int i = 1; i < pts.Count; i++)
            Assert.True(pts[i].X >= pts[i - 1].X, "X must be non-decreasing (the plot runs left-to-right).");
    }

    // --- A/B overlay: both runs on ONE shared axis so the comparison is fair (not flattered) ---

    [Fact]
    public void Overlay_BothRunsShareOneAxis_TheLargerOfTheTwoMaxima()
    {
        // « Avant » has the bigger spike (50 ms); « Après » tops out at 20 ms. The shared axis must be 50, so the
        // Après peak sits visibly BELOW the top — not rescaled to also touch it, which would hide the improvement.
        var before = Enumerable.Repeat(16.0, 300).ToList(); before[150] = 50.0;
        var after = Enumerable.Repeat(16.0, 300).ToList(); after[150] = 20.0;

        var ov = FrameTimeGraph.BuildOverlay(before, after, width: 1000, height: 200);

        Assert.Equal(50.0, ov.YMaxMs);
        Assert.Contains(ov.Before, p => p.Y == 0);        // the 50 ms Avant spike reaches the top
        Assert.DoesNotContain(ov.After, p => p.Y == 0);   // the 20 ms Après spike does NOT — it really is lower
        Assert.Contains(ov.After, p => p.Y > 0);
    }

    [Fact]
    public void Overlay_NeitherRunHasFrames_DrawsNothing()
    {
        var ov = FrameTimeGraph.BuildOverlay(Array.Empty<double>(), Array.Empty<double>());
        Assert.Equal(0, ov.YMaxMs);
        Assert.Empty(ov.Before);
        Assert.Empty(ov.After);
    }

    [Fact]
    public void Overlay_OneRunEmpty_StillDrawsTheOther_OnItsOwnMax()
    {
        var ov = FrameTimeGraph.BuildOverlay(new[] { 10.0, 30.0, 12.0 }, Array.Empty<double>(), width: 1000, height: 200);
        Assert.Equal(30.0, ov.YMaxMs);
        Assert.NotEmpty(ov.Before);
        Assert.Empty(ov.After);
    }
}
