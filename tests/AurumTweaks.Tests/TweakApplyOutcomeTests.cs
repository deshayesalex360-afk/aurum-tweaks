using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the honesty surface of the tweak engine's result mapping (<see cref="TweakApplyOutcome.From"/>):
/// success is reported ONLY when no operation failed, a partial failure carries an "N/M" message, and the
/// reboot flag is preserved either way. Pure — no registry, no process — so the truth-claim the UI shows is
/// verified directly. (The end-to-end apply/revert partial-failure paths are covered by
/// <see cref="TweakServiceTests"/>.)
/// </summary>
public class TweakApplyOutcomeTests
{
    [Theory]
    [InlineData(0, 3, true)]
    [InlineData(0, 1, false)]
    public void From_AllSucceeded_IsSuccessWithNoError(int failed, int total, bool reboot)
    {
        var r = TweakApplyOutcome.From(failed, total, reboot);

        Assert.True(r.Success);
        Assert.Null(r.Error);
        Assert.Equal(reboot, r.RequiresReboot);
    }

    [Theory]
    [InlineData(1, 3, "1/3")]
    [InlineData(2, 2, "2/2")]
    public void From_AnyFailure_IsFailureWithCountMessage(int failed, int total, string fragment)
    {
        var r = TweakApplyOutcome.From(failed, total, requiresReboot: true);

        Assert.False(r.Success);
        Assert.NotNull(r.Error);
        Assert.Contains(fragment, r.Error);
        Assert.True(r.RequiresReboot);   // reboot requirement is preserved regardless of outcome
    }
}
