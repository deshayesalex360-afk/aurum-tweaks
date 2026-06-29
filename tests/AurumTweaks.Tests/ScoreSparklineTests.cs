using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the score-sparkline projection's honesty invariants: the vertical scale is a FIXED 0-100 (a small move stays
/// visually small — the line never auto-zooms to exaggerate it), X is even chronological spacing oldest→newest, and
/// fewer than two points draws nothing (no fabricated line from a single reading). Pure geometry — no render pass.
/// </summary>
public class ScoreSparklineTests
{
    private static ScoreSnapshot Snap(int score) => new(DateTime.UtcNow, score);

    private static IReadOnlyList<ScoreSnapshot> Series(params int[] scores)
        => scores.Select(Snap).ToArray();

    [Fact]
    public void Project_FewerThanTwoSamples_DrawsNothing()
    {
        // A single point is not a trend, and zero points is not a line — the caller hides the sparkline instead.
        Assert.Empty(ScoreSparkline.Project(Array.Empty<ScoreSnapshot>(), 200, 40));
        Assert.Empty(ScoreSparkline.Project(Series(80), 200, 40));
    }

    [Fact]
    public void Project_NonPositiveBox_DrawsNothing()
    {
        Assert.Empty(ScoreSparkline.Project(Series(40, 80), 0, 40));
        Assert.Empty(ScoreSparkline.Project(Series(40, 80), 200, 0));
    }

    [Fact]
    public void Project_SpacesPointsEvenly_OldestLeft_NewestRight()
    {
        var pts = ScoreSparkline.Project(Series(10, 50, 90), width: 200, height: 40);

        Assert.Equal(3, pts.Count);
        Assert.Equal(0, pts[0].X, 6);     // oldest pinned to the left edge
        Assert.Equal(100, pts[1].X, 6);   // evenly spaced
        Assert.Equal(200, pts[^1].X, 6);  // newest pinned to the right edge
    }

    [Fact]
    public void Project_MapsScoreToHeight_FixedZeroToHundred_AndInverts()
    {
        // height 100, padding 0: a perfect score sits at the top (Y=0), zero at the bottom (Y=100), half in the middle.
        var pts = ScoreSparkline.Project(Series(100, 0, 50), width: 300, height: 100);

        Assert.Equal(0, pts[0].Y, 6);    // 100 → top
        Assert.Equal(100, pts[1].Y, 6);  // 0 → bottom
        Assert.Equal(50, pts[2].Y, 6);   // 50 → middle
    }

    [Fact]
    public void Project_DoesNotAutoZoom_SoASmallMoveStaysSmall()
    {
        // THE honesty pin: 75→78 is a 3-point move. On a fixed 0-100 scale over a 100px box that is a 3px rise —
        // NOT stretched to fill the height the way a min/max auto-zoom would. The exact magnitude lives in the text;
        // the line must not over-dramatize it.
        var pts = ScoreSparkline.Project(Series(75, 78), width: 200, height: 100);

        Assert.Equal(25, pts[0].Y, 6);  // 100*(1-0.75)
        Assert.Equal(22, pts[1].Y, 6);  // 100*(1-0.78)
        Assert.Equal(3, Math.Abs(pts[0].Y - pts[1].Y), 6);
    }

    [Fact]
    public void Project_FlatScore_ProducesAHorizontalLine()
    {
        // A score that never moved is an honest flat line — same height across, X still advancing.
        var pts = ScoreSparkline.Project(Series(60, 60, 60), width: 200, height: 40);

        Assert.All(pts, p => Assert.Equal(pts[0].Y, p.Y, 6));
        Assert.True(pts[0].X < pts[1].X && pts[1].X < pts[2].X);
    }

    [Fact]
    public void Project_ClampsOutOfRangeScores_ToTheFixedScale()
    {
        // Scores are 0-100 by construction, but a defensive clamp keeps a stray value from flying off the box.
        var pts = ScoreSparkline.Project(Series(140, -20), width: 200, height: 100);

        Assert.Equal(0, pts[0].Y, 6);    // clamped to 100 → top
        Assert.Equal(100, pts[1].Y, 6);  // clamped to 0 → bottom
    }

    [Fact]
    public void Project_HonoursPadding_SoAThickStrokeStaysOffTheEdges()
    {
        // padding insets every side, so a point at score 100 (top) or 0 (bottom) doesn't get clipped to half-stroke.
        var pts = ScoreSparkline.Project(Series(100, 0), width: 200, height: 100, padding: 4);

        Assert.Equal(4, pts[0].X, 6);          // left edge + padding
        Assert.Equal(196, pts[^1].X, 6);       // right edge - padding
        Assert.Equal(4, pts[0].Y, 6);          // 100 → top + padding
        Assert.Equal(96, pts[1].Y, 6);         // 0 → bottom - padding
    }
}
