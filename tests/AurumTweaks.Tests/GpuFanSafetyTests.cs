using System.Collections.Generic;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the fan-safety core. The load-bearing rule: Aurum NEVER sets a fan below the hard floor (a pinned-low
/// fan can cook the die), whatever the user asks or the card's own minimum allows; a curve must be monotonic
/// (speed can't drop as temp rises); and lone values are plausibility-gated so a mis-read layout disables the
/// axis instead of trusting garbage.
/// </summary>
public class GpuFanSafetyTests
{
    [Theory]
    [InlineData(50, 0, 50)]      // in range, no card min → unchanged
    [InlineData(5, 0, 20)]       // below hard floor → raised to 20
    [InlineData(0, 0, 20)]       // zero → hard floor (never leaves the fan effectively off)
    [InlineData(150, 0, 100)]    // above max → 100
    [InlineData(25, 40, 40)]     // card min 40 wins over both request and hard floor
    [InlineData(10, 40, 40)]     // request below card min AND floor → card min
    public void ClampManualPercent_NeverBelowTheHardFloorOrCardMin(int req, int cardMin, int expected)
        => Assert.Equal(expected, GpuFanSafety.ClampManualPercent(req, cardMin));

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(-1, false)]
    [InlineData(101, false)]
    public void IsPlausiblePercent_GatesTheReadLayout(int pct, bool ok)
        => Assert.Equal(ok, GpuFanSafety.IsPlausiblePercent(pct));

    [Theory]
    [InlineData(0, true)]
    [InlineData(6000, true)]
    [InlineData(-1, false)]
    [InlineData(6001, false)]
    public void IsPlausibleRpm_GatesTheTachRead(int rpm, bool ok)
        => Assert.Equal(ok, GpuFanSafety.IsPlausibleRpm(rpm));

    [Fact]
    public void IsValidCurve_AcceptsAMonotonicRisingCurve()
    {
        var curve = new List<FanCurvePoint> { new(40, 30), new(60, 50), new(80, 80), new(90, 100) };
        Assert.True(GpuFanSafety.IsValidCurve(curve));
    }

    [Fact]
    public void IsValidCurve_RejectsSpeedDroppingAsTempRises()
    {
        var curve = new List<FanCurvePoint> { new(40, 60), new(60, 40) };   // hotter but slower → invalid
        Assert.False(GpuFanSafety.IsValidCurve(curve));
    }

    [Fact]
    public void IsValidCurve_RejectsBelowFloor_AndNonAscendingTemps_AndTooFewPoints()
    {
        Assert.False(GpuFanSafety.IsValidCurve(new List<FanCurvePoint> { new(40, 10), new(60, 50) }));  // 10 < floor
        Assert.False(GpuFanSafety.IsValidCurve(new List<FanCurvePoint> { new(60, 30), new(60, 50) }));  // temp not strictly rising
        Assert.False(GpuFanSafety.IsValidCurve(new List<FanCurvePoint> { new(40, 30) }));               // < 2 points
    }

    [Fact]
    public void ClampCurve_ForcesFloorAndMonotonicity_SoTheResultIsAlwaysSafe()
    {
        var messy = new List<FanCurvePoint> { new(40, 5), new(60, 40), new(80, 30), new(90, 200) };
        var fixedCurve = GpuFanSafety.ClampCurve(messy);

        Assert.Equal(20, fixedCurve[0].SpeedPct);    // 5 → floor 20
        Assert.Equal(40, fixedCurve[1].SpeedPct);
        Assert.Equal(40, fixedCurve[2].SpeedPct);    // 30 raised to previous 40 (monotonic)
        Assert.Equal(100, fixedCurve[3].SpeedPct);   // 200 → 100
        Assert.True(GpuFanSafety.IsValidCurve(fixedCurve));
    }
}
