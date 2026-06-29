using System;
using System.Collections.Generic;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure core of the HIDUSBF-style input feature (<see cref="InputTuningLogic"/>, extracted from
/// <see cref="InputDeviceService"/>). This is an honesty surface, not just formatting: the feature's whole
/// premise is that the <b>true USB polling rate is unreadable from Windows without a kernel driver we don't
/// ship</b>, so the guidance must always say that and the summary must never fabricate a Hz figure. Also
/// pins the bus classifier (what we tell the user their device is connected over) and the pointer-accel
/// advice. No WMI / running processes / registry touched.
/// </summary>
public class InputTuningLogicTests
{
    // ---- ClassifyBus: PNPDeviceID prefix → human bus + wireless flag ----------------

    [Theory]
    [InlineData(@"USB\VID_046D&PID_C547\6&1a2b3c", "USB", false)]
    [InlineData(@"BTHLEDevice\Dev_F81D4FAE\7&abc", "Bluetooth LE", true)]
    [InlineData(@"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&", "Bluetooth", true)]
    [InlineData(@"BTH\MS_BTHPAN\6&", "Bluetooth", true)]
    [InlineData(@"ACPI\PNP0F03\4&", "PS/2", false)]                       // PS/2 mouse under ACPI
    [InlineData(@"HID\VID_046D&PID_C08B&MI_01&COL01\7&", "USB (HID)", false)]
    [InlineData(@"HID\GenericCollectionDevice", "HID", false)]            // HID-class, no USB markers
    [InlineData(@"SERIO\VEN_0001&PS2MOUSE\3&", "PS/2", false)]            // secondary VEN_+PS2 heuristic
    [InlineData(@"ROOT\SOMETHINGELSE\0000", "HID", false)]               // unknown → generic HID fallback
    [InlineData("", "HID", false)]
    [InlineData(null, "HID", false)]
    public void ClassifyBus_MapsPnpPrefixToBus(string? pnp, string expectedBus, bool expectedWireless)
    {
        var (bus, wireless) = InputTuningLogic.ClassifyBus(pnp!);
        Assert.Equal(expectedBus, bus);
        Assert.Equal(expectedWireless, wireless);
    }

    // ---- MouseAccelerationText: the pointer-accel advice we CAN act on ---------------

    [Fact]
    public void MouseAccelerationText_On_WarnsAndTellsHowToDisable()
    {
        var t = InputTuningLogic.MouseAccelerationText(true);
        Assert.Contains("ACTIVE", t);
        Assert.Contains("désactive", t, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MouseAccelerationText_Off_ConfirmsOneToOneAim()
    {
        var t = InputTuningLogic.MouseAccelerationText(false);
        Assert.Contains("désactivée", t);
        Assert.Contains("1:1", t);
    }

    // ---- BuildGuidance: the honesty-bearing polling-rate guidance --------------------

    [Fact]
    public void BuildGuidance_AlwaysLeadsWithThePollingRateHonestyStatement()
    {
        var g = InputTuningLogic.BuildGuidance(new List<string>());

        Assert.Equal(5, g.Count);
        // The load-bearing honesty line: true rate is unreadable without a kernel driver.
        Assert.Contains("ne se lit pas", g[0]);
        Assert.Contains("sans pilote noyau", g[0]);
        Assert.Contains(g, x => x.Contains("HIDUSBF"));
        Assert.Contains(g, x => x.Contains("1000 Hz"));
    }

    [Fact]
    public void BuildGuidance_NoSoftware_PointsToOnboardMemory()
    {
        var g = InputTuningLogic.BuildGuidance(new List<string>());

        Assert.Contains(g, x => x.Contains("Aucun logiciel constructeur détecté"));
        Assert.Contains(g, x => x.Contains("onboard memory"));
    }

    [Fact]
    public void BuildGuidance_WithSoftware_NamesItAndPrefersIt_NotTheFallback()
    {
        var g = InputTuningLogic.BuildGuidance(new List<string> { "Logitech G HUB", "Razer Synapse" });

        Assert.Equal(5, g.Count);
        Assert.Contains(g, x => x.Contains("Logitech G HUB, Razer Synapse"));
        Assert.Contains(g, x => x.Contains("plus sûr"));
        Assert.DoesNotContain(g, x => x.Contains("Aucun logiciel constructeur détecté"));
    }

    // ---- BuildSummary: the one-line headline (counts, never a fabricated rate) -------

    [Fact]
    public void BuildSummary_NoDevices_SaysSo_AndShowsAccelState()
        => Assert.Equal("Aucun périphérique HID détecté · accélération souris OFF",
            InputTuningLogic.BuildSummary(0, 0, 0, 0, false));

    [Fact]
    public void BuildSummary_FullLine_CountsSoftwareAndAccelOn()
        => Assert.Equal(
            "2 souris · 1 clavier(s) · 3 autre(s) HID · 2 logiciel(s) constructeur actif(s) · accélération souris ON",
            InputTuningLogic.BuildSummary(2, 1, 3, 2, true));

    [Fact]
    public void BuildSummary_OmitsZeroCounts_AndSoftwareWhenNone()
        => Assert.Equal("1 souris · 1 clavier(s) · accélération souris OFF",
            InputTuningLogic.BuildSummary(1, 1, 0, 0, false));

    [Fact]
    public void BuildSummary_NeverFabricatesAPollingRate()
        // The summary is a count line — it must never carry a Hz figure (the honest limitation).
        => Assert.DoesNotContain("Hz", InputTuningLogic.BuildSummary(1, 1, 0, 1, true));
}
