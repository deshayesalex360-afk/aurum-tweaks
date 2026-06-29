using System;
using System.Globalization;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>How regularly frames were delivered, judged on the frame-times alone. A verdict on SMOOTHNESS only —
/// never on whether the FPS level is high enough (that depends on the game and the monitor).</summary>
public enum FrameConsistencyLevel { Unknown, Smooth, Moderate, Choppy }

/// <summary>
/// A frame-time regularity verdict: the <see cref="Level"/>, the French explanation shown to the user, and the
/// 1%-low-as-a-share-of-average ratio it was judged on (clamped to [0,1]). <see cref="Label"/> is the single shared
/// French word for the level so the on-screen badge and the shareable report can't drift.
/// </summary>
public sealed record FrameConsistencyAssessment(FrameConsistencyLevel Level, string Message, double LowToAvgRatio)
{
    public string Label => FrameConsistencyVerdict.Label(Level);
}

/// <summary>
/// Pure verdict core for frame-time REGULARITY — the missing twin of <see cref="LatencyVerdict"/> on the benchmark
/// page (every other diagnostic in the app ships a verdict; the frame-time stats did not). Errors-first, mirroring
/// <see cref="LatencyVerdict"/> / <c>StabilityVerdict</c>: no usable run — or too few frames to support a real
/// « 1% low » — ⇒ Unknown, never a fabricated « régulière ». Otherwise the WORSE of two scale-free, game-agnostic
/// signals drives the level: the 1%-low-as-a-share-of-average ratio (how far the lows decouple from the mean — the
/// CapFrameX/reviewer measure) and the stutter rate (share of frames over 2× the median).
///
/// <para>Honesty boundary (load-bearing): this judges ONLY how EVENLY frames arrived, NOT whether the FPS is
/// « good » — a smooth 45 fps and a smooth 240 fps both score « régulière ». The thresholds are deliberately
/// conservative and the UI/report label them « indicatif ». The Choppy message points at general causes
/// (limite CPU/GPU, throttle, pilote/DPC) rather than naming a culprit that frame-times alone cannot identify.</para>
/// </summary>
public static class FrameConsistencyVerdict
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Below this frame count the « 99th percentile » is just the near-slowest frame, not a real 1% low —
    /// so the verdict declines to pronounce rather than read a level off a sample that cannot support one.</summary>
    public const int MinFrames = 100;

    // 1% low as a fraction of the average FPS. ≥ 0.85 → the lows hug the mean (regular); ≥ 0.70 → a noticeable but
    // tolerable decouple; below → the lows fall away from the mean, felt as hitching.
    public const double SmoothRatio = 0.85;
    public const double ModerateRatio = 0.70;

    // Share of frames slower than 2× the median (FrameTimeStats.StutterPct). Even a few doubled frames are visible,
    // so isolated spikes escalate a run whose ratio alone would have looked fine.
    public const double SmoothStutterPct = 1.0;
    public const double ModerateStutterPct = 5.0;

    public static FrameConsistencyAssessment Evaluate(FrameTimeStats stats)
    {
        if (stats is null || stats.FrameCount == 0 || stats.AvgFps <= 0)
            return new(FrameConsistencyLevel.Unknown,
                "Pas de capture exploitable — lance une capture ou importe un CSV de frame-times pour évaluer la régularité.",
                0);

        if (stats.FrameCount < MinFrames)
            return new(FrameConsistencyLevel.Unknown,
                $"Trop peu de frames ({stats.FrameCount}) pour un « 1% low » fiable (vise ~{MinFrames}+). "
                + "Capture plus longtemps avant de juger la régularité.",
                0);

        // Clamped to [0,1]: a 1% low at or above the average (a rare heavy-tail artefact) means the lows are excellent,
        // not « better than the mean » — reporting it as 100 % is the honest reading and keeps the message sane.
        double ratio = Math.Clamp(stats.P1LowFps / stats.AvgFps, 0, 1);

        // Worse of the two signals wins — higher enum ordinal = worse (Choppy=3 > Moderate=2 > Smooth=1), the same
        // « worst drives the level » shape as LatencyVerdict's Max(DPC%, ISR%).
        var byRatio = ratio >= SmoothRatio ? FrameConsistencyLevel.Smooth
                    : ratio >= ModerateRatio ? FrameConsistencyLevel.Moderate
                    : FrameConsistencyLevel.Choppy;
        var byStutter = stats.StutterPct < SmoothStutterPct ? FrameConsistencyLevel.Smooth
                      : stats.StutterPct < ModerateStutterPct ? FrameConsistencyLevel.Moderate
                      : FrameConsistencyLevel.Choppy;
        var level = (FrameConsistencyLevel)Math.Max((int)byRatio, (int)byStutter);

        string pct = (ratio * 100).ToString("0", Fr);
        string message = level switch
        {
            FrameConsistencyLevel.Smooth =>
                $"Diffusion régulière : 1% low à {pct} % du FPS moyen, saccades rares. Les images arrivent à un rythme "
                + "régulier sur cette capture (cela ne juge pas le niveau de FPS, qui dépend du jeu et de l'écran).",
            FrameConsistencyLevel.Moderate =>
                $"Régularité correcte : 1% low à {pct} % du FPS moyen. Le rythme des images est globalement tenu, avec "
                + "quelques décrochages — à surveiller si tu ressens des saccades en jeu.",
            // Reached by a low ratio OR a high stutter rate; the « et/ou » keeps the sentence true for either driver
            // (the reported ratio is a plain fact, never dressed up as the cause).
            _ =>
                $"Diffusion irrégulière : 1% low à {pct} % du FPS moyen, et/ou des saccades reviennent trop souvent. "
                + "Cherche une limite CPU/GPU, un throttle thermique/électrique ou un pilote (DPC) — le FPS moyen seul "
                + "masque ces décrochages.",
        };

        return new(level, message, ratio);
    }

    /// <summary>The French level word — the single source shared by the on-screen badge and the shareable report, so
    /// the two can never drift apart (mirrors <see cref="LatencyVerdict.Label"/>).</summary>
    public static string Label(FrameConsistencyLevel level) => level switch
    {
        FrameConsistencyLevel.Smooth => "Régulière",
        FrameConsistencyLevel.Moderate => "Correcte",
        FrameConsistencyLevel.Choppy => "Irrégulière",
        _ => "Indéterminée",
    };
}
