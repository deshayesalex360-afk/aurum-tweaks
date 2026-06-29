using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="DriveHealthTextReport"/> — the shareable « est-ce que mon SSD meurt ? » paste. Honesty contract: it
/// renders only the REAL state the page already read (Windows' own SMART interpretation), leads with the shared
/// <see cref="DriveHealthReport.Headline"/> so the paste matches the screen, prints « — » for a counter Windows didn't
/// expose (never a fabricated zero), carries the USB-bridge caveat so an empty external drive never reads as « healthy »,
/// distinguishes a failed query from an all-clear, and keeps the read-only / never-sent / back-up-anyway footer lines.
/// </summary>
public class DriveHealthTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 21, 14, 30, 0, DateTimeKind.Utc);

    private static DriveHealthInfo Drive(
        string name = "Samsung 990 Pro", DriveHealth health = DriveHealth.Healthy, DriveMedia media = DriveMedia.Ssd,
        string bus = "NVMe", long size = 1000204886016, int? temp = 35, int? wear = 2,
        long? hours = 1200, long? errors = 0)
        => new(name, media, health, bus, size, temp, wear, hours, errors);

    private static DriveHealthReport Report(params DriveHealthInfo[] drives) => new(drives, QueryOk: true);
    private static DriveHealthReport Failed() => new(Array.Empty<DriveHealthInfo>(), QueryOk: false);

    [Fact]
    public void Header_AlwaysCarriesTitle()
    {
        var text = DriveHealthTextReport.Render(Report(Drive()), When);
        Assert.Contains("Aurum Tweaks — Santé des disques", text);
    }

    [Fact]
    public void Headline_AllHealthy_IsRendered()
    {
        var text = DriveHealthTextReport.Render(Report(Drive(), Drive(name: "WD Blue")), When);
        Assert.Contains("Tous les disques sont sains.", text);
    }

    [Fact]
    public void FailedQuery_SaysReadImpossible_AndIndisponibleHeadline_NotAnAllClear()
    {
        var text = DriveHealthTextReport.Render(Failed(), When);
        Assert.Contains("État des disques indisponible", text);          // never a fabricated « sains »
        Assert.Contains("Lecture impossible", text);
        Assert.DoesNotContain("Tous les disques sont sains", text);
    }

    [Fact]
    public void NoDrives_ButQueryOk_SaysNothingWasListed()
    {
        var text = DriveHealthTextReport.Render(Report(), When);
        Assert.Contains("DISQUES (0)", text);
        Assert.Contains("Aucun disque physique listé", text);
    }

    [Fact]
    public void HealthyDrive_ListsIdentityAndVerdictLabel()
    {
        var text = DriveHealthTextReport.Render(Report(Drive()), When);
        Assert.Contains("• Samsung 990 Pro  [Sain]", text);
        Assert.Contains("SSD", text);
        Assert.Contains("NVMe", text);
        Assert.Contains("35 °C", text);
    }

    [Fact]
    public void UnhealthyDrive_IsFlaggedDefaillant_WithWindowsVerdictMessage_AndAlerteHeadline()
    {
        var text = DriveHealthTextReport.Render(Report(Drive(name: "Vieux SSD", health: DriveHealth.Unhealthy)), When);
        Assert.Contains("[Défaillant]", text);
        Assert.Contains("Alerte", text);                                  // headline never cheerier than the worst drive
        Assert.Contains("envisage un remplacement", text);               // Windows' own verdict message, verbatim
    }

    [Fact]
    public void HighWearHealthyDrive_EscalatesToWatch_NeverToFailing()
    {
        // Windows says Healthy but wear is past the watch threshold → « À surveiller », never « Défaillant ».
        var text = DriveHealthTextReport.Render(Report(Drive(wear: 90)), When);
        Assert.Contains("[À surveiller]", text);
        Assert.DoesNotContain("[Défaillant]", text);
    }

    [Fact]
    public void AbsentCounters_RenderDash_NeverAFabricatedZero()
    {
        // A drive Windows reported no reliability counters for: every absent metric must be « — », not « 0 ».
        var text = DriveHealthTextReport.Render(
            Report(Drive(temp: null, wear: null, hours: null, errors: null)), When);
        Assert.Contains("Température", text);
        Assert.Contains("Usure", text);
        Assert.DoesNotContain("0 °C", text);   // no fabricated temperature
        Assert.DoesNotContain("0 %", text);    // no fabricated wear
    }

    [Fact]
    public void UsbDrive_IsMarkedExternal_AndCarriesTheSmartMaskedCaveat()
    {
        var text = DriveHealthTextReport.Render(
            Report(Drive(name: "Disque externe", bus: "USB", temp: null, wear: null, hours: null, errors: null)), When);
        Assert.Contains("(externe)", text);
        Assert.Contains("le pont masque souvent les compteurs SMART", text);
    }

    [Fact]
    public void EveryDrive_IsListed_WithTheCountHeaderAndTally()
    {
        var text = DriveHealthTextReport.Render(
            Report(Drive(name: "Disque A"), Drive(name: "Disque B", wear: 90)), When);
        Assert.Contains("DISQUES (2)", text);
        Assert.Contains("1 sain(s), 1 à surveiller, 0 en alerte", text);
        Assert.Contains("Disque A", text);
        Assert.Contains("Disque B", text);
    }

    [Fact]
    public void Footer_KeepsTheReadOnlyNeverSentAndBackupHonestyLines()
    {
        var text = DriveHealthTextReport.Render(Report(Drive()), When);
        Assert.Contains("jamais envoyé", text);
        Assert.Contains("aucun gain de FPS", text);
        Assert.Contains("sauvegarde tes données", text);
    }
}
