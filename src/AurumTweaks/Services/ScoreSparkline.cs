using System;
using System.Collections.Generic;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>One projected point of the score sparkline, in pixel space (WPF screen coords — Y grows downward, so a
/// higher score yields a SMALLER Y). Pure data, so the projection geometry is unit-testable without a render pass.</summary>
public readonly record struct ScorePoint(double X, double Y);

/// <summary>
/// Projects the recorded score timeline onto a fixed-size sparkline — the visual companion to the textual « +13 pts
/// depuis le … » trend. Two honesty choices are baked in and load-bearing:
///
/// 1. <b>Vertical scale is a fixed 0-100</b>, never auto-zoomed to the series' own min/max. A 75→78 climb therefore
///    looks like the small move it is, not a dramatic full-height surge — the exact magnitude already lives in the
///    trend text, so the line must not exaggerate it. Height literally reads as « altitude vers 100 ».
/// 2. <b>X is even chronological spacing</b> (oldest→newest), NOT time-proportional. Each point is one recorded
///    measurement (the store dedupes unchanged scores), so even spacing honestly shows the sequence of readings
///    without pretending the gaps between them are uniform time. Direction (left=older, right=newer) stays true.
///
/// A line needs at least two points; one or zero reads as « pas encore d'historique » and the caller hides the
/// sparkline (same ≥2-samples gate as <see cref="ScoreProgress.HasTrend"/>). Pure: no WPF types in, plain
/// <see cref="ScorePoint"/> out — a thin converter repackages the result into a PointCollection for the Polyline.
/// </summary>
public static class ScoreSparkline
{
    /// <summary>The absolute vertical range. Fixed (not data-driven) so the line never over-dramatizes a small move.</summary>
    public const int ScoreFloor = 0;
    public const int ScoreCeiling = 100;

    /// <summary>
    /// Even-spaced, fixed-0-100 projection of <paramref name="samples"/> (oldest-first) into a
    /// <paramref name="width"/>×<paramref name="height"/> box, inset by <paramref name="padding"/> on every side so a
    /// thick stroke at score 0 or 100 isn't clipped at the edge. Fewer than two points (or a non-positive box) yields
    /// an empty list — there's no honest line to draw yet.
    /// </summary>
    public static IReadOnlyList<ScorePoint> Project(
        IReadOnlyList<ScoreSnapshot> samples, double width, double height, double padding = 0)
    {
        if (samples.Count < 2 || width <= 0 || height <= 0)
            return Array.Empty<ScorePoint>();

        double usableW = Math.Max(0, width - 2 * padding);
        double usableH = Math.Max(0, height - 2 * padding);
        int last = samples.Count - 1;
        const double span = ScoreCeiling - ScoreFloor;

        var points = new ScorePoint[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            double x = padding + usableW * i / last;
            double norm = (Math.Clamp(samples[i].Score, ScoreFloor, ScoreCeiling) - ScoreFloor) / span; // 0..1, 1 = best
            double y = padding + usableH * (1 - norm);  // invert: best score sits at the top (smallest Y)
            points[i] = new ScorePoint(x, y);
        }
        return points;
    }
}
