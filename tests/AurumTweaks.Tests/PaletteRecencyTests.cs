using System;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure recency reorder behind the palette's empty-query launcher view: recent pages float to the top in
/// recency order, only Page rows are promotable (recency is page-nav history), unmatched/stale keys are ignored,
/// and the result is always a permutation of the input — never a dropped or duplicated row.
/// </summary>
public class PaletteRecencyTests
{
    private static PaletteEntry Pg(string id) => new(id, id, "Page", PaletteEntryKind.Page);
    private static PaletteEntry Ac(string id) => new(id, id, "Action", PaletteEntryKind.Action);
    private static PaletteEntry Tw(string id) => new(id, id, "Tweak", PaletteEntryKind.Tweak);

    [Fact]
    public void NoRecentKeys_LeavesOrderUnchanged()
    {
        var entries = new[] { Pg("A"), Pg("B"), Pg("C") };

        var result = PaletteRecency.PrioritizeRecent(entries, Array.Empty<string>());

        Assert.Equal(new[] { "A", "B", "C" }, result.Select(e => e.Id));
    }

    [Fact]
    public void RecentPages_FloatToTop_InRecencyOrder()
    {
        var entries = new[] { Pg("A"), Pg("B"), Pg("C"), Pg("D") };

        var result = PaletteRecency.PrioritizeRecent(entries, new[] { "C", "A" });   // C is most recent

        Assert.Equal(new[] { "C", "A", "B", "D" }, result.Select(e => e.Id));
    }

    [Fact]
    public void NonRecentRows_KeepTheirOriginalRelativeOrder()
    {
        var entries = new[] { Pg("A"), Ac("X"), Pg("B"), Tw("T"), Pg("C") };

        var result = PaletteRecency.PrioritizeRecent(entries, new[] { "C" });

        // C promoted; every other row stays in its original order behind it.
        Assert.Equal(new[] { "C", "A", "X", "B", "T" }, result.Select(e => e.Id));
    }

    [Fact]
    public void OnlyPages_ArePromotable_NotActionsOrTweaks()
    {
        var entries = new[] { Pg("A"), Ac("X"), Tw("T") };

        // Even if an action/tweak id were recorded as "recent", it must not be promoted — recency is page-nav only.
        var result = PaletteRecency.PrioritizeRecent(entries, new[] { "X", "T" });

        Assert.Equal(new[] { "A", "X", "T" }, result.Select(e => e.Id));
    }

    [Fact]
    public void UnmatchedRecentKey_IsIgnored()
    {
        var entries = new[] { Pg("A"), Pg("B") };

        var result = PaletteRecency.PrioritizeRecent(entries, new[] { "Zzz", "B" });

        Assert.Equal(new[] { "B", "A" }, result.Select(e => e.Id));   // Zzz names no row → ignored; B promoted
    }

    [Fact]
    public void Result_IsAlwaysAPermutation_NoDropsOrDuplicates()
    {
        var entries = new[] { Pg("A"), Ac("X"), Pg("B"), Pg("C") };

        var result = PaletteRecency.PrioritizeRecent(entries, new[] { "B", "C", "A" });

        Assert.Equal(entries.Length, result.Count);
        Assert.Equal(entries.Select(e => e.Id).OrderBy(x => x), result.Select(e => e.Id).OrderBy(x => x));
    }
}
