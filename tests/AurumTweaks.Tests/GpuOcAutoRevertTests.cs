using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure auto-revert decision + countdown. The honesty/safety point: the countdown fires ONLY on
/// axes that can actually black-screen or hang the GPU (core/mem offsets, AMD GPU/VRAM frequency), never
/// on the bounded power/temp axes — so the safety net is meaningful, not noise. The countdown never
/// under/overflows and always states the automatic outcome.
/// </summary>
public class GpuOcAutoRevertTests
{
    private static GpuOcProfile P(int core = 0, int mem = 0, int power = 100, int temp = 83, int mv = 900,
                                  int amdGfx = 0, int amdVram = 0)
        => new(core, mem, power, temp, mv) { AmdMaxFreqMhz = amdGfx, AmdMaxVramFreqMhz = amdVram };

    // ---- WarrantsCountdown: only risky (frequency) axes trigger the net ----

    [Fact]
    public void NoChange_DoesNotWarrantCountdown()
        => Assert.False(GpuOcAutoRevert.WarrantsCountdown(P(), P()));

    [Theory]
    [InlineData(0, 150)]     // NVIDIA core offset changed
    public void CoreOffsetChange_WarrantsCountdown(int before, int after)
        => Assert.True(GpuOcAutoRevert.WarrantsCountdown(P(core: before), P(core: after)));

    [Fact]
    public void MemoryOffsetChange_WarrantsCountdown()
        => Assert.True(GpuOcAutoRevert.WarrantsCountdown(P(mem: 0), P(mem: 1200)));

    [Fact]
    public void AmdGpuFrequencyChange_WarrantsCountdown()
        => Assert.True(GpuOcAutoRevert.WarrantsCountdown(P(amdGfx: 2500), P(amdGfx: 2800)));

    [Fact]
    public void AmdVramFrequencyChange_WarrantsCountdown()
        => Assert.True(GpuOcAutoRevert.WarrantsCountdown(P(amdVram: 2400), P(amdVram: 2600)));

    [Fact]
    public void PowerOrTempOrVoltageChangeAlone_DoesNotWarrantCountdown()
    {
        // These are bounded by the card window (or never applied, for voltage) — they can't hang the
        // display, so they must NOT force a countdown.
        Assert.False(GpuOcAutoRevert.WarrantsCountdown(P(power: 100), P(power: 133)));
        Assert.False(GpuOcAutoRevert.WarrantsCountdown(P(temp: 83), P(temp: 90)));
        Assert.False(GpuOcAutoRevert.WarrantsCountdown(P(mv: 900), P(mv: 1000)));
    }

    // ---- OcAutoRevertCountdown: arithmetic + label ----

    [Fact]
    public void Start_UsesTheDefaultWindow_AndClampsToAtLeastOne()
    {
        Assert.Equal(GpuOcAutoRevert.DefaultWindowSeconds, OcAutoRevertCountdown.Start().RemainingSeconds);
        Assert.Equal(1, OcAutoRevertCountdown.Start(0).RemainingSeconds);
        Assert.Equal(1, OcAutoRevertCountdown.Start(-5).TotalSeconds);
    }

    [Fact]
    public void Tick_CountsDownToZero_NeverBelow()
    {
        var c = OcAutoRevertCountdown.Start(2);
        Assert.False(c.Expired);
        c = c.Tick();
        Assert.Equal(1, c.RemainingSeconds);
        Assert.False(c.Expired);
        c = c.Tick();
        Assert.Equal(0, c.RemainingSeconds);
        Assert.True(c.Expired);
        c = c.Tick();                       // one extra tick must not underflow
        Assert.Equal(0, c.RemainingSeconds);
        Assert.True(c.Expired);
    }

    [Fact]
    public void Label_StatesTheAutomaticOutcome_WhileCounting_AndOnExpiry()
    {
        var c = OcAutoRevertCountdown.Start(10);
        Assert.Contains("Conserver", c.Label);
        Assert.Contains("10 s", c.Label);
        Assert.Contains("automatique", c.Label);   // nothing hidden: the auto-revert is stated up front

        for (int i = 0; i < 10; i++) c = c.Tick();
        Assert.True(c.Expired);
        Assert.Contains("précédents", c.Label);
    }
}
