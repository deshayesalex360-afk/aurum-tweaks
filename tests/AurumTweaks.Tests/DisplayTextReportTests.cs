using System;
using System.Collections.Generic;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="DisplayTextReport"/> — the shareable « Affichage » paste. Honesty contract: it lays out only the REAL
/// per-monitor state the page already read (live mode, the best advertised rate at the current resolution, and the same
/// honest <see cref="MonitorState.Verdict"/> shown on screen), it prints « illisible » for an unreadable mode and « non
/// énumérée » when the driver advertised no matching modes (never a fake « at max »), and the footer keeps the no-FPS and
/// read-only / never-sent honesty lines. The numbers here are integer Hz, so the asserts are locale-independent.
/// </summary>
public class DisplayTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 21, 14, 30, 0, DateTimeKind.Utc);

    // A 1080p monitor running at `currentHz`, advertising the given rates at 1920×1080. CurrentReadable unless told otherwise.
    private static MonitorState Monitor(string name, bool primary, int currentHz, int[] advertisedHz, bool readable = true)
    {
        var modes = new List<DisplayMode>();
        foreach (var hz in advertisedHz) modes.Add(new DisplayMode(1920, 1080, hz, 32));
        return new MonitorState(
            DeviceName: @"\\.\DISPLAY1",
            FriendlyName: name,
            IsPrimary: primary,
            CurrentReadable: readable,
            Current: new DisplayMode(1920, 1080, currentHz, 32),
            Orientation: DisplayOrientation.Landscape,
            SupportedModes: modes);
    }

    private static DisplayReport Report(params MonitorState[] monitors) => new(monitors);

    [Fact]
    public void Header_AlwaysCarriesTitle()
    {
        var text = DisplayTextReport.Render(Report(), When);
        Assert.Contains("Aurum Tweaks — Affichage", text);
    }

    [Fact]
    public void NoMonitors_SaysNothingWasRead_AndCarriesTheHeadline()
    {
        var text = DisplayTextReport.Render(Report(), When);
        Assert.Contains("Aucun écran actif lu", text);
        Assert.Contains("Aucun écran actif détecté.", text);   // the report's own honest headline flows through
    }

    [Fact]
    public void Monitor_IsListedWithItsModeAndOrientation()
    {
        var text = DisplayTextReport.Render(Report(Monitor("Dell U2720Q", primary: true, 144, new[] { 60, 144 })), When);
        Assert.Contains("Dell U2720Q", text);
        Assert.Contains("1920×1080 · 144 Hz", text);
        Assert.Contains("Paysage", text);
    }

    [Fact]
    public void PrimaryMonitor_IsFlagged()
    {
        var text = DisplayTextReport.Render(Report(Monitor("Écran principal", primary: true, 144, new[] { 144 })), When);
        Assert.Contains("(principal)", text);
    }

    [Fact]
    public void BelowMaxMonitor_NamesTheHigherRate_InMaxAndVerdict()
    {
        var text = DisplayTextReport.Render(Report(Monitor("Panneau 144", primary: true, 60, new[] { 60, 144 })), When);
        Assert.Contains("144 Hz à 1920×1080", text);                  // the « fréquence max » row
        Assert.Contains("60 Hz actif alors que 144 Hz est disponible", text);   // the on-screen verdict, verbatim
    }

    [Fact]
    public void AtMaxMonitor_ReadsAsMaxedOut()
    {
        var text = DisplayTextReport.Render(Report(Monitor("Panneau 144", primary: true, 144, new[] { 60, 144 })), When);
        Assert.Contains("Fréquence maximale", text);
    }

    [Fact]
    public void UnreadableMode_SaysIllisible_NotAFabricatedMode()
    {
        var text = DisplayTextReport.Render(Report(Monitor("Écran HS", primary: false, 0, new[] { 60 }, readable: false)), When);
        Assert.Contains("illisible", text);
    }

    [Fact]
    public void NoEnumeratedModes_SaysNonEnumeree_NotAFakeMax()
    {
        var text = DisplayTextReport.Render(Report(Monitor("Écran inconnu", primary: true, 60, Array.Empty<int>())), When);
        Assert.Contains("non énumérée", text);
        Assert.Contains("Modes non énumérés", text);   // the honest verdict, not a « at max » claim
    }

    [Fact]
    public void EveryMonitor_GetsItsOwnEntry()
    {
        var text = DisplayTextReport.Render(Report(
            Monitor("Écran A", primary: true, 144, new[] { 144 }),
            Monitor("Écran B", primary: false, 60, new[] { 60 })), When);
        Assert.Contains("Écran A", text);
        Assert.Contains("Écran B", text);
        Assert.Contains("ÉCRANS (2)", text);
    }

    [Fact]
    public void Footer_KeepsTheNoFpsAndReadOnlyHonestyLines()
    {
        var text = DisplayTextReport.Render(Report(Monitor("Écran", primary: true, 144, new[] { 144 })), When);
        Assert.Contains("n'augmente pas les FPS", text);
        Assert.Contains("jamais envoyé", text);
        Assert.Contains("lecture seule", text);
    }
}
