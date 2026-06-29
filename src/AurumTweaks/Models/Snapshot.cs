using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using AurumTweaks.Services;

namespace AurumTweaks.Models;

/// <summary>
/// One tweak's detected state at the instant a snapshot was captured. The display name is stored ALONGSIDE the id
/// on purpose: a snapshot is a historical record, so it must stay readable even after the catalogue later renames
/// or drops the tweak — the diff shows what you saw then, not what the live catalogue says now.
/// </summary>
public sealed record SnapshotEntry(string TweakId, string TweakName, TweakAppliedState State);

/// <summary>
/// A point-in-time picture of the WHOLE tweak catalogue's live applied/not-applied/indeterminate state — the
/// "before" a later "now" is compared against to reveal drift (a tweak that WAS applied and silently isn't any
/// more). Persisted as JSON under <c>%LOCALAPPDATA%\AurumTweaks\Snapshots</c>. Honest by construction: every entry
/// is a real per-tweak probe (<see cref="TweakAppliedState.Indeterminate"/> stays Indeterminate, never a guessed ✓).
/// </summary>
public sealed class SystemSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("capturedUtc")]
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>An optional user label ("avant MAJ Windows"). Empty falls back to the timestamp for display.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<SnapshotEntry> Entries { get; set; } = new();

    // Display-only computed labels (the View binds these; the stored time is always UTC).
    [JsonIgnore] public int AppliedCount => Entries.Count(e => e.State == TweakAppliedState.Applied);
    [JsonIgnore] public int NotAppliedCount => Entries.Count(e => e.State == TweakAppliedState.NotApplied);
    [JsonIgnore] public int IndeterminateCount => Entries.Count(e => e.State == TweakAppliedState.Indeterminate);
    [JsonIgnore] public string LocalTimestampLabel => CapturedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    [JsonIgnore] public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? LocalTimestampLabel : Label;
    [JsonIgnore] public string StateSummaryLabel =>
        $"{AppliedCount} appliqué(s) · {NotAppliedCount} non · {IndeterminateCount} indéterminé(s)";
}

/// <summary>
/// One tweak's change between two snapshots. <see cref="From"/> null = the tweak exists only in the newer snapshot
/// (added to the catalogue since the baseline); <see cref="To"/> null = it exists only in the older one (removed).
/// </summary>
public sealed record SnapshotChange(string TweakId, string TweakName, TweakAppliedState? From, TweakAppliedState? To)
{
    public string FromLabel => StateLabel(From);
    public string ToLabel => StateLabel(To);

    /// <summary>"Appliqué → Non appliqué" — the human-readable transition for the row.</summary>
    public string TransitionLabel => $"{FromLabel} → {ToLabel}";

    private static string StateLabel(TweakAppliedState? state) => state switch
    {
        TweakAppliedState.Applied => "Appliqué",
        TweakAppliedState.NotApplied => "Non appliqué",
        TweakAppliedState.Indeterminate => "Indéterminé",
        _ => "Absent"   // null → the tweak wasn't in that snapshot at all
    };
}

/// <summary>
/// The result of diffing two snapshots. Buckets are deliberately separated by MEANING, not just "changed": a
/// <see cref="Regressions"/> entry (was genuinely Applied, is now genuinely NotApplied) is the alarming
/// "something reverted my tweak" signal the whole feature exists to surface, and it is kept distinct from
/// <see cref="Uncertain"/> — any transition touching <see cref="TweakAppliedState.Indeterminate"/>, where we
/// refuse to claim a confident regression. Computed by the pure <see cref="SnapshotDiff"/>.
/// </summary>
public sealed class SnapshotComparison
{
    /// <summary>Applied → NotApplied: a tweak that was on and silently isn't any more. The killer drift signal.</summary>
    public IReadOnlyList<SnapshotChange> Regressions { get; init; } = Array.Empty<SnapshotChange>();

    /// <summary>NotApplied → Applied: a tweak that became active since the baseline.</summary>
    public IReadOnlyList<SnapshotChange> Improvements { get; init; } = Array.Empty<SnapshotChange>();

    /// <summary>Any other change involving Indeterminate — honestly reported as unclear, never a claimed regression.</summary>
    public IReadOnlyList<SnapshotChange> Uncertain { get; init; } = Array.Empty<SnapshotChange>();

    /// <summary>Tweaks present only in the newer snapshot (catalogue grew since the baseline).</summary>
    public IReadOnlyList<SnapshotChange> Added { get; init; } = Array.Empty<SnapshotChange>();

    /// <summary>Tweaks present only in the baseline (catalogue shrank — the tweak no longer exists here).</summary>
    public IReadOnlyList<SnapshotChange> Removed { get; init; } = Array.Empty<SnapshotChange>();

    public int UnchangedCount { get; init; }

    /// <summary>The honest one-line headline (French) — empty buckets contribute no clause.</summary>
    public string Summary { get; init; } = string.Empty;

    public bool HasRegressions => Regressions.Count > 0;
    public bool HasImprovements => Improvements.Count > 0;
    public bool HasUncertain => Uncertain.Count > 0;
    public bool HasAdded => Added.Count > 0;
    public bool HasRemoved => Removed.Count > 0;
    public bool HasAnyChange => HasRegressions || HasImprovements || HasUncertain || HasAdded || HasRemoved;
}
