using System;
using System.Collections.Generic;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="PowerPlanTextReport"/> — the shareable « Plan d'alimentation » paste. Honesty contract: it lays out
/// only the REAL state the page already read (the active plan, every installed scheme with the active one marked exactly
/// as powercfg flagged it, and the active plan's processor detail), it prints « non lu » rather than a fabricated value
/// when powercfg couldn't read the processor knobs, and the footer states the report is read-only and never sent. Scheme
/// identity rides on the GUID, which appears verbatim so a paste can be cross-checked.
/// </summary>
public class PowerPlanTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 21, 14, 30, 0, DateTimeKind.Utc);

    private static readonly Guid BalancedId = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid UltimateId = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private static PowerPlanReport Plan(string? activeName, params PowerScheme[] schemes) =>
        new(schemes, activeName, UltimatePresent: false);

    private static ProcessorPowerDetail Detail(int? min, int? max, int? cores, bool ok = true) =>
        new(min, max, cores, ok);

    [Fact]
    public void Header_AlwaysCarriesTitle()
    {
        var text = PowerPlanTextReport.Render(Plan(null), null, When);
        Assert.Contains("Aurum Tweaks — Plan d'alimentation", text);
    }

    [Fact]
    public void ActivePlan_ShownWhenPresent()
    {
        var text = PowerPlanTextReport.Render(Plan("Performances ultimes"), null, When);
        Assert.Contains("Plan actif : Performances ultimes", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ActivePlan_BlankOrNull_RendersDash(string? active)
    {
        var text = PowerPlanTextReport.Render(Plan(active), null, When);
        Assert.Contains("Plan actif : —", text);
    }

    [Fact]
    public void EmptyScheme_List_SaysNothingWasRead()
    {
        var text = PowerPlanTextReport.Render(Plan(null), null, When);
        Assert.Contains("Aucun plan lu", text);
    }

    [Fact]
    public void EachScheme_IsListedWithItsGuid_ActiveOneMarked()
    {
        var text = PowerPlanTextReport.Render(Plan("Performances ultimes",
            new PowerScheme(BalancedId, "Utilisation normale (équilibré)", IsActive: false),
            new PowerScheme(UltimateId, "Performances ultimes", IsActive: true)), null, When);

        Assert.Contains("Utilisation normale (équilibré)", text);
        Assert.Contains("Performances ultimes", text);
        Assert.Contains(BalancedId.ToString(), text);     // GUID printed verbatim for cross-checking
        Assert.Contains(UltimateId.ToString(), text);
        Assert.Contains("● Performances ultimes", text);   // active gets the bullet
    }

    [Fact]
    public void InactiveScheme_DoesNotGetTheActiveBullet()
    {
        var text = PowerPlanTextReport.Render(Plan(null,
            new PowerScheme(BalancedId, "Utilisation normale (équilibré)", IsActive: false)), null, When);
        Assert.DoesNotContain("● Utilisation normale", text);
    }

    [Fact]
    public void ProcessorDetail_WhenRead_RendersRowsAndInterpretation()
    {
        var text = PowerPlanTextReport.Render(Plan("Performances élevées"), Detail(100, 100, 100), When);
        Assert.Contains("DÉTAIL PROCESSEUR (sur secteur)", text);
        Assert.Contains("État minimal", text);
        Assert.Contains("État maximal", text);
        Assert.Contains("Parcage des cœurs", text);
        Assert.Contains("100 %", text);
        Assert.Contains("ne réduit jamais", text);          // the real interpretation flows through
        Assert.DoesNotContain("non lu", text);
    }

    [Fact]
    public void ProcessorDetail_Null_SaysNonLu()
    {
        var text = PowerPlanTextReport.Render(Plan("Performances élevées"), null, When);
        Assert.Contains("non lu", text);
    }

    [Fact]
    public void ProcessorDetail_FailedQuery_SaysNonLu_NotAFabricatedZero()
    {
        var text = PowerPlanTextReport.Render(Plan("Performances élevées"), Detail(null, null, null, ok: false), When);
        Assert.Contains("non lu", text);
        Assert.DoesNotContain("0 %", text);                 // never a clean-looking fabricated zero sheet
    }

    [Fact]
    public void Footer_KeepsTheReadOnlyAndNeverSentHonestyLines()
    {
        var text = PowerPlanTextReport.Render(Plan("Performances élevées"), Detail(100, 100, 100), When);
        Assert.Contains("jamais envoyé", text);
        Assert.Contains("lecture seule", text);
    }
}
