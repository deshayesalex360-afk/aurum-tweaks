using System;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="InputDeviceTextReport"/> — the shareable « Périphériques (entrée) » paste a user drops when the FR scene
/// asks « c'est quoi ta config souris/clavier, l'accel est off ? ». Honesty contract: it renders only the REAL state the
/// page already read (connected HID devices and how they're wired, the vendor app that owns the polling rate, the Windows
/// pointer-acceleration flag), leads with the page's own <see cref="InputTuningReport.Summary"/> so the paste matches the
/// screen, prints the per-device <see cref="InputDeviceInfo.Summary"/> line, shows « Fabricant » only when it adds something,
/// distinguishes an empty read (no devices / no vendor software) from a populated one without fabricating a bullet, and — the
/// page's signature caveat — never invents a Hz figure because the TRUE USB polling rate isn't readable without a kernel
/// filter driver. Footer keeps read-only / never-sent / no-FPS.
/// </summary>
public class InputDeviceTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 21, 14, 30, 0, DateTimeKind.Utc);

    private static InputDeviceInfo Device(
        string name, string type = "Souris", string manufacturer = "Logitech",
        string bus = "USB", bool wireless = false)
        => new()
        {
            Name = name, DeviceType = type, Manufacturer = manufacturer,
            Bus = bus, IsWireless = wireless
        };

    private static InputTuningReport Report(
        string summary = "2 souris, 1 clavier — accélération souris OFF.",
        string mouseAccelText = "Accélération souris désactivée (precision pointeur OFF) — idéal pour viser.",
        InputDeviceInfo[]? devices = null,
        string[]? software = null,
        string[]? guidance = null)
        => new()
        {
            Summary = summary,
            MouseAccelerationText = mouseAccelText,
            Devices = (devices ?? new[] { Device("Logitech G Pro X Superlight") }).ToList(),
            DetectedSoftware = (software ?? Array.Empty<string>()).ToList(),
            Guidance = (guidance ?? Array.Empty<string>()).ToList()
        };

    [Fact]
    public void Header_AlwaysCarriesTitle()
    {
        var text = InputDeviceTextReport.Render(Report(), When);
        Assert.Contains("Aurum Tweaks — Périphériques (entrée)", text);
    }

    [Fact]
    public void LeadsWithSummary()
    {
        var text = InputDeviceTextReport.Render(
            Report(summary: "2 souris, 1 clavier — accélération souris OFF."), When);
        Assert.Contains("2 souris, 1 clavier — accélération souris OFF.", text);
    }

    [Fact]
    public void Devices_AreListed_WithCountNameAndSummary()
    {
        var text = InputDeviceTextReport.Render(
            Report(devices: new[]
            {
                Device("Logitech G Pro X Superlight", type: "Souris", bus: "USB", wireless: true),
                Device("Ducky One 3", type: "Clavier", bus: "USB")
            }), When);
        Assert.Contains("PÉRIPHÉRIQUES (2)", text);
        Assert.Contains("• Logitech G Pro X Superlight", text);
        Assert.Contains("Souris · USB · sans-fil", text);   // the device's own Summary line
        Assert.Contains("• Ducky One 3", text);
        Assert.Contains("Clavier · USB", text);
    }

    [Fact]
    public void Manufacturer_AppearsOnlyWhenPresent()
    {
        var withMfr = InputDeviceTextReport.Render(
            Report(devices: new[] { Device("Souris X", manufacturer: "Razer") }), When);
        Assert.Contains("Fabricant", withMfr);
        Assert.Contains("Razer", withMfr);

        var noMfr = InputDeviceTextReport.Render(
            Report(devices: new[] { Device("Souris générique", manufacturer: "") }), When);
        Assert.DoesNotContain("Fabricant", noMfr);   // a blank manufacturer adds nothing → omitted
    }

    [Fact]
    public void DetectedSoftware_IsListed_WhenRunning()
    {
        var text = InputDeviceTextReport.Render(
            Report(software: new[] { "Logitech G HUB", "Razer Synapse" }), When);
        Assert.Contains("LOGICIELS CONSTRUCTEUR", text);
        Assert.Contains("• Logitech G HUB", text);
        Assert.Contains("• Razer Synapse", text);
    }

    [Fact]
    public void NoDetectedSoftware_SaysNoneRunning_NotAFabricatedEntry()
    {
        var text = InputDeviceTextReport.Render(
            Report(software: Array.Empty<string>()), When);
        Assert.Contains("LOGICIELS CONSTRUCTEUR", text);
        Assert.Contains("Aucun logiciel constructeur détecté en cours d'exécution", text);
    }

    [Fact]
    public void MouseAcceleration_TextIsRendered()
    {
        var text = InputDeviceTextReport.Render(
            Report(mouseAccelText: "Accélération souris ACTIVÉE — à désactiver pour viser au pixel."), When);
        Assert.Contains("ACCÉLÉRATION SOURIS", text);
        Assert.Contains("Accélération souris ACTIVÉE — à désactiver pour viser au pixel.", text);
    }

    [Fact]
    public void Guidance_IsListed_UnderPollingRateSection()
    {
        var text = InputDeviceTextReport.Render(
            Report(guidance: new[]
            {
                "Le vrai polling rate USB n'est pas lisible par Windows sans pilote noyau.",
                "1000 Hz est le standard compétitif ; 500 Hz suffit souvent."
            }), When);
        Assert.Contains("POLLING RATE / LATENCE", text);
        Assert.Contains("• Le vrai polling rate USB n'est pas lisible par Windows sans pilote noyau.", text);
        Assert.Contains("• 1000 Hz est le standard compétitif ; 500 Hz suffit souvent.", text);
    }

    [Fact]
    public void Empty_SaysNoDevices_NoSoftware_AndNoFabricatedBullet()
    {
        var text = InputDeviceTextReport.Render(
            Report(devices: Array.Empty<InputDeviceInfo>(),
                   software: Array.Empty<string>(),
                   guidance: Array.Empty<string>()), When);
        Assert.Contains("PÉRIPHÉRIQUES (0)", text);
        Assert.Contains("Aucun périphérique HID détecté par Windows", text);
        Assert.Contains("Aucun logiciel constructeur détecté en cours d'exécution", text);
        Assert.DoesNotContain("  • ", text);   // no fabricated device / software / guidance bullet
    }

    [Fact]
    public void Footer_KeepsHonestyContract_NoFabricatedHz_NoFpsClaim()
    {
        var text = InputDeviceTextReport.Render(Report(), When);
        Assert.Contains("jamais envoyé", text);
        Assert.Contains("polling rate USB n'est pas lisible sans pilote noyau", text);
        Assert.Contains("aucun chiffre de Hz n'est inventé", text);
        Assert.Contains("aucun gain de FPS", text);
    }
}
