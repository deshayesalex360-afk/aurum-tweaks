using System;
using System.Collections.Generic;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure mapping from a GPU-OC apply/reset outcome to a <see cref="JournalEntry"/>, so an overclock lands
/// in the same "what did Aurum change" record as every other mutation (the Journal page and its insights).
/// Before this, a GPU OC write was invisible in the journal — the app omitted its most impactful action.
/// A GPU write is either read-back-confirmed by the service or a failure — there is no "applied but
/// unconfirmed" state — so a success lands entirely in <see cref="JournalEntry.Confirmed"/> and a failure
/// in <see cref="JournalEntry.Failed"/>. The caller stamps the timestamp so this stays deterministic.
/// Side-effect-free → unit-testable.
/// </summary>
public static class GpuOcJournal
{
    /// <summary>Same action strings the tweak/profile paths use, so JournalInsights classifies an OC apply
    /// as an application and an OC reset as a restoration.</summary>
    public const string ApplyAction = "Application";
    public const string ResetAction = "Restauration";

    public static JournalEntry ForApply(GpuOcApplyResult result, DateTime timestampUtc)
        => Build(ApplyAction, result, timestampUtc);

    public static JournalEntry ForReset(GpuOcApplyResult result, DateTime timestampUtc)
        => Build(ResetAction, result, timestampUtc);

    private static JournalEntry Build(string action, GpuOcApplyResult result, DateTime timestampUtc)
    {
        // The descriptor is free-form (the journal's "TweakIds" are rendered verbatim); we lead with a
        // stable "GPU OC —" marker so an overclock is scannable among tweak ids.
        string descriptor = result.Success
            ? $"GPU OC — {result.Applied}"
            : $"GPU OC (échec) — {result.Error}";
        var ids = new List<string> { descriptor };

        return new JournalEntry(
            timestampUtc,
            action,
            Succeeded: result.Success ? 1 : 0,
            Failed: result.Success ? 0 : 1,
            TweakIds: ids,
            Unconfirmed: Array.Empty<string>())
        {
            // A successful GPU write is already read-back-confirmed by the service → it belongs in Confirmed.
            Confirmed = result.Success ? ids : Array.Empty<string>(),
        };
    }
}
