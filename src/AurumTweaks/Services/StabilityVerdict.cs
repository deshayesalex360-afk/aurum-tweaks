namespace AurumTweaks.Services;

/// <summary>
/// The four honest outcomes of a stability run (RAM memtest or CPU coherence test), in priority order.
/// </summary>
public enum StabilityOutcome
{
    /// <summary>Couldn't run at all (e.g. no memory could be allocated). Not cancelled, not completed.</summary>
    DidNotRun,

    /// <summary>At least one error was caught. A real failure — meaningful even if the run was then cancelled.</summary>
    Unstable,

    /// <summary>Stopped early with zero errors. Proves nothing about stability.</summary>
    Cancelled,

    /// <summary>Ran to completion with zero errors.</summary>
    Stable,
}

/// <summary>
/// Pure classifier shared by the RAM and CPU stability pages so a SINGLE rule decides the verdict shown to
/// the user. This is the honesty-critical decision of the whole stability surface, so it is extracted here
/// and pinned by tests rather than re-implemented per page.
///
/// <para>The load-bearing invariants: a run NEVER reads as <see cref="StabilityOutcome.Stable"/> unless it
/// completed with zero errors, and a caught miscalculation/bit-flip OUTRANKS a cancellation — an error is
/// meaningful even on a partial run ("des cœurs sains ne se trompent jamais", "de la vraie RAM ne doit
/// jamais échouer"). That errors-first ordering matches what the verdict cards and the services' own notes
/// already do, so the status line can never under-state a real failure as a mere "interrompu".</para>
/// </summary>
public static class StabilityVerdict
{
    public static StabilityOutcome Classify(bool completed, bool cancelled, int errorCount)
    {
        if (errorCount > 0) return StabilityOutcome.Unstable;   // errors first: a real failure outranks a cancel
        if (cancelled) return StabilityOutcome.Cancelled;       // clean partial run — proves nothing
        if (completed) return StabilityOutcome.Stable;          // ran fully, zero errors
        return StabilityOutcome.DidNotRun;                      // couldn't execute at all
    }
}
