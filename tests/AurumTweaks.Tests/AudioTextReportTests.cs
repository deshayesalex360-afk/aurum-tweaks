using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="AudioTextReport"/> — the shareable « Son » paste. Honesty contract: it renders only the REAL state the
/// page already read (built through the same <see cref="AudioReport.From"/> factory production uses), the « Source » line
/// distinguishes a value read from the registry, the implicit Windows default, and a failed read (never a fabricated
/// preference), each device's status is shown verbatim with a healthy/unhealthy marker, and the footer keeps the
/// only-ducking-is-written and read-only / never-sent honesty lines.
/// </summary>
public class AudioTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 21, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Header_AlwaysCarriesTitle()
    {
        var text = AudioTextReport.Render(AudioReport.From(readOk: true, duckingRaw: "3"), When);
        Assert.Contains("Aurum Tweaks — Son", text);
    }

    [Fact]
    public void DoNothing_ReadsAsRecommended_WithRegistrySource()
    {
        var text = AudioTextReport.Render(AudioReport.From(readOk: true, duckingRaw: "3"), When);
        Assert.Contains("Ne rien faire", text);
        Assert.Contains("oui — « Ne rien faire »", text);
        Assert.Contains("valeur lue dans le registre", text);
    }

    [Fact]
    public void MuteOthers_IsNotRecommended_ForGaming()
    {
        var text = AudioTextReport.Render(AudioReport.From(readOk: true, duckingRaw: "2"), When);
        Assert.Contains("Couper tous les autres sons", text);
        Assert.Contains("non — « Ne rien faire »", text);
    }

    [Fact]
    public void AbsentValue_ReadsAsImplicitWindowsDefault_NotAFabricatedPreference()
    {
        // readOk false → no value present; production reports the implicit Windows default (reduce 80 %), said plainly.
        var text = AudioTextReport.Render(AudioReport.From(readOk: false, duckingRaw: null), When);
        Assert.Contains("implicite — aucune valeur définie", text);
    }

    [Fact]
    public void FailedRead_SaysReadImpossible_AndSourceNonLue()
    {
        var text = AudioTextReport.Render(AudioReport.Failed, When);
        Assert.Contains("Lecture des réglages audio impossible.", text);
        Assert.Contains("non lue — réglages audio illisibles", text);
    }

    [Fact]
    public void SilentScheme_IsFlagged()
    {
        var text = AudioTextReport.Render(AudioReport.From(readOk: true, duckingRaw: "3", schemeRaw: ".None"), When);
        Assert.Contains("Aucun son", text);
        Assert.Contains("(silencieux)", text);
    }

    [Fact]
    public void Devices_AreListed_WithHealthyAndUnhealthyMarkers()
    {
        var devices = new[]
        {
            new AudioDevice("Realtek High Definition Audio", "Realtek", "OK"),
            new AudioDevice("USB DAC", "", "Error")
        };
        var text = AudioTextReport.Render(AudioReport.From(true, "3", null, devices), When);
        Assert.Contains("PÉRIPHÉRIQUES AUDIO (2)", text);
        Assert.Contains("● Realtek High Definition Audio", text);   // healthy device gets the filled bullet
        Assert.Contains("○ USB DAC", text);                          // anything-but-OK gets the hollow one, status verbatim
        Assert.Contains("Error", text);
    }

    [Fact]
    public void NoDevices_SaysNothingWasListed()
    {
        var text = AudioTextReport.Render(AudioReport.From(readOk: true, duckingRaw: "3"), When);
        Assert.Contains("Aucun périphérique listé", text);
    }

    [Fact]
    public void Footer_KeepsTheOnlyDuckingWrittenAndReadOnlyHonestyLines()
    {
        var text = AudioTextReport.Render(AudioReport.From(readOk: true, duckingRaw: "3"), When);
        Assert.Contains("mode exclusif", text);     // names what Aurum does NOT touch
        Assert.Contains("jamais envoyé", text);
        Assert.Contains("lecture seule", text);
    }
}
