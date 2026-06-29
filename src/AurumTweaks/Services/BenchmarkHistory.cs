using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AurumTweaks.Services;

/// <summary>
/// One archived benchmark run as shown in the history list: where its raw frame-time CSV lives, when it was
/// captured, and a few honest headline metrics so a past run is recognisable at a glance without re-opening it.
/// Every number is read back from the stored CSV (re-imported bit-exact via <see cref="FrameTimeCsvParser"/>);
/// nothing here is invented. The display helpers carry the fr-FR formatting the history card binds directly.
/// </summary>
public sealed record BenchmarkHistoryEntry(
    string FilePath,
    DateTime CapturedAt,
    string Process,
    int FrameCount,
    double AvgFps,
    double P1LowFps)
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public string CapturedAtLabel => CapturedAt.ToString("dd/MM/yyyy · HH:mm", Fr);
    public string ProcessLabel => string.IsNullOrWhiteSpace(Process) ? "Process inconnu" : Process;
    public string SummaryLabel =>
        $"{FrameCount} frames · {AvgFps.ToString("0.0", Fr)} FPS moy. · 1% low {P1LowFps.ToString("0.0", Fr)}";
}

/// <summary>
/// Pure naming + ordering + prune rules for the persistent benchmark run history — extracted (no I/O) because the
/// load-bearing properties are testable without the disk:
/// <list type="bullet">
/// <item><b>Sortable, locale-free filenames.</b> <see cref="BuildFileName"/> stamps a run as
/// <c>aurum-run-yyyyMMdd-HHmmss.csv</c> with the invariant culture, so the capture time round-trips through the
/// name (<see cref="TryParseTimestamp"/>) and a plain alphabetical sort is already chronological.</item>
/// <item><b>Bounded growth.</b> <see cref="SelectExpired"/> picks the runs beyond the newest <c>keep</c> for
/// deletion, so the history can never grow without limit — the same honesty/safety cap as the change journal.</item>
/// </list>
/// </summary>
public static class BenchmarkHistory
{
    public const int DefaultMaxRuns = 50;
    public const string FilePrefix = "aurum-run-";
    public const string SearchPattern = FilePrefix + "*.csv";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    public static string BuildFileName(DateTime capturedAt) =>
        FilePrefix + capturedAt.ToString(StampFormat, CultureInfo.InvariantCulture) + ".csv";

    /// <summary>Recover the capture time encoded in a history filename; false for anything that isn't ours.</summary>
    public static bool TryParseTimestamp(string fileName, out DateTime capturedAt)
    {
        capturedAt = default;
        if (string.IsNullOrEmpty(fileName)) return false;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)) return false;

        string stamp = stem[FilePrefix.Length..];
        return DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out capturedAt);
    }

    public static IReadOnlyList<BenchmarkHistoryEntry> Order(IEnumerable<BenchmarkHistoryEntry> entries) =>
        (entries ?? Enumerable.Empty<BenchmarkHistoryEntry>())
            .OrderByDescending(e => e.CapturedAt)
            .ToList();

    /// <summary>
    /// The runs to delete to keep the history at <paramref name="keep"/> entries: everything after the newest
    /// <paramref name="keep"/> in a list already ordered newest-first. Empty when nothing must go. The cap IS the
    /// safety property (an unbounded archive could fill the disk), so it lives here and is pinned by tests.
    /// </summary>
    public static IReadOnlyList<BenchmarkHistoryEntry> SelectExpired(
        IReadOnlyList<BenchmarkHistoryEntry> orderedNewestFirst, int keep = DefaultMaxRuns)
    {
        if (keep < 0) keep = 0;
        if (orderedNewestFirst is null || orderedNewestFirst.Count <= keep)
            return Array.Empty<BenchmarkHistoryEntry>();

        var expired = new List<BenchmarkHistoryEntry>(orderedNewestFirst.Count - keep);
        for (int i = keep; i < orderedNewestFirst.Count; i++)
            expired.Add(orderedNewestFirst[i]);
        return expired;
    }
}
