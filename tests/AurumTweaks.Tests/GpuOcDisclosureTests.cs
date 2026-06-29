using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the GPU-OC disclosure honesty. The native backend writes core/memory frequency offsets ONLY (verified in
/// <see cref="GpuOcService"/>/NvApi), so the page's power-limit and voltage sliders must never read as Aurum-applied.
/// The standing note says so plainly, and the value-aware "ignored axes" clause fires exactly when the user set a
/// power-limit/voltage that Aurum then discards — turning a would-be dead control into an honest reference. Pure.
/// </summary>
public class GpuOcDisclosureTests
{
    private static GpuOcProfile Profile(int core = 0, int mem = 0, int power = 100, int temp = 83, int voltage = 0)
        => new(core, mem, power, temp, voltage);

    [Fact]
    public void IgnoredAxesNote_NeutralPowerAndNoVoltage_IsEmpty()
        => Assert.Equal(string.Empty, GpuOcDisclosure.IgnoredAxesNote(Profile(core: 150, mem: 1200)));

    [Fact]
    public void IgnoredAxesNote_NonStockPowerLimit_NamesPowerLimit()
    {
        var note = GpuOcDisclosure.IgnoredAxesNote(Profile(power: 120));
        Assert.Contains("power limit", note);
        Assert.Contains("non appliqué", note);
    }

    [Fact]
    public void IgnoredAxesNote_VoltageTargetSet_NamesVoltage()
        => Assert.Contains("voltage", GpuOcDisclosure.IgnoredAxesNote(Profile(voltage: 900)));

    [Fact]
    public void IgnoredAxesNote_BothSet_NamesBoth()
    {
        var note = GpuOcDisclosure.IgnoredAxesNote(Profile(power: 115, voltage: 875));
        Assert.Contains("power limit", note);
        Assert.Contains("voltage", note);
    }

    [Fact]
    public void SlidersNote_NamesCoreMemAsApplied_AndDisownsPowerAndVoltage()
    {
        // The standing truth shown beside the sliders: applied axes named, the two non-applied ones explicitly
        // sent to Afterburner — so the controls can't masquerade as part of "Appliquer profil".
        Assert.Contains("core et mémoire", GpuOcDisclosure.SlidersNote);
        Assert.Contains("power limit", GpuOcDisclosure.SlidersNote);
        Assert.Contains("voltage", GpuOcDisclosure.SlidersNote);
        Assert.Contains("Afterburner", GpuOcDisclosure.SlidersNote);
    }
}
