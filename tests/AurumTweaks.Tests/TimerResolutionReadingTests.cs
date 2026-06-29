using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="TimerResolutionReading"/> — the honest interpretation of <c>NtQueryTimerResolution</c>'s 100-ns values
/// behind the Latency page's « Résolution du minuteur système » card. The load-bearing honesty points: the API's inverted
/// naming is untangled (its « Maximum resolution » is the MOST precise / smallest value, its « Minimum » is the coarse
/// default), milliseconds render in fr-FR (« 0,5 ms »), the verdict is keyed on the active value, and a failed query
/// (<c>QueryOk=false</c>) reads as « indisponible » rather than a fabricated 0 ms.
/// </summary>
public class TimerResolutionReadingTests
{
    // Convenience: (current, default-coarse, best-precise) in 100-ns units, mirroring the record's field order.
    private static TimerResolutionReading R(uint current, uint min, uint max, bool ok = true) =>
        new(ok, current, min, max);

    [Fact]
    public void Level_IsHigh_WhenCurrentAtOrBelowOneMs()
    {
        Assert.Equal(TimerResolutionLevel.High, R(5000, 156250, 5000).Level);    // 0.5 ms
        Assert.Equal(TimerResolutionLevel.High, R(10000, 156250, 5000).Level);   // 1.0 ms boundary is still High
    }

    [Fact]
    public void Level_IsMedium_BetweenOneAndTenMs()
        => Assert.Equal(TimerResolutionLevel.Medium, R(20000, 156250, 5000).Level);   // 2.0 ms

    [Fact]
    public void Level_IsDefault_AtOrAboveTenMs()
    {
        Assert.Equal(TimerResolutionLevel.Default, R(100000, 156250, 5000).Level);   // 10.0 ms boundary → Default
        Assert.Equal(TimerResolutionLevel.Default, R(156250, 156250, 5000).Level);   // 15.625 ms default
    }

    [Fact]
    public void Level_IsUnknown_WhenQueryFailed()
        => Assert.Equal(TimerResolutionLevel.Unknown, R(0, 0, 0, ok: false).Level);

    [Fact]
    public void Displays_FormatMilliseconds_InFrenchCulture()
    {
        var r = R(5000, 160000, 5000);
        Assert.Equal("0,5 ms", r.CurrentDisplay);   // comma decimal separator, not a period
        Assert.Equal("0,5 ms", r.BestDisplay);       // « Maximum resolution » field = most precise
        Assert.Equal("16 ms", r.DefaultDisplay);     // « Minimum resolution » field = coarse default
    }

    [Fact]
    public void CurrentMs_ConvertsHundredNsToMilliseconds()
        => Assert.Equal(0.5, R(5000, 156250, 5000).CurrentMs);

    [Fact]
    public void Headline_High_CallsItFine()
        => Assert.Contains("fine", R(5000, 156250, 5000).Headline);

    [Fact]
    public void Headline_Default_NamesTheDefault()
        => Assert.Contains("défaut", R(156250, 156250, 5000).Headline);

    [Fact]
    public void Detail_Default_PointsAtTheSupportedMaximum()
        // The honest hand-off: the user learns the best the platform could reach, stated as a real value.
        => Assert.Contains("0,5 ms", R(156250, 156250, 5000).Detail);

    [Fact]
    public void FailedQuery_IsHonest_NotAFabricatedReading()
    {
        var r = R(0, 0, 0, ok: false);
        Assert.Equal(TimerResolutionLevel.Unknown, r.Level);
        Assert.Equal("Résolution indisponible", r.Headline);
        Assert.Contains("n'a pas pu lire", r.Detail);
    }
}
