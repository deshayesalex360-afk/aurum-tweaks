using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

// Pure-core tests for the « Mémoire vive » page. The native probe (GlobalMemoryStatusEx / NtQuerySystemInformation /
// NtSetSystemInformation) is deliberately NOT tested — it's I/O glue, same policy as the latency probe. What's pinned
// here is every honesty- and correctness-bearing decision: the composition math, the measured flush delta, the
// kind→kernel-command map (a wrong constant would silently fire the wrong command), the catalog's frozen kind set,
// and the advice wording.

/// <summary>
/// Pins the composition record's derivations. The load-bearing honesty points: the in-use figure is clamped so it can
/// never go negative, the percentages never divide by zero and are clamped 0–100, and the standby/free/modified split
/// reports "—" (never a fabricated 0) when the page-list read failed.
/// </summary>
public class MemoryCompositionTests
{
    // 16 / 8 / 4 / 2 Go in bytes — clean powers so ByteSize renders whole "Go" strings.
    private const long Total = 16L * 1024 * 1024 * 1024;
    private const long Available = 8L * 1024 * 1024 * 1024;
    private const long Standby = 4L * 1024 * 1024 * 1024;
    private const long Free = 2L * 1024 * 1024 * 1024;
    private const long Modified = 1L * 1024 * 1024 * 1024;

    private static MemoryComposition Full() =>
        new(Total, Available, Standby, Free, Modified, DetailAvailable: true);

    [Fact]
    public void InUseBytes_IsTotalMinusAvailable()
        => Assert.Equal(Total - Available, Full().InUseBytes);

    [Fact]
    public void InUseBytes_ClampsToZero_WhenAvailableExceedsTotal()
    {
        // Defensive: the two figures come from one API call, but never emit a negative "in use".
        var c = new MemoryComposition(100, 150, 0, 0, 0, DetailAvailable: false);
        Assert.Equal(0, c.InUseBytes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(Total, true)]
    public void HasData_TracksTotal(long total, bool expected)
        => Assert.Equal(expected, new MemoryComposition(total, 0, 0, 0, 0, false).HasData);

    [Fact]
    public void Percentages_AreComputedAgainstTotal()
    {
        var c = Full();
        Assert.Equal(50.0, c.InUsePercent, 3);       // 8 / 16
        Assert.Equal(50.0, c.AvailablePercent, 3);   // 8 / 16
        Assert.Equal(25.0, c.StandbyPercent, 3);     // 4 / 16
        Assert.Equal(12.5, c.FreePercent, 3);        // 2 / 16
    }

    [Fact]
    public void Percentages_AreZero_WhenTotalIsZero()
    {
        // Divide-by-zero guard — an empty/unreadable snapshot must not throw or emit NaN.
        var c = MemoryComposition.Empty;
        Assert.Equal(0.0, c.InUsePercent);
        Assert.Equal(0.0, c.AvailablePercent);
        Assert.Equal(0.0, c.StandbyPercent);
    }

    [Fact]
    public void Percentages_AreClampedToHundred()
    {
        // A part larger than total (shouldn't happen, but the page lists and GlobalMemoryStatusEx are separate reads)
        // must clamp to 100, never overflow the bar.
        var c = new MemoryComposition(100, 200, 0, 0, 0, DetailAvailable: false);
        Assert.Equal(100.0, c.AvailablePercent);
    }

    [Fact]
    public void Displays_UseByteSize_WhenDetailAvailable()
    {
        var c = Full();
        Assert.Equal("16 Go", c.TotalDisplay);
        Assert.Equal("8 Go", c.InUseDisplay);
        Assert.Equal("8 Go", c.AvailableDisplay);
        Assert.Equal("4 Go", c.StandbyDisplay);
        Assert.Equal("2 Go", c.FreeDisplay);
        Assert.Equal("1 Go", c.ModifiedDisplay);
    }

    [Fact]
    public void DetailDisplays_AreDash_WhenDetailUnavailable()
    {
        // The honesty rule: when the page-list query couldn't be read, show "—", never a fabricated 0.
        var c = new MemoryComposition(Total, Available, 0, 0, 0, DetailAvailable: false);
        Assert.Equal("—", c.StandbyDisplay);
        Assert.Equal("—", c.FreeDisplay);
        Assert.Equal("—", c.ModifiedDisplay);
        Assert.Equal("—", c.StandbyPercentDisplay);
        Assert.Equal("—", c.FreePercentDisplay);
        Assert.Equal("—", c.ModifiedPercentDisplay);
    }

    [Fact]
    public void TotalAndAvailableDisplays_StayValid_WhenDetailUnavailable()
    {
        // Total/Available come from GlobalMemoryStatusEx, which is independent of the page-list read — so they remain
        // real even when the detail split is "—".
        var c = new MemoryComposition(Total, Available, 0, 0, 0, DetailAvailable: false);
        Assert.Equal("16 Go", c.TotalDisplay);
        Assert.Equal("8 Go", c.AvailableDisplay);
        Assert.Equal("8 Go", c.InUseDisplay);
        Assert.NotEqual("—", c.InUsePercentDisplay);
        Assert.NotEqual("—", c.AvailablePercentDisplay);
    }

    [Fact]
    public void PercentDisplays_AreFrenchFormatted()
    {
        var c = Full();
        Assert.Equal("50,0 %", c.InUsePercentDisplay);
        Assert.Equal("25,0 %", c.StandbyPercentDisplay);
        Assert.Equal("12,5 %", c.FreePercentDisplay);
    }

    [Fact]
    public void Empty_IsZeroAndDetailUnavailable()
    {
        var c = MemoryComposition.Empty;
        Assert.False(c.HasData);
        Assert.False(c.DetailAvailable);
        Assert.Equal(0, c.TotalBytes);
        Assert.Equal("—", c.StandbyDisplay);
    }
}

/// <summary>
/// Pins the curated flush catalog. The load-bearing assertion is <see cref="Actions_KindSetIsExactlyTheTwoSupportedCommands"/>:
/// the page only ever exposes the two well-understood RAMMap-class commands. Adding a misleading action (or one with no
/// kernel-command mapping) has to be a deliberate, reviewed change to this test.
/// </summary>
public class MemoryFlushCatalogTests
{
    [Fact]
    public void Actions_AreNonEmpty()
        => Assert.NotEmpty(MemoryFlushCatalog.Actions);

    [Fact]
    public void Actions_KindSetIsExactlyTheTwoSupportedCommands()
    {
        var kinds = MemoryFlushCatalog.Actions.Select(a => a.Kind).ToHashSet();
        Assert.Equal(
            new HashSet<MemoryFlushKind> { MemoryFlushKind.StandbyList, MemoryFlushKind.WorkingSets },
            kinds);
    }

    [Fact]
    public void Actions_HaveUniqueKinds()
    {
        var kinds = MemoryFlushCatalog.Actions.Select(a => a.Kind).ToList();
        Assert.Equal(kinds.Count, kinds.Distinct().Count());
    }

    [Fact]
    public void Actions_AllHaveLabelAndAdvice()
    {
        Assert.All(MemoryFlushCatalog.Actions, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Label));
            Assert.False(string.IsNullOrWhiteSpace(a.Advice));
        });
    }

    [Theory]
    [InlineData(MemoryFlushKind.StandbyList, "Vider le cache standby")]
    [InlineData(MemoryFlushKind.WorkingSets, "Vider les working sets")]
    public void Label_ReturnsMatchingActionLabel(MemoryFlushKind kind, string expected)
        => Assert.Equal(expected, MemoryFlushCatalog.Label(kind));

    [Fact]
    public void StandbyAdvice_StatesAvailableDoesNotChange()
    {
        // The whole point of the page: be honest that purging standby does NOT raise "available" memory.
        var advice = MemoryFlushCatalog.Actions.First(a => a.Kind == MemoryFlushKind.StandbyList).Advice;
        Assert.Contains("DISPONIBLE ne bouge pas", advice);
    }
}

/// <summary>
/// Pins the kind→SYSTEM_MEMORY_LIST_COMMAND map. A wrong constant here would silently fire the wrong kernel command —
/// exactly the kind of invisible mistake the honesty mandate forbids — so the values are frozen and every enum value
/// is required to resolve.
/// </summary>
public class MemoryListCommandTests
{
    [Fact]
    public void Constants_MatchWindowsValues()
    {
        Assert.Equal(4, MemoryListCommand.MemoryPurgeStandbyList);
        Assert.Equal(2, MemoryListCommand.MemoryEmptyWorkingSets);
    }

    [Theory]
    [InlineData(MemoryFlushKind.StandbyList, 4)]
    [InlineData(MemoryFlushKind.WorkingSets, 2)]
    public void ForKind_MapsToTheCorrectCommand(MemoryFlushKind kind, int expected)
        => Assert.Equal(expected, MemoryListCommand.ForKind(kind));

    [Fact]
    public void ForKind_ResolvesEveryDeclaredKind()
    {
        // If a new kind is ever added without a command mapping, this fails instead of throwing at runtime.
        foreach (var kind in Enum.GetValues<MemoryFlushKind>())
            Assert.True(MemoryListCommand.ForKind(kind) > 0);
    }
}

/// <summary>
/// Pins the measured-outcome math. The reported figure is always a clamped before/after delta — never an estimate —
/// and <see cref="MemoryFlushOutcome.DidSomething"/> correctly separates "freed X" from "ran but nothing moved" from
/// "the call failed".
/// </summary>
public class MemoryFlushOutcomeTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static MemoryComposition Comp(long standby, long available, bool detail = true) =>
        new(16 * Gb, available, standby, 0, 0, detail);

    [Fact]
    public void StandbyReleased_IsBeforeMinusAfter()
    {
        var o = new MemoryFlushOutcome(MemoryFlushKind.StandbyList, true, Comp(4 * Gb, 8 * Gb), Comp(1 * Gb, 8 * Gb));
        Assert.Equal(3 * Gb, o.StandbyReleased);
    }

    [Fact]
    public void StandbyReleased_ClampsToZero_WhenCacheGrew()
    {
        // Standby rebuilds constantly; if it grew between reads, report 0 released, never a negative number.
        var o = new MemoryFlushOutcome(MemoryFlushKind.StandbyList, true, Comp(1 * Gb, 8 * Gb), Comp(4 * Gb, 8 * Gb));
        Assert.Equal(0, o.StandbyReleased);
    }

    [Fact]
    public void AvailableGained_IsAfterMinusBefore()
    {
        var o = new MemoryFlushOutcome(MemoryFlushKind.WorkingSets, true, Comp(0, 2 * Gb), Comp(0, 5 * Gb));
        Assert.Equal(3 * Gb, o.AvailableGained);
    }

    [Fact]
    public void AvailableGained_ClampsToZero_WhenAvailableDropped()
    {
        var o = new MemoryFlushOutcome(MemoryFlushKind.WorkingSets, true, Comp(0, 5 * Gb), Comp(0, 2 * Gb));
        Assert.Equal(0, o.AvailableGained);
    }

    [Fact]
    public void Headline_PicksStandbyReleased_ForStandbyList()
    {
        var o = new MemoryFlushOutcome(MemoryFlushKind.StandbyList, true, Comp(4 * Gb, 2 * Gb), Comp(1 * Gb, 5 * Gb));
        Assert.Equal(o.StandbyReleased, o.Headline);
        Assert.Equal(3 * Gb, o.Headline);
    }

    [Fact]
    public void Headline_PicksAvailableGained_ForWorkingSets()
    {
        var o = new MemoryFlushOutcome(MemoryFlushKind.WorkingSets, true, Comp(4 * Gb, 2 * Gb), Comp(1 * Gb, 5 * Gb));
        Assert.Equal(o.AvailableGained, o.Headline);
        Assert.Equal(3 * Gb, o.Headline);
    }

    [Fact]
    public void HeadlineDisplay_FormatsWithByteSize()
    {
        var o = new MemoryFlushOutcome(MemoryFlushKind.StandbyList, true, Comp(4 * Gb, 8 * Gb), Comp(1 * Gb, 8 * Gb));
        Assert.Equal("3 Go", o.HeadlineDisplay);
    }

    [Fact]
    public void DidSomething_TrueOnlyWhenInvokedAndMeasuredPositive()
    {
        var freed = new MemoryFlushOutcome(MemoryFlushKind.StandbyList, true, Comp(4 * Gb, 8 * Gb), Comp(1 * Gb, 8 * Gb));
        Assert.True(freed.DidSomething);
    }

    [Fact]
    public void DidSomething_FalseWhenNotInvoked_EvenWithPositiveDelta()
    {
        // The call failed: never claim a result just because the snapshots happen to differ.
        var o = new MemoryFlushOutcome(MemoryFlushKind.StandbyList, false, Comp(4 * Gb, 8 * Gb), Comp(1 * Gb, 8 * Gb));
        Assert.False(o.DidSomething);
    }

    [Fact]
    public void DidSomething_FalseWhenNothingMoved()
    {
        var o = new MemoryFlushOutcome(MemoryFlushKind.StandbyList, true, Comp(2 * Gb, 8 * Gb), Comp(2 * Gb, 8 * Gb));
        Assert.False(o.DidSomething);
    }

    [Fact]
    public void DidSomething_FalseForStandbyPurge_WhenDetailWasUnavailable()
    {
        // The documented "effectué, non mesurable" case: with no page-list detail the standby figures are 0, so the
        // delta is 0 and DidSomething is correctly false even though the kernel call ran.
        var before = Comp(0, 8 * Gb, detail: false);
        var after = Comp(0, 8 * Gb, detail: false);
        var o = new MemoryFlushOutcome(MemoryFlushKind.StandbyList, true, before, after);
        Assert.Equal(0, o.Headline);
        Assert.False(o.DidSomething);
    }

    [Fact]
    public void Failed_IsNotInvokedWithEmptySnapshotsAndKindPreserved()
    {
        var o = MemoryFlushOutcome.Failed(MemoryFlushKind.WorkingSets);
        Assert.False(o.Invoked);
        Assert.Equal(MemoryFlushKind.WorkingSets, o.Kind);
        Assert.Equal(0, o.Headline);
        Assert.False(o.DidSomething);
    }
}

/// <summary>Pins the one-line status wording for the three honest states: no data, full detail, detail unavailable.</summary>
public class MemoryAdviceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Summarize_NoData_SaysUnavailable()
        => Assert.Equal("Composition mémoire indisponible.", MemoryAdvice.Summarize(MemoryComposition.Empty));

    [Fact]
    public void Summarize_WithDetail_ListsStandbyAndFree()
    {
        var c = new MemoryComposition(16 * Gb, 8 * Gb, 4 * Gb, 2 * Gb, 1 * Gb, DetailAvailable: true);
        Assert.Equal(
            "8 Go en cours d'utilisation · 4 Go en cache (standby) · 2 Go libre(s) sur 16 Go.",
            MemoryAdvice.Summarize(c));
    }

    [Fact]
    public void Summarize_WithoutDetail_FallsBackToAvailableAndFlagsMissingSplit()
    {
        var c = new MemoryComposition(16 * Gb, 8 * Gb, 0, 0, 0, DetailAvailable: false);
        Assert.Equal(
            "8 Go en cours d'utilisation · 8 Go disponible(s) sur 16 Go (détail standby/libre indisponible).",
            MemoryAdvice.Summarize(c));
    }
}
