using System;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the honesty surface of <see cref="JournalInsights.Compute"/> — the pure aggregate behind the Journal page's
/// "Synthèse" card. It counts what the trail RECORDED (apply vs revert by the stored action, the tweak-change tallies,
/// the failures, the writes verification couldn't confirm) and ranks the tweaks most often left unconfirmed — a real
/// count of recorded events, never a derived "reliability". An empty journal reports no activity (no fabricated zero
/// run), the failure / unconfirmed clauses appear in the summary ONLY when they really happened, ids group
/// case-insensitively (matching the catalogue) keeping the first-seen spelling, the ranking is capped and
/// deterministically ordered, and a blank id is tolerated (a total function over possibly hand-edited data) rather
/// than ranked. Pure (no store); the file read is untested glue in <see cref="ApplyJournal"/>.
/// </summary>
public class JournalInsightsTests
{
    private static readonly DateTime Fixed = new(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc);

    private static JournalEntry Apply(int succeeded = 1, int failed = 0, string[]? unconfirmed = null, DateTime? at = null)
        => new(at ?? Fixed, "Application", succeeded, failed, Array.Empty<string>(), unconfirmed ?? Array.Empty<string>());

    private static JournalEntry Revert(int succeeded = 1, int failed = 0, DateTime? at = null)
        => new(at ?? Fixed, "Restauration", succeeded, failed, Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void Compute_OnAnEmptyJournal_ReportsNoActivity()
    {
        var stats = JournalInsights.Compute(Array.Empty<JournalEntry>());

        Assert.False(stats.HasActivity);
        Assert.Equal(0, stats.TotalBatches);
        Assert.Empty(stats.MostUnconfirmed);
        Assert.Null(stats.FirstActivityUtc);
        Assert.Null(stats.LastActivityUtc);
        Assert.Equal("Aucune activité enregistrée.", stats.Summary);
    }

    [Fact]
    public void Compute_ClassifiesBatches_ByTheirRecordedAction()
    {
        var stats = JournalInsights.Compute(new[] { Apply(), Apply(), Revert() });

        Assert.Equal(3, stats.TotalBatches);
        Assert.Equal(2, stats.ApplyBatches);
        Assert.Equal(1, stats.RevertBatches);
        Assert.True(stats.HasActivity);
    }

    [Fact]
    public void Compute_SumsSucceeded_IntoAppliedVsReverted_ByAction()
    {
        var stats = JournalInsights.Compute(new[] { Apply(succeeded: 5), Apply(succeeded: 3), Revert(succeeded: 4) });

        Assert.Equal(8, stats.TotalApplied);
        Assert.Equal(4, stats.TotalReverted);
    }

    [Fact]
    public void Compute_SumsFailuresAndUnconfirmed_AcrossEveryBatch()
    {
        var stats = JournalInsights.Compute(new[]
        {
            Apply(succeeded: 2, failed: 1, unconfirmed: new[] { "a", "b" }),
            Apply(succeeded: 1, failed: 2, unconfirmed: new[] { "a" }),
            Revert(succeeded: 1)
        });

        Assert.Equal(3, stats.TotalFailures);       // 1 + 2 + 0
        Assert.Equal(3, stats.TotalUnconfirmed);    // 2 + 1 + 0
    }

    [Fact]
    public void Compute_RanksTheMostOftenUnconfirmed_DescendingByCount()
    {
        // "a" flagged in 3 batches, "c" in 2, "b" in 1 → a, c, b.
        var stats = JournalInsights.Compute(new[]
        {
            Apply(unconfirmed: new[] { "a", "b", "c" }),
            Apply(unconfirmed: new[] { "a", "c" }),
            Apply(unconfirmed: new[] { "a" })
        });

        Assert.Collection(stats.MostUnconfirmed,
            f => { Assert.Equal("a", f.TweakId); Assert.Equal(3, f.Count); },
            f => { Assert.Equal("c", f.TweakId); Assert.Equal(2, f.Count); },
            f => { Assert.Equal("b", f.TweakId); Assert.Equal(1, f.Count); });
    }

    [Fact]
    public void Compute_BreaksCountTies_ByIdAscending_SoTheRankingIsStable()
    {
        var stats = JournalInsights.Compute(new[] { Apply(unconfirmed: new[] { "zeta", "alpha" }) });

        Assert.Equal(new[] { "alpha", "zeta" }, stats.MostUnconfirmed.Select(f => f.TweakId));
    }

    [Fact]
    public void Compute_CountsUnconfirmedCaseInsensitively_KeepingFirstSeenSpelling()
    {
        var stats = JournalInsights.Compute(new[]
        {
            Apply(unconfirmed: new[] { "HPET" }),
            Apply(unconfirmed: new[] { "hpet" })
        });

        var only = Assert.Single(stats.MostUnconfirmed);
        Assert.Equal("HPET", only.TweakId);     // first-seen casing wins, not coerced
        Assert.Equal(2, only.Count);
        Assert.Equal("HPET — 2×", only.Label);
    }

    [Fact]
    public void Compute_CapsTheRanking_ToTheRequestedTop()
    {
        // Four distinct ids, all count 1 → tie broken by id asc, then capped to the top 2.
        var stats = JournalInsights.Compute(new[] { Apply(unconfirmed: new[] { "d", "a", "c", "b" }) }, topUnconfirmed: 2);

        Assert.Equal(new[] { "a", "b" }, stats.MostUnconfirmed.Select(f => f.TweakId));
    }

    [Fact]
    public void Compute_TracksTheActivitySpan_FromOldestToNewest()
    {
        var t1 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 6, 18, 9, 0, 0, DateTimeKind.Utc);

        // Fed out of order on purpose: the span is min/max, not the first/last row.
        var stats = JournalInsights.Compute(new[] { Apply(at: t2), Apply(at: t1), Revert(at: t3) });

        Assert.Equal(t1, stats.FirstActivityUtc);
        Assert.Equal(t3, stats.LastActivityUtc);
    }

    [Fact]
    public void Compute_Summary_ShowsFailureAndUnconfirmedClauses_OnlyWhenTheyOccur()
    {
        var clean = JournalInsights.Compute(new[] { Apply(succeeded: 2), Revert(succeeded: 1) });
        Assert.Contains("2 lot(s)", clean.Summary);
        Assert.Contains("1 application(s), 1 restauration(s)", clean.Summary);
        Assert.DoesNotContain("échec", clean.Summary);
        Assert.DoesNotContain("non confirmé", clean.Summary);

        var dirty = JournalInsights.Compute(new[] { Apply(succeeded: 1, failed: 1, unconfirmed: new[] { "x" }) });
        Assert.Contains("1 échec(s)", dirty.Summary);
        Assert.Contains("1 non confirmé(s)", dirty.Summary);
    }

    [Fact]
    public void Compute_IgnoresBlankUnconfirmedIds_RatherThanRankingThem()
    {
        // A hand-edited / degenerate row can carry a blank id; it can't name a tweak, so it never becomes a ranked
        // row — but it still counts toward the raw total, which mirrors exactly what that entry's own line shows.
        var stats = JournalInsights.Compute(new[] { Apply(unconfirmed: new[] { "", "  ", "real" }) });

        Assert.Equal("real", Assert.Single(stats.MostUnconfirmed).TweakId);
        Assert.Equal(3, stats.TotalUnconfirmed);
    }
}
