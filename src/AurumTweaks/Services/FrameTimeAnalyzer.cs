using System;
using System.Collections.Generic;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure, deterministic frame-time statistics. Zero hardware/OS access — it is just arithmetic over a
/// list of per-frame times in milliseconds, so it is fully unit-testable and identical whether the
/// frames came from a live ETW capture or an imported CSV.
///
/// <para>Definitions (documented so results are reproducible):
/// average FPS = 1000 / mean(frame time); the "1% low" is the FPS at the 99th-percentile frame time
/// (i.e. only 1% of frames are slower), and the "0.1% low" is the FPS at the 99.9th percentile.
/// Stutter % is the share of frames slower than 2× the median frame time. Percentiles use linear
/// interpolation between closest ranks (Excel PERCENTILE.INC convention). The consecutive-delta metric is
/// the one ORDER-dependent statistic — mean(|t[i] − t[i-1]|) over the frames in capture order — measuring
/// the temporal jitter every other (order-independent) metric here is blind to.</para>
/// </summary>
public static class FrameTimeAnalyzer
{
    public static FrameTimeStats Compute(IReadOnlyList<double> frameTimesMs)
    {
        if (frameTimesMs is null) return FrameTimeStats.Empty;

        // Keep only physically meaningful frame times.
        var clean = new List<double>(frameTimesMs.Count);
        foreach (double t in frameTimesMs)
            if (t > 0 && !double.IsNaN(t) && !double.IsInfinity(t)) clean.Add(t);

        if (clean.Count == 0) return FrameTimeStats.Empty;

        var sorted = clean.ToArray();
        Array.Sort(sorted);   // ascending: sorted[0] = fastest frame, sorted[^1] = slowest

        double sum = 0;
        foreach (double t in clean) sum += t;
        double avg = sum / clean.Count;

        double median = Percentile(sorted, 50);
        double p99 = Percentile(sorted, 99);
        double p999 = Percentile(sorted, 99.9);

        double varSum = 0;
        foreach (double t in clean) varSum += (t - avg) * (t - avg);
        double std = Math.Sqrt(varSum / clean.Count);

        double stutterThreshold = 2.0 * median;
        int stutters = 0;
        foreach (double t in clean) if (t > stutterThreshold) stutters++;

        // Order-aware smoothness: average jump between consecutive frames. Walks `clean` (capture order),
        // NOT `sorted` — that's the whole point, it sees the temporal jitter the sorted-multiset stats can't.
        double consecutiveSum = 0;
        for (int i = 1; i < clean.Count; i++) consecutiveSum += Math.Abs(clean[i] - clean[i - 1]);
        double consecutiveDelta = clean.Count > 1 ? consecutiveSum / (clean.Count - 1) : 0;

        return new FrameTimeStats
        {
            FrameCount = clean.Count,
            DurationSec = sum / 1000.0,
            AvgFrameTimeMs = avg,
            MedianFrameTimeMs = median,
            P99FrameTimeMs = p99,
            P999FrameTimeMs = p999,
            AvgFps = 1000.0 / avg,
            MinFps = 1000.0 / sorted[^1],
            MaxFps = 1000.0 / sorted[0],
            P1LowFps = 1000.0 / p99,
            P01LowFps = 1000.0 / p999,
            StdDevMs = std,
            StutterPct = 100.0 * stutters / clean.Count,
            ConsecutiveDeltaMs = consecutiveDelta
        };
    }

    /// <summary>
    /// Linear-interpolated percentile on an ascending-sorted array, <paramref name="p"/> in [0,100].
    /// Returns 0 for an empty array. Equivalent to Excel PERCENTILE.INC.
    /// </summary>
    public static double Percentile(double[] sortedAscending, double p)
    {
        if (sortedAscending.Length == 0) return 0;
        if (sortedAscending.Length == 1) return sortedAscending[0];

        p = Math.Clamp(p, 0, 100);
        double rank = p / 100.0 * (sortedAscending.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sortedAscending[lo];

        double frac = rank - lo;
        return sortedAscending[lo] + frac * (sortedAscending[hi] - sortedAscending[lo]);
    }
}

/// <summary>
/// Honesty guard for the tail "lows" (1% / 0,1% low). They are percentile metrics, so they need a populated tail:
/// 0,1% of 1000 frames is a single frame, so below that a « 0,1% low » is essentially one sample — a real number,
/// but too few to read as a stable metric. This is the ONE place that threshold lives, so the single-run benchmark
/// paste (<see cref="BenchmarkTextReport"/>) and the A/B comparer (<see cref="BenchmarkComparer"/>) judge "too few
/// frames" identically and their pastes can never disagree (the anti-drift mandate).
/// </summary>
public static class FrameSampleAdequacy
{
    /// <summary>Below this frame count the 0,1% low rests on ~1 frame; at/above it the tail lows are trustworthy.</summary>
    public const int MinFramesForTailLows = 1000;

    /// <summary>True when a run has frames but too few for its 1%/0,1% low to be statistically stable.</summary>
    public static bool TailLowsAreThin(int frameCount) => frameCount > 0 && frameCount < MinFramesForTailLows;
}
