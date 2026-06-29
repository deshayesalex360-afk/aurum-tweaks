using System;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="BenchmarkHistory"/> — the pure naming/ordering/prune rules behind the persistent benchmark run
/// history. The load-bearing properties: a run's capture time round-trips THROUGH its filename (so the list's order
/// is locale-free and a plain alphabetical sort is already chronological), only our own files are recognised (a
/// foreign CSV dropped in the folder is left alone, never mis-dated), and the archive is BOUNDED — the prune keeps
/// the newest N and hands back exactly the older runs to delete, so it can never grow without limit.
/// </summary>
public class BenchmarkHistoryTests
{
    // --- Filenames: sortable, locale-free, and a round-trip for the capture time ---

    [Fact]
    public void BuildFileName_IsSortablePrefixedAndStampedToTheSecond()
    {
        var name = BenchmarkHistory.BuildFileName(new DateTime(2026, 6, 25, 14, 30, 5));
        Assert.Equal("aurum-run-20260625-143005.csv", name);
    }

    [Fact]
    public void Timestamp_RoundTripsThroughTheFilename()
    {
        var when = new DateTime(2026, 1, 9, 8, 7, 6);
        Assert.True(BenchmarkHistory.TryParseTimestamp(BenchmarkHistory.BuildFileName(when), out var parsed));
        Assert.Equal(when, parsed);
    }

    [Fact]
    public void TryParseTimestamp_AcceptsAFullPath_NotJustABareName()
    {
        var name = BenchmarkHistory.BuildFileName(new DateTime(2026, 3, 4, 5, 6, 7));
        Assert.True(BenchmarkHistory.TryParseTimestamp(@"C:\Users\me\AppData\Local\AurumTweaks\Benchmarks\" + name, out var parsed));
        Assert.Equal(new DateTime(2026, 3, 4, 5, 6, 7), parsed);
    }

    [Theory]
    [InlineData("capture.csv")]                 // not one of ours
    [InlineData("aurum-run-nope.csv")]          // right prefix, garbage stamp
    [InlineData("aurum-run-20261301-000000.csv")] // month 13 → not a real date
    [InlineData("")]
    public void TryParseTimestamp_RejectsForeignOrMalformedNames(string fileName)
        => Assert.False(BenchmarkHistory.TryParseTimestamp(fileName, out _));

    // --- Ordering: newest first ---

    [Fact]
    public void Order_PutsTheNewestRunFirst()
    {
        var older = Run(new DateTime(2026, 6, 20, 10, 0, 0));
        var newer = Run(new DateTime(2026, 6, 25, 10, 0, 0));
        var mid = Run(new DateTime(2026, 6, 22, 10, 0, 0));

        var ordered = BenchmarkHistory.Order(new[] { older, newer, mid });

        Assert.Equal(new[] { newer, mid, older }, ordered);
    }

    [Fact]
    public void Order_NullInput_IsEmpty_NotACrash()
        => Assert.Empty(BenchmarkHistory.Order(null!));

    // --- Prune: the archive is bounded (the safety property) ---

    [Fact]
    public void SelectExpired_UnderTheCap_DeletesNothing()
    {
        var runs = Ordered(3);
        Assert.Empty(BenchmarkHistory.SelectExpired(runs, keep: 5));
    }

    [Fact]
    public void SelectExpired_OverTheCap_ReturnsExactlyTheOldestTail()
    {
        var runs = Ordered(5);   // newest-first: index 0 is newest, 4 is oldest
        var expired = BenchmarkHistory.SelectExpired(runs, keep: 2);

        Assert.Equal(3, expired.Count);
        Assert.Equal(new[] { runs[2], runs[3], runs[4] }, expired);   // the three oldest go; the two newest stay
    }

    [Fact]
    public void SelectExpired_KeepZero_DeletesEverything()
        => Assert.Equal(4, BenchmarkHistory.SelectExpired(Ordered(4), keep: 0).Count);

    [Fact]
    public void SelectExpired_NegativeKeep_IsTreatedAsZero_NeverThrows()
        => Assert.Equal(3, BenchmarkHistory.SelectExpired(Ordered(3), keep: -7).Count);

    // --- Entry display helpers (fr-FR, with an honest fallback for a missing process) ---

    [Fact]
    public void Entry_SummaryAndDate_UseFrenchFormatting()
    {
        var e = new BenchmarkHistoryEntry("r.csv", new DateTime(2026, 6, 25, 14, 30, 0), "game.exe", 1234, 144.2, 98.7);
        Assert.Equal("1234 frames · 144,2 FPS moy. · 1% low 98,7", e.SummaryLabel);
        Assert.Equal("25/06/2026 · 14:30", e.CapturedAtLabel);
        Assert.Equal("game.exe", e.ProcessLabel);
    }

    [Fact]
    public void Entry_BlankProcess_FallsBackToAnHonestLabel_NotAnEmptyString()
        => Assert.Equal("Process inconnu",
            new BenchmarkHistoryEntry("r.csv", DateTime.Now, "  ", 10, 60, 50).ProcessLabel);

    // --- helpers ---

    private static BenchmarkHistoryEntry Run(DateTime when)
        => new($"aurum-run-{when:yyyyMMdd-HHmmss}.csv", when, "game", 1000, 120, 96);

    // n runs, already newest-first (index 0 newest), one day apart — the shape SelectExpired expects.
    private static BenchmarkHistoryEntry[] Ordered(int n)
        => Enumerable.Range(0, n).Select(i => Run(new DateTime(2026, 6, 25).AddDays(-i))).ToArray();
}
