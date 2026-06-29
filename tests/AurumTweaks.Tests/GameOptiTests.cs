using System;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

public class GameOptiCatalogTests
{
    [Fact]
    public void Tweaks_NotEmpty() => Assert.NotEmpty(GameTweakCatalog.Tweaks);

    [Fact]
    public void Ids_AreUnique()
        => Assert.Equal(GameTweakCatalog.Tweaks.Count,
                        GameTweakCatalog.Tweaks.Select(t => t.Id).Distinct().Count());

    [Fact]
    public void ValueNames_AreNonEmptyAndSpaceFree()
        => Assert.All(GameTweakCatalog.Tweaks, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.ValueName));
            Assert.DoesNotContain(' ', t.ValueName);
        });

    /// <summary>
    /// The load-bearing safety guard: this page may ONLY touch the four documented gaming/responsiveness keys —
    /// the MMCSS SystemProfile (HKLM), the GameDVR policy (HKLM), and the two per-user Game DVR keys (HKCU). A
    /// future catalog edit that strays to an arbitrary location fails the build instead of silently shipping a
    /// registry footgun. Every value is a DWord (these tweaks are all numeric flags).
    /// </summary>
    [Fact]
    public void AllTweaks_TargetKnownGamingKeys()
    {
        var allowed = new[]
        {
            ("HKLM", GameTweakCatalog.SystemProfile),
            ("HKLM", GameTweakCatalog.GameDvrPolicy),
            ("HKCU", GameTweakCatalog.GameConfigStore),
            ("HKCU", GameTweakCatalog.GameDvrUser),
        };
        Assert.All(GameTweakCatalog.Tweaks, t =>
        {
            Assert.Contains((t.Hive, t.Key), allowed);
            Assert.Equal(RegistryValueType.DWord, t.Kind);
        });
    }

    [Fact]
    public void EveryTweak_HasDistinctOptimizedAndDefaultValues()
        => Assert.All(GameTweakCatalog.Tweaks, t => Assert.NotEqual(t.OptimizedValue, t.DefaultValue));

    [Fact]
    public void EveryTweak_HasLabelAndAdvice()
        => Assert.All(GameTweakCatalog.Tweaks, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Label));
            Assert.False(string.IsNullOrWhiteSpace(t.Advice));
        });

    [Fact]
    public void EveryCategory_HasNonEmptyLabel()
        => Assert.All(Enum.GetValues<GameTweakCategory>(),
                      c => Assert.False(string.IsNullOrWhiteSpace(GameTweakCatalog.CategoryLabel(c))));

    [Fact]
    public void Find_IsCaseInsensitive_AndMatchesById()
    {
        var lower = GameTweakCatalog.Find("system-responsiveness");
        var upper = GameTweakCatalog.Find("SYSTEM-RESPONSIVENESS");
        Assert.NotNull(lower);
        Assert.Same(lower, upper);
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData(null)]
    [InlineData("")]
    public void Find_UnknownOrNull_ReturnsNull(string? id) => Assert.Null(GameTweakCatalog.Find(id));

    /// <summary>
    /// Honesty/correctness pin: the network-throttle tweak DISABLES throttling by writing the max DWord
    /// (0xFFFFFFFF), not by deleting the value — and that optimized value must compare numerically equal to the
    /// signed form (-1) and the unsigned-decimal form Windows can report. Default is the documented 10.
    /// </summary>
    [Fact]
    public void NetworkThrottling_DisablesViaMaxDword()
    {
        var t = GameTweakCatalog.Find("network-throttling");
        Assert.NotNull(t);
        Assert.Equal(GameTweakCategory.Network, t!.Category);
        Assert.Equal("10", t.DefaultValue);
        Assert.True(RegistryValue.TryParseDword(t.OptimizedValue, out var v));
        Assert.Equal(unchecked((int)0xFFFFFFFF), v);   // == -1
        Assert.True(RegistryValue.Matches(t.OptimizedValue, "-1", RegistryValueType.DWord));
        Assert.True(RegistryValue.Matches(t.OptimizedValue, "4294967295", RegistryValueType.DWord));
    }
}

public class GameTweakStateTests
{
    private static readonly GameTweak Dword =
        new("x", "L", "A", GameTweakCategory.Network, "HKCU", "K", "V", RegistryValueType.DWord, "0", "1");
    private static readonly GameTweak WithNote =
        new("xn", "L", "A", GameTweakCategory.Network, "HKCU", "K", "V", RegistryValueType.DWord, "0", "1", "caveat note");
    private static readonly GameTweak Throttle =
        new("nt", "L", "A", GameTweakCategory.Network, "HKLM", "K", "V", RegistryValueType.DWord, "0xFFFFFFFF", "10");

    [Fact]
    public void Absent_ReadsAsDefault_NotFabricatedOptimized()
    {
        var s = new GameTweakState(Dword, null, false);
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
        var s = new GameTweakState(Dword, "0", true);
        Assert.True(s.IsOptimized);
        Assert.False(s.IsDefault);
        Assert.False(s.CanOptimize);
        Assert.True(s.CanRestore);
        Assert.True(s.ShowOptimizedBadge);
        Assert.Equal("Optimisé", s.StateDisplay);
    }

    [Fact]
    public void PresentDefault_CanOnlyOptimize()
    {
        var s = new GameTweakState(Dword, "1", true);
        Assert.True(s.IsDefault);
        Assert.False(s.IsOptimized);
        Assert.True(s.CanOptimize);
        Assert.False(s.CanRestore);
        Assert.Equal("Défaut Windows", s.StateDisplay);
    }

    [Fact]
    public void PresentCustomValue_IsNeither_BothActionsOffered()
    {
        var s = new GameTweakState(Dword, "5", true);
        Assert.True(s.IsCustomValue);
        Assert.False(s.IsOptimized);
        Assert.False(s.IsDefault);
        Assert.True(s.CanOptimize);
        Assert.True(s.CanRestore);
        Assert.Contains("5", s.StateDisplay);
    }

    [Theory]
    [InlineData("0x0", true, false)]   // hex zero matches optimized "0" numerically
    [InlineData("0x1", false, true)]   // hex one matches default "1" numerically
    public void Dword_ComparesNumerically(string live, bool optimized, bool isDefault)
    {
        var s = new GameTweakState(Dword, live, true);
        Assert.Equal(optimized, s.IsOptimized);
        Assert.Equal(isDefault, s.IsDefault);
    }

    /// <summary>
    /// The unsigned-DWord honesty case: when the optimized value is 0xFFFFFFFF, every equivalent form Windows can
    /// store/return (-1, hex, unsigned decimal) must read back as « Optimisé », and the documented default 10 as
    /// « Défaut » — so the live state can never lie about whether the throttle is off.
    /// </summary>
    [Theory]
    [InlineData("-1", true, false)]
    [InlineData("0xffffffff", true, false)]
    [InlineData("4294967295", true, false)]
    [InlineData("10", false, true)]
    public void MaxDword_OptimizedValue_MatchesSignedAndUnsignedForms(string live, bool optimized, bool isDefault)
    {
        var s = new GameTweakState(Throttle, live, true);
        Assert.Equal(optimized, s.IsOptimized);
        Assert.Equal(isDefault, s.IsDefault);
    }

    [Fact]
    public void HasNote_FollowsTweak()
    {
        Assert.False(new GameTweakState(Dword, null, false).HasNote);
        var n = new GameTweakState(WithNote, null, false);
        Assert.True(n.HasNote);
        Assert.Equal("caveat note", n.Note);
    }

    [Fact]
    public void CategoryLabel_SurfacesCatalogLabel()
        => Assert.Equal(GameTweakCatalog.CategoryLabel(GameTweakCategory.Network),
                        new GameTweakState(Dword, null, false).CategoryLabel);

    /// <summary>No row may be fully dead: every state offers at least one of Optimiser/Rétablir.</summary>
    [Theory]
    [InlineData("0", true)]    // optimized → restore
    [InlineData("1", true)]    // default   → optimize
    [InlineData(null, false)]  // absent    → optimize
    [InlineData("5", true)]    // custom    → both
    public void EveryState_OffersAtLeastOneAction(string? live, bool present)
    {
        var s = new GameTweakState(Dword, live, present);
        Assert.True(s.CanOptimize || s.CanRestore);
    }
}

public class GameTweakPlanTests
{
    [Fact]
    public void OptimizeAll_CoversEveryTweakOnce_WithOptimizedValue()
    {
        var plan = GameTweakPlan.OptimizeAll(GameTweakCatalog.Tweaks);
        Assert.Equal(GameTweakCatalog.Tweaks.Count, plan.Count);
        Assert.All(plan, w => Assert.Equal(w.Tweak.OptimizedValue, w.Value));
    }

    [Fact]
    public void RestoreAll_CoversEveryTweakOnce_WithDefaultValue()
    {
        var plan = GameTweakPlan.RestoreAll(GameTweakCatalog.Tweaks);
        Assert.Equal(GameTweakCatalog.Tweaks.Count, plan.Count);
        Assert.All(plan, w => Assert.Equal(w.Tweak.DefaultValue, w.Value));
    }
}

public class GameTweakReportTests
{
    private static GameTweakState St(string? live, bool present) =>
        new(new("x", "L", "A", GameTweakCategory.Network, "HKCU", "K", "V", RegistryValueType.DWord, "0", "1"),
            live, present);

    [Fact]
    public void Counts_TallyEachBucket_AbsentCountsAsDefault()
    {
        var r = new GameTweakReport(new[] { St("0", true), St("1", true), St("5", true), St(null, false) });
        Assert.Equal(4, r.Total);
        Assert.Equal(1, r.OptimizedCount);
        Assert.Equal(2, r.DefaultCount);   // present-default + absent
        Assert.Equal(1, r.CustomCount);
    }

    [Fact]
    public void AllOptimized_TrueOnlyWhenEveryoneOptimized()
    {
        Assert.True(new GameTweakReport(new[] { St("0", true), St("0", true) }).AllOptimized);
        Assert.False(new GameTweakReport(new[] { St("0", true), St("1", true) }).AllOptimized);
        Assert.False(new GameTweakReport(Array.Empty<GameTweakState>()).AllOptimized);   // count>0 guard
    }

    [Fact]
    public void NoneOptimized_TrueWhenNobodyOptimized()
    {
        Assert.True(new GameTweakReport(new[] { St("1", true), St(null, false) }).NoneOptimized);
        Assert.False(new GameTweakReport(new[] { St("0", true) }).NoneOptimized);
    }
}

/// <summary>
/// Pins <see cref="DisplayGpuState"/> — the read-only HAGS/MPO interpretation. Honesty contract: a present value is graded
/// by its well-known DWord semantics (numeric, so "0x2" == "2"), an absent value is « non configuré / défaut » (never a
/// fabricated on/off), and an unrecognised HAGS value is « valeur inhabituelle » rather than silently called off.
/// </summary>
public class DisplayGpuStateTests
{
    [Theory]
    [InlineData("2", GpuToggleState.Enabled)]
    [InlineData("0x2", GpuToggleState.Enabled)]   // numeric comparison, not string
    [InlineData("1", GpuToggleState.Disabled)]
    [InlineData("0x1", GpuToggleState.Disabled)]
    public void Hags_PresentKnownValue_IsGraded(string raw, GpuToggleState expected)
        => Assert.Equal(expected, new DisplayGpuState(true, raw, false, null).Hags);

    [Fact]
    public void Hags_Enabled_ReadsActivé()
        => Assert.Equal("Activé", new DisplayGpuState(true, "2", false, null).HagsDisplay);

    [Fact]
    public void Hags_Disabled_ReadsDésactivé()
        => Assert.Equal("Désactivé", new DisplayGpuState(true, "1", false, null).HagsDisplay);

    [Fact]
    public void Hags_Absent_IsUnknown_AndReadsNonConfiguré_NotOff()
    {
        var s = new DisplayGpuState(false, null, false, null);
        Assert.Equal(GpuToggleState.Unknown, s.Hags);
        Assert.Contains("Non configuré", s.HagsDisplay);   // honest default, never a fabricated « Désactivé »
        Assert.DoesNotContain("Désactivé", s.HagsDisplay);
    }

    [Fact]
    public void Hags_PresentUnusualValue_IsUnknown_AndShowsTheRawValue()
    {
        var s = new DisplayGpuState(true, "3", false, null);
        Assert.Equal(GpuToggleState.Unknown, s.Hags);
        Assert.Contains("Valeur inhabituelle (3)", s.HagsDisplay);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("0x5")]   // numeric comparison
    public void Mpo_FiveMeansDisabled(string raw)
    {
        var s = new DisplayGpuState(false, null, true, raw);
        Assert.True(s.MpoDisabled);
        Assert.Contains("Désactivé", s.MpoDisplay);
    }

    [Theory]
    [InlineData(false, null)]   // absent → Windows default (on)
    [InlineData(true, "0")]     // any non-5 value → still effectively on
    public void Mpo_AbsentOrOther_ReadsAsWindowsDefaultOn(bool present, string? raw)
    {
        var s = new DisplayGpuState(false, null, present, raw);
        Assert.False(s.MpoDisabled);
        Assert.Equal("Activé — défaut Windows", s.MpoDisplay);
    }
}

public class GameOptiServiceTests
{
    private static (GameOptiService svc, FakeRegistryService reg) New()
    {
        var reg = new FakeRegistryService(new EventLog());
        return (new GameOptiService(reg), reg);
    }

    private static string PathOf(GameTweak t) => $"{t.Hive}\\{t.Key}\\{t.ValueName}";

    [Fact]
    public async Task GetReport_ReadsSeededOptimized_AndAbsentDefault()
    {
        var (svc, reg) = New();
        var net = GameTweakCatalog.Find("network-throttling")!;
        reg.Seed(net.Hive, net.Key, net.ValueName, "-1");   // how Windows stores 0xFFFFFFFF — proves the round-trip

        var r = await svc.GetReportAsync();

        var netState = r.Tweaks.First(t => t.Id == "network-throttling");
        Assert.True(netState.IsPresent);
        Assert.True(netState.IsOptimized);   // "-1" must read back as optimised, not a custom value
        Assert.All(r.Tweaks.Where(t => t.Id != "network-throttling"), t => Assert.False(t.IsPresent));
        Assert.All(r.Tweaks.Where(t => t.Id != "network-throttling"), t => Assert.True(t.IsDefault));
    }

    /// <summary>
    /// The read-only HAGS/MPO extras must reflect the real registry: with both keys seeded to their well-known
    /// values the report grades HAGS « Activé » and MPO « Désactivé », proving the service actually reads
    /// <c>HwSchMode</c>/<c>OverlayTestMode</c> from HKLM rather than fabricating a state.
    /// </summary>
    [Fact]
    public async Task GetReport_ReadsSeededHagsAndMpo_FromRegistry()
    {
        var (svc, reg) = New();
        reg.Seed("HKLM", DisplayGpuState.HagsKey, DisplayGpuState.HagsValueName, "2");
        reg.Seed("HKLM", DisplayGpuState.MpoKey, DisplayGpuState.MpoValueName, "5");

        var r = await svc.GetReportAsync();

        Assert.NotNull(r.DisplayGpu);
        Assert.Equal(GpuToggleState.Enabled, r.DisplayGpu!.Hags);
        Assert.True(r.DisplayGpu.MpoDisabled);
    }

    /// <summary>
    /// When neither key exists the report still carries a non-null <see cref="DisplayGpuState"/>, but it reads as the
    /// honest default (« non configuré ») — HAGS Unknown and MPO not disabled — never a fabricated on/off.
    /// </summary>
    [Fact]
    public async Task GetReport_AbsentHagsAndMpo_AreUnknownAndDefault_NeverFabricated()
    {
        var (svc, _) = New();

        var r = await svc.GetReportAsync();

        Assert.NotNull(r.DisplayGpu);
        Assert.Equal(GpuToggleState.Unknown, r.DisplayGpu!.Hags);
        Assert.False(r.DisplayGpu.MpoDisabled);
    }

    [Fact]
    public async Task SetOptimized_True_WritesOptimizedValue()
    {
        var (svc, reg) = New();
        var net = GameTweakCatalog.Find("network-throttling")!;

        var ok = await svc.SetOptimizedAsync("network-throttling", optimize: true);

        Assert.True(ok);
        Assert.Equal("0xFFFFFFFF", reg.Store[PathOf(net)]);
    }

    [Fact]
    public async Task SetOptimized_False_WritesDefaultValue()
    {
        var (svc, reg) = New();
        var resp = GameTweakCatalog.Find("system-responsiveness")!;

        var ok = await svc.SetOptimizedAsync("system-responsiveness", optimize: false);

        Assert.True(ok);
        Assert.Equal("20", reg.Store[PathOf(resp)]);   // SystemResponsiveness default = 20
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
    public async Task ApplyAll_Optimize_WritesEveryTweakOptimized()
    {
        var (svc, reg) = New();

        var ok = await svc.ApplyAllAsync(optimize: true);

        Assert.True(ok);
        foreach (var t in GameTweakCatalog.Tweaks)
            Assert.Equal(t.OptimizedValue, reg.Store[PathOf(t)]);
    }

    [Fact]
    public async Task ApplyAll_Restore_WritesEveryTweakDefault()
    {
        var (svc, reg) = New();

        var ok = await svc.ApplyAllAsync(optimize: false);

        Assert.True(ok);
        foreach (var t in GameTweakCatalog.Tweaks)
            Assert.Equal(t.DefaultValue, reg.Store[PathOf(t)]);
    }
}
