using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure gates guarding the NVAPI thermal-policies (temperature target) layout — the same
/// trust model as <see cref="GpuPowerLimitTests"/>: an undocumented-but-community-standard layout is
/// only trusted after these gates pass on the actual card. The "real card" values below are the ones
/// actually read from the dev RTX 4080 SUPER by the read-only verification probe (°C·256 fixed point:
/// min 16 640 = 65 °C, default 21 504 = 84 °C, max 22 528 = 88 °C, current 22 528 = the machine's
/// real raised temp target) — matching the card's documented Afterburner window, not invented numbers.
/// </summary>
public class GpuThermalLimitTests
{
    // Real values read on the RTX 4080 SUPER (°C × 256).
    private const int RealMin = 16_640;      // 65 °C
    private const int RealDef = 21_504;      // 84 °C
    private const int RealMax = 22_528;      // 88 °C
    private const int RealCurrent = 22_528;  // 88 °C — the user's actual maxed target

    // ---- Unit conversion: °C·256 <-> °C -----------------------------------

    [Theory]
    [InlineData(65, RealMin)]
    [InlineData(84, RealDef)]
    [InlineData(88, RealMax)]
    public void ToRaw_ConvertsCelsiusToFixedPoint(int celsius, int raw)
        => Assert.Equal(raw, GpuThermalLimit.ToRaw(celsius));

    [Theory]
    [InlineData(RealMin, 65)]
    [InlineData(RealDef, 84)]
    [InlineData(RealMax, 88)]
    [InlineData(21_376, 84)]     // 83.5 °C rounds to 84 — non-multiples must not break the read
    public void FromRaw_RoundsToNearestDegree(int raw, int celsius)
        => Assert.Equal(celsius, GpuThermalLimit.FromRaw(raw));

    // ---- Plausibility of a lone value: 40 °C to 110 °C --------------------

    [Theory]
    [InlineData(40 * 256, true)]     // inclusive floor
    [InlineData(110 * 256, true)]    // inclusive ceiling
    [InlineData(RealCurrent, true)]
    [InlineData(40 * 256 - 1, false)]
    [InlineData(110 * 256 + 1, false)]
    [InlineData(88, false)]          // a bare °C without the <<8 scale must NOT pass (wrong-offset guard)
    [InlineData(0, false)]           // zeroed struct field must never pass
    [InlineData(-22_528, false)]
    public void IsPlausibleRaw_AcceptsOnlyCredibleFixedPointTargets(int raw, bool plausible)
        => Assert.Equal(plausible, GpuThermalLimit.IsPlausibleRaw(raw));

    // ---- Window plausibility ----------------------------------------------

    [Fact]
    public void IsPlausibleWindow_RealCardValues_Pass()
        => Assert.True(GpuThermalLimit.IsPlausibleWindow(RealMin, RealDef, RealMax));

    [Theory]
    [InlineData(RealMax, RealDef, RealMin)]   // reversed order
    [InlineData(RealDef, RealMin, RealMax)]   // default below min
    [InlineData(0, RealDef, RealMax)]         // zeroed min (wrong offset would read 0)
    [InlineData(RealMin, RealDef, 0)]         // zeroed max
    public void IsPlausibleWindow_IncoherentOrZeroed_Fails(int min, int def, int max)
        => Assert.False(GpuThermalLimit.IsPlausibleWindow(min, def, max));

    // ---- Full coherence: the gate that turns the native write path on -----

    [Fact]
    public void IsCoherent_RealCardValues_Pass()
        => Assert.True(GpuThermalLimit.IsCoherent(RealMin, RealDef, RealMax, RealCurrent));

    [Theory]
    [InlineData(RealMin)]    // exactly at min — inclusive
    [InlineData(RealMax)]    // exactly at max — inclusive (the real current IS the max here)
    public void IsCoherent_CurrentOnTheBounds_Passes(int current)
        => Assert.True(GpuThermalLimit.IsCoherent(RealMin, RealDef, RealMax, current));

    [Theory]
    [InlineData(RealMin - 1)]
    [InlineData(RealMax + 1)]
    [InlineData(0)]
    public void IsCoherent_CurrentOutsideTheWindow_Fails(int current)
        => Assert.False(GpuThermalLimit.IsCoherent(RealMin, RealDef, RealMax, current));

    [Fact]
    public void IsCoherent_SubOneDegreeWindow_Fails_SoTheClampCannotThrow()
    {
        // A window inside a single degree (raw values need not be multiples of 256): plausible, ordered,
        // current inside — yet ceil(84.004°C)=85 > floor(84.996°C)=84, which would make ClampToCardC throw.
        Assert.False(GpuThermalLimit.IsCoherent(21_505, 21_600, 21_759, 21_600));
        Assert.Throws<System.ArgumentException>(() => GpuThermalLimit.ClampToCardC(84, 21_505, 21_759));
    }

    // ---- Card clamp: requested °C pulled inward into the card's window ----

    [Theory]
    [InlineData(95, 88)]    // above card max
    [InlineData(50, 65)]    // below card min
    [InlineData(84, 84)]    // inside → untouched
    [InlineData(65, 65)]    // exactly min
    [InlineData(88, 88)]    // exactly max
    public void ClampToCardC_KeepsTheResultInsideTheCardWindow(int requested, int expected)
        => Assert.Equal(expected, GpuThermalLimit.ClampToCardC(requested, RealMin, RealMax));
}
