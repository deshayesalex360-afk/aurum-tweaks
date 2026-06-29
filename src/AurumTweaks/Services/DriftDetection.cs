using System;
using System.Collections.Generic;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure resolver of the user's STANDING intent per tweak from the change journal — no I/O, fully unit-testable. For
/// each tweak id it walks the journal newest-first and lets the MOST RECENT batch that touched the id decide:
///  - newest touch is an "Application" that PROVED the id live (id ∈ that batch's <see cref="JournalEntry.Confirmed"/>)
///    → the id is "supposed to be on, and we once saw it on": a drift candidate.
///  - newest touch is a "Restauration" (intended off), or an apply that did NOT confirm it (failed / read back wrong,
///    so no proven-on baseline) → NOT a candidate.
/// Older entries for an already-decided id are ignored: a later revert supersedes an earlier confirmed apply, and a
/// later failed re-apply supersedes an earlier success — we refuse to claim drift from a state the latest action
/// never actually established. That conservatism is the honesty guarantee; the diff against the live machine is
/// <see cref="DriftAnalysis"/>. Mirrors how <see cref="JournalInsights"/> sits beside the journal it reads.
/// </summary>
public static class JournalApplyIntent
{
    public static IReadOnlyList<string> Resolve(IReadOnlyList<JournalEntry> entriesNewestFirst)
    {
        // Once an id appears in a batch (the newest one, since the list is newest-first) its fate is sealed; later
        // (older) entries for it are noise. Case-insensitive to match id handling across the catalogue.
        var decided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        foreach (var e in entriesNewestFirst)
        {
            var confirmed = new HashSet<string>(e.Confirmed, StringComparer.OrdinalIgnoreCase);
            // "Restauration" is the only revert label the journal writes; anything else is an apply.
            var isApply = !string.Equals(e.Action, "Restauration", StringComparison.Ordinal);

            foreach (var id in e.TweakIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!decided.Add(id)) continue;             // a newer batch already settled this id
                if (isApply && confirmed.Contains(id))      // proven live in its most recent apply → candidate
                    candidates.Add(id);
            }
        }

        return candidates;
    }
}

/// <summary>
/// Pure diff of "what should be on (and was proven on)" against "what the machine reports now" — no I/O. Takes the
/// drift-candidate ids from <see cref="JournalApplyIntent.Resolve"/> and the live per-id states, and buckets each:
///  - <see cref="TweakAppliedState.NotApplied"/> → DRIFTED (proven on before, proven off now).
///  - <see cref="TweakAppliedState.Applied"/>    → persisted (still on; the happy path).
///  - <see cref="TweakAppliedState.Indeterminate"/>, or no live reading at all → Unverifiable (no honest claim).
/// Indeterminate and missing ids are NEVER counted as drift: a tweak we can't read back now must not be reported as
/// regressed. The result is the enum-free <see cref="DriftReport"/>. Pairs with <see cref="JournalApplyIntent"/> the
/// way <c>RevertVerifier</c> pairs with <c>TweakVerifier</c>.
/// </summary>
public static class DriftAnalysis
{
    public static DriftReport Detect(IEnumerable<string> intendedLiveIds,
                                     IReadOnlyDictionary<string, TweakAppliedState> liveStates)
    {
        var drifted = new List<string>();
        int persisted = 0, unverifiable = 0;

        foreach (var id in intendedLiveIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!liveStates.TryGetValue(id, out var state))
            {
                unverifiable++;   // no live reading for this id (e.g. a tweak dropped from the catalogue) — no claim
                continue;
            }

            switch (state)
            {
                case TweakAppliedState.NotApplied: drifted.Add(id); break;   // proven on before, proven off now
                case TweakAppliedState.Applied:    persisted++; break;
                default:                           unverifiable++; break;    // Indeterminate → unreadable now, no claim
            }
        }

        return new DriftReport(drifted, persisted, unverifiable);
    }
}
