using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the GPU-OC disclosure honesty. The native backend always writes core/memory frequency offsets, and
/// writes the power limit ONLY on cards where the community power-policies layout passed its on-card read
/// verification (powerNative). So the disclosure must switch with that verification: while power is not
/// native, the power slider must never read as Aurum-applied — and once it IS verified-native, the apply
/// status must never disclaim an axis that genuinely applied. Voltage is never applied either way. Pure.
/// </summary>
public class GpuOcDisclosureTests
{
    private static GpuOcProfile Profile(int core = 0, int mem = 0, int power = 100, int temp = 83, int voltage = 0)
        => new(core, mem, power, temp, voltage);

    // ---- Power NOT native (unverified card): power + voltage are both disclaimed ----

    [Fact]
    public void IgnoredAxesNote_NeutralPowerAndNoVoltage_IsEmpty()
        => Assert.Equal(string.Empty, GpuOcDisclosure.IgnoredAxesNote(Profile(core: 150, mem: 1200), powerNative: false));

    [Fact]
    public void IgnoredAxesNote_NonStockPowerLimit_NamesPowerLimit()
    {
        var note = GpuOcDisclosure.IgnoredAxesNote(Profile(power: 120), powerNative: false);
        Assert.Contains("power limit", note);
        Assert.Contains("non appliqué", note);
    }

    [Fact]
    public void IgnoredAxesNote_VoltageTargetSet_NamesVoltage()
        => Assert.Contains("voltage", GpuOcDisclosure.IgnoredAxesNote(Profile(voltage: 900), powerNative: false));

    [Fact]
    public void IgnoredAxesNote_BothSet_NamesBoth()
    {
        var note = GpuOcDisclosure.IgnoredAxesNote(Profile(power: 115, voltage: 875), powerNative: false);
        Assert.Contains("power limit", note);
        Assert.Contains("voltage", note);
    }

    // ---- Power native (verified card): power genuinely applies → must NOT be disclaimed ----

    [Fact]
    public void IgnoredAxesNote_PowerNative_NonStockPowerLimit_IsNotDisclaimed()
        => Assert.Equal(string.Empty, GpuOcDisclosure.IgnoredAxesNote(Profile(power: 120), powerNative: true));

    [Fact]
    public void IgnoredAxesNote_PowerNative_VoltageIsStillDisclaimed()
    {
        // Voltage is never applied, verified power backend or not.
        var note = GpuOcDisclosure.IgnoredAxesNote(Profile(power: 120, voltage: 875), powerNative: true);
        Assert.Contains("voltage", note);
        Assert.DoesNotContain("power limit", note);
    }

    // ---- Standing sliders note: switches with the verification, never oversells ----

    [Fact]
    public void SlidersNote_OffsetsOnly_NamesCoreMemAsApplied_AndDisownsPowerAndVoltage()
    {
        var note = GpuOcDisclosure.SlidersNote(offsetsNative: true, GpuPowerBackendKind.None, tempNative: false);
        Assert.Contains("core et mémoire", note);
        Assert.Contains("power limit", note);
        Assert.Contains("voltage", note);
        Assert.Contains("Afterburner", note);
        // No native unofficial axis → the undocumented-interface caveat has nothing to qualify.
        Assert.DoesNotContain("pas documentée", note);
    }

    [Fact]
    public void SlidersNote_NvapiPowerNative_ClaimsPowerLimit_WithItsCaveats_AndStillDisownsVoltage()
    {
        var note = GpuOcDisclosure.SlidersNote(offsetsNative: true, GpuPowerBackendKind.NvapiCommunity, tempNative: false);
        Assert.Contains("core et mémoire", note);
        Assert.Contains("power limit", note);
        // The two honesty caveats of the native claim: undocumented interface, read-back confirmation.
        Assert.Contains("pas documentée", note);
        Assert.Contains("relecture", note);
        // Voltage never applies — still routed to Afterburner.
        Assert.Contains("voltage", note);
        Assert.Contains("Afterburner", note);
        // Temp is unverified here → its row is hidden, and the note must not claim it.
        Assert.DoesNotContain("température", note);
    }

    [Fact]
    public void SlidersNote_NvapiPowerAndTempNative_ClaimsBoth_AndStillDisownsVoltage()
    {
        var note = GpuOcDisclosure.SlidersNote(offsetsNative: true, GpuPowerBackendKind.NvapiCommunity, tempNative: true);
        Assert.Contains("core et mémoire", note);
        Assert.Contains("power limit", note);
        Assert.Contains("cible de température", note);
        Assert.Contains("pas documentées", note);
        Assert.Contains("relecture", note);
        Assert.Contains("voltage", note);
        Assert.Contains("Afterburner", note);
    }

    [Fact]
    public void SlidersNote_AdlxPower_ClaimsTheDocumentedApi_WithoutTheUndocumentedCaveat()
    {
        // AMD path: power applies through ADLX (documented) while NVAPI offsets don't apply at all.
        var note = GpuOcDisclosure.SlidersNote(offsetsNative: false, GpuPowerBackendKind.AdlxDocumented, tempNative: false);
        Assert.Contains("power limit", note);
        Assert.Contains("ADLX", note);
        Assert.Contains("officielle", note);
        Assert.Contains("relecture", note);
        Assert.DoesNotContain("pas documentée", note);              // ADLX must never be painted as undocumented
        Assert.Contains("offsets core/mémoire", note);              // referred honestly to Adrenalin
        Assert.Contains("voltage", note);
    }

    [Fact]
    public void SlidersNote_TempNativeButPowerUnverified_ClaimsTemp_DisownsPower_NoMislabel()
    {
        // Reachable NVIDIA state: EnsureThermalVerified passes while EnsurePowerVerified fails. The temp
        // axis must be claimed (undocumented-interface caveat + read-back) and the power limit disowned.
        var note = GpuOcDisclosure.SlidersNote(offsetsNative: true, GpuPowerBackendKind.None, tempNative: true);
        Assert.Contains("cible de température", note);   // applied
        Assert.Contains("pas documentée", note);          // temp is community-layout, singular caveat
        Assert.Contains("relecture", note);
        Assert.Contains("power limit", note);             // disowned in the Non-appliqué clause
        Assert.Contains("voltage", note);
        Assert.Contains("Afterburner", note);
    }

    [Fact]
    public void SlidersNote_NoBackendAtAll_ClaimsNothing()
    {
        var note = GpuOcDisclosure.SlidersNote(offsetsNative: false, GpuPowerBackendKind.None, tempNative: false);
        Assert.Contains("Aucun backend", note);
        Assert.Contains("Afterburner", note);
        Assert.Contains("Adrenalin", note);
        Assert.DoesNotContain("applique nativement", note);         // nothing applies → nothing is claimed
    }
}
