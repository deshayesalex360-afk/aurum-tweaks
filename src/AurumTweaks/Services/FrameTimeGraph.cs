using System;
using System.Collections.Generic;

namespace AurumTweaks.Services;

/// <summary>One plotted point in the graph's fixed logical viewport (WPF-free so the geometry math is unit-testable).</summary>
public readonly record struct GraphPoint(double X, double Y);

/// <summary>
/// Pure geometry for the benchmark page's frame-time plot — the CapFrameX-style line that shows, at a glance, how flat
/// (smooth) or spiky (stuttery) a run was. It draws the REAL captured frame-times; nothing is smoothed away or invented.
///
/// <para>Honesty-bearing, hence extracted and tested:
/// <list type="bullet">
/// <item><b>Spikes survive downsampling.</b> A long run has more frames than the plot has pixels, so frames are bucketed
/// into columns — but each column emits BOTH its min AND its max (a min/max envelope), so a single 1-frame hitch is never
/// averaged into the baseline and hidden. Averaging the buckets would erase the very stutters the plot exists to show.</item>
/// <item><b>The axis is truthful.</b> y is a linear map of frame-time onto a 0 → <paramref name="yMaxMs"/> axis with the
/// 0 ms baseline at the bottom and spikes going UP, so a taller line always means a longer frame-time. The caller passes
/// the run's real max as <c>yMaxMs</c>, so nothing is clipped off the top.</item>
/// </list></para>
/// </summary>
public static class FrameTimeGraph
{
    // Fixed logical viewport; the view scales it to fill the card via a Viewbox. Width caps the column count, so a long
    // capture downsamples to at most ~Width columns (×2 envelope points) — bounded work regardless of frame count.
    public const double ViewWidth = 1000;
    public const double ViewHeight = 200;

    /// <summary>Map a frame-time to a y in [0,<paramref name="height"/>]: 0 ms at the bottom, <paramref name="yMaxMs"/>
    /// at the top (spikes up). Clamped so a value above the axis max lands on the top edge, never off-canvas.</summary>
    public static double Y(double ms, double yMaxMs, double height = ViewHeight)
        => yMaxMs <= 0 ? height : height - Math.Clamp(ms / yMaxMs, 0, 1) * height;

    public static IReadOnlyList<GraphPoint> BuildEnvelope(
        IReadOnlyList<double> framesMs, double yMaxMs, double width = ViewWidth, double height = ViewHeight)
    {
        var pts = new List<GraphPoint>();
        if (framesMs is null || framesMs.Count == 0 || yMaxMs <= 0 || width <= 0 || height <= 0)
            return pts;

        int n = framesMs.Count;

        // Sparse: one point per frame, evenly spaced. A lone frame is centred (no width to span).
        if (n <= width)
        {
            double dx = n == 1 ? 0 : width / (n - 1);
            for (int i = 0; i < n; i++)
                pts.Add(new GraphPoint(n == 1 ? width / 2 : i * dx, Y(framesMs[i], yMaxMs, height)));
            return pts;
        }

        // Dense: bucket into columns; each column emits its peak then its trough so a 1-frame spike keeps its true height.
        int columns = (int)width;
        for (int c = 0; c < columns; c++)
        {
            int start = (int)((long)c * n / columns);
            int end = (int)((long)(c + 1) * n / columns);
            if (end <= start) end = start + 1;
            if (end > n) end = n;

            double min = double.MaxValue, max = double.MinValue;
            for (int i = start; i < end; i++)
            {
                double v = framesMs[i];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            double x = columns == 1 ? width / 2 : (double)c / (columns - 1) * width;
            pts.Add(new GraphPoint(x, Y(max, yMaxMs, height)));   // peak (top of the column)
            if (max != min)
                pts.Add(new GraphPoint(x, Y(min, yMaxMs, height))); // trough (bottom of the column)
        }
        return pts;
    }

    /// <summary>
    /// Two runs on ONE shared axis, for the A/B overlay. The shared <c>yMaxMs</c> is the larger of the two runs' maxima,
    /// so the comparison is FAIR: a tweak that shaved the spikes makes the « Après » line visibly lower and flatter,
    /// while the « Avant » line keeps its true height. Scaling each run to its own axis would flatten a worse run to look
    /// identical to a better one — the dishonest version this exists to avoid. Returns empty envelopes (and yMax 0) when
    /// neither run carries raw frames, so the caller can decline to draw rather than invent a line.
    /// </summary>
    public static FrameTimeOverlay BuildOverlay(
        IReadOnlyList<double> beforeMs, IReadOnlyList<double> afterMs, double width = ViewWidth, double height = ViewHeight)
    {
        double yMax = Math.Max(MaxOrZero(beforeMs), MaxOrZero(afterMs));
        return new FrameTimeOverlay(
            BuildEnvelope(beforeMs, yMax, width, height),
            BuildEnvelope(afterMs, yMax, width, height),
            yMax);
    }

    private static double MaxOrZero(IReadOnlyList<double> xs)
    {
        double m = 0;
        if (xs is not null)
            foreach (double v in xs)
                if (v > m) m = v;
        return m;
    }
}

/// <summary>The A/B overlay: both runs' envelopes on the shared <see cref="YMaxMs"/> axis (see <see cref="FrameTimeGraph.BuildOverlay"/>).</summary>
public sealed record FrameTimeOverlay(IReadOnlyList<GraphPoint> Before, IReadOnlyList<GraphPoint> After, double YMaxMs);
