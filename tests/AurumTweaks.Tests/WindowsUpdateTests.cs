using System;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

public class WindowsUpdateCatalogTests
{
    [Fact]
    public void Tweaks_NotEmpty() => Assert.NotEmpty(WindowsUpdateCatalog.Tweaks);

    [Fact]
    public void Ids_AreUnique()
        => Assert.Equal(WindowsUpdateCatalog.Tweaks.Count,
                        WindowsUpdateCatalog.Tweaks.Select(t => t.Id).Distinct().Count());

    [Fact]
    public void ValueNames_AreNonEmptyAndSpaceFree()
        => Assert.All(WindowsUpdateCatalog.Tweaks, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.ValueName));
            Assert.DoesNotContain(' ', t.ValueName);
        });

    /// <summary>
    /// The load-bearing safety guard: this page may ONLY touch the four documented Windows Update keys — the Delivery
    /// Optimization policy, the WindowsUpdate policy + its AU subkey, and the legacy DriverSearching key. All are
    /// machine-wide (HKLM) DWords. A future catalog edit that strays to an arbitrary location (or a per-user hive)
    /// fails the build instead of silently shipping a policy footgun.
    /// </summary>
    [Fact]
    public void AllTweaks_TargetKnownWindowsUpdateKeys()
    {
        var allowed = new[]
        {
            ("HKLM", WindowsUpdateCatalog.DeliveryOptimization),
            ("HKLM", WindowsUpdateCatalog.WindowsUpdate),
            ("HKLM", WindowsUpdateCatalog.WindowsUpdateAu),
            ("HKLM", WindowsUpdateCatalog.DriverSearching),
        };
        Assert.All(WindowsUpdateCatalog.Tweaks, t =>
        {
            Assert.Equal("HKLM", t.Hive);
            Assert.Contains((t.Hive, t.Key), allowed);
            Assert.Equal(RegistryValueType.DWord, t.Kind);
        });
    }

    [Fact]
    public void EveryTweak_HasDistinctOptimizedAndDefaultValues()
        => Assert.All(WindowsUpdateCatalog.Tweaks, t => Assert.NotEqual(t.OptimizedValue, t.DefaultValue));

    [Fact]
    public void EveryTweak_HasLabelAndAdvice()
        => Assert.All(WindowsUpdateCatalog.Tweaks, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Label));
            Assert.False(string.IsNullOrWhiteSpace(t.Advice));
        });

    [Fact]
    public void EveryTweak_HasNote()
        => Assert.All(WindowsUpdateCatalog.Tweaks, t => Assert.False(string.IsNullOrWhiteSpace(t.Note)));

    [Fact]
    public void EveryCategory_HasNonEmptyLabel()
        => Assert.All(Enum.GetValues<WindowsUpdateCategory>(),
                      c => Assert.False(string.IsNullOrWhiteSpace(WindowsUpdateCatalog.CategoryLabel(c))));

    [Fact]
    public void Find_IsCaseInsensitive_AndMatchesById()
    {
        var lower = WindowsUpdateCatalog.Find("no-auto-reboot");
        var upper = WindowsUpdateCatalog.Find("NO-AUTO-REBOOT");
        Assert.NotNull(lower);
        Assert.Same(lower, upper);
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData(null)]
    [InlineData("")]
    public void Find_UnknownOrNull_ReturnsNull(string? id) => Assert.Null(WindowsUpdateCatalog.Find(id));

    /// <summary>
    /// Honesty/correctness pin: stopping the P2P upload writes DODownloadMode = 0 (HTTP only), and the documented
    /// default it restores is 1 (LAN peering, Windows' modern consumer default). These must differ and sit in the
    /// Bandwidth bucket, so the page can never lie about whether your upload is being used to seed strangers.
    /// </summary>
    [Fact]
    public void DeliveryOptimization_StopsP2pVia0()
    {
        var t = WindowsUpdateCatalog.Find("delivery-optimization-p2p");
        Assert.NotNull(t);
        Assert.Equal(WindowsUpdateCategory.Bandwidth, t!.Category);
        Assert.Equal("DODownloadMode", t.ValueName);
        Assert.Equal("0", t.OptimizedValue);
        Assert.Equal("1", t.DefaultValue);
    }

    [Fact]
    public void NoAutoReboot_OptimizedIs1_DefaultIs0()
    {
        var t = WindowsUpdateCatalog.Find("no-auto-reboot");
        Assert.NotNull(t);
        Assert.Equal(WindowsUpdateCategory.Reboot, t!.Category);
        Assert.Equal("NoAutoRebootWithLoggedOnUsers", t.ValueName);
        Assert.Equal("1", t.OptimizedValue);
        Assert.Equal("0", t.DefaultValue);
    }

    [Fact]
    public void ExcludeWuDrivers_OptimizedIs1_DefaultIs0()
    {
        var t = WindowsUpdateCatalog.Find("exclude-wu-drivers");
        Assert.NotNull(t);
        Assert.Equal(WindowsUpdateCategory.Drivers, t!.Category);
        Assert.Equal("ExcludeWUDriversInQualityUpdate", t.ValueName);
        Assert.Equal("1", t.OptimizedValue);
        Assert.Equal("0", t.DefaultValue);
    }
}

public class WindowsUpdateTweakStateTests
{
    private static readonly WindowsUpdateTweak Dword =
        new("x", "L", "A", WindowsUpdateCategory.Bandwidth, "HKLM", "K", "V", RegistryValueType.DWord, "0", "1");
    private static readonly WindowsUpdateTweak WithNote =
        new("xn", "L", "A", WindowsUpdateCategory.Reboot, "HKLM", "K", "V", RegistryValueType.DWord, "1", "0", "caveat note");

    [Fact]
    public void Absent_ReadsAsDefault_NotFabricatedApplied()
    {
        var s = new WindowsUpdateTweakState(Dword, null, false);
        Assert.True(s.IsDefault);          // absent ⇒ Windows default behaviour
        Assert.False(s.IsOptimized);
        Assert.False(s.IsCustomValue);
        Assert.True(s.CanOptimize);
        Assert.False(s.CanRestore);        // restoring an absent key is a no-op → no dead button
        Assert.False(s.ShowOptimizedBadge);
        Assert.Contains("Non configuré", s.StateDisplay);
    }

    [Fact]
    public void PresentOptimized_CanOnlyRestore_AndBadges()
    {
        var s = new WindowsUpdateTweakState(Dword, "0", true);
        Assert.True(s.IsOptimized);
        Assert.False(s.IsDefault);
        Assert.False(s.CanOptimize);
        Assert.True(s.CanRestore);
        Assert.True(s.ShowOptimizedBadge);
        Assert.Equal("Appliqué", s.StateDisplay);
    }

    [Fact]
    public void PresentDefault_CanOnlyOptimize()
    {
        var s = new WindowsUpdateTweakState(Dword, "1", true);
        Assert.True(s.IsDefault);
        Assert.False(s.IsOptimized);
        Assert.True(s.CanOptimize);
        Assert.False(s.CanRestore);
        Assert.Equal("Défaut Windows", s.StateDisplay);
    }

    [Fact]
    public void PresentCustomValue_IsNeither_BothActionsOffered()
    {
        var s = new WindowsUpdateTweakState(Dword, "3", true);
        Assert.True(s.IsCustomValue);
        Assert.False(s.IsOptimized);
        Assert.False(s.IsDefault);
        Assert.True(s.CanOptimize);
        Assert.True(s.CanRestore);
        Assert.Contains("3", s.StateDisplay);
    }

    [Theory]
    [InlineData("0x0", true, false)]   // hex zero matches optimized "0" numerically
    [InlineData("0x1", false, true)]   // hex one matches default "1" numerically
    public void Dword_ComparesNumerically(string live, bool optimized, bool isDefault)
    {
        var s = new WindowsUpdateTweakState(Dword, live, true);
        Assert.Equal(optimized, s.IsOptimized);
        Assert.Equal(isDefault, s.IsDefault);
    }

    [Fact]
    public void HasNote_FollowsTweak()
    {
        Assert.False(new WindowsUpdateTweakState(Dword, null, false).HasNote);
        var n = new WindowsUpdateTweakState(WithNote, null, false);
        Assert.True(n.HasNote);
        Assert.Equal("caveat note", n.Note);
    }

    [Fact]
    public void CategoryLabel_SurfacesCatalogLabel()
        => Assert.Equal(WindowsUpdateCatalog.CategoryLabel(WindowsUpdateCategory.Bandwidth),
                        new WindowsUpdateTweakState(Dword, null, false).CategoryLabel);

    /// <summary>No row may be fully dead: every state offers at least one of Optimiser/Rétablir.</summary>
    [Theory]
    [InlineData("0", true)]    // optimized → restore
    [InlineData("1", true)]    // default   → optimize
    [InlineData(null, false)]  // absent    → optimize
    [InlineData("3", true)]    // custom    → both
    public void EveryState_OffersAtLeastOneAction(string? live, bool present)
    {
        var s = new WindowsUpdateTweakState(Dword, live, present);
        Assert.True(s.CanOptimize || s.CanRestore);
    }
}

public class WindowsUpdatePlanTests
{
    [Fact]
    public void OptimizeAll_CoversEveryTweakOnce_WithOptimizedValue()
    {
        var plan = WindowsUpdatePlan.OptimizeAll(WindowsUpdateCatalog.Tweaks);
        Assert.Equal(WindowsUpdateCatalog.Tweaks.Count, plan.Count);
        Assert.All(plan, w => Assert.Equal(w.Tweak.OptimizedValue, w.Value));
    }

    [Fact]
    public void RestoreAll_CoversEveryTweakOnce_WithDefaultValue()
    {
        var plan = WindowsUpdatePlan.RestoreAll(WindowsUpdateCatalog.Tweaks);
        Assert.Equal(WindowsUpdateCatalog.Tweaks.Count, plan.Count);
        Assert.All(plan, w => Assert.Equal(w.Tweak.DefaultValue, w.Value));
    }
}

public class WindowsUpdateApplyOutcomeTests
{
    [Fact]
    public void AllAccepted_WhenAcceptedEqualsTotal()
    {
        var o = new WindowsUpdateApplyOutcome(4, 4);
        Assert.True(o.AllAccepted);
        Assert.Equal(0, o.Refused);
        Assert.Contains("4/4", o.Summary);
        Assert.Contains("acceptée", o.Summary);
    }

    /// <summary>The honesty differentiator: a refused HKLM policy write is reported, never folded into a fake success.</summary>
    [Fact]
    public void SomeRefused_SummaryNamesRefusedCount()
    {
        var o = new WindowsUpdateApplyOutcome(4, 3);
        Assert.False(o.AllAccepted);
        Assert.Equal(1, o.Refused);
        Assert.Contains("3/4", o.Summary);
        Assert.Contains("refusée", o.Summary);
    }

    [Fact]
    public void ZeroTotal_IsNotAllAccepted_AndStatesNothingToApply()
    {
        var o = new WindowsUpdateApplyOutcome(0, 0);
        Assert.False(o.AllAccepted);   // count>0 guard — empty is not a success
        Assert.Contains("Aucun", o.Summary);
    }
}

public class WindowsUpdateReportTests
{
    private static WindowsUpdateTweakState St(string? live, bool present) =>
        new(new("x", "L", "A", WindowsUpdateCategory.Bandwidth, "HKLM", "K", "V", RegistryValueType.DWord, "0", "1"),
            live, present);

    [Fact]
    public void Counts_TallyEachBucket_AbsentCountsAsDefault()
    {
        var r = new WindowsUpdateReport(new[] { St("0", true), St("1", true), St("3", true), St(null, false) });
        Assert.Equal(4, r.Total);
        Assert.Equal(1, r.OptimizedCount);
        Assert.Equal(2, r.DefaultCount);   // present-default + absent
        Assert.Equal(1, r.CustomCount);
    }

    [Fact]
    public void AllOptimized_TrueOnlyWhenEveryoneOptimized()
    {
        Assert.True(new WindowsUpdateReport(new[] { St("0", true), St("0", true) }).AllOptimized);
        Assert.False(new WindowsUpdateReport(new[] { St("0", true), St("1", true) }).AllOptimized);
        Assert.False(new WindowsUpdateReport(Array.Empty<WindowsUpdateTweakState>()).AllOptimized);   // count>0 guard
    }

    [Fact]
    public void NoneOptimized_TrueWhenNobodyOptimized()
    {
        Assert.True(new WindowsUpdateReport(new[] { St("1", true), St(null, false) }).NoneOptimized);
        Assert.False(new WindowsUpdateReport(new[] { St("0", true) }).NoneOptimized);
    }
}

public class WindowsUpdateServiceTests
{
    private static (WindowsUpdateService svc, FakeRegistryService reg) New()
    {
        var reg = new FakeRegistryService(new EventLog());
        return (new WindowsUpdateService(reg), reg);
    }

    private static string PathOf(WindowsUpdateTweak t) => $"{t.Hive}\\{t.Key}\\{t.ValueName}";

    [Fact]
    public async Task GetReport_ReadsSeededOptimized_AndAbsentDefault()
    {
        var (svc, reg) = New();
        var p2p = WindowsUpdateCatalog.Find("delivery-optimization-p2p")!;
        reg.Seed(p2p.Hive, p2p.Key, p2p.ValueName, "0");   // P2P upload disabled

        var r = await svc.GetReportAsync();

        var p2pState = r.Tweaks.First(t => t.Id == "delivery-optimization-p2p");
        Assert.True(p2pState.IsPresent);
        Assert.True(p2pState.IsOptimized);
        Assert.All(r.Tweaks.Where(t => t.Id != "delivery-optimization-p2p"), t => Assert.False(t.IsPresent));
        Assert.All(r.Tweaks.Where(t => t.Id != "delivery-optimization-p2p"), t => Assert.True(t.IsDefault));
    }

    [Fact]
    public async Task SetOptimized_True_WritesOptimizedValue()
    {
        var (svc, reg) = New();
        var reboot = WindowsUpdateCatalog.Find("no-auto-reboot")!;

        var ok = await svc.SetOptimizedAsync("no-auto-reboot", optimize: true);

        Assert.True(ok);
        Assert.Equal("1", reg.Store[PathOf(reboot)]);   // NoAutoRebootWithLoggedOnUsers = 1
    }

    [Fact]
    public async Task SetOptimized_False_WritesDefaultValue()
    {
        var (svc, reg) = New();
        var p2p = WindowsUpdateCatalog.Find("delivery-optimization-p2p")!;

        var ok = await svc.SetOptimizedAsync("delivery-optimization-p2p", optimize: false);

        Assert.True(ok);
        Assert.Equal("1", reg.Store[PathOf(p2p)]);   // DODownloadMode default = 1 (LAN)
    }

    [Fact]
    public async Task SetOptimized_UnknownId_ReturnsFalse_AndWritesNothing()
    {
        var (svc, reg) = New();
        var ok = await svc.SetOptimizedAsync("does-not-exist", optimize: true);

        Assert.False(ok);
        Assert.Empty(reg.Store);
    }

    [Fact]
    public async Task ApplyAll_Optimize_WritesEveryTweakOptimized_AllAccepted()
    {
        var (svc, reg) = New();

        var outcome = await svc.ApplyAllAsync(optimize: true);

        Assert.True(outcome.AllAccepted);
        Assert.Equal(WindowsUpdateCatalog.Tweaks.Count, outcome.Accepted);
        foreach (var t in WindowsUpdateCatalog.Tweaks)
            Assert.Equal(t.OptimizedValue, reg.Store[PathOf(t)]);
    }

    [Fact]
    public async Task ApplyAll_Restore_WritesEveryTweakDefault()
    {
        var (svc, reg) = New();

        var outcome = await svc.ApplyAllAsync(optimize: false);

        Assert.True(outcome.AllAccepted);
        foreach (var t in WindowsUpdateCatalog.Tweaks)
            Assert.Equal(t.DefaultValue, reg.Store[PathOf(t)]);
    }

    /// <summary>
    /// The load-bearing honesty test: when the system refuses one of the HKLM policy writes (managed device / no
    /// rights), the outcome reports exactly how many writes were accepted — it never claims a blanket success. We
    /// simulate the refusal by failing the DODownloadMode write; the other three still land.
    /// </summary>
    [Fact]
    public async Task ApplyAll_WithRefusedWrite_ReportsAcceptedCountHonestly()
    {
        var (svc, reg) = New();
        reg.FailWritesForName.Add("DODownloadMode");   // the device/policy refuses this one

        var outcome = await svc.ApplyAllAsync(optimize: true);

        Assert.False(outcome.AllAccepted);
        Assert.Equal(WindowsUpdateCatalog.Tweaks.Count - 1, outcome.Accepted);
        Assert.Equal(1, outcome.Refused);
        Assert.Contains("refusée", outcome.Summary);
    }
}
