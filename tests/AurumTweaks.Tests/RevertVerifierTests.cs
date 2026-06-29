using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the post-revert verifier behind the Tweaks page's "Vérification après restauration" banner — the honest
/// mirror of <see cref="TweakVerifierTests"/>. Same three-bucket <see cref="VerificationReport"/>, but the state→bucket
/// mapping is INVERTED on purpose, because the question is the opposite: after a revert we ask "is this tweak now OFF?".
/// So <see cref="TweakAppliedState.NotApplied"/> (read back off) is the GOOD outcome (Confirmed reverted), while
/// <see cref="TweakAppliedState.Applied"/> (still reads live despite the engine reporting the revert) is the alarming
/// "still active" case that must surface as Unconfirmed — never softened to a clean "tout restauré". Indeterminate
/// (shell-only, no readback) makes no claim either way. Pure fold, order-preserving, no I/O.
/// </summary>
public class RevertVerifierTests
{
    private static VerificationReport Build(params (string, TweakAppliedState)[] probed)
        => RevertVerifier.Build(probed);

    [Fact]
    public void Build_InvertsApplyMapping_OffIsConfirmed_OnIsUnconfirmed()
    {
        var r = Build(
            ("reverted", TweakAppliedState.NotApplied),   // off now → the revert took
            ("stillOn", TweakAppliedState.Applied),       // still live → revert didn't take
            ("shell", TweakAppliedState.Indeterminate));  // can't read back → no claim

        Assert.Equal(new[] { "reverted" }, r.Confirmed);
        Assert.Equal(new[] { "stillOn" }, r.Unconfirmed);
        Assert.Equal(new[] { "shell" }, r.Unverifiable);
    }

    [Fact]
    public void Build_PreservesInputOrder_WithinBuckets()
    {
        var r = Build(
            ("a", TweakAppliedState.NotApplied),
            ("b", TweakAppliedState.Applied),
            ("c", TweakAppliedState.NotApplied),
            ("d", TweakAppliedState.Applied));

        Assert.Equal(new[] { "a", "c" }, r.Confirmed);
        Assert.Equal(new[] { "b", "d" }, r.Unconfirmed);
    }

    [Fact]
    public void Build_AllReverted_HasNoUnconfirmed()
    {
        var r = Build(("a", TweakAppliedState.NotApplied), ("b", TweakAppliedState.NotApplied));

        Assert.False(r.HasUnconfirmed);
        Assert.Empty(r.Unconfirmed);
    }

    [Fact]
    public void Build_AnyStillActive_FlagsUnconfirmed()
        => Assert.True(Build(
            ("gone", TweakAppliedState.NotApplied),
            ("survived", TweakAppliedState.Applied)).HasUnconfirmed);

    [Fact]
    public void Build_IndeterminateAlone_IsUnverifiable_NeverAFailure()
    {
        var r = Build(("shell", TweakAppliedState.Indeterminate));

        Assert.False(r.HasUnconfirmed);                  // unverifiable ≠ "still active" — we make no claim
        Assert.Equal(new[] { "shell" }, r.Unverifiable);
        Assert.Empty(r.Unconfirmed);
    }

    [Fact]
    public void Build_Empty_IsCleanReport()
    {
        var r = Build();

        Assert.False(r.HasUnconfirmed);
        Assert.Empty(r.Confirmed);
        Assert.Empty(r.Unconfirmed);
        Assert.Empty(r.Unverifiable);
    }

    [Fact]
    public void UnconfirmedLabel_JoinsStillActiveForDisplay()
        => Assert.Equal("alpha, beta", Build(
            ("alpha", TweakAppliedState.Applied),
            ("beta", TweakAppliedState.Applied)).UnconfirmedLabel);
}
