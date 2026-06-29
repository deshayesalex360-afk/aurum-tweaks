using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the human-readable size formatter behind the « Nettoyage disque » page. Base-1024 with French unit names and
/// a comma decimal — fixed culture, so these assertions hold on any build agent regardless of its locale.
/// </summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 o")]
    [InlineData(-512, "0 o")]      // defensive: a size can't be negative, but never emit a weird "-512 o"
    [InlineData(1, "1 o")]
    [InlineData(512, "512 o")]
    [InlineData(1023, "1023 o")]
    [InlineData(1024, "1 Ko")]
    [InlineData(1536, "1,5 Ko")]   // 1.5 * 1024 — French comma decimal
    [InlineData(10240, "10 Ko")]
    [InlineData(1048576, "1 Mo")]
    [InlineData(2621440, "2,5 Mo")]
    [InlineData(1073741824, "1 Go")]
    [InlineData(1610612736, "1,5 Go")]
    [InlineData(1099511627776, "1 To")]
    public void Format_ProducesExpectedFrenchString(long bytes, string expected)
        => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void Format_RoundsToTwoDecimalsFromMegabytesUp()
        => Assert.Equal("5,24 Go", ByteSize.Format(5_626_407_485)); // ~5.239 GiB → two-decimal rounding
}

/// <summary>
/// Pins the curated cleanup catalog. The load-bearing assertion is <see cref="Catalog_AutoCleanSetIsExactlyTheSafeSelfHealingLocations"/>:
/// the app only ever auto-deletes folders Windows recreates on demand. Adding a risky location (WinSxS, Windows.old,
/// the Recycle Bin, restore points) to this list would turn a safe "clear temp files" into an irreversible footgun,
/// so the set is frozen here and any addition has to be a deliberate, reviewed change to this test.
/// </summary>
public class CleanupTargetCatalogTests
{
    [Fact]
    public void Catalog_AutoCleanSetIsExactlyTheSafeSelfHealingLocations()
    {
        var expected = new[]
        {
            CleanupCategory.UserTemp,
            CleanupCategory.WindowsTemp,
            CleanupCategory.CrashDumps,
            CleanupCategory.WindowsUpdateCache
        };
        Assert.Equal(expected.OrderBy(c => c), CleanupTargetCatalog.Targets.Select(t => t.Category).OrderBy(c => c));
    }

    [Fact]
    public void Catalog_CoversEveryDefinedCategory()
    {
        var defined = System.Enum.GetValues<CleanupCategory>();
        var covered = CleanupTargetCatalog.Targets.Select(t => t.Category).ToHashSet();
        Assert.All(defined, c => Assert.Contains(c, covered));
    }

    [Fact]
    public void Catalog_EveryTargetHasLabelAndAdvice()
        => Assert.All(CleanupTargetCatalog.Targets, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Label));
            Assert.False(string.IsNullOrWhiteSpace(t.Advice));
        });

    [Fact]
    public void Catalog_CategoriesAreDistinct()
        => Assert.Equal(CleanupTargetCatalog.Targets.Count,
                        CleanupTargetCatalog.Targets.Select(t => t.Category).Distinct().Count());

    [Theory]
    [InlineData(CleanupCategory.UserTemp)]
    [InlineData(CleanupCategory.WindowsUpdateCache)]
    public void Label_ReturnsTheTargetLabel(CleanupCategory category)
        => Assert.Equal(CleanupTargetCatalog.Targets.First(t => t.Category == category).Label,
                        CleanupTargetCatalog.Label(category));
}

/// <summary>Pins <see cref="CleanupItem"/> / <see cref="CleanupReport"/> aggregation — the numbers the page headlines
/// are honest sums of real measured bytes, and "present but empty" never counts as reclaimable.</summary>
public class CleanupReportTests
{
    private static CleanupTarget T(CleanupCategory c) => new(c, c.ToString(), "advice");

    private static CleanupItem Item(CleanupCategory c, long bytes, bool exists = true)
        => new(T(c), bytes, exists);

    [Fact]
    public void Item_HasReclaimable_OnlyWhenPresentAndNonEmpty()
    {
        Assert.True(Item(CleanupCategory.UserTemp, 1).HasReclaimable);
        Assert.False(Item(CleanupCategory.UserTemp, 0).HasReclaimable);                 // present but empty
        Assert.False(Item(CleanupCategory.UserTemp, 5_000, exists: false).HasReclaimable); // absent folder
    }

    [Fact]
    public void Item_SizeDisplay_MatchesByteSizeFormat()
        => Assert.Equal(ByteSize.Format(1536), Item(CleanupCategory.WindowsTemp, 1536).SizeDisplay);

    [Fact]
    public void Report_TotalBytes_IsTheSumOfItems()
    {
        var report = new CleanupReport(new[]
        {
            Item(CleanupCategory.UserTemp, 1000),
            Item(CleanupCategory.WindowsTemp, 2000),
            Item(CleanupCategory.CrashDumps, 0)
        });
        Assert.Equal(3000, report.TotalBytes);
        Assert.Equal(ByteSize.Format(3000), report.TotalDisplay);
    }

    [Fact]
    public void Report_ReclaimableCount_IgnoresEmptyAndAbsent()
    {
        var report = new CleanupReport(new[]
        {
            Item(CleanupCategory.UserTemp, 1000),                       // counts
            Item(CleanupCategory.WindowsTemp, 0),                       // empty → no
            Item(CleanupCategory.CrashDumps, 9_999, exists: false)      // absent → no
        });
        Assert.Equal(1, report.ReclaimableCount);
    }
}

/// <summary>
/// Pins the load-bearing honesty rule of the cleaner: the « espace libéré » figure is the space that genuinely
/// disappeared (before − after), clamped at zero, and a partial clean (locked files left behind) is reported as
/// partial — never dressed up as a clean sweep or the optimistic pre-scan estimate.
/// </summary>
public class CleanupOutcomeTests
{
    [Fact]
    public void Freed_IsBytesActuallyGone()
        => Assert.Equal(800, new CleanupOutcome(1000, 200).Freed);

    [Fact]
    public void Freed_IsZeroWhenEverythingWasLocked()
        => Assert.Equal(0, new CleanupOutcome(1000, 1000).Freed);

    [Fact]
    public void Freed_NeverNegative_EvenIfTheFolderGrewDuringTheClean()
        => Assert.Equal(0, new CleanupOutcome(200, 1000).Freed); // clamped — never fabricate or go negative

    [Fact]
    public void FullyCleared_OnlyWhenNothingReclaimableRemains()
    {
        Assert.True(new CleanupOutcome(1000, 0).FullyCleared);
        Assert.False(new CleanupOutcome(1000, 200).FullyCleared); // 200 bytes survived (locked)
    }

    [Fact]
    public void FreedDisplay_MatchesByteSizeFormat()
        => Assert.Equal(ByteSize.Format(800), new CleanupOutcome(1000, 200).FreedDisplay);

    [Fact]
    public void Empty_IsZeroAndFullyCleared()
    {
        Assert.Equal(0, CleanupOutcome.Empty.Freed);
        Assert.True(CleanupOutcome.Empty.FullyCleared);
    }

    [Fact]
    public void Sum_AddsBeforeAndAfterAcrossOutcomes()
    {
        var sum = CleanupOutcome.Sum(new[]
        {
            new CleanupOutcome(1000, 200),
            new CleanupOutcome(500, 500),   // all locked
            new CleanupOutcome(300, 0)
        });
        Assert.Equal(1800, sum.BytesBefore);
        Assert.Equal(700, sum.BytesAfter);
        Assert.Equal(1100, sum.Freed);
    }

    [Fact]
    public void Sum_OfNothing_IsEmpty()
    {
        var sum = CleanupOutcome.Sum(System.Array.Empty<CleanupOutcome>());
        Assert.Equal(0, sum.BytesBefore);
        Assert.Equal(0, sum.BytesAfter);
    }
}
