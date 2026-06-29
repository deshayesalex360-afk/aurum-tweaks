using System;
using System.Collections.Generic;
using AurumTweaks.Models;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pure unit tests for the computed flags on <see cref="HardwareInfo"/>.
/// These flags gate which BIOS settings and tweaks are shown, so a wrong flag
/// leaks platform-specific advice onto the wrong machine — exactly the class of
/// bug found during the visual review (Ryzen misclassified as Intel via "6-Core").
/// </summary>
public class HardwareInfoTests
{
    // ---- IsAmd / IsIntel: the misclassification guard --------------------

    [Fact]
    public void IsIntel_IsFalse_ForRyzen_WhoseNameContainsCore()
    {
        // "6-Core Processor" must NOT trip the Intel "Core" heuristic.
        var hw = new HardwareInfo
        {
            CpuName = "AMD Ryzen 5 7600X 6-Core Processor",
            CpuVendor = "AuthenticAMD"
        };

        Assert.True(hw.IsAmd);
        Assert.False(hw.IsIntel);
    }

    [Theory]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", "AuthenticAMD")]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", "AuthenticAMD")]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", "")]
    public void IsAmd_IsTrue_ForRyzenParts(string name, string vendor)
    {
        var hw = new HardwareInfo { CpuName = name, CpuVendor = vendor };
        Assert.True(hw.IsAmd);
        Assert.False(hw.IsIntel);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-13700K", "GenuineIntel")]
    [InlineData("Intel(R) Core(TM) i9-14900K", "GenuineIntel")]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12600K", "")]
    public void IsIntel_IsTrue_ForIntelParts(string name, string vendor)
    {
        var hw = new HardwareInfo { CpuName = name, CpuVendor = vendor };
        Assert.True(hw.IsIntel);
        Assert.False(hw.IsAmd);
    }

    // ---- X3D / dual-CCD gating -------------------------------------------

    [Theory]
    [InlineData(CpuFamily.Ryzen7000X3D)]
    [InlineData(CpuFamily.Ryzen9000X3D)]
    [InlineData(CpuFamily.Ryzen5000X3D)]
    public void IsX3D_IsTrue_ForX3DFamilies(CpuFamily family)
        => Assert.True(new HardwareInfo { DetectedFamily = family }.IsX3D);

    [Theory]
    [InlineData(CpuFamily.Ryzen7000)]
    [InlineData(CpuFamily.IntelCore13)]
    [InlineData(CpuFamily.Unknown)]
    public void IsX3D_IsFalse_ForNonX3DFamilies(CpuFamily family)
        => Assert.False(new HardwareInfo { DetectedFamily = family }.IsX3D);

    [Fact]
    public void IsDualCcdX3D_IsTrue_For16Core7950X3D()
    {
        var hw = new HardwareInfo { DetectedFamily = CpuFamily.Ryzen7000X3D, CpuCores = 16 };
        Assert.True(hw.IsDualCcdX3D);
    }

    [Fact]
    public void IsDualCcdX3D_IsFalse_For8Core7800X3D_MonoCcd()
    {
        var hw = new HardwareInfo { DetectedFamily = CpuFamily.Ryzen7000X3D, CpuCores = 8 };
        Assert.False(hw.IsDualCcdX3D);
    }

    [Fact]
    public void IsDualCcdX3D_IsFalse_ForNonX3D_EvenWithManyCores()
    {
        var hw = new HardwareInfo { DetectedFamily = CpuFamily.Ryzen7000, CpuCores = 16 };
        Assert.False(hw.IsDualCcdX3D);
    }

    // ---- SMT-off detection -----------------------------------------------

    [Fact]
    public void SmtCapableButOff_IsTrue_WhenActiveThreadsBelowSiliconMax()
    {
        // 6 cores, 6 active threads, silicon can do 12 → SMT disabled in BIOS.
        var hw = new HardwareInfo { CpuCores = 6, CpuThreads = 6, CpuMaxThreads = 12 };
        Assert.True(hw.SmtCapableButOff);
    }

    [Fact]
    public void SmtCapableButOff_IsFalse_WhenSmtOn()
    {
        var hw = new HardwareInfo { CpuCores = 6, CpuThreads = 12, CpuMaxThreads = 12 };
        Assert.False(hw.SmtCapableButOff);
    }

    [Fact]
    public void SmtCapableButOff_IsFalse_WhenMaxThreadsUnavailable()
    {
        // Some platforms don't expose ThreadCount; max == active must not false-positive.
        var hw = new HardwareInfo { CpuCores = 8, CpuThreads = 16, CpuMaxThreads = 16 };
        Assert.False(hw.SmtCapableButOff);
    }

    // ---- RAM speed / EXPO heuristic --------------------------------------

    [Fact]
    public void RamRunningBelowRated_IsTrue_WhenConfiguredWellBelowRated()
    {
        var hw = new HardwareInfo { RamConfiguredMhz = 4800, RamRatedMhz = 6000 };
        Assert.True(hw.RamRunningBelowRated);
    }

    [Fact]
    public void RamRunningBelowRated_IsFalse_WhenAtRatedSpeed()
    {
        var hw = new HardwareInfo { RamConfiguredMhz = 6000, RamRatedMhz = 6000 };
        Assert.False(hw.RamRunningBelowRated);
    }

    [Fact]
    public void RamRunningBelowRated_IsFalse_WhenRatedUnknown()
    {
        var hw = new HardwareInfo { RamConfiguredMhz = 4800, RamRatedMhz = 0 };
        Assert.False(hw.RamRunningBelowRated);
    }

    [Theory]
    [InlineData(5899, 6000, true)]   // 101 MHz under rated → a real EXPO-off gap, flag it
    [InlineData(5900, 6000, false)]  // exactly 100 MHz under → on the margin, no alarm
    [InlineData(5950, 6000, false)]  // 50 MHz under (normal DDR5 reporting jitter) → must NOT nag
    public void RamRunningBelowRated_HasA100MhzMargin_SoItDoesNotFalseNagAboutTrivialDeltas(
        int configured, int rated, bool expected)
        // The '- 100' margin is the honesty term: it stops us telling a user their RAM is "below rated"
        // (i.e. "enable EXPO/XMP") when it's effectively at speed and only off by reporting rounding.
        => Assert.Equal(expected,
            new HardwareInfo { RamConfiguredMhz = configured, RamRatedMhz = rated }.RamRunningBelowRated);

    [Theory]
    [InlineData("DDR5", true)]
    [InlineData("DDR4", false)]
    [InlineData("", false)]
    public void IsDdr5_MatchesRamType(string ramType, bool expected)
        => Assert.Equal(expected, new HardwareInfo { RamType = ramType }.IsDdr5);

    // ---- BIOS age --------------------------------------------------------

    [Fact]
    public void BiosAgeMonths_IsMinusOne_WhenReleaseDateUnknown()
        => Assert.Equal(-1, new HardwareInfo { BiosReleaseDate = null }.BiosAgeMonths);

    [Fact]
    public void BiosLikelyOutdated_IsTrue_ForTwoYearOldBios()
    {
        var hw = new HardwareInfo { BiosReleaseDate = DateTime.Now.AddMonths(-24) };
        Assert.True(hw.BiosAgeMonths >= 18);
        Assert.True(hw.BiosLikelyOutdated);
    }

    [Fact]
    public void BiosLikelyOutdated_IsFalse_ForRecentBios()
    {
        var hw = new HardwareInfo { BiosReleaseDate = DateTime.Now.AddMonths(-2) };
        Assert.False(hw.BiosLikelyOutdated);
    }

    // ---- Misc computed ---------------------------------------------------

    [Fact]
    public void TotalRamGb_ConvertsBytesToGiB()
    {
        var hw = new HardwareInfo { TotalRamBytes = 32L * 1024 * 1024 * 1024 };
        Assert.Equal(32.0, hw.TotalRamGb, precision: 3);
    }

    [Fact]
    public void PrimaryDisplay_PicksHighestRefreshRate()
    {
        var hw = new HardwareInfo
        {
            Displays = new List<DisplayInfo>
            {
                new() { Name = "DELL", Width = 2560, Height = 1440, CurrentRefreshHz = 60 },
                new() { Name = "AW2725DF", Width = 2560, Height = 1440, CurrentRefreshHz = 360 },
                new() { Name = "TV", Width = 3840, Height = 2160, CurrentRefreshHz = 120 },
            }
        };

        Assert.NotNull(hw.PrimaryDisplay);
        Assert.Equal(360, hw.PrimaryDisplay!.CurrentRefreshHz);
    }

    [Fact]
    public void PrimaryDisplay_IsNull_WhenNoDisplays()
        => Assert.Null(new HardwareInfo().PrimaryDisplay);
}
