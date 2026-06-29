using System;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the honesty-bearing driver-scan logic — the pure <see cref="DriverClassification"/> core
/// extracted from <see cref="DriverScanService"/>, plus the <see cref="DriverInfo"/> status props the
/// UI renders. These decide what we nag the user about and how the result reads, so they are exactly
/// where a regression would mislead: nagging about a fresh (or Microsoft inbox) driver, mislabelling a
/// broken device as "OK", or claiming everything is "à jour" when it isn't. A fixed <c>asOf</c> keeps the
/// 3-year age rule independent of the wall clock.
/// </summary>
public class DriverClassificationTests
{
    private static readonly DateTime AsOf = new(2026, 1, 1);

    // ---- IsWorthChecking: the "old, non-Microsoft, worth a look" rule -----------

    [Fact]
    public void IsWorthChecking_NullDate_IsFalse()
        // No date → we can't honestly call it old, so we never flag it.
        => Assert.False(DriverClassification.IsWorthChecking(null, "ASUS", "Realtek", AsOf));

    [Theory]
    [InlineData(0, false)]       // brand new
    [InlineData(-1094, false)]   // just under 3 years
    [InlineData(-1095, false)]   // exactly 3 years is NOT "older than" 3 years
    [InlineData(-1096, true)]    // one day past the threshold
    [InlineData(-3650, true)]    // ~10 years
    public void IsWorthChecking_AgeThreshold_IsStrictlyGreaterThanThreeYears(int dayOffset, bool expected)
        => Assert.Equal(expected,
            DriverClassification.IsWorthChecking(AsOf.AddDays(dayOffset), "ASUS", "Realtek", AsOf));

    [Theory]
    [InlineData("Microsoft", "Realtek")]
    [InlineData("ASUS", "Microsoft")]
    [InlineData("microsoft corporation", "Generic")]   // case-insensitive
    public void IsWorthChecking_MicrosoftInbox_IsNeverFlagged_EvenWhenAncient(string mfr, string provider)
        // Inbox drivers being old is normal/stable — nagging about them would be noise, not honesty.
        => Assert.False(DriverClassification.IsWorthChecking(AsOf.AddDays(-3650), mfr, provider, AsOf));

    [Fact]
    public void IsWorthChecking_NonMicrosoftAndAncient_IsFlagged()
        => Assert.True(DriverClassification.IsWorthChecking(AsOf.AddDays(-3650), "ASUS", "Realtek", AsOf));

    // ---- MapErrorCode: ConfigManagerErrorCode → French reason -------------------

    [Theory]
    [InlineData(10, "ne peut pas démarrer")]
    [InlineData(22, "désactivé")]
    [InlineData(28, "manquants")]
    [InlineData(43, "arrêté")]
    public void MapErrorCode_KnownCodes_HaveSpecificText(int code, string fragment)
        => Assert.Contains(fragment, DriverClassification.MapErrorCode(code));

    [Fact]
    public void MapErrorCode_UnknownCode_FallsBackButKeepsTheNumber()
    {
        // An unmapped code must not vanish or read as a known fault — surface the raw code honestly.
        var text = DriverClassification.MapErrorCode(999);
        Assert.Contains("999", text);
        Assert.Contains("code", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- BuildSummary: the headline sentence -----------------------------------

    [Fact]
    public void BuildSummary_WithProblems_LeadsWithProblemCount()
    {
        var s = DriverClassification.BuildSummary(problemCount: 2, oldCount: 3, totalCount: 50);
        Assert.Contains("2 périphérique(s) en erreur", s);
        Assert.Contains("3 pilote(s) ancien(s)", s);
        Assert.Contains("50", s);
    }

    [Fact]
    public void BuildSummary_NoProblems_ButOldDrivers_SaysNoErrors_AndDoesNotClaimUpToDate()
    {
        var s = DriverClassification.BuildSummary(problemCount: 0, oldCount: 3, totalCount: 50);
        Assert.StartsWith("Aucun périphérique en erreur", s);
        Assert.Contains("3 pilote(s) ancien(s)", s);
        Assert.DoesNotContain("à jour", s);   // old drivers remain → not an all-clear
    }

    [Fact]
    public void BuildSummary_AllClean_ClaimsUpToDate_OnlyWhenTrulyClean()
    {
        var s = DriverClassification.BuildSummary(problemCount: 0, oldCount: 0, totalCount: 50);
        Assert.Contains("Aucun problème détecté", s);
        Assert.Contains("à jour", s);
        Assert.Contains("50", s);
    }

    // ---- DriverInfo status props the UI renders --------------------------------

    [Fact]
    public void DriverInfo_StatusLabel_ProblemOutranksOld_OutranksOk()
    {
        Assert.Equal("PROBLÈME", new DriverInfo { IsProblem = true, IsOld = true }.StatusLabel);
        Assert.Equal("À VÉRIFIER", new DriverInfo { IsProblem = false, IsOld = true }.StatusLabel);
        Assert.Equal("OK", new DriverInfo { IsProblem = false, IsOld = false }.StatusLabel);
    }

    [Fact]
    public void DriverInfo_Priority_SortsProblemThenOldThenOk()
    {
        Assert.Equal(1000, new DriverInfo { IsProblem = true }.Priority);
        Assert.Equal(500, new DriverInfo { IsOld = true }.Priority);
        Assert.Equal(100, new DriverInfo().Priority);
    }

    [Fact]
    public void DriverInfo_UnknownDate_ReadsHonestly_NotAsYearZero()
    {
        var d = new DriverInfo { DriverDate = null };
        Assert.Equal(-1, d.AgeYears);
        Assert.Equal("date inconnue", d.DriverDateText);
    }

    [Fact]
    public void DriverInfo_KnownDate_FormatsIso()
        => Assert.Equal("2021-07-15", new DriverInfo { DriverDate = new DateTime(2021, 7, 15) }.DriverDateText);
}
