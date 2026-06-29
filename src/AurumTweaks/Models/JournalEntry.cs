using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AurumTweaks.Models;

/// <summary>
/// One recorded entry in the change journal: a single apply/revert batch the app ran, with the honest tally it
/// reported and — for an apply — which tweaks the post-apply verification could NOT confirm were live. This is
/// the persisted audit trail the mandate calls for: the user can see exactly what the app changed, when, and
/// whether it stuck, long after the footer status has scrolled away. Pure data + display-only computed labels
/// (the View binds these directly); the factory that fills it from a batch result lives in Services.
/// </summary>
public sealed record JournalEntry(
    DateTime TimestampUtc,
    string Action,
    int Succeeded,
    int Failed,
    IReadOnlyList<string> TweakIds,
    IReadOnlyList<string> Unconfirmed)
{
    /// <summary>
    /// The tweaks this apply batch's post-apply verification POSITIVELY confirmed live — read back from the machine
    /// at apply time with the value matching what was expected (<c>VerificationReport.Confirmed</c>). Distinct from
    /// the bare <see cref="TweakIds"/> attempt list: a tweak that failed, or applied-but-read-back-wrong, is NOT in
    /// here. That distinction is the honest foundation drift detection needs — only a tweak once PROVEN on can later
    /// be PROVEN drifted-off; a never-confirmed tweak now reading off is consistent with it never having stuck, so
    /// calling that "drift" would be a fabricated alarm. A revert batch confirms nothing applied, so it stays empty.
    /// Non-positional with an empty default so journals written before this field existed load as "" rather than
    /// null (System.Text.Json leaves the initializer when the JSON key is absent) — backward-compatible by design.
    /// </summary>
    public IReadOnlyList<string> Confirmed { get; init; } = Array.Empty<string>();

    /// <summary>True when the batch reported any failed tweak — the entry must not read like a clean run.</summary>
    [JsonIgnore] public bool HasFailures => Failed > 0;

    /// <summary>True when post-apply verification flagged at least one tweak it couldn't confirm was live.</summary>
    [JsonIgnore] public bool HasUnconfirmed => Unconfirmed.Count > 0;

    [JsonIgnore] public string TweakIdsLabel => string.Join(", ", TweakIds);

    [JsonIgnore] public string UnconfirmedLabel => string.Join(", ", Unconfirmed);

    /// <summary>The honest one-line headline: the action, the real success count, and — only when they exist —
    /// the failures and the writes verification couldn't confirm. A clean run shows none of the extra clauses.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var s = $"{Action} · {Succeeded} réussi(s)";
            if (HasFailures) s += $", {Failed} échec(s)";
            if (HasUnconfirmed) s += $" · {Unconfirmed.Count} non confirmé(s)";
            return s;
        }
    }

    /// <summary>Local wall-clock label for the row (display only — the stored time is always UTC).</summary>
    [JsonIgnore] public string LocalTimestampLabel => TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}

/// <summary>
/// One tweak id paired with how many journal batches flagged it — the ranked rows of
/// <see cref="JournalStatistics.MostUnconfirmed"/>. A plain count of a recorded event, never a derived
/// "reliability" the data can't honestly support.
/// </summary>
public sealed record TweakFrequency(string TweakId, int Count)
{
    public string Label => $"{TweakId} — {Count}×";
}

/// <summary>
/// A read-only synthesis of the whole change journal: how many batches ran (applies vs reverts), how many
/// tweak-changes those batches reported applied / reverted, how many failed, how many writes the post-apply
/// verification could NOT confirm, the activity span, and — the diagnostic part — the tweaks most often left
/// unconfirmed across all batches. Built by <see cref="AurumTweaks.Services.JournalInsights.Compute"/> as a pure
/// aggregate of what was recorded: it sums real tallies and counts real "unconfirmed" flags, and deliberately
/// never derives a success/reliability rate (an "unconfirmed" write is a write with no readback, not a proven
/// failure). Display-only computed labels — the View binds these. Never persisted, so no JSON attributes.
/// </summary>
public sealed record JournalStatistics
{
    public int TotalBatches { get; init; }
    public int ApplyBatches { get; init; }
    public int RevertBatches { get; init; }
    public int TotalApplied { get; init; }
    public int TotalReverted { get; init; }
    public int TotalFailures { get; init; }
    public int TotalUnconfirmed { get; init; }
    public DateTime? FirstActivityUtc { get; init; }
    public DateTime? LastActivityUtc { get; init; }
    public IReadOnlyList<TweakFrequency> MostUnconfirmed { get; init; } = Array.Empty<TweakFrequency>();

    public bool HasActivity => TotalBatches > 0;
    public bool HasFailures => TotalFailures > 0;
    public bool HasUnconfirmed => MostUnconfirmed.Count > 0;

    public string? FirstActivityLabel => FirstActivityUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string? LastActivityLabel => LastActivityUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    /// <summary>Honest one-line headline. An empty journal reads as an explicit "no activity"; otherwise the batch
    /// breakdown, with the failure / unconfirmed clauses shown ONLY when non-zero (a clean history shows neither) —
    /// the same "no extra clause unless it really happened" rule as <see cref="JournalEntry.Summary"/>.</summary>
    public string Summary
    {
        get
        {
            if (!HasActivity) return "Aucune activité enregistrée.";
            var s = $"{TotalBatches} lot(s) · {ApplyBatches} application(s), {RevertBatches} restauration(s)";
            if (HasFailures) s += $" · {TotalFailures} échec(s)";
            if (TotalUnconfirmed > 0) s += $" · {TotalUnconfirmed} non confirmé(s)";
            return s;
        }
    }
}
