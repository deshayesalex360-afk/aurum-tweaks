using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure gates that decide whether the community NVAPI power-policies layout can be trusted
/// on a given card. These gates are the ONLY thing standing between an undocumented struct layout and
/// a native write, so they must be strict: any implausible or incoherent read keeps the power axis
/// honestly unavailable. The "real card" values used below (min 46 875 / default 100 000 /
/// max 120 313 PCM, current 119 000) are the ones actually read from the dev RTX 4080 SUPER by the
/// read-only verification probe — not invented numbers.
/// </summary>
public class GpuPowerLimitTests
{
    // Real values read on the RTX 4080 SUPER (per-cent-mille, 100 000 = 100 %).
    private const int RealMin = 46_875;
    private const int RealDef = 100_000;
    private const int RealMax = 120_313;
    private const int RealCurrent = 119_000;

    // ---- Unit conversion: per-cent-mille <-> percent ----------------------

    [Theory]
    [InlineData(100, 100_000)]
    [InlineData(119, 119_000)]
    [InlineData(50, 50_000)]
    public void ToPcm_ConvertsPercentToPerCentMille(int pct, int pcm)
        => Assert.Equal(pcm, GpuPowerLimit.ToPcm(pct));

    [Theory]
    [InlineData(100_000, 100)]
    [InlineData(119_000, 119)]
    [InlineData(RealMin, 47)]    // 46.875 % rounds to 47
    [InlineData(RealMax, 120)]   // 120.313 % rounds to 120
    public void FromPcm_RoundsToNearestPercent(int pcm, int pct)
        => Assert.Equal(pct, GpuPowerLimit.FromPcm(pcm));

    // ---- Plausibility of a lone value: 10 % to 400 % ----------------------

    [Theory]
    [InlineData(10_000, true)]     // 10 % — inclusive floor
    [InlineData(400_000, true)]    // 400 % — inclusive ceiling
    [InlineData(RealCurrent, true)]
    [InlineData(9_999, false)]     // below floor
    [InlineData(400_001, false)]   // above ceiling
    [InlineData(0, false)]         // zeroed struct field must never pass
    [InlineData(-100_000, false)]
    public void IsPlausiblePcm_AcceptsOnlyCredibleTargets(int pcm, bool plausible)
        => Assert.Equal(plausible, GpuPowerLimit.IsPlausiblePcm(pcm));

    // ---- Window plausibility: each bound credible AND ordered -------------

    [Fact]
    public void IsPlausibleWindow_RealCardValues_Pass()
        => Assert.True(GpuPowerLimit.IsPlausibleWindow(RealMin, RealDef, RealMax));

    [Theory]
    [InlineData(RealMax, RealDef, RealMin)]      // reversed order
    [InlineData(RealDef, RealMin, RealMax)]      // default below min
    [InlineData(0, RealDef, RealMax)]            // zeroed min (wrong offset would read 0)
    [InlineData(RealMin, RealDef, 0)]            // zeroed max
    public void IsPlausibleWindow_IncoherentOrZeroed_Fails(int min, int def, int max)
        => Assert.False(GpuPowerLimit.IsPlausibleWindow(min, def, max));

    // ---- Full coherence: the gate that turns the native write path on -----

    [Fact]
    public void IsCoherent_RealCardValues_Pass()
        => Assert.True(GpuPowerLimit.IsCoherent(RealMin, RealDef, RealMax, RealCurrent));

    [Theory]
    [InlineData(RealMin)]   // current exactly at min — inclusive
    [InlineData(RealMax)]   // current exactly at max — inclusive
    public void IsCoherent_CurrentOnTheBounds_Passes(int current)
        => Assert.True(GpuPowerLimit.IsCoherent(RealMin, RealDef, RealMax, current));

    [Theory]
    [InlineData(RealMin - 1)]     // below the card's own window
    [InlineData(RealMax + 1)]     // above it
    [InlineData(0)]               // zeroed read
    public void IsCoherent_CurrentOutsideTheWindow_Fails(int current)
        => Assert.False(GpuPowerLimit.IsCoherent(RealMin, RealDef, RealMax, current));

    [Fact]
    public void IsCoherent_SubOnePercentWindow_Fails_SoTheClampCannotThrow()
    {
        // A window narrower than one whole percent, not containing an integer percent: plausible and
        // ordered, current inside — yet ceil(min%) > floor(max%), which would make ClampToCardPct do
        // Math.Clamp(v, 101, 100) and throw. IsCoherent must reject it so the axis stays unavailable.
        Assert.False(GpuPowerLimit.IsCoherent(100_100, 100_500, 100_900, 100_500));
        // Sanity: the clamp really would throw on that window, proving the gate is load-bearing.
        Assert.Throws<System.ArgumentException>(() => GpuPowerLimit.ClampToCardPct(100, 100_100, 100_900));
    }

    // ---- Card clamp: requested % pulled inward into the card's window -----

    [Theory]
    [InlineData(133, 120)]   // above card max (120.313 → floor 120)
    [InlineData(40, 47)]     // below card min (46.875 → ceil 47)
    [InlineData(110, 110)]   // inside → untouched
    [InlineData(120, 120)]   // exactly the floored max → untouched
    [InlineData(47, 47)]     // exactly the ceiled min → untouched
    public void ClampToCardPct_RoundsInward_SoTheResultIsAlwaysInsideTheWindow(int requested, int expected)
        => Assert.Equal(expected, GpuPowerLimit.ClampToCardPct(requested, RealMin, RealMax));
}
