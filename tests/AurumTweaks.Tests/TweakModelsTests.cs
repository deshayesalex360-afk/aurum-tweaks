using System.Collections.Generic;
using System.ComponentModel;
using AurumTweaks.Models;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the two honesty-bearing computed flags on the Tweak model spine.
/// <see cref="AntiCheatMatrix.HasAnyConcern"/> is the safety gate: it must light up if ANY of the six
/// tracked anti-cheat engines is not Safe, because the competitive filter uses it to keep ban-risky
/// tweaks away from a player running Vanguard/EAC/BattlEye/FACEIT/Ricochet/ESEA. A forgotten engine in
/// that OR would silently mark a banned tweak "no concern" — the worst kind of regression here.
/// <see cref="TweakApplicability.IsHardwareSpecific"/> drives the adaptive "tuned for your PC" bonus; a
/// forgotten axis would mis-rank a hardware-specific tweak. Each test isolates one engine / one axis so a
/// dropped term can't hide behind another.
/// Also pins that the runtime-state flags (<see cref="Tweak.IsApplied"/>/<see cref="Tweak.IsSelected"/>)
/// raise PropertyChanged: the Tweaks page sets them AFTER rows render (load-time detection, apply, revert),
/// and the per-row "✓ Appliqué" badge + selection checkbox bind to them — a plain POCO would leave that UI
/// silently stale, an honesty defect this test catches if anyone de-observables the model.
/// </summary>
public class TweakModelsTests
{
    // ---- AntiCheatMatrix.HasAnyConcern: the anti-cheat safety gate -----------------

    [Fact]
    public void HasAnyConcern_AllEnginesSafe_IsFalse()
        => Assert.False(new AntiCheatMatrix().HasAnyConcern);

    [Theory]
    [InlineData("vanguard")]
    [InlineData("eac")]
    [InlineData("battleye")]
    [InlineData("faceit")]
    [InlineData("ricochet")]
    [InlineData("esea")]
    public void HasAnyConcern_AnySingleEngineNotSafe_IsTrue(string engine)
    {
        // Alternate Banned/Risky so the test also proves both non-Safe states count as a concern.
        var m = new AntiCheatMatrix();
        switch (engine)
        {
            case "vanguard": m.Vanguard = AntiCheatStatus.Banned; break;
            case "eac":      m.EasyAntiCheat = AntiCheatStatus.Risky; break;
            case "battleye": m.BattlEye = AntiCheatStatus.Banned; break;
            case "faceit":   m.Faceit = AntiCheatStatus.Risky; break;
            case "ricochet": m.Ricochet = AntiCheatStatus.Banned; break;
            case "esea":     m.Esea = AntiCheatStatus.Risky; break;
        }
        Assert.True(m.HasAnyConcern);
    }

    // ---- TweakApplicability.IsHardwareSpecific: the adaptive specificity bonus ------

    [Fact]
    public void IsHardwareSpecific_NoConstraints_IsFalse()
        => Assert.False(new TweakApplicability().IsHardwareSpecific);

    [Theory]
    [InlineData("cpuVendors")]
    [InlineData("cpuFamilies")]
    [InlineData("gpuVendors")]
    [InlineData("ramTypes")]
    [InlineData("minRamGb")]
    [InlineData("desktopOnly")]
    [InlineData("ssdOnly")]
    [InlineData("requiresWin11")]
    public void IsHardwareSpecific_AnySingleAxisConstrained_IsTrue(string axis)
    {
        var a = new TweakApplicability();
        switch (axis)
        {
            case "cpuVendors":   a.CpuVendors.Add("AMD"); break;
            case "cpuFamilies":  a.CpuFamilies.Add(CpuFamily.Ryzen7000X3D); break;
            case "gpuVendors":   a.GpuVendors.Add(GpuVendor.Nvidia); break;
            case "ramTypes":     a.RamTypes.Add("DDR5"); break;
            case "minRamGb":     a.MinRamGb = 16; break;
            case "desktopOnly":  a.DesktopOnly = true; break;
            case "ssdOnly":      a.SsdOnly = true; break;
            case "requiresWin11": a.RequiresWin11 = true; break;
        }
        Assert.True(a.IsHardwareSpecific);
    }

    // ---- Runtime-state observability: the per-row badge/checkbox depend on it -------

    [Fact]
    public void IsApplied_RaisesPropertyChanged_SoTheBadgeUpdatesAfterDetection()
        => AssertRaisesChangeFor(nameof(Tweak.IsApplied), t => t.IsApplied = true);

    [Fact]
    public void IsSelected_RaisesPropertyChanged_SoTheCheckboxStaysInSync()
        => AssertRaisesChangeFor(nameof(Tweak.IsSelected), t => t.IsSelected = true);

    private static void AssertRaisesChangeFor(string property, System.Action<Tweak> mutate)
    {
        var tweak = new Tweak();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)tweak).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        mutate(tweak);
        Assert.Contains(property, raised);
    }
}
