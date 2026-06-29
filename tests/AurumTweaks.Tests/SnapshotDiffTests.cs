using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the honesty rule of <see cref="SnapshotDiff.Compare"/>: a regression (the whole feature's reason to exist)
/// is asserted ONLY when both ends are readable — genuinely Applied before, genuinely NotApplied now. Any transition
/// touching <see cref="TweakAppliedState.Indeterminate"/> is "uncertain", NEVER a claimed regression: we refuse to
/// raise a false alarm when one side couldn't be read back. Catalogue churn (a tweak present on only one side) is
/// added/removed, kept apart from a real state change. Pure (no I/O), so the classification is verified directly.
/// </summary>
public class SnapshotDiffTests
{
    private static SnapshotEntry E(string id, TweakAppliedState state, string? name = null)
        => new(id, name ?? id, state);

    private static SystemSnapshot Snap(params SnapshotEntry[] entries)
        => new() { Entries = entries.ToList() };

    [Fact]
    public void AppliedThenNotApplied_IsRegression_NotUncertain()
    {
        var c = SnapshotDiff.Compare(
            Snap(E("t", TweakAppliedState.Applied)),
            Snap(E("t", TweakAppliedState.NotApplied)));

        var reg = Assert.Single(c.Regressions);
        Assert.Equal("t", reg.TweakId);
        Assert.Equal(TweakAppliedState.Applied, reg.From);
        Assert.Equal(TweakAppliedState.NotApplied, reg.To);
        Assert.True(c.HasRegressions);
        Assert.Empty(c.Uncertain);
        Assert.Empty(c.Improvements);
    }

    [Fact]
    public void NotAppliedThenApplied_IsImprovement()
    {
        var c = SnapshotDiff.Compare(
            Snap(E("t", TweakAppliedState.NotApplied)),
            Snap(E("t", TweakAppliedState.Applied)));

        var imp = Assert.Single(c.Improvements);
        Assert.Equal("t", imp.TweakId);
        Assert.True(c.HasImprovements);
        Assert.Empty(c.Regressions);
    }

    [Fact]
    public void AppliedThenIndeterminate_IsUncertain_NeverRegression()
    {
        // The cardinal honesty case: we KNEW it was on, but now we can't read it back. That is NOT proof it
        // reverted — so it must land in "uncertain", and the alarming Regressions bucket stays empty.
        var c = SnapshotDiff.Compare(
            Snap(E("t", TweakAppliedState.Applied)),
            Snap(E("t", TweakAppliedState.Indeterminate)));

        Assert.Empty(c.Regressions);
        Assert.False(c.HasRegressions);
        var unc = Assert.Single(c.Uncertain);
        Assert.Equal("t", unc.TweakId);
    }

    [Fact]
    public void IndeterminateThenNotApplied_IsUncertain_NeverRegression()
    {
        // We never knew it was on (unreadable baseline), so its absence now can't be a confident regression.
        var c = SnapshotDiff.Compare(
            Snap(E("t", TweakAppliedState.Indeterminate)),
            Snap(E("t", TweakAppliedState.NotApplied)));

        Assert.Empty(c.Regressions);
        Assert.Single(c.Uncertain);
    }

    [Fact]
    public void IndeterminateThenApplied_IsUncertain_NeverImprovement()
    {
        var c = SnapshotDiff.Compare(
            Snap(E("t", TweakAppliedState.Indeterminate)),
            Snap(E("t", TweakAppliedState.Applied)));

        Assert.Empty(c.Improvements);
        Assert.Single(c.Uncertain);
    }

    [Fact]
    public void SameState_IsUnchanged_NoBuckets()
    {
        var c = SnapshotDiff.Compare(
            Snap(E("a", TweakAppliedState.Applied), E("b", TweakAppliedState.NotApplied)),
            Snap(E("a", TweakAppliedState.Applied), E("b", TweakAppliedState.NotApplied)));

        Assert.Equal(2, c.UnchangedCount);
        Assert.False(c.HasAnyChange);
    }

    [Fact]
    public void TweakOnlyInCurrent_IsAdded_WithNullFrom()
    {
        var c = SnapshotDiff.Compare(
            Snap(),
            Snap(E("new", TweakAppliedState.Applied)));

        var added = Assert.Single(c.Added);
        Assert.Equal("new", added.TweakId);
        Assert.Null(added.From);
        Assert.Equal(TweakAppliedState.Applied, added.To);
        Assert.True(c.HasAdded);
        Assert.Empty(c.Regressions);   // a brand-new tweak is never a regression
    }

    [Fact]
    public void TweakOnlyInBaseline_IsRemoved_WithNullTo()
    {
        var c = SnapshotDiff.Compare(
            Snap(E("gone", TweakAppliedState.Applied)),
            Snap());

        var removed = Assert.Single(c.Removed);
        Assert.Equal("gone", removed.TweakId);
        Assert.Equal(TweakAppliedState.Applied, removed.From);
        Assert.Null(removed.To);
        Assert.True(c.HasRemoved);
        Assert.Empty(c.Regressions);   // a tweak the catalogue dropped is "removed", not "reverted"
    }

    [Fact]
    public void Summary_WhenIdentical_SaysNoChange()
    {
        var c = SnapshotDiff.Compare(
            Snap(E("t", TweakAppliedState.Applied)),
            Snap(E("t", TweakAppliedState.Applied)));

        Assert.Equal("Aucun changement depuis l'instantané de référence.", c.Summary);
    }

    [Fact]
    public void Summary_ListsEachNonEmptyBucket_ThenUnchanged()
    {
        // 1 regression + 1 improvement + 1 unchanged → every non-empty clause, joined, with the unchanged tail.
        var c = SnapshotDiff.Compare(
            Snap(E("reg", TweakAppliedState.Applied),
                 E("imp", TweakAppliedState.NotApplied),
                 E("same", TweakAppliedState.Applied)),
            Snap(E("reg", TweakAppliedState.NotApplied),
                 E("imp", TweakAppliedState.Applied),
                 E("same", TweakAppliedState.Applied)));

        Assert.Equal("1 régression(s) · 1 amélioration(s) · 1 inchangé(s)", c.Summary);
    }

    [Fact]
    public void Regressions_AreOrderedByNameThenId_Deterministically()
    {
        // HashSet iteration is unordered; the diff must impose a stable (name, id) order so the UI list and the
        // tests never flake. Names chosen so alphabetical name order ("Alpha","Beta","Zulu") differs from id order.
        var c = SnapshotDiff.Compare(
            Snap(E("z", TweakAppliedState.Applied, "Alpha"),
                 E("a", TweakAppliedState.Applied, "Zulu"),
                 E("m", TweakAppliedState.Applied, "Beta")),
            Snap(E("z", TweakAppliedState.NotApplied, "Alpha"),
                 E("a", TweakAppliedState.NotApplied, "Zulu"),
                 E("m", TweakAppliedState.NotApplied, "Beta")));

        Assert.Equal(new[] { "Alpha", "Beta", "Zulu" }, c.Regressions.Select(r => r.TweakName).ToArray());
    }

    [Fact]
    public void DuplicateIds_DoNotThrow_LastOccurrenceWins()
    {
        // A snapshot file is persisted and could be hand-edited: a duplicate id must never crash the diff. The
        // defensive indexer build keeps Compare a total function — last write wins, exactly like the real maps.
        var baseline = Snap(E("t", TweakAppliedState.NotApplied), E("t", TweakAppliedState.Applied)); // last = Applied
        var current = Snap(E("t", TweakAppliedState.NotApplied));

        var c = SnapshotDiff.Compare(baseline, current);

        Assert.Single(c.Regressions);   // Applied (last in baseline) → NotApplied = a regression, no exception
    }

    [Fact]
    public void MixedScenario_SortsEveryEntryIntoTheRightBucket()
    {
        var c = SnapshotDiff.Compare(
            Snap(E("reg", TweakAppliedState.Applied),
                 E("imp", TweakAppliedState.NotApplied),
                 E("unc", TweakAppliedState.Applied),
                 E("same", TweakAppliedState.NotApplied),
                 E("gone", TweakAppliedState.Applied)),
            Snap(E("reg", TweakAppliedState.NotApplied),
                 E("imp", TweakAppliedState.Applied),
                 E("unc", TweakAppliedState.Indeterminate),
                 E("same", TweakAppliedState.NotApplied),
                 E("added", TweakAppliedState.Applied)));

        Assert.Equal("reg", Assert.Single(c.Regressions).TweakId);
        Assert.Equal("imp", Assert.Single(c.Improvements).TweakId);
        Assert.Equal("unc", Assert.Single(c.Uncertain).TweakId);
        Assert.Equal("added", Assert.Single(c.Added).TweakId);
        Assert.Equal("gone", Assert.Single(c.Removed).TweakId);
        Assert.Equal(1, c.UnchangedCount);
    }
}
