using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="ProfileLiveState"/> — the honest tally behind the Profiles page's "Vérifier" button, which probes
/// the live machine and answers "how much of this profile is actually applied right now?". The load-bearing rule
/// (shared with <see cref="ITweakService.DetectStatesAsync"/>): the applied count is <c>Applied</c> ONLY. An
/// <see cref="TweakAppliedState.Indeterminate"/> op (shell-only, nothing to read back) is disclosed on its own, never
/// folded into "appliqué", and the ✓ is earned ONLY when every resolved tweak read back Applied — so the button can
/// never paint a green check it didn't actually read.
/// </summary>
public class ProfileLiveStateTests
{
    [Fact]
    public void Summarize_CountsEachBucket_AndTotal()
    {
        var s = ProfileLiveState.Summarize(new[]
        {
            TweakAppliedState.Applied,
            TweakAppliedState.Applied,
            TweakAppliedState.NotApplied,
            TweakAppliedState.Indeterminate,
        });

        Assert.Equal(2, s.Applied);
        Assert.Equal(1, s.NotApplied);
        Assert.Equal(1, s.Indeterminate);
        Assert.Equal(4, s.Total);
    }

    [Fact]
    public void Summarize_Empty_IsAllZeros()
    {
        var s = ProfileLiveState.Summarize(System.Array.Empty<TweakAppliedState>());

        Assert.Equal(0, s.Applied);
        Assert.Equal(0, s.NotApplied);
        Assert.Equal(0, s.Indeterminate);
        Assert.Equal(0, s.Total);
        Assert.False(s.FullyApplied);   // nothing to verify ⇒ no ✓
    }

    [Fact]
    public void FullyApplied_OnlyWhenEveryResolvedTweakReadBackApplied()
    {
        Assert.True(ProfileLiveState.Summarize(new[] { TweakAppliedState.Applied, TweakAppliedState.Applied }).FullyApplied);
        Assert.False(ProfileLiveState.Summarize(new[] { TweakAppliedState.Applied, TweakAppliedState.NotApplied }).FullyApplied);
    }

    [Fact]
    public void FullyApplied_IsFalse_WhenAnyOpIsIndeterminate()
    {
        // The honesty rule: an unverifiable op must NOT count toward "appliqué", so a profile that is "applied except
        // for one shell-only op we can't read" is NOT fully applied — it must not earn the ✓.
        var s = ProfileLiveState.Summarize(new[] { TweakAppliedState.Applied, TweakAppliedState.Indeterminate });

        Assert.Equal(1, s.Applied);
        Assert.False(s.FullyApplied);
    }

    [Fact]
    public void Label_FullyApplied_ShowsRatioAndCheck()
    {
        var label = ProfileLiveState.Summarize(new[] { TweakAppliedState.Applied, TweakAppliedState.Applied }).Label("Mon setup");

        Assert.Equal("« Mon setup » : 2/2 tweak(s) appliqué(s) ✓", label);
    }

    [Fact]
    public void Label_PartiallyApplied_DisclosesNotApplied_AndEndsWithPeriod_NoCheck()
    {
        var label = ProfileLiveState.Summarize(new[]
        {
            TweakAppliedState.Applied,
            TweakAppliedState.NotApplied,
            TweakAppliedState.NotApplied,
        }).Label("Setup");

        Assert.Equal("« Setup » : 1/3 tweak(s) appliqué(s), 2 non appliqué(s).", label);
        Assert.DoesNotContain("✓", label);
    }

    [Fact]
    public void Label_DisclosesIndeterminate_Separately_NeverAsApplied()
    {
        var label = ProfileLiveState.Summarize(new[]
        {
            TweakAppliedState.Applied,
            TweakAppliedState.Indeterminate,
        }).Label("Setup");

        Assert.Equal("« Setup » : 1/2 tweak(s) appliqué(s), 1 indéterminé(s).", label);
    }

    [Fact]
    public void Label_DisclosesBoth_NotAppliedThenIndeterminate()
    {
        var label = ProfileLiveState.Summarize(new[]
        {
            TweakAppliedState.Applied,
            TweakAppliedState.NotApplied,
            TweakAppliedState.Indeterminate,
        }).Label("Setup");

        Assert.Equal("« Setup » : 1/3 tweak(s) appliqué(s), 1 non appliqué(s), 1 indéterminé(s).", label);
    }

    [Fact]
    public void Label_Empty_SaysNothingToVerify()
    {
        var label = ProfileLiveState.Summarize(System.Array.Empty<TweakAppliedState>()).Label("Vide");

        Assert.Equal("« Vide » ne contient aucun tweak à vérifier.", label);
    }
}
