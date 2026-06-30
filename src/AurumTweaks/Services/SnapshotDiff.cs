using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Resolves the on-disk path for a snapshot id — pure (no I/O) so the path-containment rule is unit-testable, the
/// same extraction pattern as <see cref="ProfilePath"/>. Builds from the file name ONLY
/// (<see cref="Path.GetFileName(string)"/>) so an id carrying ".." or a rooted path can never escape
/// <paramref name="snapshotsDir"/> — plain <see cref="Path.Combine(string, string)"/> would honour an absolute
/// second argument and write anywhere.
/// </summary>
public static class SnapshotPath
{
    public static string For(string snapshotsDir, string id)
        => Path.Combine(snapshotsDir, Path.GetFileName($"{id}.json"));
}

/// <summary>
/// Pure diff of two <see cref="SystemSnapshot"/>s — no I/O, so the load-bearing classification rules are pinned by
/// tests without a real machine, the same extraction pattern as <see cref="ProfileComposition"/> /
/// <see cref="TweakDetection"/>. The honesty surface lives here:
/// <list type="bullet">
/// <item>A <b>regression</b> (the alarming "something reverted my tweak" signal) is asserted ONLY when both ends are
///   readable: genuinely <see cref="TweakAppliedState.Applied"/> before, genuinely
///   <see cref="TweakAppliedState.NotApplied"/> now. The mirror is an <b>improvement</b>.</item>
/// <item>ANY transition touching <see cref="TweakAppliedState.Indeterminate"/> is <b>uncertain</b>, never a claimed
///   regression — we refuse to raise a false alarm when one side couldn't be read back.</item>
/// <item>A tweak present on only one side is <b>added</b> / <b>removed</b> (the catalogue changed), kept apart from a
///   real state change.</item>
/// </list>
/// </summary>
public static class SnapshotDiff
{
    public static SnapshotComparison Compare(SystemSnapshot baseline, SystemSnapshot current)
    {
        // Build id→entry maps defensively (indexer assignment, NOT ToDictionary): a snapshot file is persisted and
        // could be hand-edited/corrupt, so a duplicate id must never throw here — last occurrence wins and the diff
        // stays a pure total function. Case-insensitive ids match the rest of the catalogue (ProfileComposition).
        var baseMap = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in baseline.Entries) baseMap[e.TweakId] = e;
        var currMap = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in current.Entries) currMap[e.TweakId] = e;

        var ids = new HashSet<string>(baseMap.Keys, StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(currMap.Keys);

        var regressions = new List<SnapshotChange>();
        var improvements = new List<SnapshotChange>();
        var uncertain = new List<SnapshotChange>();
        var added = new List<SnapshotChange>();
        var removed = new List<SnapshotChange>();
        var unchanged = 0;

        foreach (var id in ids)
        {
            baseMap.TryGetValue(id, out var b);
            currMap.TryGetValue(id, out var c);
            // Prefer the most recent display name; fall back to whichever side has one, then the bare id.
            var name = c?.TweakName ?? b?.TweakName ?? id;

            if (b is null) { added.Add(new SnapshotChange(id, name, null, c!.State)); continue; }
            if (c is null) { removed.Add(new SnapshotChange(id, name, b.State, null)); continue; }
            if (b.State == c.State) { unchanged++; continue; }

            var change = new SnapshotChange(id, name, b.State, c.State);
            if (b.State == TweakAppliedState.Applied && c.State == TweakAppliedState.NotApplied)
                regressions.Add(change);
            else if (b.State == TweakAppliedState.NotApplied && c.State == TweakAppliedState.Applied)
                improvements.Add(change);
            else
                uncertain.Add(change);   // any transition involving Indeterminate → unclear, never a fake regression
        }

        return new SnapshotComparison
        {
            Regressions = Ordered(regressions),
            Improvements = Ordered(improvements),
            Uncertain = Ordered(uncertain),
            Added = Ordered(added),
            Removed = Ordered(removed),
            UnchangedCount = unchanged,
            Summary = BuildSummary(regressions.Count, improvements.Count, uncertain.Count,
                                   added.Count, removed.Count, unchanged)
        };
    }

    // Deterministic order in every bucket (name, then id) so the UI list and the tests are stable regardless of the
    // HashSet's unordered iteration.
    private static IReadOnlyList<SnapshotChange> Ordered(List<SnapshotChange> changes) =>
        changes.OrderBy(c => c.TweakName, StringComparer.OrdinalIgnoreCase)
               .ThenBy(c => c.TweakId, StringComparer.OrdinalIgnoreCase)
               .ToList();

    private static string BuildSummary(int reg, int imp, int unc, int add, int rem, int unchanged)
    {
        var parts = new List<string>();
        if (reg > 0) parts.Add($"{reg} régression(s)");
        if (imp > 0) parts.Add($"{imp} amélioration(s)");
        if (unc > 0) parts.Add($"{unc} incertain(s)");
        if (add > 0) parts.Add($"{add} ajouté(s)");
        if (rem > 0) parts.Add($"{rem} retiré(s)");
        return parts.Count == 0
            ? "Aucun changement depuis l'instantané de référence."
            : string.Join(" · ", parts) + $" · {unchanged} inchangé(s)";
    }
}

/// <summary>
/// Pure renderer from a <see cref="SnapshotComparison"/> to a plain-text drift report the user can save or paste
/// (forum, support thread). No I/O — the file write / clipboard copy is thin glue in the VM, so the report's SHAPE
/// (header, the bucket sections, a section ONLY when it has rows) is unit-testable. Faithful by construction: it
/// lays out exactly what <see cref="SnapshotDiff.Compare"/> classified, in the same order and with the same honest
/// wording the page shows — it never re-derives or embellishes an outcome. When the two sides were captured by
/// different Aurum builds it adds the <see cref="SnapshotVersionProvenance"/> caveat, so a cross-version difference
/// isn't misread as a real drift. Mirrors <see cref="JournalTextReport"/>.
/// </summary>
public static class SnapshotReport
{
    public static string Render(SnapshotComparison comparison, string baselineLabel, string targetLabel, DateTime generatedUtc,
        string? baselineVersion = null, string? targetVersion = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Comparaison d'instantané");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Référence : {baselineLabel} → {targetLabel}");
        sb.AppendLine(comparison.Summary);
        // When the two sides come from different Aurum builds, warn that a difference may be a version change, not a real
        // drift — emitted only when that can be said honestly (both versions known and differing), omitted otherwise.
        var versionCaveat = SnapshotVersionProvenance.CrossVersionCaveat(baselineVersion, targetVersion);
        if (versionCaveat is not null)
            sb.AppendLine(versionCaveat);
        sb.AppendLine(new string('-', 48));

        // Same order as the page so the file reads like the panel. Each section is emitted ONLY when it has rows —
        // an empty heading would imply a category of change that didn't happen (the JournalTextReport rule).
        AppendSection(sb, "RÉGRESSIONS (étaient appliqués, ne le sont plus)", comparison.Regressions);
        AppendSection(sb, "INCERTAINS (un état n'a pas pu être relu — non comptés comme régression)", comparison.Uncertain);
        AppendSection(sb, "AMÉLIORATIONS (appliqués depuis la référence)", comparison.Improvements);
        AppendSection(sb, "AJOUTÉS AU CATALOGUE depuis la référence", comparison.Added);
        AppendSection(sb, "RETIRÉS DU CATALOGUE depuis la référence", comparison.Removed);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string heading, IReadOnlyList<SnapshotChange> rows)
    {
        if (rows.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine(heading + " :");
        foreach (var r in rows)
            sb.AppendLine($"  - {r.TweakName} [{r.TweakId}] : {r.TransitionLabel}");
    }
}

/// <summary>
/// The one honest reason a snapshot COMPARISON cares about build versions: when its two sides were captured by
/// DIFFERENT Aurum builds, a difference between them can come from a change in what a tweak DOES across versions (a
/// registry value the catalogue redefined, a tweak renamed), not a real drift on the machine. This pure helper returns
/// that caveat line — and ONLY when it can be stated honestly: both versions must be KNOWN and actually differ. An
/// unknown side (an older snapshot, or a foreign file that never recorded its version) yields no caveat, because a
/// difference we can't see must not be claimed; identical versions yield none either. No I/O — a value in, a string or
/// null out — so the rule is unit-testable. The single-state report instead stamps « Version à la capture » directly,
/// because it has exactly one capture; only a two-capture diff needs this cross-version reconciliation.
/// </summary>
public static class SnapshotVersionProvenance
{
    public static string? CrossVersionCaveat(string? baselineVersion, string? targetVersion)
    {
        var b = baselineVersion?.Trim();
        var t = targetVersion?.Trim();
        // A side we don't know → no confident claim; same build → nothing to warn about. Only a known, real gap warns.
        if (string.IsNullOrEmpty(b) || string.IsNullOrEmpty(t)) return null;
        if (string.Equals(b, t, StringComparison.OrdinalIgnoreCase)) return null;
        return $"Attention : instantanés de versions différentes ({b} → {t}) — un écart peut venir d'un changement entre ces versions d'Aurum, pas d'une dérive réelle de la machine.";
    }
}

/// <summary>
/// Pure renderer for a SINGLE <see cref="SystemSnapshot"/> as a plain-text state report — the human-readable
/// counterpart to the machine-readable JSON export (<see cref="SnapshotPortability"/>). A user pastes this on a forum
/// or support thread to show « voici l'état exact de mes tweaks » without sharing a JSON file nobody can read at a
/// glance, and without the awkward capture-twice-and-compare detour the <see cref="SnapshotReport"/> drift renderer
/// would otherwise force. No I/O — the clipboard copy / file write is thin glue in the VM — so the report's SHAPE is
/// unit-testable. Honest by construction:
/// <list type="bullet">
/// <item>tweaks are grouped by the SAME tri-state the page detected — Appliqués / Non appliqués / Indéterminés — and an
///   <see cref="TweakAppliedState.Indeterminate"/> tweak is listed under its OWN heading, NEVER folded into
///   « appliqué » or « non appliqué »;</item>
/// <item>the header carries the capture time AND the build version that took the capture — both frozen, because the
///   state is a HISTORICAL probe (it may have drifted since, and the file may be reopened on another build) — plus the
///   per-state counts (<see cref="SystemSnapshot.StateSummaryLabel"/>) so the totals stay visible even where an empty
///   section is omitted (the version line is omitted, never guessed, for a record that didn't store one);</item>
/// <item>the footer keeps the load-bearing caveat — « indéterminé » means the state could not be read back (a shell
///   tweak with no reliable re-read), NOT « désactivé ».</item>
/// </list>
/// Mirrors <see cref="SnapshotReport"/> (the comparison renderer) and <see cref="JournalTextReport"/>.
/// </summary>
public static class SnapshotStateReport
{
    public static string Render(SystemSnapshot snapshot, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — État de l'instantané");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Instantané : {snapshot.DisplayLabel}");
        sb.AppendLine($"Capturé le {snapshot.LocalTimestampLabel}");
        // The build that CAPTURED this snapshot — frozen at capture, NOT the version reading it back, because a snapshot
        // can be exported on one build and reopened on another. Omitted (never guessed) when the record didn't store it.
        if (!string.IsNullOrWhiteSpace(snapshot.AppVersion))
            sb.AppendLine($"Version à la capture : {snapshot.AppVersion.Trim()}");
        sb.AppendLine(snapshot.StateSummaryLabel);
        sb.AppendLine(new string('=', 48));

        if (snapshot.Entries.Count == 0)
        {
            // A captured/imported snapshot always carries entries, but keep the renderer a total function: an empty
            // record says so plainly rather than printing bare headings that would read as "nothing is applied".
            sb.AppendLine();
            sb.AppendLine("Aucun tweak enregistré dans cet instantané.");
            return sb.ToString();
        }

        // Each section is emitted ONLY when it has rows — an empty heading would imply a category that isn't there.
        // The header's StateSummaryLabel already carries every count, so omitting a 0-row section loses no information.
        // Headings stay plain words + count (the « indéterminé ≠ désactivé » clarifier lives in the always-present
        // footer, so it can't read as part of a row count).
        AppendSection(sb, "APPLIQUÉS", snapshot.Entries, TweakAppliedState.Applied);
        AppendSection(sb, "NON APPLIQUÉS", snapshot.Entries, TweakAppliedState.NotApplied);
        AppendSection(sb, "INDÉTERMINÉS", snapshot.Entries, TweakAppliedState.Indeterminate);

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("État détecté par lecture réelle du registre et des services AU MOMENT DE LA CAPTURE — il a pu changer depuis.");
        sb.AppendLine("« Indéterminé » = l'état n'a pas pu être relu (ex. tweak appliqué par commande shell sans relecture fiable), PAS « désactivé ».");
        return sb.ToString();
    }

    // Faithful to SnapshotDiff's ordering (name, then id) so the report is deterministic regardless of catalogue order.
    private static void AppendSection(StringBuilder sb, string heading, IReadOnlyList<SnapshotEntry> entries, TweakAppliedState state)
    {
        var rows = entries.Where(e => e.State == state)
                          .OrderBy(e => e.TweakName, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(e => e.TweakId, StringComparer.OrdinalIgnoreCase)
                          .ToList();
        if (rows.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"{heading} ({rows.Count}) :");
        foreach (var e in rows)
            sb.AppendLine($"  - {e.TweakName} [{e.TweakId}]");
    }
}

/// <summary>
/// Pure (de)serialization for moving a single <see cref="SystemSnapshot"/> in or out of a portable file — so a
/// carefully captured baseline survives a Windows reinstall (snapshots live under <c>%LOCALAPPDATA%</c>, which a
/// reinstall wipes) or can be carried to another machine / shared with the community. The honesty surface is
/// <see cref="TryImport"/>: a foreign or hand-edited file is VALIDATED, not trusted — unreadable JSON and an
/// entry-less snapshot are refused with a French reason rather than imported as a silent, uncomparable record, and
/// every import is given a FRESH id so it can never overwrite an existing local snapshot that happens to share the
/// original's id. No disk I/O lives here (the file read/write is thin glue in <see cref="SnapshotService"/>), so
/// these rules are unit-testable without a real file. Mirrors the <see cref="SnapshotDiff"/> extraction pattern.
/// </summary>
public static class SnapshotPortability
{
    /// <summary>The one canonical JSON shape for snapshots — on disk AND in an exported file: indented (a shared file
    /// is meant to be human-readable), case-insensitive on read (tolerate a hand-edited file), enums as strings (a
    /// saved state stays readable and survives a future reorder of <see cref="TweakAppliedState"/>). Reused by
    /// <see cref="SnapshotService"/> so the store and the portable file can never drift to different shapes.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(SystemSnapshot snapshot) => JsonSerializer.Serialize(snapshot, JsonOptions);

    /// <summary>
    /// Parse + validate + normalize a snapshot read from a portable file. Pure (no disk): returns false with a French
    /// <paramref name="error"/> when the file can't be trusted; on success <paramref name="snapshot"/> carries a fresh
    /// id (never collides with a stored one), only well-formed entries, and a sane capture time.
    /// </summary>
    public static bool TryImport(string json, DateTime nowUtc, out SystemSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = null;

        SystemSnapshot? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SystemSnapshot>(json, JsonOptions);
        }
        catch (JsonException)
        {
            error = "Fichier d'instantané illisible : JSON invalide.";
            return false;
        }
        if (parsed is null)
        {
            error = "Fichier d'instantané vide ou illisible.";
            return false;
        }

        // Drop malformed rows (a hand-edited / foreign file can carry a null entry or one with no id) — they can't be
        // matched to a tweak, so keeping them would only pollute a later diff. A null Entries list is treated as empty.
        var entries = (parsed.Entries ?? new List<SnapshotEntry>())
            .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.TweakId))
            .ToList();
        if (entries.Count == 0)
        {
            error = "L'instantané importé ne contient aucun tweak exploitable.";
            return false;
        }

        snapshot = new SystemSnapshot
        {
            Id = Guid.NewGuid().ToString(),   // fresh id: an import never overwrites a stored snapshot sharing the original's
            // Preserve the original capture time (it's a historical record from elsewhere); substitute "now" only when
            // the file carries no real time — the zero date — so an import never sorts as, or displays, year 0001.
            CapturedUtc = parsed.CapturedUtc == default ? nowUtc : parsed.CapturedUtc,
            Label = parsed.Label?.Trim() ?? string.Empty,
            // Preserve the version that CAPTURED the file — historical provenance from elsewhere, exactly like the capture
            // time above; a file that never recorded it (older snapshot / foreign file) stays empty, rendered as omitted.
            AppVersion = parsed.AppVersion?.Trim() ?? string.Empty,
            Entries = entries
        };
        return true;
    }
}
