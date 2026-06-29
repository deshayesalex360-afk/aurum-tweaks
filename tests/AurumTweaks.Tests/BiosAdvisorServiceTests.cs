using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Regression tests for <see cref="BiosAdvisorService.BuildReport"/> — the per-PC filter
/// that decides which BIOS cards a machine sees. The headline guard is the misclassification
/// bug found in the visual review: a Ryzen ("6-Core Processor") must NEVER be shown
/// Intel-exclusive settings, and vice-versa. We also lock in the dual-CCD X3D gate, the
/// priority ordering, and the handful of states we can actually read back from Windows.
/// </summary>
public class BiosAdvisorServiceTests
{
    // Local copies of the advisor's private family buckets so the "no cross-platform leak"
    // assertions can be expressed structurally (a setting is Intel-only iff every family in
    // its Compatibility list is an Intel family) instead of hard-coding ids.
    private static readonly CpuFamily[] IntelFamilies =
    {
        CpuFamily.IntelCore12, CpuFamily.IntelCore13, CpuFamily.IntelCore14, CpuFamily.IntelCoreUltra
    };

    private static readonly CpuFamily[] AmdFamilies =
    {
        CpuFamily.Ryzen3000, CpuFamily.Ryzen5000, CpuFamily.Ryzen5000X3D,
        CpuFamily.Ryzen7000, CpuFamily.Ryzen7000X3D, CpuFamily.Ryzen9000, CpuFamily.Ryzen9000X3D
    };

    // ---- Fixtures mirroring real machines --------------------------------

    /// <summary>The actual test bench: 7600X with SMT off in BIOS and EXPO off (RAM at JEDEC).</summary>
    private static HardwareInfo Amd7600X() => new()
    {
        CpuName = "AMD Ryzen 5 7600X 6-Core Processor",
        CpuVendor = "AuthenticAMD",
        CpuCores = 6,
        CpuThreads = 6,          // SMT disabled → 6 active threads
        CpuMaxThreads = 12,      // silicon can do 12
        DetectedFamily = CpuFamily.Ryzen7000,
        DetectedBiosVendor = BiosVendor.Asrock,
        RamType = "DDR5",
        RamConfiguredMhz = 4800, // EXPO off → JEDEC
        RamRatedMhz = 6000
    };

    private static HardwareInfo Amd7950X3D() => new()
    {
        CpuName = "AMD Ryzen 9 7950X3D 16-Core Processor",
        CpuVendor = "AuthenticAMD",
        CpuCores = 16,
        CpuThreads = 32,
        CpuMaxThreads = 32,
        DetectedFamily = CpuFamily.Ryzen7000X3D,
        DetectedBiosVendor = BiosVendor.Asus
    };

    private static HardwareInfo Amd7800X3D() => new()
    {
        CpuName = "AMD Ryzen 7 7800X3D 8-Core Processor",
        CpuVendor = "AuthenticAMD",
        CpuCores = 8,
        CpuThreads = 16,
        CpuMaxThreads = 16,
        DetectedFamily = CpuFamily.Ryzen7000X3D,
        DetectedBiosVendor = BiosVendor.Asus
    };

    private static HardwareInfo Intel13700K() => new()
    {
        CpuName = "13th Gen Intel(R) Core(TM) i7-13700K",
        CpuVendor = "GenuineIntel",
        CpuCores = 16,
        CpuThreads = 24,
        CpuMaxThreads = 24,
        DetectedFamily = CpuFamily.IntelCore13,
        DetectedBiosVendor = BiosVendor.Msi,
        RamType = "DDR5"
    };

    private static IReadOnlyList<string> Ids(BiosAdvisorReport r)
        => r.Recommendations.Select(x => x.Setting.Id).ToList();

    // ---- The misclassification guard (both directions) -------------------

    [Fact]
    public void AmdReport_LeaksNoIntelExclusiveSetting()
    {
        var report = new BiosAdvisorService().BuildReport(Amd7600X(), TweakTier.Avance);

        var leaked = report.Recommendations
            .Where(r => r.Setting.Compatibility.Count > 0
                        && r.Setting.Compatibility.All(f => IntelFamilies.Contains(f)))
            .Select(r => r.Setting.Id)
            .ToList();

        Assert.Empty(leaked);
    }

    [Fact]
    public void IntelReport_LeaksNoAmdExclusiveSetting()
    {
        var report = new BiosAdvisorService().BuildReport(Intel13700K(), TweakTier.Avance);

        var leaked = report.Recommendations
            .Where(r => r.Setting.Compatibility.Count > 0
                        && r.Setting.Compatibility.All(f => AmdFamilies.Contains(f)))
            .Select(r => r.Setting.Id)
            .ToList();

        Assert.Empty(leaked);
    }

    [Fact]
    public void AmdReport_HasAmdAndUniversal_NotIntelById()
    {
        var ids = Ids(new BiosAdvisorService().BuildReport(Amd7600X(), TweakTier.Avance));

        // Intel-only cards must be absent (the "Core" heuristic regression).
        Assert.DoesNotContain("xmp-intel", ids);
        Assert.DoesNotContain("intel-default-settings", ids);
        Assert.DoesNotContain("ptt-enabled", ids);
        Assert.DoesNotContain("ecore-htt-intel", ids);
        Assert.DoesNotContain("intel-apo", ids);
        Assert.DoesNotContain("vmd-controller", ids);

        // AMD cards for a Ryzen 7000 part must be present.
        Assert.Contains("expo-xmp", ids);
        Assert.Contains("smt-control", ids);
        Assert.Contains("vsoc-cap", ids);
        Assert.Contains("ftpm-enabled", ids);

        // Universal platform cards must be present regardless of vendor.
        Assert.Contains("secure-boot", ids);
        Assert.Contains("rebar-above4g", ids);
        Assert.Contains("bios-update", ids);
    }

    [Fact]
    public void IntelReport_HasIntelAndUniversal_NotAmdById()
    {
        var ids = Ids(new BiosAdvisorService().BuildReport(Intel13700K(), TweakTier.Avance));

        Assert.DoesNotContain("expo-xmp", ids);
        Assert.DoesNotContain("smt-control", ids);
        Assert.DoesNotContain("vsoc-cap", ids);
        Assert.DoesNotContain("pbo-curve-optimizer", ids);
        Assert.DoesNotContain("x3d-ccd-prefer-cache", ids);

        Assert.Contains("xmp-intel", ids);
        Assert.Contains("intel-default-settings", ids);  // 13th gen is in scope
        Assert.Contains("ptt-enabled", ids);

        Assert.Contains("secure-boot", ids);
        Assert.Contains("bios-update", ids);
    }

    // ---- Dual-CCD X3D gating ---------------------------------------------

    [Fact]
    public void X3dCcdCard_Appears_ForDualCcdX3D()
        => Assert.Contains("x3d-ccd-prefer-cache",
            Ids(new BiosAdvisorService().BuildReport(Amd7950X3D(), TweakTier.Avance)));

    [Fact]
    public void X3dCcdCard_Hidden_ForMonoCcdX3D()
        => Assert.DoesNotContain("x3d-ccd-prefer-cache",
            Ids(new BiosAdvisorService().BuildReport(Amd7800X3D(), TweakTier.Avance)));

    [Fact]
    public void X3dCcdCard_Hidden_ForNonX3D()
        => Assert.DoesNotContain("x3d-ccd-prefer-cache",
            Ids(new BiosAdvisorService().BuildReport(Amd7600X(), TweakTier.Avance)));

    // ---- Ranking ----------------------------------------------------------

    [Fact]
    public void Recommendations_AreOrderedByPriorityDescending()
    {
        var recs = new BiosAdvisorService().BuildReport(Amd7600X(), TweakTier.Avance).Recommendations;

        Assert.NotEmpty(recs);
        for (int i = 1; i < recs.Count; i++)
        {
            Assert.True(recs[i - 1].Priority >= recs[i].Priority,
                $"Priority must not increase: index {i - 1} ({recs[i - 1].Priority}) < index {i} ({recs[i].Priority})");
        }
    }

    [Fact]
    public void Report_CountsAgreeWithRecommendationStates()
    {
        var report = new BiosAdvisorService().BuildReport(Amd7600X(), TweakTier.Avance);

        Assert.Equal(report.Recommendations.Count(r => r.State == BiosCheckState.ActionNeeded), report.ActionNeededCount);
        Assert.Equal(report.Recommendations.Count(r => r.State == BiosCheckState.Verify), report.VerifyCount);
        Assert.Equal(report.Recommendations.Count(r => r.State == BiosCheckState.Optimal), report.OptimalCount);
    }

    // ---- Read-back state detection ---------------------------------------

    [Fact]
    public void SmtControl_IsVerify_AndReportsDisabled_WhenSmtOff()
    {
        var rec = new BiosAdvisorService().BuildReport(Amd7600X(), TweakTier.Avance)
            .Recommendations.Single(r => r.Setting.Id == "smt-control");

        Assert.Equal(BiosCheckState.Verify, rec.State);
        Assert.Contains("DÉSACTIVÉ", rec.DetectedStateText);
        Assert.Contains("12", rec.DetectedStateText);   // surfaces the silicon max
    }

    [Fact]
    public void SmtControl_IsOptimal_WhenSmtOn()
    {
        var hw = Amd7600X();
        hw.CpuThreads = 12;     // SMT on → threads == 2× cores
        hw.CpuMaxThreads = 12;

        var rec = new BiosAdvisorService().BuildReport(hw, TweakTier.Avance)
            .Recommendations.Single(r => r.Setting.Id == "smt-control");

        Assert.Equal(BiosCheckState.Optimal, rec.State);
    }

    [Fact]
    public void ExpoXmp_IsActionNeeded_WhenRamBelowRated()
    {
        var rec = new BiosAdvisorService().BuildReport(Amd7600X(), TweakTier.Avance)
            .Recommendations.Single(r => r.Setting.Id == "expo-xmp");

        Assert.Equal(BiosCheckState.ActionNeeded, rec.State);
        Assert.Contains("DÉSACTIVÉ", rec.DetectedStateText);
    }

    [Fact]
    public void ExpoXmp_IsOptimal_WhenRamAtRated()
    {
        var hw = Amd7600X();
        hw.RamConfiguredMhz = 6000;   // EXPO on
        hw.RamRatedMhz = 6000;

        var rec = new BiosAdvisorService().BuildReport(hw, TweakTier.Avance)
            .Recommendations.Single(r => r.Setting.Id == "expo-xmp");

        Assert.Equal(BiosCheckState.Optimal, rec.State);
    }

    // ---- Vendor path + summary -------------------------------------------

    [Fact]
    public void VendorPath_UsesDetectedBoardVendor()
    {
        var rec = new BiosAdvisorService().BuildReport(Amd7600X(), TweakTier.Avance)
            .Recommendations.Single(r => r.Setting.Id == "expo-xmp");

        // Asrock board → the Asrock menu path, not the ASUS fallback.
        Assert.Equal("OC Tweaker > DRAM Configuration > Load XMP Setting", rec.VendorPath);
    }

    [Fact]
    public void PlatformSummary_IncludesCpuName()
    {
        var report = new BiosAdvisorService().BuildReport(Amd7600X(), TweakTier.Avance);
        Assert.Contains("7600X", report.PlatformSummary);
    }
}
