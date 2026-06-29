using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the honesty rule baked into <see cref="TweakDetection.Aggregate"/>: the precedence by which per-op
/// probes (true = applied, false = differs, null = unreadable) fold into a tweak's <see cref="TweakAppliedState"/>.
/// The whole-tweak probe in <see cref="TweakService.IsAppliedAsync"/> leans on this, so the I/O-free decision is
/// verified directly here. The cardinal rule: never report Applied on partial knowledge.
/// </summary>
public class TweakDetectionTests
{
    [Fact]
    public void EveryOpApplied_IsApplied()
        => Assert.Equal(TweakAppliedState.Applied, TweakDetection.Aggregate(new bool?[] { true, true }));

    [Fact]
    public void SingleAppliedOp_IsApplied()
        => Assert.Equal(TweakAppliedState.Applied, TweakDetection.Aggregate(new bool?[] { true }));

    [Fact]
    public void AnyConfirmedOffOp_IsNotApplied()
        => Assert.Equal(TweakAppliedState.NotApplied, TweakDetection.Aggregate(new bool?[] { true, false }));

    [Fact]
    public void ConfirmedOffOp_DominatesUnreadable()
        // A known-off op outranks an unreadable one: we KNOW the tweak isn't fully applied.
        => Assert.Equal(TweakAppliedState.NotApplied, TweakDetection.Aggregate(new bool?[] { false, null, true }));

    [Fact]
    public void AppliedPlusUnreadable_IsIndeterminate()
        // Registry part confirmed applied, but a shell op we can't read back → won't claim the whole tweak.
        => Assert.Equal(TweakAppliedState.Indeterminate, TweakDetection.Aggregate(new bool?[] { true, null }));

    [Fact]
    public void AllUnreadable_IsIndeterminate()
        => Assert.Equal(TweakAppliedState.Indeterminate, TweakDetection.Aggregate(new bool?[] { null, null }));

    [Fact]
    public void NoProbes_IsIndeterminate()
        // A tweak with nothing to probe can't be asserted applied — Indeterminate, not a free "Applied".
        => Assert.Equal(TweakAppliedState.Indeterminate, TweakDetection.Aggregate(Array.Empty<bool?>()));

    // --- AggregateAfterRevert: the dual fold used to verify a revert actually took. Here the same per-op probes
    // (true = STILL applied, false = confirmed off, null = unreadable) answer a different question — "is this tweak
    // now off?" — so the dominator flips: a single still-on op makes the tweak Applied (= STILL ACTIVE, the alarming
    // case), which is the OPPOSITE of Aggregate's "any off op → NotApplied". Reusing Aggregate here would let one
    // reverted op mask a sibling that's still live; these pin that it doesn't.

    [Fact]
    public void AfterRevert_EveryOpOff_IsNotApplied()
        // The honest "tout restauré": every op reads back off → confirmed fully reverted.
        => Assert.Equal(TweakAppliedState.NotApplied, TweakDetection.AggregateAfterRevert(new bool?[] { false, false }));

    [Fact]
    public void AfterRevert_SingleOffOp_IsNotApplied()
        => Assert.Equal(TweakAppliedState.NotApplied, TweakDetection.AggregateAfterRevert(new bool?[] { false }));

    [Fact]
    public void AfterRevert_AnyStillOnOp_IsApplied()
        // One op still reads applied despite the revert → the tweak is STILL ACTIVE, surface it.
        => Assert.Equal(TweakAppliedState.Applied, TweakDetection.AggregateAfterRevert(new bool?[] { false, true }));

    [Fact]
    public void AfterRevert_StillOnOp_DominatesUnreadableAndOff()
        // The mirror of Aggregate.ConfirmedOffOp_DominatesUnreadable: a known still-on op outranks both an off and an
        // unreadable sibling — we KNOW part of the tweak survived the revert, so we won't soften it to Indeterminate.
        => Assert.Equal(TweakAppliedState.Applied, TweakDetection.AggregateAfterRevert(new bool?[] { false, null, true }));

    [Fact]
    public void AfterRevert_OffPlusUnreadable_IsIndeterminate()
        // Registry part confirmed off, but a shell op we can't read back → won't claim the whole tweak fully reverted.
        => Assert.Equal(TweakAppliedState.Indeterminate, TweakDetection.AggregateAfterRevert(new bool?[] { false, null }));

    [Fact]
    public void AfterRevert_AllUnreadable_IsIndeterminate()
        => Assert.Equal(TweakAppliedState.Indeterminate, TweakDetection.AggregateAfterRevert(new bool?[] { null, null }));

    [Fact]
    public void AfterRevert_NoProbes_IsIndeterminate()
        // Nothing to read back can't be asserted fully reverted — Indeterminate, not a free "NotApplied".
        => Assert.Equal(TweakAppliedState.Indeterminate, TweakDetection.AggregateAfterRevert(Array.Empty<bool?>()));
}
