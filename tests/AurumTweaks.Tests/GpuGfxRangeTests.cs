using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure gates guarding the AMD ADLX GPU max-frequency window. ADLX is AMD's official
/// documented API, but the same trust model applies as for every other backend: an incoherent read
/// keeps the native write path off. The window is the driver's own — an absolute clock (pre-Navi4,
/// e.g. 500–3000 MHz) or an offset from base (Navi4+, e.g. -200..+200 MHz) — so the gates must accept
/// both shapes, and Aurum never re-interprets the scale, only round-trips inside the reported window.
/// </summary>
public class GpuGfxRangeTests
{
    // ---- Window plausibility (covers absolute clocks and offset windows) ----

    [Theory]
    [InlineData(500, 3000, 1, true)]     // absolute clock window
    [InlineData(-200, 200, 5, true)]     // Navi4+ offset window (spans negatives)
    [InlineData(0, 2800, 0, true)]       // step 0 = no grid
    [InlineData(-2000, 200, 1, true)]    // inclusive floor of the envelope (min == -2000)
    [InlineData(500, 6000, 1, true)]     // inclusive ceiling of the envelope (max == 6000)
    [InlineData(3000, 500, 1, false)]    // reversed
    [InlineData(1500, 1500, 0, false)]   // degenerate (min == max)
    [InlineData(-2001, 200, 1, false)]   // just below the credible envelope
    [InlineData(500, 6001, 1, false)]    // just above it
    [InlineData(500, 3000, 3000, false)] // step wider than the window
    public void IsPlausible_AcceptsOnlyCredibleWindows(int min, int max, int step, bool plausible)
        => Assert.Equal(plausible, GpuGfxRange.IsPlausible(min, max, step));

    // ---- Coherence: current inside the window — the write-path gate ---------

    [Theory]
    [InlineData(2800, true)]   // inside
    [InlineData(500, true)]    // exactly min
    [InlineData(3000, true)]   // exactly max
    [InlineData(499, false)]   // below
    [InlineData(3001, false)]  // above
    public void IsCoherent_RequiresCurrentInsideTheWindow(int current, bool coherent)
        => Assert.Equal(coherent, GpuGfxRange.IsCoherent(500, 3000, 1, current));

    [Fact]
    public void IsCoherent_ImplausibleWindow_FailsRegardlessOfCurrent()
        => Assert.False(GpuGfxRange.IsCoherent(3000, 500, 1, 2800));

    // ---- Clamp: into the window, aligned onto the driver's step grid --------

    [Theory]
    [InlineData(3500, 3000)]   // above max
    [InlineData(100, 500)]     // below min
    [InlineData(2800, 2800)]   // inside, step 1 → untouched
    public void Clamp_Step1_OnlyClampsToTheWindow(int requested, int expected)
        => Assert.Equal(expected, GpuGfxRange.Clamp(requested, 500, 3000, 1));

    [Theory]
    [InlineData(2812, 2810)]   // off the 10-grid anchored at 500 → aligned down to 2810
    [InlineData(2810, 2810)]   // already on the grid
    [InlineData(9999, 3000)]   // clamped to max first, which is on the grid (500 + 250*10)
    public void Clamp_CoarseStep_AlignsDownOntoTheDriverGrid(int requested, int expected)
        => Assert.Equal(expected, GpuGfxRange.Clamp(requested, 500, 3000, 10));

    [Fact]
    public void Clamp_OffsetWindow_HandlesNegatives()
        => Assert.Equal(-200, GpuGfxRange.Clamp(-999, -200, 200, 5));
}
