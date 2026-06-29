using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>How optimized the machine reads, in five honest bands. Language-neutral so the pure core can't drift
/// from a reworded French label — the view maps the band to its FR word and brush.</summary>
public enum ScoreGrade
{
    /// <summary>No verifiable data to score on (detection hasn't run, or every recommended tweak is unreadable).</summary>
    NoData,
    Poor,
    Partial,
    Good,
    VeryGood,
    Excellent
}

/// <summary>One category's slice of the scorecard: how many of its recommended-and-readable tweaks are live, and
/// the same weighted percent the headline uses — so a per-category bar and the overall number can never contradict.</summary>
public sealed record CategoryScore(TweakCategory Category, int AppliedCount, int VerifiableCount, int Percent);

/// <summary>
/// The result of scoring the machine: a 0-100 <see cref="Score"/>, its <see cref="Grade"/> band, the honest
/// counts behind it, and a per-category breakdown. <see cref="HasData"/> is the guard the UI checks before
/// showing a number — a scorecard built from nothing readable is <see cref="ScoreGrade.NoData"/>, never a
/// fabricated "0/100 — your PC is terrible".
/// </summary>
public sealed record OptimizationScorecard(
    int Score,
    ScoreGrade Grade,
    int AppliedCount,
    int VerifiableCount,
    int IndeterminateCount,
    IReadOnlyList<CategoryScore> Categories)
{
    public bool HasData => VerifiableCount > 0;

    /// <summary>The single shared French word for the band, so the score ring and the per-category copy can't drift
    /// from a reworded label (the <see cref="FrameConsistencyAssessment.Label"/> pattern).</summary>
    public string GradeLabel => OptimizationScore.GradeLabel(Grade);

    /// <summary>The empty scorecard shown before detection has produced anything readable.</summary>
    public static readonly OptimizationScorecard Empty =
        new(0, ScoreGrade.NoData, 0, 0, 0, Array.Empty<CategoryScore>());
}

/// <summary>One recommended-and-applicable tweak's contribution to the scorecard: its category, its ranking
/// <see cref="Weight"/> (the catalog priority — a high-value tweak moves the needle more), and the live tri-state
/// probe of whether it's actually applied right now.</summary>
public readonly record struct ScoreInput(TweakCategory Category, int Weight, TweakAppliedState State);

/// <summary>
/// The "à quel point ton PC est optimisé" scorecard — the capstone of on-system state detection. It turns the
/// per-tweak <see cref="TweakAppliedState"/> probe into ONE weighted 0-100 number plus a per-category map, so the
/// front door can answer "where am I, and where can I still gain?" at a glance.
///
/// <para>Two honesty rules are load-bearing and pinned by tests:</para>
/// <list type="bullet">
/// <item><b>Unverifiable never counts.</b> An <see cref="TweakAppliedState.Indeterminate"/> tweak (shell-only,
/// no readback) is excluded from BOTH the numerator and the denominator — so 100 stays genuinely reachable and we
/// never penalise the user for a value Windows won't let us read. The count is surfaced separately, disclosed, not
/// hidden.</item>
/// <item><b>The denominator is the safe recommended set, not the whole catalog.</b> The caller passes only the
/// tweaks that are recommended AND applicable to this machine; skipping a risky Extreme tweak we never advised must
/// not drag the score down. Applying more weight-bearing tweaks can only ever raise the number, never lower it.</item>
/// </list>
///
/// <para>The score is weight-averaged by <see cref="ScoreInput.Weight"/> (clamped to ≥1 so every recommended tweak
/// counts for something): <c>round(appliedWeight / verifiableWeight × 100)</c>. Empty/all-indeterminate input is
/// <see cref="ScoreGrade.NoData"/>, not a fabricated zero.</para>
/// </summary>
public static class OptimizationScore
{
    public static OptimizationScorecard Compute(IEnumerable<ScoreInput> inputs)
    {
        if (inputs is null) return OptimizationScorecard.Empty;

        var items = inputs as IReadOnlyList<ScoreInput> ?? inputs.ToList();
        int indeterminate = items.Count(i => i.State == TweakAppliedState.Indeterminate);

        // Only readable tweaks (Applied / NotApplied) score. Indeterminate ones are set aside — counted for the
        // honest disclosure, but absent from every weight so they can neither help nor hurt the number.
        var readable = items.Where(i => i.State != TweakAppliedState.Indeterminate).ToList();
        if (readable.Count == 0)
            return OptimizationScorecard.Empty with { IndeterminateCount = indeterminate };

        var categories = readable
            .GroupBy(i => i.Category)
            .Select(g => ScoreCategory(g.Key, g))
            .OrderBy(c => c.Category)   // stable, fixed-position bars — they don't jump as the user applies tweaks
            .ToList();

        int score = WeightedPercent(readable);
        return new OptimizationScorecard(
            Score: score,
            Grade: GradeFor(score),
            AppliedCount: readable.Count(i => i.State == TweakAppliedState.Applied),
            VerifiableCount: readable.Count,
            IndeterminateCount: indeterminate,
            Categories: categories);
    }

    private static CategoryScore ScoreCategory(TweakCategory category, IEnumerable<ScoreInput> group)
    {
        var rows = group as IReadOnlyList<ScoreInput> ?? group.ToList();
        return new CategoryScore(
            category,
            AppliedCount: rows.Count(i => i.State == TweakAppliedState.Applied),
            VerifiableCount: rows.Count,
            Percent: WeightedPercent(rows));
    }

    // Weight each tweak by its catalog priority (≥1 so a 0-priority tweak still nudges the bar), then the applied
    // share of the total weight. No readable rows → 0, but callers only reach here with a non-empty readable set.
    private static int WeightedPercent(IReadOnlyCollection<ScoreInput> rows)
    {
        double total = rows.Sum(i => Weight(i));
        if (total <= 0) return 0;
        double applied = rows.Where(i => i.State == TweakAppliedState.Applied).Sum(i => Weight(i));
        return (int)Math.Round(applied / total * 100, MidpointRounding.AwayFromZero);
    }

    private static int Weight(ScoreInput i) => Math.Max(1, i.Weight);

    private static ScoreGrade GradeFor(int score) => score switch
    {
        >= 90 => ScoreGrade.Excellent,
        >= 75 => ScoreGrade.VeryGood,
        >= 60 => ScoreGrade.Good,
        >= 35 => ScoreGrade.Partial,
        _ => ScoreGrade.Poor
    };

    /// <summary>The band's French label — the one shown on the score ring and reused by the report, so the two
    /// can never disagree. <see cref="ScoreGrade.NoData"/> reads as a neutral « en analyse », never a verdict.</summary>
    public static string GradeLabel(ScoreGrade grade) => grade switch
    {
        ScoreGrade.Excellent => "Optimisé",
        ScoreGrade.VeryGood => "Très bien",
        ScoreGrade.Good => "Bien",
        ScoreGrade.Partial => "Partiel",
        ScoreGrade.Poor => "À optimiser",
        _ => "En analyse"
    };
}
