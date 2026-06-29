using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AurumTweaks.Models;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>
/// JSON-file optimization-score timeline under <c>%LOCALAPPDATA%\AurumTweaks\Score\history.json</c>, oldest first
/// and bounded via <see cref="ScoreHistory.Record"/>. Same persistence shape as <see cref="ApplyJournal"/>.
/// Critically, both reads and writes swallow and log any I/O failure: the timeline is a side-record of a detection
/// pass, never a reason the score refresh itself appears to fail — the honesty mandate cuts both ways here too.
/// <see cref="RecordAsync"/> skips the disk write entirely when the score is unchanged (the pure core returns the
/// same reference), so reopening the app on an unchanged machine doesn't churn the file or the timeline.
/// </summary>
public sealed class ScoreHistoryStore : IScoreHistoryStore
{
    private readonly string _file;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ScoreHistoryStore()
    {
        var dir = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\AurumTweaks\\Score");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "history.json");
    }

    public async Task<IReadOnlyList<ScoreSnapshot>> LoadAsync()
    {
        if (!File.Exists(_file)) return Array.Empty<ScoreSnapshot>();
        try
        {
            await using var s = File.OpenRead(_file);
            var list = await JsonSerializer.DeserializeAsync<List<ScoreSnapshot>>(s, JsonOpts);
            return list ?? (IReadOnlyList<ScoreSnapshot>)Array.Empty<ScoreSnapshot>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load score history {File}", _file);
            return Array.Empty<ScoreSnapshot>();
        }
    }

    public async Task<IReadOnlyList<ScoreSnapshot>> RecordAsync(int score)
    {
        var existing = await LoadAsync();
        var updated = ScoreHistory.Record(existing, new ScoreSnapshot(DateTime.UtcNow, score));

        // Unchanged score → same reference → nothing new to persist. Return what we loaded without touching disk.
        if (ReferenceEquals(updated, existing)) return existing;

        try
        {
            await using var s = File.Create(_file);
            await JsonSerializer.SerializeAsync(s, updated, JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to record score history entry");
        }
        return updated;
    }
}
