using System;
using System.Collections.Generic;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the honesty rules of drift detection's two pure cores. <see cref="JournalApplyIntent.Resolve"/> may only
/// nominate a tweak as "supposed to be on" when its MOST RECENT journal touch was an apply that PROVED it live; a
/// later revert or a later failed re-apply must veto it. <see cref="DriftAnalysis.Detect"/> may only call a
/// candidate "drifted" when the machine now reads it explicitly off — an Indeterminate or unread id is never a
/// fabricated regression. Both cores are I/O-free, so the decisions are verified directly here.
/// </summary>
public class DriftDetectionTests
{
    private static JournalEntry Apply(string[] tweakIds, string[] confirmed)
        => new(DateTime.UtcNow, "Application", tweakIds.Length, 0, tweakIds, Array.Empty<string>()) { Confirmed = confirmed };

    private static JournalEntry CleanApply(params string[] ids) => Apply(ids, ids);

    private static JournalEntry Revert(params string[] ids)
        => new(DateTime.UtcNow, "Restauration", ids.Length, 0, ids, Array.Empty<string>());

    private static IReadOnlyDictionary<string, TweakAppliedState> States(
        params (string id, TweakAppliedState state)[] pairs)
    {
        var d = new Dictionary<string, TweakAppliedState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, state) in pairs) d[id] = state;
        return d;
    }

    // ---- JournalApplyIntent.Resolve ----

    [Fact]
    public void Resolve_ConfirmedApply_IsACandidate()
    {
        var intent = JournalApplyIntent.Resolve(new[] { CleanApply("a", "b") });
        Assert.Equal(new[] { "a", "b" }, intent);
    }

    [Fact]
    public void Resolve_AppliedButNotConfirmed_IsNotACandidate()
    {
        // "b" was attempted but never read back live (failed or read back wrong) → no proven-on baseline.
        var intent = JournalApplyIntent.Resolve(new[] { Apply(new[] { "a", "b" }, new[] { "a" }) });
        Assert.Equal(new[] { "a" }, intent);
    }

    [Fact]
    public void Resolve_RevertSupersedesAnEarlierConfirmedApply()
    {
        // Newest first: the revert of "a" is the latest word, so "a" is intended OFF — not a drift candidate.
        var intent = JournalApplyIntent.Resolve(new[] { Revert("a"), CleanApply("a") });
        Assert.Empty(intent);
    }

    [Fact]
    public void Resolve_ConfirmedApplySupersedesAnEarlierRevert()
    {
        var intent = JournalApplyIntent.Resolve(new[] { CleanApply("a"), Revert("a") });
        Assert.Equal(new[] { "a" }, intent);
    }

    [Fact]
    public void Resolve_NewerFailedReapplySupersedesAnEarlierSuccess()
    {
        // The latest apply listed "a" but didn't confirm it; we refuse to claim drift from a state the latest try
        // never re-established, even though an older batch did once confirm it.
        var intent = JournalApplyIntent.Resolve(new[]
        {
            Apply(new[] { "a" }, Array.Empty<string>()),
            CleanApply("a")
        });
        Assert.Empty(intent);
    }

    [Fact]
    public void Resolve_IdOnlyEverReverted_IsNotACandidate()
    {
        Assert.Empty(JournalApplyIntent.Resolve(new[] { Revert("a") }));
    }

    [Fact]
    public void Resolve_IgnoresBlankIdsAndEmptyJournal()
    {
        Assert.Empty(JournalApplyIntent.Resolve(Array.Empty<JournalEntry>()));
        var intent = JournalApplyIntent.Resolve(new[] { Apply(new[] { " ", "a" }, new[] { " ", "a" }) });
        Assert.Equal(new[] { "a" }, intent);
    }

    // ---- DriftAnalysis.Detect ----

    [Fact]
    public void Detect_CandidateNowOff_IsDrift()
    {
        var report = DriftAnalysis.Detect(new[] { "a" }, States(("a", TweakAppliedState.NotApplied)));
        Assert.True(report.HasDrift);
        Assert.Equal(new[] { "a" }, report.Drifted);
        Assert.Equal(0, report.PersistedCount);
        Assert.Equal(0, report.UnverifiableCount);
    }

    [Fact]
    public void Detect_CandidateStillOn_IsPersistedNotDrift()
    {
        var report = DriftAnalysis.Detect(new[] { "a" }, States(("a", TweakAppliedState.Applied)));
        Assert.False(report.HasDrift);
        Assert.Equal(1, report.PersistedCount);
    }

    [Fact]
    public void Detect_CandidateIndeterminate_IsUnverifiableNotDrift()
    {
        var report = DriftAnalysis.Detect(new[] { "a" }, States(("a", TweakAppliedState.Indeterminate)));
        Assert.False(report.HasDrift);
        Assert.Equal(1, report.UnverifiableCount);
    }

    [Fact]
    public void Detect_CandidateWithNoLiveReading_IsUnverifiableNotDrift()
    {
        var report = DriftAnalysis.Detect(new[] { "gone" }, States(("other", TweakAppliedState.Applied)));
        Assert.False(report.HasDrift);
        Assert.Equal(1, report.UnverifiableCount);
    }

    [Fact]
    public void Detect_MixedSet_BucketsEachHonestly()
    {
        var report = DriftAnalysis.Detect(
            new[] { "off", "on", "unknown" },
            States(("off", TweakAppliedState.NotApplied),
                   ("on", TweakAppliedState.Applied),
                   ("unknown", TweakAppliedState.Indeterminate)));
        Assert.Equal(new[] { "off" }, report.Drifted);
        Assert.Equal(1, report.PersistedCount);
        Assert.Equal(1, report.UnverifiableCount);
    }

    [Fact]
    public void Detect_NoCandidates_HasNoDrift()
    {
        var report = DriftAnalysis.Detect(Array.Empty<string>(), States(("a", TweakAppliedState.NotApplied)));
        Assert.False(report.HasDrift);
        Assert.Empty(report.Drifted);
        Assert.Equal(0, report.DriftedCount);
    }

    // ---- the two cores together: the honesty crux end-to-end ----

    [Fact]
    public void Pipeline_FailedApplyThatIsOff_IsNotMislabeledAsDrift()
    {
        // "a" was confirmed live then drifted off (real drift); "b" only ever failed to apply and is off (NOT drift —
        // it never had a proven-on baseline). Resolve must drop "b" before Detect ever sees it.
        var journal = new[] { Apply(new[] { "a", "b" }, new[] { "a" }) };
        var intent = JournalApplyIntent.Resolve(journal);
        var report = DriftAnalysis.Detect(intent,
            States(("a", TweakAppliedState.NotApplied), ("b", TweakAppliedState.NotApplied)));
        Assert.Equal(new[] { "a" }, report.Drifted);
    }
}
