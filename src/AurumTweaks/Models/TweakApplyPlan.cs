using System;
using System.Collections.Generic;
using System.Linq;

namespace AurumTweaks.Models;

/// <summary>One concrete operation in an apply plan, tagged with the tweak it belongs to.</summary>
public sealed record PlannedOperation(string TweakId, OperationSummary Operation)
{
    /// <summary>This operation will run but the engine cannot automatically undo it — its revert is the
    /// <see cref="TweakOperationSummary.NoRevert"/> sentinel. The one risk a reversible-by-promise engine must own;
    /// computed from that sentinel so the plan's count, its banner, and the per-op marker share ONE definition.</summary>
    public bool IsIrreversible => Operation.Revert == TweakOperationSummary.NoRevert;
}

/// <summary>A per-kind tally ("Registre × 12") for the plan's at-a-glance header.</summary>
public sealed record KindCount(string Kind, int Count);

/// <summary>
/// The full, reviewable consequence of applying a set of selected tweaks — built once, before anything runs, so
/// the user gives INFORMED consent to a batch of as-admin changes. Honesty mandate, made concrete: every
/// operation that will execute is enumerated (reusing <see cref="TweakOperationSummary"/>, the same data the
/// engine dispatches on), the kinds are tallied, a reboot requirement is surfaced, and — critically — the plan
/// admits HOW MANY operations the engine cannot automatically undo (<see cref="IrreversibleCount"/>),
/// rather than presenting a batch as cleanly reversible when it isn't.
/// </summary>
public sealed record ApplyPlan(
    IReadOnlyList<PlannedOperation> Operations,
    IReadOnlyList<KindCount> CountsByKind,
    int TweakCount,
    bool RequiresReboot,
    int IrreversibleCount)
{
    public int TotalOperations => Operations.Count;

    /// <summary>Whether ANY operation can't be auto-reverted — derived from <see cref="IrreversibleCount"/> so the
    /// yes/no banner and the exact count can never disagree (the count is the honest quantity, this is the gate).</summary>
    public bool HasIrreversible => IrreversibleCount > 0;
}

/// <summary>Pure builder for an <see cref="ApplyPlan"/>. No I/O — pinned by a value table in the tests.</summary>
public static class TweakApplyPlan
{
    public static ApplyPlan Build(IEnumerable<Tweak> tweaks)
    {
        var list = tweaks?.ToList() ?? new List<Tweak>();

        var ops = list
            .SelectMany(t => TweakOperationSummary.Summarize(t).Select(op => new PlannedOperation(t.Id, op)))
            .ToList();

        // Tally by the display kind; biggest group first, then alphabetical so the header is deterministic.
        var counts = ops
            .GroupBy(o => o.Operation.Kind, StringComparer.Ordinal)
            .Select(g => new KindCount(g.Key, g.Count()))
            .OrderByDescending(k => k.Count)
            .ThenBy(k => k.Kind, StringComparer.Ordinal)
            .ToList();

        var requiresReboot = list.Any(t => t.RequiresReboot);
        var irreversibleCount = ops.Count(o => o.IsIrreversible);

        return new ApplyPlan(ops, counts, list.Count, requiresReboot, irreversibleCount);
    }
}
