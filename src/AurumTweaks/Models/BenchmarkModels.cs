using System;
using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>
/// Pure frame-time metrics computed from a list of per-frame times (milliseconds). Every number here
/// is derived by deterministic arithmetic from the captured frame times — nothing is invented. The
/// "lows" use the percentile convention (the FPS at the 99th / 99.9th-percentile frame time), which is
/// what CapFrameX/most reviewers report; it is documented so the result is reproducible.
/// </summary>
public sealed record FrameTimeStats
{
    public int FrameCount { get; init; }
    public double DurationSec { get; init; }

    public double AvgFps { get; init; }
    public double MinFps { get; init; }        // = 1000 / slowest frame
    public double MaxFps { get; init; }        // = 1000 / fastest frame

    public double AvgFrameTimeMs { get; init; }
    public double MedianFrameTimeMs { get; init; }
    public double P99FrameTimeMs { get; init; }
    public double P999FrameTimeMs { get; init; }

    public double P1LowFps { get; init; }      // "1% low"   = 1000 / P99 frame time
    public double P01LowFps { get; init; }     // "0.1% low" = 1000 / P99.9 frame time

    public double StdDevMs { get; init; }
    public double StutterPct { get; init; }    // % of frames slower than 2× the median frame time

    /// <summary>
    /// Mean absolute frame-time change between consecutive frames — mean(|t[i] − t[i-1]|). Unlike every
    /// other metric here it is ORDER-dependent: it measures temporal jitter (the 8↔16 ms oscillation a
    /// player perceives as stutter), which average / percentiles / std-dev / stutter% are all blind to —
    /// shuffle the frames and those are unchanged, but smoothness isn't. Lower = smoother; 0 for a perfectly
    /// flat stream or a single frame.
    /// </summary>
    public double ConsecutiveDeltaMs { get; init; }

    public static FrameTimeStats Empty { get; } = new();
}

/// <summary>A completed capture/analysis: the stats, the raw frame times, and honest provenance notes.</summary>
public sealed record BenchmarkResult
{
    public string Source { get; init; } = string.Empty;          // e.g. "ETW DXGI · game.exe" or "CSV · capture.csv"
    public string TargetProcess { get; init; } = string.Empty;
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public FrameTimeStats Stats { get; init; } = FrameTimeStats.Empty;
    public IReadOnlyList<double> FrameTimesMs { get; init; } = Array.Empty<double>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool HasData => Stats.FrameCount > 0;
}

/// <summary>
/// Honest capability report for the live-capture backend (mirrors the GPU OC backend-status pattern).
/// Live ETW capture needs an elevated process (real-time ETW session); when that is not the case we say
/// so plainly and steer the user to the always-available CSV import path instead of pretending.
/// </summary>
public sealed record BenchmarkBackendStatus
{
    public bool LiveCaptureAvailable { get; init; }
    public bool IsElevated { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>Result of parsing a frame-time CSV (PresentMon / CapFrameX-export / plain frametime column).</summary>
public sealed record FrameCsvParseResult
{
    public IReadOnlyList<double> FrameTimesMs { get; init; } = Array.Empty<double>();
    public string Column { get; init; } = string.Empty;     // which column the frame times came from
    public string Process { get; init; } = string.Empty;    // detected app/process column value, if any
    public int SkippedRows { get; init; }

    /// <summary>
    /// True when <see cref="FrameTimesMs"/> was DERIVED by differencing a cumulative-timestamp column
    /// (Fraps' <c>Time (ms)</c>, PresentMon's <c>TimeInSeconds</c>) rather than read straight from a
    /// per-frame column. The transform is exact — tᵢ − tᵢ₋₁ — so no value is invented, but it is flagged
    /// so the provenance shown to the user stays honest about how the frame-times were obtained.
    /// </summary>
    public bool Differenced { get; init; }

    public bool Ok => FrameTimesMs.Count > 0;
}

/// <summary>
/// One metric's before→after movement in an A/B benchmark. <see cref="HigherIsBetter"/> fixes the meaning
/// of the sign so the UI can colour a real improvement green whether the metric is an FPS (up is good) or a
/// frame-time / stutter / std-dev (down is good). Everything is plain arithmetic over the two captures — the
/// sign is never flipped to flatter a result, so a regression shows as a regression.
/// </summary>
public sealed record MetricDelta
{
    public string Label { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public double Before { get; init; }
    public double After { get; init; }

    /// <summary>True when a larger value is the better one (FPS); false when smaller is better (ms / stutter %).</summary>
    public bool HigherIsBetter { get; init; }

    public double Delta => After - Before;

    /// <summary>Percent change relative to the baseline; 0 when the baseline is 0 (never NaN/∞ — no fake number).</summary>
    public double PercentChange => Before != 0 ? (After - Before) / Before * 100.0 : 0;

    /// <summary>Moved in the good direction by more than float dust.</summary>
    public bool Improved => HigherIsBetter ? Delta > Epsilon : Delta < -Epsilon;

    /// <summary>Moved in the bad direction by more than float dust — surfaced honestly, never hidden.</summary>
    public bool Regressed => HigherIsBetter ? Delta < -Epsilon : Delta > Epsilon;

    private const double Epsilon = 1e-6;
}

/// <summary>
/// A before/after benchmark comparison — the proof an optimization actually moved the numbers. Holds both
/// real captures, the headline movement (average FPS), the secondary metric deltas, and honest comparability
/// caveats (different process, short run, mismatched durations, too few frames for the tail lows) so an indicative comparison is never dressed
/// up as a strict A/B. No verdict ("optimisation réussie") is invented; only the arithmetic is reported.
/// </summary>
public sealed record BenchmarkComparison
{
    public BenchmarkResult Before { get; init; } = new();
    public BenchmarkResult After { get; init; } = new();
    public MetricDelta Headline { get; init; } = new();
    public IReadOnlyList<MetricDelta> Metrics { get; init; } = Array.Empty<MetricDelta>();
    public IReadOnlyList<string> Caveats { get; init; } = Array.Empty<string>();
}
