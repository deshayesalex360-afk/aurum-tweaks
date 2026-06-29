using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

// Test helpers shared across the Display fixtures.
internal static class DisplayFixtures
{
    public static DisplayMode M(int w, int h, int hz, int bpp = 32) => new(w, h, hz, bpp);

    public static MonitorState Mon(
        DisplayMode current,
        IReadOnlyList<DisplayMode> modes,
        bool currentReadable = true,
        bool primary = true,
        string device = @"\\.\DISPLAY1",
        string friendly = "AW2725DF",
        DisplayOrientation orient = DisplayOrientation.Landscape)
        => new(device, friendly, primary, currentReadable, current, orient, modes);

    public static readonly DisplayMode[] Panel1440 =
    {
        M(2560, 1440, 60), M(2560, 1440, 120), M(2560, 1440, 144)
    };
}

public class DisplayModeTests
{
    [Fact]
    public void IsValid_True_ForRealResolution() => Assert.True(DisplayFixtures.M(1920, 1080, 60).IsValid);

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    public void IsValid_False_WhenDimensionMissing(int w, int h) => Assert.False(DisplayFixtures.M(w, h, 60).IsValid);

    [Fact]
    public void ResolutionLabel_Formats() => Assert.Equal("2560×1440", DisplayFixtures.M(2560, 1440, 144).ResolutionLabel);
}

public class DisplayDiagnosticsTests
{
    private static readonly DisplayMode[] Modes =
    {
        DisplayFixtures.M(1920, 1080, 60), DisplayFixtures.M(1920, 1080, 240),
        DisplayFixtures.M(2560, 1440, 60), DisplayFixtures.M(2560, 1440, 120), DisplayFixtures.M(2560, 1440, 144),
    };

    [Fact]
    public void MaxRefreshAt_PicksHighest_ForThatResolutionOnly()
        => Assert.Equal(144, DisplayDiagnostics.MaxRefreshAt(Modes, 2560, 1440));

    [Fact]
    public void MaxRefreshAt_IgnoresOtherResolutions()
        => Assert.Equal(240, DisplayDiagnostics.MaxRefreshAt(Modes, 1920, 1080));

    [Fact]
    public void MaxRefreshAt_Zero_WhenNoMatch()
        => Assert.Equal(0, DisplayDiagnostics.MaxRefreshAt(Modes, 3840, 2160));

    [Fact]
    public void RefreshRatesAt_AreDistinctAndAscending_ForResolution()
        => Assert.Equal(new[] { 60, 120, 144 }, DisplayDiagnostics.RefreshRatesAt(Modes, 2560, 1440));

    [Fact]
    public void RefreshRatesAt_Empty_WhenNoMatch()
        => Assert.Empty(DisplayDiagnostics.RefreshRatesAt(Modes, 3840, 2160));

    [Fact]
    public void RefreshRatesAt_DropsZeroAndNegative()
    {
        var modes = new[] { DisplayFixtures.M(2560, 1440, 0), DisplayFixtures.M(2560, 1440, -1), DisplayFixtures.M(2560, 1440, 60) };
        Assert.Equal(new[] { 60 }, DisplayDiagnostics.RefreshRatesAt(modes, 2560, 1440));
    }
}

public class RefreshOptionTests
{
    [Fact]
    public void Current_IsNotSelectable_AndLabelledActive()
    {
        var o = new RefreshOption(@"\\.\DISPLAY1", 2560, 1440, 60, IsCurrent: true);
        Assert.False(o.IsSelectable);
        Assert.Equal("60 Hz · actif", o.Label);
    }

    [Fact]
    public void NonCurrent_IsSelectable_AndPlainLabel()
    {
        var o = new RefreshOption(@"\\.\DISPLAY1", 2560, 1440, 144, IsCurrent: false);
        Assert.True(o.IsSelectable);
        Assert.Equal("144 Hz", o.Label);
    }
}

public class MonitorStateTests
{
    [Fact]
    public void BelowMax_OffersRaise_NeverClaimsAtMax()
    {
        var m = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 60), DisplayFixtures.Panel1440);

        Assert.True(m.CanRaiseRefresh);
        Assert.False(m.IsAtMaxRefresh);
        Assert.Equal(144, m.MaxRefreshAtCurrent);
        Assert.Equal(144, m.RaiseTarget.Hz);
        Assert.True(m.RaiseTarget.IsSelectable);
        Assert.Equal("Passer à 144 Hz", m.RaiseLabel);
        Assert.Contains("60 Hz actif", m.Verdict);
        Assert.Contains("144 Hz", m.Verdict);
        Assert.DoesNotContain("✓", m.Verdict);
        Assert.True(m.ShowRefreshOptions);
    }

    [Fact]
    public void AtMax_OffersNothing_AndConfirmsHonestly()
    {
        var m = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 144), DisplayFixtures.Panel1440);

        Assert.True(m.IsAtMaxRefresh);
        Assert.False(m.CanRaiseRefresh);
        Assert.StartsWith("✓", m.Verdict);
        Assert.Contains("144 Hz", m.Verdict);
    }

    [Fact]
    public void UnreadableCurrent_GivesNoActionAndNoFabrication()
    {
        var m = DisplayFixtures.Mon(DisplayFixtures.M(0, 0, 0), DisplayFixtures.Panel1440, currentReadable: false);

        Assert.False(m.CanRaiseRefresh);
        Assert.False(m.IsAtMaxRefresh);
        Assert.False(m.ShowRefreshOptions);
        Assert.Equal("Mode courant illisible", m.CurrentLabel);
        Assert.Equal("Mode courant illisible — diagnostic indisponible.", m.Verdict);
    }

    [Fact]
    public void NoModeList_CannotCompare_NoDeadButtons()
    {
        var m = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 60), new DisplayMode[0]);

        Assert.Equal(0, m.MaxRefreshAtCurrent);
        Assert.False(m.CanRaiseRefresh);
        Assert.False(m.IsAtMaxRefresh);
        Assert.False(m.ShowRefreshOptions);
        Assert.Equal("Modes non énumérés — impossible de comparer à la fréquence maximale.", m.Verdict);
    }

    [Fact]
    public void RefreshOptions_OnlyForCurrentResolution_AndMarkCurrent()
    {
        var modes = new[]
        {
            DisplayFixtures.M(1920, 1080, 240),         // a different resolution — must be excluded
            DisplayFixtures.M(2560, 1440, 60), DisplayFixtures.M(2560, 1440, 120),
        };
        var m = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 60), modes);

        var hz = m.RefreshOptions.Select(o => o.Hz).ToArray();
        Assert.Equal(new[] { 60, 120 }, hz);
        Assert.DoesNotContain(240, hz);                 // the 1080p rate never leaks in
        Assert.Equal(120, m.MaxRefreshAtCurrent);       // nor inflates the max
        Assert.Single(m.RefreshOptions, o => o.IsCurrent);
        Assert.True(m.RefreshOptions.Single(o => o.Hz == 60).IsCurrent);
        Assert.True(m.RefreshOptions.Single(o => o.Hz == 120).IsSelectable);
    }

    [Fact]
    public void SingleRate_ShowsNoSelector_AndNoRaise()
    {
        var m = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 60), new[] { DisplayFixtures.M(2560, 1440, 60) });

        Assert.False(m.CanRaiseRefresh);
        Assert.False(m.ShowRefreshOptions);
        Assert.Single(m.RefreshOptions);
    }

    [Fact]
    public void DisplayName_FallsBackToDevice_WhenFriendlyBlank()
    {
        var m = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 60), DisplayFixtures.Panel1440, friendly: "   ");
        Assert.Equal(@"\\.\DISPLAY1", m.DisplayName);
    }

    [Fact]
    public void CurrentLabel_FormatsResolutionRefreshDepth()
    {
        var m = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 60, 32), DisplayFixtures.Panel1440);
        Assert.Equal("2560×1440 · 60 Hz · 32 bits", m.CurrentLabel);
    }

    [Theory]
    [InlineData(DisplayOrientation.Landscape, "Paysage")]
    [InlineData(DisplayOrientation.Portrait, "Portrait (90°)")]
    [InlineData(DisplayOrientation.LandscapeFlipped, "Paysage inversé (180°)")]
    [InlineData(DisplayOrientation.PortraitFlipped, "Portrait inversé (270°)")]
    public void OrientationLabel_Maps(DisplayOrientation o, string expected)
    {
        var m = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 60), DisplayFixtures.Panel1440, orient: o);
        Assert.Equal(expected, m.OrientationLabel);
    }

    // The no-dead-button invariant: whenever a raise is offered, the target is a real, selectable advertised rate.
    [Theory]
    [InlineData(60, true)]
    [InlineData(120, true)]
    [InlineData(144, false)]
    public void RaiseInvariant_TargetIsAlwaysASelectableAdvertisedRate(int currentHz, bool expectRaise)
    {
        var m = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, currentHz), DisplayFixtures.Panel1440);

        Assert.Equal(expectRaise, m.CanRaiseRefresh);
        if (m.CanRaiseRefresh)
        {
            Assert.Equal(m.MaxRefreshAtCurrent, m.RaiseTarget.Hz);
            Assert.True(m.RaiseTarget.IsSelectable);
            Assert.Contains(m.RefreshOptions, o => o.Hz == m.MaxRefreshAtCurrent && o.IsSelectable);
        }
    }
}

public class DisplayApplyOutcomeTests
{
    [Fact]
    public void Verified_WhenSuccessAndMeasuredMatchesRequested()
    {
        var o = new DisplayApplyOutcome(@"\\.\DISPLAY1", 144, 144, DisplayChangeStatus.Succeeded);
        Assert.True(o.Verified);
        Assert.Equal("Fréquence appliquée et vérifiée : 144 Hz.", o.Summary);
    }

    [Fact]
    public void NotVerified_WhenSuccessButMeasuredDiffers_ReportsBothRates()
    {
        var o = new DisplayApplyOutcome(@"\\.\DISPLAY1", 144, 60, DisplayChangeStatus.Succeeded);
        Assert.False(o.Verified);
        Assert.Contains("60 Hz", o.Summary);
        Assert.Contains("144 Hz", o.Summary);
    }

    [Fact]
    public void RequiresRestart_IsNotVerified_AndMentionsReboot()
    {
        var o = new DisplayApplyOutcome(@"\\.\DISPLAY1", 144, 60, DisplayChangeStatus.RequiresRestart);
        Assert.False(o.Verified);
        Assert.Contains("redémarrage", o.Summary);
    }

    [Fact]
    public void BadMode_ReportsRefusal()
    {
        var o = new DisplayApplyOutcome(@"\\.\DISPLAY1", 240, 60, DisplayChangeStatus.BadMode);
        Assert.False(o.Verified);
        Assert.Contains("refusé", o.Summary);
    }

    [Fact]
    public void Failed_ReportsFailure()
    {
        var o = new DisplayApplyOutcome(@"\\.\DISPLAY1", 144, 60, DisplayChangeStatus.Failed);
        Assert.False(o.Verified);
        Assert.Contains("échoué", o.Summary);
    }

    [Fact]
    public void NotAttempted_Factory_IsHonestNoop()
    {
        var o = DisplayApplyOutcome.NotAttempted(@"\\.\DISPLAY1");
        Assert.Equal(DisplayChangeStatus.NotAttempted, o.Status);
        Assert.False(o.Verified);
        Assert.Equal("Aucun changement appliqué.", o.Summary);
    }
}

public class DisplayReportTests
{
    [Fact]
    public void Empty_IsHonestlyEmpty()
    {
        var r = new DisplayReport(new List<MonitorState>());
        Assert.False(r.Any);
        Assert.Equal(0, r.Count);
        Assert.False(r.AllAtMax);
        Assert.Equal("Aucun écran actif détecté.", r.Headline);
    }

    [Fact]
    public void AllAtMax_WhenEveryReadableMonitorIsConfirmedMax()
    {
        var a = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 144), DisplayFixtures.Panel1440);
        var b = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 144), DisplayFixtures.Panel1440, device: @"\\.\DISPLAY2");
        var r = new DisplayReport(new[] { a, b });

        Assert.Equal(0, r.BelowMaxCount);
        Assert.True(r.AllAtMax);
        Assert.Equal("Tous les écrans tournent à leur fréquence maximale.", r.Headline);
    }

    [Fact]
    public void BelowMax_IsCountedAndWarned()
    {
        var below = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 60), DisplayFixtures.Panel1440);
        var atMax = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 144), DisplayFixtures.Panel1440, device: @"\\.\DISPLAY2");
        var r = new DisplayReport(new[] { below, atMax });

        Assert.Equal(1, r.BelowMaxCount);
        Assert.False(r.AllAtMax);
        Assert.Contains("1 écran(s) sous", r.Headline);
    }

    [Fact]
    public void UnknownOnly_NeverClaimsAllAtMax()
    {
        var unknown = DisplayFixtures.Mon(DisplayFixtures.M(0, 0, 0), DisplayFixtures.Panel1440, currentReadable: false);
        var r = new DisplayReport(new[] { unknown });

        Assert.Equal(0, r.BelowMaxCount);
        Assert.False(r.AllAtMax);
        Assert.Equal("1 écran(s) détecté(s).", r.Headline);
    }

    [Fact]
    public void NoModeList_DoesNotCountAsAtMax()
    {
        var noModes = DisplayFixtures.Mon(DisplayFixtures.M(2560, 1440, 60), new DisplayMode[0]);
        var r = new DisplayReport(new[] { noModes });

        Assert.False(r.AllAtMax);
        Assert.Equal("1 écran(s) détecté(s).", r.Headline);
    }
}

public class DisplayServiceMappingTests
{
    [Theory]
    [InlineData(0, DisplayChangeStatus.Succeeded)]
    [InlineData(1, DisplayChangeStatus.RequiresRestart)]
    [InlineData(-2, DisplayChangeStatus.BadMode)]
    [InlineData(-1, DisplayChangeStatus.Failed)]
    [InlineData(99, DisplayChangeStatus.Failed)]
    public void MapStatus_MapsNativeCode(int code, DisplayChangeStatus expected)
        => Assert.Equal(expected, DisplayService.MapStatus(code));

    [Theory]
    [InlineData(0, DisplayOrientation.Landscape)]
    [InlineData(1, DisplayOrientation.Portrait)]
    [InlineData(2, DisplayOrientation.LandscapeFlipped)]
    [InlineData(3, DisplayOrientation.PortraitFlipped)]
    [InlineData(7, DisplayOrientation.Landscape)]
    public void MapOrientation_MapsDevmodeValue(int dm, DisplayOrientation expected)
        => Assert.Equal(expected, DisplayService.MapOrientation(dm));
}
