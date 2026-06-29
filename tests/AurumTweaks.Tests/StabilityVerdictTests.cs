using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the single honesty-critical decision behind both stability pages (RAM memtest + CPU coherence test):
/// how a run's three raw facts (completed / cancelled / errorCount) become the verdict the user sees.
/// The stakes: a "STABLE" shown on a cancelled or errored run, or a real miscalculation downgraded to a
/// harmless "interrompu", would lure a user into trusting an unstable OC. So this proves:
///   • STABLE is reachable ONLY when the run completed with zero errors;
///   • a caught error OUTRANKS a cancellation (errors-first), matching the verdict cards and service notes —
///     a partial run that already caught a bit-flip is INSTABLE, never a mere "interrompu";
///   • a clean cancel proves nothing (Cancelled), and a run that couldn't execute is DidNotRun.
/// </summary>
public class StabilityVerdictTests
{
    [Fact]
    public void CompletedWithNoErrors_IsStable()
        => Assert.Equal(StabilityOutcome.Stable,
            StabilityVerdict.Classify(completed: true, cancelled: false, errorCount: 0));

    [Fact]
    public void CompletedWithErrors_IsUnstable()
        => Assert.Equal(StabilityOutcome.Unstable,
            StabilityVerdict.Classify(completed: true, cancelled: false, errorCount: 3));

    [Fact]
    public void CancelledWithNoErrors_IsCancelled_ProvesNothing()
        => Assert.Equal(StabilityOutcome.Cancelled,
            StabilityVerdict.Classify(completed: false, cancelled: true, errorCount: 0));

    // The load-bearing case the old per-VM verdict got wrong: a run that caught a real miscalculation and was
    // THEN stopped must read as INSTABLE, not be masked as a harmless "interrompu".
    [Fact]
    public void CancelledButWithErrors_IsUnstable_ErrorsOutrankCancellation()
        => Assert.Equal(StabilityOutcome.Unstable,
            StabilityVerdict.Classify(completed: false, cancelled: true, errorCount: 1));

    [Fact]
    public void NeitherCompletedNorCancelled_DidNotRun()
        => Assert.Equal(StabilityOutcome.DidNotRun,
            StabilityVerdict.Classify(completed: false, cancelled: false, errorCount: 0));

    // Exhaustive guarantee over the whole input matrix: Stable iff (completed && no errors). Nothing else.
    [Theory]
    [InlineData(true, false, 0, StabilityOutcome.Stable)]
    [InlineData(true, false, 5, StabilityOutcome.Unstable)]
    [InlineData(true, true, 0, StabilityOutcome.Cancelled)]  // degenerate (services guarantee completed==!cancelled); cancel still outranks completed → never Stable
    [InlineData(false, true, 0, StabilityOutcome.Cancelled)]
    [InlineData(false, true, 2, StabilityOutcome.Unstable)]
    [InlineData(false, false, 0, StabilityOutcome.DidNotRun)]
    [InlineData(false, false, 9, StabilityOutcome.Unstable)]
    public void Classify_CoversTheMatrix(bool completed, bool cancelled, int errors, StabilityOutcome expected)
        => Assert.Equal(expected, StabilityVerdict.Classify(completed, cancelled, errors));

    // The one invariant that matters most, stated directly: STABLE ⟺ completed AND zero errors.
    [Theory]
    [InlineData(true, false, 0, true)]
    [InlineData(true, true, 0, false)]   // a "completed" flag never overrides an active cancellation into Stable
    [InlineData(true, false, 1, false)]
    [InlineData(false, false, 0, false)]
    public void StableRequiresCompletedAndClean(bool completed, bool cancelled, int errors, bool expectStable)
        => Assert.Equal(expectStable,
            StabilityVerdict.Classify(completed, cancelled, errors) == StabilityOutcome.Stable);
}
