using System;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="OptimizationScore"/> — the front-door "à quel point ton PC est optimisé" scorecard that turns
/// the per-tweak <see cref="TweakAppliedState"/> probe into one weighted 0-100 number. The load-bearing properties
/// are honesty properties: an UNVERIFIABLE tweak counts for nothing (so a perfect score stays genuinely reachable
/// and the user is never penalised for a value Windows won't read back), applying more weight-bearing tweaks can
/// only ever RAISE the number, and nothing to score on reads as « pas de données » — never a fabricated zero.
/// </summary>
public class OptimizationScoreTests
{
    private static ScoreInput In(TweakAppliedState state, int weight = 50, TweakCategory cat = TweakCategory.PerformanceMultimedia)
        => new(cat, weight, state);

    // --- The empty / unscorable cases: « no data », never a fake 0 ---

    [Fact]
    public void NoInput_IsNoData_NotAFabricatedZero()
    {
        var card = OptimizationScore.Compute(Array.Empty<ScoreInput>());
        Assert.False(card.HasData);
        Assert.Equal(ScoreGrade.NoData, card.Grade);
    }

    [Fact]
    public void NullInput_IsNoData_NotACrash()
        => Assert.Equal(ScoreGrade.NoData, OptimizationScore.Compute(null!).Grade);

    [Fact]
    public void AllIndeterminate_IsNoData_ButTheCountIsStillDisclosed()
    {
        var card = OptimizationScore.Compute(new[]
        {
            In(TweakAppliedState.Indeterminate),
            In(TweakAppliedState.Indeterminate),
        });
        Assert.False(card.HasData);                 // nothing readable → unscorable, not 0/100
        Assert.Equal(2, card.IndeterminateCount);   // …but we say so, honestly
        Assert.Equal(0, card.VerifiableCount);
    }

    // --- The endpoints ---

    [Fact]
    public void EveryReadableTweakApplied_Is100_Excellent()
    {
        var card = OptimizationScore.Compute(new[]
        {
            In(TweakAppliedState.Applied),
            In(TweakAppliedState.Applied),
            In(TweakAppliedState.Applied),
        });
        Assert.Equal(100, card.Score);
        Assert.Equal(ScoreGrade.Excellent, card.Grade);
        Assert.Equal(3, card.AppliedCount);
    }

    [Fact]
    public void NothingApplied_IsZero_Poor()
    {
        var card = OptimizationScore.Compute(new[]
        {
            In(TweakAppliedState.NotApplied),
            In(TweakAppliedState.NotApplied),
        });
        Assert.Equal(0, card.Score);
        Assert.Equal(ScoreGrade.Poor, card.Grade);
        Assert.True(card.HasData);                  // « 0 of 2 applied » IS data — distinct from « nothing to score »
    }

    // --- THE honesty property: an unverifiable tweak counts for nothing ---

    [Fact]
    public void Indeterminate_IsExcludedFromBothSides_So100StaysReachable()
    {
        // One applied, one unreadable. A naive applied/total would read 50 % and cap the user at a B forever —
        // dishonestly penalising them for a shell-only tweak we can't probe. The unreadable one must drop out of
        // the denominator entirely, leaving a true 100.
        var card = OptimizationScore.Compute(new[]
        {
            In(TweakAppliedState.Applied),
            In(TweakAppliedState.Indeterminate),
        });
        Assert.Equal(100, card.Score);
        Assert.Equal(1, card.VerifiableCount);      // the denominator is the READABLE set, not the whole input
        Assert.Equal(1, card.IndeterminateCount);   // disclosed beside the score, not silently folded in
    }

    [Fact]
    public void ApplyingMoreTweaks_NeverLowersTheScore()
    {
        var before = OptimizationScore.Compute(new[] { In(TweakAppliedState.Applied), In(TweakAppliedState.NotApplied) });
        var after = OptimizationScore.Compute(new[] { In(TweakAppliedState.Applied), In(TweakAppliedState.Applied) });
        Assert.True(after.Score >= before.Score);
        Assert.Equal(100, after.Score);
    }

    // --- Weighting: a high-value tweak moves the needle more than a trivial one ---

    [Fact]
    public void Score_IsWeightedByPriority_NotAFlatCount()
    {
        // High-priority tweak applied, low-priority one not. A flat count says 50 %; the weighting must lean high.
        var card = OptimizationScore.Compute(new[]
        {
            In(TweakAppliedState.Applied, weight: 90),
            In(TweakAppliedState.NotApplied, weight: 10),
        });
        Assert.Equal(90, card.Score);   // 90 / (90+10)
    }

    [Fact]
    public void ZeroPriorityTweak_StillCounts_WeightFlooredToOne()
    {
        // Two 0-priority tweaks, one applied. If weight floored to 0 the denominator would be 0 → a 0 or a crash;
        // flooring each to 1 gives an honest 50 %.
        var card = OptimizationScore.Compute(new[]
        {
            In(TweakAppliedState.Applied, weight: 0),
            In(TweakAppliedState.NotApplied, weight: 0),
        });
        Assert.Equal(50, card.Score);
    }

    // --- Grade bands ---

    [Theory]
    [InlineData(100, ScoreGrade.Excellent)]
    [InlineData(90, ScoreGrade.Excellent)]
    [InlineData(89, ScoreGrade.VeryGood)]
    [InlineData(75, ScoreGrade.VeryGood)]
    [InlineData(74, ScoreGrade.Good)]
    [InlineData(60, ScoreGrade.Good)]
    [InlineData(59, ScoreGrade.Partial)]
    [InlineData(35, ScoreGrade.Partial)]
    [InlineData(34, ScoreGrade.Poor)]
    [InlineData(0, ScoreGrade.Poor)]
    public void Grade_TracksTheScoreBands(int target, ScoreGrade expected)
    {
        // Build `target` applied out of 100 unit-weight tweaks → score == target exactly, isolating the band rule.
        var inputs = Enumerable.Range(0, 100)
            .Select(i => In(i < target ? TweakAppliedState.Applied : TweakAppliedState.NotApplied, weight: 1))
            .ToArray();
        var card = OptimizationScore.Compute(inputs);
        Assert.Equal(target, card.Score);
        Assert.Equal(expected, card.Grade);
    }

    [Fact]
    public void GradeLabel_IsTheSharedFrenchWord_AndNoDataReadsNeutral()
    {
        // The label is what the score ring and the report both render — pin it so a reword can't silently diverge,
        // and so the unscorable case stays a neutral « en analyse » rather than a fabricated verdict.
        Assert.Equal("Optimisé", OptimizationScore.GradeLabel(ScoreGrade.Excellent));
        Assert.Equal("À optimiser", OptimizationScore.GradeLabel(ScoreGrade.Poor));
        Assert.Equal("En analyse", OptimizationScore.GradeLabel(ScoreGrade.NoData));
        // The record's helper and the static must agree — they're the same source.
        Assert.Equal(
            OptimizationScore.GradeLabel(ScoreGrade.Good),
            (OptimizationScorecard.Empty with { Grade = ScoreGrade.Good }).GradeLabel);
    }

    // --- Per-category breakdown ---

    [Fact]
    public void Categories_BreakDownPerCategory_AndStayInAStableOrder()
    {
        var card = OptimizationScore.Compute(new[]
        {
            In(TweakAppliedState.Applied,    cat: TweakCategory.NetworkLatency),
            In(TweakAppliedState.NotApplied, cat: TweakCategory.NetworkLatency),
            In(TweakAppliedState.Applied,    cat: TweakCategory.PrivacyTelemetry),
        });

        // One row per category that had something readable.
        Assert.Equal(2, card.Categories.Count);

        var net = card.Categories.Single(c => c.Category == TweakCategory.NetworkLatency);
        Assert.Equal(1, net.AppliedCount);
        Assert.Equal(2, net.VerifiableCount);
        Assert.Equal(50, net.Percent);

        var priv = card.Categories.Single(c => c.Category == TweakCategory.PrivacyTelemetry);
        Assert.Equal(100, priv.Percent);

        // Stable order = the enum's order (PrivacyTelemetry precedes NetworkLatency), so bars don't jump around.
        Assert.Equal(
            card.Categories.OrderBy(c => c.Category).Select(c => c.Category),
            card.Categories.Select(c => c.Category));
    }

    [Fact]
    public void Categories_OmitAnAllIndeterminateCategory_NoEmptyBar()
    {
        var card = OptimizationScore.Compute(new[]
        {
            In(TweakAppliedState.Applied,        cat: TweakCategory.Gaming),
            In(TweakAppliedState.Indeterminate,  cat: TweakCategory.Security),   // nothing readable in Security
        });
        Assert.Single(card.Categories);
        Assert.Equal(TweakCategory.Gaming, card.Categories[0].Category);
    }
}
