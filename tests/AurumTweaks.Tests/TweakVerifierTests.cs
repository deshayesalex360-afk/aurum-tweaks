using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the post-apply verifier behind the Tweaks page's "Vérification après application" banner. The honesty
/// stake: after a batch runs, we re-read the live machine and must report exactly three things without lying —
/// which tweaks the system confirms are live (Applied), which it reads back as NOT live (a real "didn't stick",
/// never dressed up as a ✓), and which we simply can't read (shell-only → Indeterminate → no claim either way).
/// Crucially, Indeterminate is NOT a failure: an unverifiable op must never inflate the "didn't stick" warning.
/// Pure fold, order-preserving, no I/O.
/// </summary>
public class TweakVerifierTests
{
    private static VerificationReport Build(params (string, TweakAppliedState)[] probed)
        => TweakVerifier.Build(probed);

    [Fact]
    public void Build_SortsEachStateIntoItsOwnBucket()
    {
        var r = Build(
            ("live", TweakAppliedState.Applied),
            ("dead", TweakAppliedState.NotApplied),
            ("shell", TweakAppliedState.Indeterminate));

        Assert.Equal(new[] { "live" }, r.Confirmed);
        Assert.Equal(new[] { "dead" }, r.Unconfirmed);
        Assert.Equal(new[] { "shell" }, r.Unverifiable);
    }

    [Fact]
    public void Build_PreservesInputOrder_WithinBuckets()
    {
        var r = Build(
            ("a", TweakAppliedState.Applied),
            ("b", TweakAppliedState.NotApplied),
            ("c", TweakAppliedState.Applied),
            ("d", TweakAppliedState.NotApplied));

        Assert.Equal(new[] { "a", "c" }, r.Confirmed);
        Assert.Equal(new[] { "b", "d" }, r.Unconfirmed);
    }

    [Fact]
    public void Build_AllConfirmed_HasNoUnconfirmed()
    {
        var r = Build(("a", TweakAppliedState.Applied), ("b", TweakAppliedState.Applied));

        Assert.False(r.HasUnconfirmed);
        Assert.Empty(r.Unconfirmed);
    }

    [Fact]
    public void Build_AnyNotApplied_FlagsUnconfirmed()
        => Assert.True(Build(
            ("ok", TweakAppliedState.Applied),
            ("nope", TweakAppliedState.NotApplied)).HasUnconfirmed);

    [Fact]
    public void Build_IndeterminateAlone_IsUnverifiable_NeverAFailure()
    {
        var r = Build(("shell", TweakAppliedState.Indeterminate));

        Assert.False(r.HasUnconfirmed);                  // unverifiable ≠ "didn't stick" — we make no claim
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
    public void UnconfirmedLabel_JoinsForDisplay()
        => Assert.Equal("alpha, beta", Build(
            ("alpha", TweakAppliedState.NotApplied),
            ("beta", TweakAppliedState.NotApplied)).UnconfirmedLabel);
}
