using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the optimization-score timeline's pure rules — the honesty/efficiency invariants the on-disk store leans
/// on: an unchanged score is NOT re-recorded (the same reference comes back, so the store skips the write and the
/// « depuis le … » date tracks the last real movement), the history is bounded oldest-first, and a trend is never
/// fabricated from a single point. Verified without touching disk, exactly like the JournalLog pure core.
/// </summary>
public class ScoreHistoryTests
{
    private static ScoreSnapshot At(int daysAgo, int score) => new(DateTime.UtcNow.AddDays(-daysAgo), score);

    // ---- Record: append, dedupe, bound ----

    [Fact]
    public void Record_AppendsDistinctScore_NewestAtTail()
    {
        var updated = ScoreHistory.Record(new[] { At(2, 60) }, At(0, 75));

        Assert.Equal(2, updated.Count);
        Assert.Equal(60, updated[0].Score);    // oldest first
        Assert.Equal(75, updated[^1].Score);   // newest at the tail
    }

    [Fact]
    public void Record_FirstEverSample_IsKept()
    {
        var updated = ScoreHistory.Record(Array.Empty<ScoreSnapshot>(), At(0, 50));

        Assert.Equal(50, Assert.Single(updated).Score);
    }

    [Fact]
    public void Record_UnchangedScore_ReturnsSameReference_SoTheStoreSkipsTheWrite()
    {
        // The store keys its "don't touch disk" decision on reference equality — a repeated identical score must
        // come back as the very same instance, not an equal copy.
        IReadOnlyList<ScoreSnapshot> existing = new[] { At(1, 80) };
        var updated = ScoreHistory.Record(existing, At(0, 80));

        Assert.Same(existing, updated);
    }

    [Fact]
    public void Record_OnlyDedupesTheAdjacentScore_NotAValueSeenEarlier()
    {
        // 80 appeared before, but the most recent point is 70, so recording 80 again IS a real change — the no-op
        // rule only fires when the new score equals the LAST point.
        var updated = ScoreHistory.Record(new[] { At(2, 80), At(1, 70) }, At(0, 80));

        Assert.Equal(3, updated.Count);
        Assert.Equal(80, updated[^1].Score);
    }

    [Fact]
    public void Record_BoundsToCap_DroppingTheOldest()
    {
        IReadOnlyList<ScoreSnapshot> series = Array.Empty<ScoreSnapshot>();
        for (int i = 0; i < 5; i++)
            series = ScoreHistory.Record(series, new ScoreSnapshot(DateTime.UtcNow.AddMinutes(i), i), cap: 3);

        // Distinct ascending scores 0..4 capped at 3 → only the newest three survive, oldest-first.
        Assert.Equal(new[] { 2, 3, 4 }, series.Select(s => s.Score));
    }

    // ---- Summarize: a value is not a trend ----

    [Fact]
    public void Summarize_FewerThanTwoSamples_HasNoTrend()
    {
        Assert.Same(ScoreProgress.None, ScoreHistory.Summarize(Array.Empty<ScoreSnapshot>()));
        Assert.False(ScoreHistory.Summarize(new[] { At(0, 90) }).HasTrend);
    }

    [Fact]
    public void Summarize_Improvement_ReportsPositiveDeltaAndDirection()
    {
        var p = ScoreHistory.Summarize(new[] { At(3, 62), At(0, 75) });

        Assert.True(p.HasTrend);
        Assert.True(p.IsImprovement);
        Assert.False(p.IsRegression);
        Assert.Equal(13, p.Delta);
        Assert.Equal("+13", p.DeltaLabel);
        Assert.Equal("En hausse", p.DirectionLabel);
        Assert.Equal(75, p.Current);
        Assert.Equal(62, p.Previous);
    }

    [Fact]
    public void Summarize_Regression_ReportsNegativeDeltaAndDirection()
    {
        var p = ScoreHistory.Summarize(new[] { At(3, 80), At(0, 68) });

        Assert.True(p.IsRegression);
        Assert.False(p.IsImprovement);
        Assert.Equal(-12, p.Delta);
        Assert.Equal("-12", p.DeltaLabel);
        Assert.Equal("En baisse", p.DirectionLabel);
    }

    [Fact]
    public void Summarize_ComparesTheTwoMostRecent_IgnoringOlderHistory()
    {
        // Older points exist, but the trend is strictly latest-vs-previous.
        var p = ScoreHistory.Summarize(new[] { At(9, 10), At(2, 70), At(0, 72) });

        Assert.Equal(2, p.Delta);
        Assert.Equal(70, p.Previous);
        Assert.Equal(72, p.Current);
    }

    [Fact]
    public void Summarize_AnchorsSinceToThePreviousReading()
    {
        var previous = At(5, 60);
        var p = ScoreHistory.Summarize(new[] { previous, At(0, 90) });

        Assert.Equal(previous.TimestampUtc, p.SinceUtc);
    }

    [Fact]
    public void Summarize_DuplicateTail_ReadsAsStableNotMovement()
    {
        // Only reachable from legacy / hand-edited data (Record dedupes adjacent-equal scores). A zero delta must
        // read as honest stability, never "+0" dressed up as progress.
        var p = ScoreHistory.Summarize(new[] { At(2, 77), At(0, 77) });

        Assert.True(p.HasTrend);
        Assert.Equal(0, p.Delta);
        Assert.Equal("0", p.DeltaLabel);
        Assert.Equal("Score stable", p.DirectionLabel);
        Assert.False(p.IsImprovement);
        Assert.False(p.IsRegression);
    }

    // ---- TrendLine: the one shared headline (anti-drift between dashboard ring and report) ----

    [Fact]
    public void TrendLine_ComposesTheSharedHeadline_OrStaysEmptyWithoutATrend()
    {
        // Empty when there is nothing to say, so a caller can bind/append it without its own guard.
        Assert.Equal(string.Empty, ScoreProgress.None.TrendLine);

        // A real move carries the signed delta and its anchor date …
        var moved = ScoreHistory.Summarize(new[] { At(2, 62), At(0, 75) }).TrendLine;
        Assert.StartsWith("En hausse · +13 pts depuis le", moved);

        // … a flat reading states honest stability with the date but NO "+0 pts" dressed up as movement.
        var flat = ScoreHistory.Summarize(new[] { At(2, 50), At(0, 50) }).TrendLine;
        Assert.StartsWith("Score stable depuis le", flat);
        Assert.DoesNotContain("pts", flat);
    }
}
