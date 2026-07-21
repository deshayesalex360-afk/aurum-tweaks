using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure gates guarding the AMD ADLX power-limit window. ADLX is AMD's official documented
/// API, but the same trust model applies as for the NVIDIA gates: a garbage or incoherent read keeps
/// the native write path off. The window lives on Adrenalin's own scale and may legitimately span
/// negative values (offset-style ranges like -10..+15) — the gates must accept that shape, and Aurum
/// never re-interprets the scale, it only round-trips values inside the driver's own window.
/// </summary>
public class AdlxPowerRangeTests
{
    // ---- Window plausibility ----------------------------------------------

    [Theory]
    [InlineData(-10, 15, 1, true)]     // classic Adrenalin offset window
    [InlineData(0, 50, 5, true)]       // absolute-style window with a coarse step
    [InlineData(-100, 400, 0, true)]   // extreme but inside the credible envelope, step 0 = no grid
    [InlineData(15, -10, 1, false)]    // reversed
    [InlineData(10, 10, 0, false)]     // degenerate (min == max)
    [InlineData(-101, 15, 1, false)]   // below the credible envelope
    [InlineData(-10, 401, 1, false)]   // above it
    [InlineData(-10, 15, 26, false)]   // step wider than the window itself
    public void IsPlausible_AcceptsOnlyCredibleWindows(int min, int max, int step, bool plausible)
        => Assert.Equal(plausible, AdlxPowerRange.IsPlausible(min, max, step));

    // ---- Coherence: current inside the window — the write-path gate --------

    [Theory]
    [InlineData(0, true)]      // typical stock value of an offset window
    [InlineData(-10, true)]    // exactly min — inclusive
    [InlineData(15, true)]     // exactly max — inclusive
    [InlineData(-11, false)]
    [InlineData(16, false)]
    public void IsCoherent_RequiresCurrentInsideTheWindow(int current, bool coherent)
        => Assert.Equal(coherent, AdlxPowerRange.IsCoherent(-10, 15, 1, current));

    [Fact]
    public void IsCoherent_ImplausibleWindow_FailsRegardlessOfCurrent()
        => Assert.False(AdlxPowerRange.IsCoherent(15, -10, 1, 0));

    // ---- Clamp: into the window, aligned onto the driver's step grid -------

    [Theory]
    [InlineData(20, 15)]     // above max
    [InlineData(-20, -10)]   // below min
    [InlineData(7, 7)]       // inside, step 1 → untouched
    public void Clamp_Step1_OnlyClampsToTheWindow(int requested, int expected)
        => Assert.Equal(expected, AdlxPowerRange.Clamp(requested, -10, 15, 1));

    [Theory]
    [InlineData(12, 10)]     // 12 is off the 5-grid anchored at -10 (…0,5,10,15) → aligned down to 10
    [InlineData(15, 15)]     // already on the grid
    [InlineData(-8, -10)]    // aligned down onto the anchor
    [InlineData(99, 15)]     // clamped to max first, which is on the grid
    public void Clamp_CoarseStep_AlignsDownOntoTheDriverGrid(int requested, int expected)
        => Assert.Equal(expected, AdlxPowerRange.Clamp(requested, -10, 15, 5));

    [Fact]
    public void Clamp_StepZero_MeansNoGrid()
        => Assert.Equal(7, AdlxPowerRange.Clamp(7, -10, 15, 0));
}
