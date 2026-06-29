using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AurumTweaks.Models;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>
/// Pure bounded-prepend for the change journal: a fresh newest-first list with <paramref name="entry"/> at the
/// front and at most <paramref name="cap"/> entries kept. Extracted (no I/O) because the cap IS the honesty/
/// safety property — an audit log that grew without limit would eventually be the thing that fills the disk —
/// and ordering (newest first) is what the page relies on; both are pinned by tests, not trusted to the store.
/// </summary>
public static class JournalLog
{
    public const int MaxEntries = 200;

    public static IReadOnlyList<JournalEntry> Prepend(IReadOnlyList<JournalEntry> existing, JournalEntry entry,
                                                      int cap = MaxEntries)
    {
        var list = new List<JournalEntry>(existing.Count + 1) { entry };
        list.AddRange(existing);
        if (cap >= 0 && list.Count > cap)
            list.RemoveRange(cap, list.Count - cap);   // drop the oldest tail, keep the newest `cap`
        return list;
    }
}

/// <summary>
/// Pure factory from a batch outcome to a <see cref="JournalEntry"/> — no I/O, so the mapping is unit-testable. The
/// <see cref="JournalEntry.Unconfirmed"/> list carries the same honest meaning in both directions: operations whose
/// claimed outcome the live readback CONTRADICTED. For an apply that's "reported applied but reads back off" (didn't
/// stick); for a revert it's the symmetric "reported reverted but reads back still active". Both come from the
/// matching <see cref="VerificationReport"/> (<c>TweakVerifier</c> / <c>RevertVerifier</c>) and are null when the
/// surface ran no re-probe. <see cref="JournalEntry.Confirmed"/> is the symmetric positive: the tweaks an apply
/// PROVED live (read back, value matched) — the airtight baseline drift detection later diffs against. Only an
/// apply seeds it: a revert's <see cref="VerificationReport.Confirmed"/> means "proven back to default", the
/// opposite claim, so <see cref="ForRevert"/> records no applied-live ids. Lives in Services because it reads
/// <see cref="BatchTweakResult"/>; mirrors how <c>TweakConflictDetector</c> pairs with the <c>TweakConflict</c>
/// model. The action labels are the user-facing French the journal page shows verbatim.
/// </summary>
public static class JournalReport
{
    public static JournalEntry ForApply(BatchTweakResult result, IEnumerable<string> tweakIds,
                                        VerificationReport? verification)
        => Build("Application", result, tweakIds, verification, appliedLive: verification?.Confirmed);

    public static JournalEntry ForRevert(BatchTweakResult result, IEnumerable<string> tweakIds,
                                         VerificationReport? verification)
        => Build("Restauration", result, tweakIds, verification, appliedLive: null);

    private static JournalEntry Build(string action, BatchTweakResult result, IEnumerable<string> tweakIds,
                                      VerificationReport? verification, IEnumerable<string>? appliedLive)
    {
        IReadOnlyList<string> unconfirmed = verification?.Unconfirmed.ToList() ?? new List<string>();
        IReadOnlyList<string> confirmed = appliedLive?.ToList() ?? new List<string>();
        return new JournalEntry(DateTime.UtcNow, action, result.Succeeded, result.Failed,
                                tweakIds.ToList(), unconfirmed)
        {
            Confirmed = confirmed
        };
    }
}

/// <summary>
/// Pure renderer from the journal to a plain-text report a user can save and share (forum, support thread, their
/// own records). No I/O — the file write is thin glue in the VM, so the report's SHAPE (header, a synthesis lead,
/// per-entry lines, the unconfirmed clause only when real) is unit-testable. Deliberately faithful to what was
/// recorded: it never re-derives or embellishes an outcome, it just lays out the stored honest tally in readable form.
/// </summary>
public static class JournalTextReport
{
    public static string Render(IReadOnlyList<JournalEntry> entries, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Journal des modifications");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine($"{entries.Count} entrée(s)");
        sb.AppendLine(new string('-', 48));

        if (entries.Count == 0)
        {
            sb.AppendLine("(aucune modification enregistrée)");
            return sb.ToString();
        }

        // Lead with the synthesis (the same pure JournalInsights the page's card shows) so a shared / exported trail
        // opens with the honest big picture — the tallies and the tweaks most often left unconfirmed — before the
        // per-entry detail. Timezone-dependent activity timestamps are included for the reader, not load-bearing.
        var stats = JournalInsights.Compute(entries);
        sb.AppendLine($"Synthèse : {stats.Summary}");
        if (stats.FirstActivityLabel is not null && stats.LastActivityLabel is not null)
            sb.AppendLine($"Activité du {stats.FirstActivityLabel} au {stats.LastActivityLabel}");
        if (stats.HasUnconfirmed)
        {
            sb.AppendLine("Tweaks le plus souvent non confirmés :");
            foreach (var f in stats.MostUnconfirmed)
                sb.AppendLine($"  - {f.Label}");
        }
        sb.AppendLine(new string('-', 48));
        sb.AppendLine();

        foreach (var e in entries)
        {
            sb.AppendLine($"[{e.LocalTimestampLabel}] {e.Summary}");
            sb.AppendLine($"  Tweaks : {e.TweakIdsLabel}");
            if (e.HasUnconfirmed)
                sb.AppendLine($"  Non confirmé(s) : {e.UnconfirmedLabel}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Pure aggregate of the change journal into a <see cref="JournalStatistics"/> — no I/O, so the counting rules
/// (apply vs revert by the recorded action, the activity span, and the ranking of the tweaks most often left
/// unconfirmed) are unit-testable without a store. Faithful to what was recorded: it sums real tallies and counts
/// real "unconfirmed" flags; it never derives a reliability/success rate the data can't honestly support — an
/// unconfirmed write is one with no readback, not a proven failure. A total function over possibly hand-edited /
/// loaded data, so it tolerates a blank id rather than throwing. Mirrors how <see cref="JournalReport"/> /
/// <see cref="JournalTextReport"/> sit beside the model they read.
/// </summary>
public static class JournalInsights
{
    public const int DefaultTopUnconfirmed = 5;

    public static JournalStatistics Compute(IReadOnlyList<JournalEntry> entries, int topUnconfirmed = DefaultTopUnconfirmed)
    {
        if (entries.Count == 0) return new JournalStatistics();

        int applyBatches = 0, revertBatches = 0, totalApplied = 0, totalReverted = 0, totalFailures = 0, totalUnconfirmed = 0;
        var first = DateTime.MaxValue;
        var last = DateTime.MinValue;

        // How many batches flagged each tweak as unconfirmed. Case-insensitive to match id handling across the
        // catalogue (SnapshotDiff / ProfileComposition); the dictionary keeps the first-seen spelling for display.
        var unconfirmedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
        {
            // "Restauration" is the only revert label the journal writes (JournalReport.ForRevert); anything else is
            // an apply. Classified by the stored label so the split matches exactly what the page shows.
            if (string.Equals(e.Action, "Restauration", StringComparison.Ordinal))
            {
                revertBatches++;
                totalReverted += e.Succeeded;
            }
            else
            {
                applyBatches++;
                totalApplied += e.Succeeded;
            }
            totalFailures += e.Failed;
            totalUnconfirmed += e.Unconfirmed.Count;
            if (e.TimestampUtc < first) first = e.TimestampUtc;
            if (e.TimestampUtc > last) last = e.TimestampUtc;

            foreach (var id in e.Unconfirmed)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                unconfirmedCounts.TryGetValue(id, out var n);
                unconfirmedCounts[id] = n + 1;
            }
        }

        var mostUnconfirmed = unconfirmedCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, topUnconfirmed))
            .Select(kv => new TweakFrequency(kv.Key, kv.Value))
            .ToList();

        return new JournalStatistics
        {
            TotalBatches = entries.Count,
            ApplyBatches = applyBatches,
            RevertBatches = revertBatches,
            TotalApplied = totalApplied,
            TotalReverted = totalReverted,
            TotalFailures = totalFailures,
            TotalUnconfirmed = totalUnconfirmed,
            FirstActivityUtc = first,
            LastActivityUtc = last,
            MostUnconfirmed = mostUnconfirmed
        };
    }
}

/// <summary>
/// JSON-file change journal under <c>%LOCALAPPDATA%\AurumTweaks\Journal\journal.json</c>, newest first and
/// bounded via <see cref="JournalLog"/>. Same persistence shape as <see cref="ProfileService"/>. Critically,
/// <see cref="RecordAsync"/> swallows and logs any I/O failure: journaling is a side-record of an apply, never a
/// reason the apply itself appears to fail — the honesty mandate cuts both ways here.
/// </summary>
public sealed class ApplyJournal : IApplyJournal
{
    private readonly string _file;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ApplyJournal()
    {
        var dir = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\AurumTweaks\\Journal");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "journal.json");
    }

    public async Task<IReadOnlyList<JournalEntry>> LoadAsync()
    {
        if (!File.Exists(_file)) return Array.Empty<JournalEntry>();
        try
        {
            await using var s = File.OpenRead(_file);
            var list = await JsonSerializer.DeserializeAsync<List<JournalEntry>>(s, JsonOpts);
            return list ?? (IReadOnlyList<JournalEntry>)Array.Empty<JournalEntry>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load journal {File}", _file);
            return Array.Empty<JournalEntry>();
        }
    }

    public async Task RecordAsync(JournalEntry entry)
    {
        try
        {
            var updated = JournalLog.Prepend(await LoadAsync(), entry);
            await using var s = File.Create(_file);
            await JsonSerializer.SerializeAsync(s, updated, JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to record journal entry");
        }
    }

    public Task ClearAsync()
    {
        try { if (File.Exists(_file)) File.Delete(_file); }
        catch (Exception ex) { Log.Warning(ex, "Failed to clear journal"); }
        return Task.CompletedTask;
    }
}
