using System;
using System.Collections.Generic;

namespace AurumTweaks.Services;

/// <summary>One point of a fan curve: at <see cref="TempC"/> °C, run the fan at <see cref="SpeedPct"/> %.</summary>
public readonly record struct FanCurvePoint(int TempC, int SpeedPct);

/// <summary>
/// Pure safety + validation for GPU fan control. Fan control is the one OC axis where a WRONG-low value is
/// genuinely dangerous (a fan pinned near 0 % under load lets the die cook toward the throttle/shutdown
/// point), so this enforces a HARD minimum floor that no requested value or curve point can go below —
/// independent of what the driver would otherwise accept — plus a manual-speed clamp and a curve-shape
/// check. Side-effect-free → unit-testable; the driver read/write lives in the interop layer, gated by
/// these rules + a read-back confirmation (same contract as every other axis).
/// </summary>
public static class GpuFanSafety
{
    /// <summary>Absolute minimum fan % Aurum will ever set, whatever the user or driver window says. A GPU
    /// die must never be left effectively unventilated under an Aurum-applied fan setting.</summary>
    public const int HardFloorPercent = 20;

    public const int MaxPercent = 100;

    /// <summary>Clamp a requested manual fan % into [max(cardMin, hard floor), 100]. The hard floor wins over
    /// a lower card minimum, so Aurum can never drive the fan into an unsafe range.</summary>
    public static int ClampManualPercent(int requestedPct, int cardMinPct = 0)
    {
        int floor = Math.Max(HardFloorPercent, Math.Max(0, cardMinPct));
        return Math.Clamp(requestedPct, floor, MaxPercent);
    }

    /// <summary>A single lone value that could plausibly be a fan % reading (0..100). Anything else means a
    /// mis-read layout → the axis reports unavailable rather than trusting it.</summary>
    public static bool IsPlausiblePercent(int pct) => pct is >= 0 and <= 100;

    /// <summary>A tachometer RPM that reads sane (0..6000). Guards the community-layout fan read.</summary>
    public static bool IsPlausibleRpm(int rpm) => rpm is >= 0 and <= 6000;

    /// <summary>
    /// A fan curve is valid when it has at least two points, temperatures strictly ascend, and fan speeds
    /// are non-decreasing (a curve that spins DOWN as it gets hotter is nonsensical) AND every speed sits
    /// at or above the hard floor and at or below 100. This is what the app must enforce before writing a
    /// user-drawn curve to the driver.
    /// </summary>
    public static bool IsValidCurve(IReadOnlyList<FanCurvePoint> points)
    {
        if (points is null || points.Count < 2) return false;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].SpeedPct < HardFloorPercent || points[i].SpeedPct > MaxPercent) return false;
            if (i > 0)
            {
                if (points[i].TempC <= points[i - 1].TempC) return false;      // temps must strictly rise
                if (points[i].SpeedPct < points[i - 1].SpeedPct) return false; // speed must not drop as temp rises
            }
        }
        return true;
    }

    /// <summary>Clamp every point of a curve into a safe/valid shape: speeds into [floor,100], and each
    /// speed raised to at least the previous point's speed so the result is monotonic non-decreasing.
    /// Temperatures are passed through (ordering is the caller's responsibility; <see cref="IsValidCurve"/>
    /// still gates the final write).</summary>
    public static IReadOnlyList<FanCurvePoint> ClampCurve(IReadOnlyList<FanCurvePoint> points)
    {
        var result = new List<FanCurvePoint>(points?.Count ?? 0);
        int prevSpeed = HardFloorPercent;
        foreach (var p in points ?? Array.Empty<FanCurvePoint>())
        {
            int speed = Math.Clamp(p.SpeedPct, HardFloorPercent, MaxPercent);
            speed = Math.Max(speed, prevSpeed);
            prevSpeed = speed;
            result.Add(p with { SpeedPct = speed });
        }
        return result;
    }
}
