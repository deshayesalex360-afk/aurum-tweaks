using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Models;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>
/// Persists captured benchmark runs so they survive the app being closed — the gap a live ETW capture otherwise
/// has (it lives only in memory). Each run is archived as its raw frame-time CSV under
/// <c>%LOCALAPPDATA%\AurumTweaks\Benchmarks</c> via the round-trippable <see cref="FrameTimeCsv"/>, so a stored run
/// re-imports bit-exact and can be reopened, compared, or pinned as the « Avant » baseline in a later session.
/// All numbers come back out of the stored CSV; nothing is fabricated. Bounded to the newest
/// <see cref="BenchmarkHistory.DefaultMaxRuns"/> runs so the archive can never grow without limit.
/// </summary>
public interface IBenchmarkHistoryService
{
    /// <summary>Archive a run's raw frames to history (no-op for an empty run). True when a file was written.
    /// Never throws: a failed archive is a side-record problem, never a reason the capture itself appears to fail.</summary>
    Task<bool> SaveAsync(BenchmarkResult result);

    /// <summary>The stored runs, newest first, each re-read from its CSV. Runs off the UI thread. Never throws.</summary>
    Task<IReadOnlyList<BenchmarkHistoryEntry>> ListAsync();

    /// <summary>Re-parse a stored run back into a full result (frames + stats) to view or pin as baseline; null if unreadable.</summary>
    Task<BenchmarkResult?> LoadAsync(string filePath);

    /// <summary>Delete one stored run's CSV. Never throws.</summary>
    Task DeleteAsync(string filePath);
}

/// <summary>
/// Default implementation. The naming, ordering and prune rules are the pure, unit-tested
/// <see cref="BenchmarkHistory"/>; the round-trippable serialisation is <see cref="FrameTimeCsv"/> /
/// <see cref="FrameTimeCsvParser"/> (via the injected <see cref="IBenchmarkService"/>). This class only adds the
/// real-world I/O — the archive directory, file writes/reads, enumeration and deletes — each defensively wrapped so
/// a failure degrades to an honest no-op (logged) instead of crashing a capture or the page.
/// </summary>
public sealed class BenchmarkHistoryService : IBenchmarkHistoryService
{
    private readonly IBenchmarkService _bench;
    private readonly string _dir;
    private readonly int _maxRuns;

    public BenchmarkHistoryService(IBenchmarkService bench, int maxRuns = BenchmarkHistory.DefaultMaxRuns)
    {
        _bench = bench;
        _maxRuns = maxRuns;
        _dir = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\AurumTweaks\\Benchmarks");
        Directory.CreateDirectory(_dir);
    }

    public async Task<bool> SaveAsync(BenchmarkResult result)
    {
        if (result is null || !result.HasData) return false;
        try
        {
            string path = Path.Combine(_dir, BenchmarkHistory.BuildFileName(result.CapturedAt));
            await File.WriteAllTextAsync(path, FrameTimeCsv.Render(result));
            await PruneAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to archive benchmark run to history");
            return false;
        }
    }

    public Task<IReadOnlyList<BenchmarkHistoryEntry>> ListAsync() => Task.Run(() =>
    {
        var entries = new List<BenchmarkHistoryEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_dir, BenchmarkHistory.SearchPattern))
            {
                // The capture time comes from the filename (locale-free, sortable); a foreign name is left alone.
                if (!BenchmarkHistory.TryParseTimestamp(Path.GetFileName(path), out var capturedAt))
                    continue;

                var r = _bench.AnalyzeCsv(path);
                if (!r.HasData) continue;   // unreadable / empty → skip rather than show a hollow row

                entries.Add(new BenchmarkHistoryEntry(
                    path, capturedAt, r.TargetProcess, r.Stats.FrameCount, r.Stats.AvgFps, r.Stats.P1LowFps));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to list benchmark history");
        }
        return BenchmarkHistory.Order(entries);
    });

    public Task<BenchmarkResult?> LoadAsync(string filePath) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(filePath)) return (BenchmarkResult?)null;
        var r = _bench.AnalyzeCsv(filePath);
        return r.HasData ? r : null;
    });

    public Task DeleteAsync(string filePath)
    {
        try { if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath)) File.Delete(filePath); }
        catch (Exception ex) { Log.Warning(ex, "Failed to delete benchmark run {File}", filePath); }
        return Task.CompletedTask;
    }

    // Trim to the newest _maxRuns after a save. The pure BenchmarkHistory.SelectExpired decides WHAT goes (the single
    // source of truth for the cap); this only performs the deletes. Best-effort — a prune failure must not fail the
    // save it follows, so it is reached only inside SaveAsync's try and DeleteAsync itself never throws.
    private async Task PruneAsync()
    {
        foreach (var expired in BenchmarkHistory.SelectExpired(await ListAsync(), _maxRuns))
            await DeleteAsync(expired.FilePath);
    }
}
