using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the file-backed <see cref="EvidenceStore"/> — the durable backing for the ONE evidence slot that stays honest
/// across a restart (the frame-time A/B, a comparison of two immutable, dated captures). Load-bearing behaviours:
/// a save→load round-trip preserves every number the proof shows; the raw frame arrays are trimmed (the report never
/// cites them); a null/hollow publish deletes the file so a cleared A/B can't resurrect; and an unreadable file
/// degrades to an honest « none », never a crash. Uses a temp file via the internal test seam, never the real profile.
/// </summary>
public sealed class EvidenceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AurumEvidenceTests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "evidence-performance.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    // A run carrying real frames + stats, so the comparison HasData on both sides and there's something to trim.
    private static BenchmarkResult Run(double avgFps) => new()
    {
        Source = "ETW DXGI · game.exe",
        TargetProcess = "game.exe",
        CapturedAt = new DateTime(2026, 6, 20, 14, 0, 0, DateTimeKind.Local),
        FrameTimesMs = Enumerable.Repeat(1000.0 / avgFps, 600).ToList(),
        Stats = new FrameTimeStats { FrameCount = 600, DurationSec = 10, AvgFps = avgFps, P1LowFps = avgFps * 0.8 }
    };

    private static BenchmarkComparison Ab(double beforeFps, double afterFps)
        => BenchmarkComparer.Compare(Run(beforeFps), Run(afterFps));

    [Fact]
    public void SaveThenLoad_RoundTripsTheComparison_PreservingTheNumbersAndProvenance()
    {
        var ab = Ab(100, 120);
        new EvidenceStore(FilePath).SavePerformance(ab);

        var loaded = new EvidenceStore(FilePath).LoadPerformance();

        Assert.NotNull(loaded);
        Assert.Equal(ab.Headline.Before, loaded!.Headline.Before, 3);
        Assert.Equal(ab.Headline.After, loaded.Headline.After, 3);
        Assert.Equal(ab.Before.Stats.AvgFps, loaded.Before.Stats.AvgFps, 3);
        Assert.Equal(ab.After.Stats.P1LowFps, loaded.After.Stats.P1LowFps, 3);
        Assert.Equal("ETW DXGI · game.exe", loaded.Before.Source);      // provenance survives the round-trip
        Assert.Equal(new DateTime(2026, 6, 20), loaded.Before.CapturedAt.ToLocalTime().Date);   // capture date kept
    }

    [Fact]
    public void Save_TrimsRawFrames_ButKeepsEveryDisplayedStat()
    {
        new EvidenceStore(FilePath).SavePerformance(Ab(100, 120));

        var loaded = new EvidenceStore(FilePath).LoadPerformance()!;

        Assert.Empty(loaded.Before.FrameTimesMs);          // the unbounded raw arrays are dropped...
        Assert.Empty(loaded.After.FrameTimesMs);
        Assert.Equal(600, loaded.Before.Stats.FrameCount); // ...but the stats the proof renders are intact
        Assert.True(loaded.Before.HasData && loaded.After.HasData);
    }

    [Fact]
    public void SaveNull_DeletesTheFile_SoAClearedABCantResurrect()
    {
        var store = new EvidenceStore(FilePath);
        store.SavePerformance(Ab(100, 120));
        Assert.True(File.Exists(FilePath));

        store.SavePerformance(null);

        Assert.False(File.Exists(FilePath));
        Assert.Null(new EvidenceStore(FilePath).LoadPerformance());
    }

    [Fact]
    public void SaveHollowComparison_IsTreatedAsClear_NotPersistedAsEmptyProof()
    {
        // A comparison whose runs carry no data is not a proof; saving it must remove any prior file, not write a shell.
        var store = new EvidenceStore(FilePath);
        store.SavePerformance(Ab(100, 120));
        Assert.True(File.Exists(FilePath));

        store.SavePerformance(new BenchmarkComparison());

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Load_MissingFile_IsNone()
        => Assert.Null(new EvidenceStore(FilePath).LoadPerformance());

    [Fact]
    public void Load_CorruptFile_IsTreatedAsNone_NeverThrows()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not valid json ");

        Assert.Null(new EvidenceStore(FilePath).LoadPerformance());
    }

    [Fact]
    public void Load_HollowComparisonOnDisk_IsDropped_NotResurrectedAsEmptyProof()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new BenchmarkComparison()));

        Assert.Null(new EvidenceStore(FilePath).LoadPerformance());
    }
}
