using System;
using System.Collections.Generic;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure, deterministic before/after comparison of two completed benchmark runs — the honesty core of the
/// "did my optimization actually help?" feature. Zero hardware/OS access: it is arithmetic over two
/// <see cref="BenchmarkResult"/> snapshots, so it is fully unit-testable (same precedent as
/// <see cref="FrameTimeAnalyzer"/>).
///
/// <para>It never invents a verdict. A drop in FPS after a tweak is reported as a regression, not buffed
/// into a win; the percentage is guarded against a zero baseline (no NaN/∞); and when the two runs are not
/// a clean A/B (different game process, a very short capture, wildly different durations, or too few frames for
/// the tail lows) it appends a plain-spoken caveat instead of pretending the delta is authoritative.</para>
/// </summary>
public static class BenchmarkComparer
{
    private const double ShortRunSeconds = 5.0;       // below this, the 1%/0.1% lows are too noisy to trust
    private const double DurationRatioWarn = 2.0;     // one run ≥ 2× the other → not apples-to-apples
    private const double FrameCountRatioWarn = 2.0;   // one run ≥ 2× the other's frames → the tail lows compare different sample sizes

    public static BenchmarkComparison Compare(BenchmarkResult before, BenchmarkResult after)
    {
        var b = before.Stats;
        var a = after.Stats;

        return new BenchmarkComparison
        {
            Before = before,
            After = after,
            Headline = Metric("FPS moyen", "FPS", b.AvgFps, a.AvgFps, higherIsBetter: true),
            Metrics = new[]
            {
                Metric("1% low", "FPS", b.P1LowFps, a.P1LowFps, higherIsBetter: true),
                Metric("0,1% low", "FPS", b.P01LowFps, a.P01LowFps, higherIsBetter: true),
                Metric("Stutter", "%", b.StutterPct, a.StutterPct, higherIsBetter: false),
                Metric("Écart-type", "ms", b.StdDevMs, a.StdDevMs, higherIsBetter: false),
                Metric("Var. img-à-img", "ms", b.ConsecutiveDeltaMs, a.ConsecutiveDeltaMs, higherIsBetter: false),
            },
            Caveats = BuildCaveats(before, after),
        };
    }

    private static MetricDelta Metric(string label, string unit, double before, double after, bool higherIsBetter)
        => new() { Label = label, Unit = unit, Before = before, After = after, HigherIsBetter = higherIsBetter };

    /// <summary>Honest comparability hedges — only added when the two runs genuinely aren't a clean A/B.</summary>
    private static IReadOnlyList<string> BuildCaveats(BenchmarkResult before, BenchmarkResult after)
    {
        var notes = new List<string>();

        string pb = before.TargetProcess.Trim();
        string pa = after.TargetProcess.Trim();
        if (pb.Length > 0 && pa.Length > 0 && !pb.Equals(pa, StringComparison.OrdinalIgnoreCase))
            notes.Add($"Captures sur deux process différents (« {pb} » → « {pa} ») : comparaison indicative, pas un A/B strict.");

        double db = before.Stats.DurationSec;
        double da = after.Stats.DurationSec;

        double shortest = Math.Min(db, da);
        if (shortest < ShortRunSeconds)
            notes.Add($"Capture courte ({shortest:0.0} s) : sur peu de frames l'écart est moins fiable — vise ≥ 20 s par run.");

        bool durationsDiverged = db > 0 && da > 0 && Math.Max(db, da) / Math.Min(db, da) >= DurationRatioWarn;
        if (durationsDiverged)
            notes.Add($"Durées très différentes ({db:0.0} s vs {da:0.0} s) : compare des runs de longueur proche pour des 1%/0,1% low fiables.");

        // The 1%/0,1% low live in the distribution's tail, so they need frames, not just seconds: 0,1% of 1000
        // frames is a single frame. A run long enough to clear the duration guards can still be frame-capped (a
        // 30 s run at 30 FPS ≈ 900 frames), leaving the « 0,1% low » resting on ~1 sample. Flag the thinner run —
        // but not when the « courte » note already said the same thing in seconds (one honest hedge, not two).
        int fb = before.Stats.FrameCount;
        int fa = after.Stats.FrameCount;
        int fewest = Math.Min(fb, fa);
        if (fb > 0 && fa > 0 && FrameSampleAdequacy.TailLowsAreThin(fewest) && shortest >= ShortRunSeconds)
            notes.Add($"Peu d'images ({fewest}) : le « 0,1% low » repose sur très peu de frames — vise ≥ {FrameSampleAdequacy.MinFramesForTailLows} images pour qu'il soit fiable.");

        // Comparable durations but very different frame counts = a frame-rate cap that moved between runs (V-Sync
        // flipped on, a limiter added). The averages stay fair, but the tail lows then compare different sample
        // sizes. Redundant once the durations already diverged — that note covers it — so only raise it standalone.
        if (fb > 0 && fa > 0 && !durationsDiverged && (double)Math.Max(fb, fa) / Math.Min(fb, fa) >= FrameCountRatioWarn)
            notes.Add($"Nombre d'images très différent ({fb} vs {fa}) à durée comparable : les 1%/0,1% low portent sur des échantillons de taille différente, comparaison indicative.");

        return notes;
    }
}
