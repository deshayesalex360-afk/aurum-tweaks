using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

public class VisualFxModeInfoTests
{
    [Theory]
    [InlineData("0", VisualFxMode.LetWindowsDecide)]
    [InlineData("1", VisualFxMode.BestAppearance)]
    [InlineData("2", VisualFxMode.BestPerformance)]
    [InlineData("3", VisualFxMode.Custom)]
    [InlineData("0x2", VisualFxMode.BestPerformance)]   // hex form, like regedit shows it
    [InlineData("0x3", VisualFxMode.Custom)]
    [InlineData(null, VisualFxMode.Unknown)]
    [InlineData("", VisualFxMode.Unknown)]
    [InlineData("   ", VisualFxMode.Unknown)]
    [InlineData("4", VisualFxMode.Unknown)]             // out of the 0–3 range
    [InlineData("-1", VisualFxMode.Unknown)]
    [InlineData("abc", VisualFxMode.Unknown)]
    public void Parse_MapsKnownDwords_RestUnknown(string? raw, VisualFxMode expected)
        => Assert.Equal(expected, VisualFxModeInfo.Parse(raw));

    [Theory]
    [InlineData(VisualFxMode.LetWindowsDecide, "0")]
    [InlineData(VisualFxMode.BestAppearance, "1")]
    [InlineData(VisualFxMode.BestPerformance, "2")]
    [InlineData(VisualFxMode.Custom, "3")]
    public void ToRegistryValue_IsTheModeNumber(VisualFxMode mode, string expected)
        => Assert.Equal(expected, VisualFxModeInfo.ToRegistryValue(mode));

    [Theory]
    [InlineData(VisualFxMode.LetWindowsDecide)]
    [InlineData(VisualFxMode.BestAppearance)]
    [InlineData(VisualFxMode.BestPerformance)]
    [InlineData(VisualFxMode.Custom)]
    public void Label_ConcreteModes_AreNonEmptyAndNotUnknown(VisualFxMode mode)
    {
        var s = VisualFxModeInfo.Label(mode);
        Assert.False(string.IsNullOrWhiteSpace(s));
        Assert.NotEqual("Inconnu", s);
    }

    [Fact]
    public void Label_Unknown_IsInconnu() => Assert.Equal("Inconnu", VisualFxModeInfo.Label(VisualFxMode.Unknown));
}

public class VisualEffectsCatalogTests
{
    [Fact]
    public void Effects_NotEmpty() => Assert.NotEmpty(VisualEffectsCatalog.Effects);

    [Fact]
    public void Ids_AreUnique()
        => Assert.Equal(VisualEffectsCatalog.Effects.Count,
                        VisualEffectsCatalog.Effects.Select(e => e.Id).Distinct().Count());

    [Fact]
    public void ValueNames_AreNonEmptyAndSpaceFree()
        => Assert.All(VisualEffectsCatalog.Effects, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.ValueName));
            Assert.DoesNotContain(' ', e.ValueName);
        });

    /// <summary>
    /// The load-bearing safety guard: this page may ONLY write per-user (HKCU) UI keys. It can never reach into
    /// HKLM or some arbitrary key — so a future catalog edit that strays outside the known cosmetic locations
    /// fails the build instead of silently shipping a registry footgun.
    /// </summary>
    [Fact]
    public void AllEffects_TargetHkcuUiKeysOnly()
    {
        var allowedKeys = new HashSet<string>
        {
            VisualEffectsCatalog.Desktop,
            VisualEffectsCatalog.WindowMetrics,
            VisualEffectsCatalog.ExplorerAdvanced,
            VisualEffectsCatalog.Dwm,
        };
        Assert.All(VisualEffectsCatalog.Effects, e =>
        {
            Assert.Equal("HKCU", e.Hive);
            Assert.Contains(e.Key, allowedKeys);
        });
    }

    [Fact]
    public void EveryEffect_HasDistinctAppearanceAndPerformanceValues()
        => Assert.All(VisualEffectsCatalog.Effects, e => Assert.NotEqual(e.AppearanceValue, e.PerformanceValue));

    [Fact]
    public void EveryEffect_KindIsStringOrDword()
        => Assert.All(VisualEffectsCatalog.Effects,
                      e => Assert.True(e.Kind is RegistryValueType.String or RegistryValueType.DWord));

    [Fact]
    public void EveryEffect_HasLabelAndAdvice()
        => Assert.All(VisualEffectsCatalog.Effects, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Label));
            Assert.False(string.IsNullOrWhiteSpace(e.Advice));
        });

    [Fact]
    public void Find_IsCaseInsensitive_AndMatchesById()
    {
        var lower = VisualEffectsCatalog.Find("drag-full-windows");
        var upper = VisualEffectsCatalog.Find("DRAG-FULL-WINDOWS");
        Assert.NotNull(lower);
        Assert.Same(lower, upper);
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData(null)]
    [InlineData("")]
    public void Find_UnknownOrNull_ReturnsNull(string? id) => Assert.Null(VisualEffectsCatalog.Find(id));

    [Fact]
    public void Catalog_IncludesClearType_FlaggedKeepOn()
    {
        var fs = VisualEffectsCatalog.Find("font-smoothing");
        Assert.NotNull(fs);
        Assert.True(fs!.KeepOn);                 // ClearType is the « à conserver » effect
        Assert.Equal("2", fs.AppearanceValue);   // FontSmoothing=2 means ClearType on
    }
}

public class EffectStateTests
{
    private static readonly VisualEffect Dword =
        new("x-dword", "L", "A", "HKCU", "K", "V", RegistryValueType.DWord, "1", "0", false);
    private static readonly VisualEffect Str =
        new("x-str", "L", "A", "HKCU", "K", "V", RegistryValueType.String, "1", "0", false);
    private static readonly VisualEffect Keep =
        new("x-keep", "L", "A", "HKCU", "K", "V", RegistryValueType.String, "2", "0", true);

    [Fact]
    public void Absent_ReadsAsAppearanceDefault_NotFabricatedOff()
    {
        var s = new EffectState(Dword, null, false);
        Assert.True(s.IsAppearance);     // Windows default = appearance
        Assert.False(s.IsPerformance);
        Assert.False(s.IsCustomValue);
        Assert.False(s.CanEnable);       // already effectively appearance → no dead button
        Assert.True(s.CanDisable);
        Assert.False(s.ShowPerformanceBadge);
        Assert.Contains("défaut", s.StateDisplay);
    }

    [Fact]
    public void PresentAtAppearance_CanOnlyDisable()
    {
        var s = new EffectState(Dword, "1", true);
        Assert.True(s.IsAppearance);
        Assert.False(s.IsPerformance);
        Assert.False(s.CanEnable);
        Assert.True(s.CanDisable);
        Assert.Equal("Activé (apparence)", s.StateDisplay);
    }

    [Fact]
    public void PresentAtPerformance_CanOnlyEnable_AndBadges()
    {
        var s = new EffectState(Dword, "0", true);
        Assert.True(s.IsPerformance);
        Assert.False(s.IsAppearance);
        Assert.True(s.CanEnable);
        Assert.False(s.CanDisable);
        Assert.True(s.ShowPerformanceBadge);
        Assert.Equal("Désactivé (performance)", s.StateDisplay);
    }

    [Fact]
    public void PresentCustomValue_IsNeitherPreset_BothActionsOffered()
    {
        var s = new EffectState(Dword, "9", true);
        Assert.True(s.IsCustomValue);
        Assert.False(s.IsAppearance);
        Assert.False(s.IsPerformance);
        Assert.True(s.CanEnable);
        Assert.True(s.CanDisable);
        Assert.Contains("9", s.StateDisplay);
    }

    [Theory]
    [InlineData("0x0", true, false)]   // hex zero matches performance "0" numerically
    [InlineData("0x1", false, true)]   // hex one matches appearance "1" numerically
    public void Dword_ComparesNumerically(string live, bool perf, bool appearance)
    {
        var s = new EffectState(Dword, live, true);
        Assert.Equal(perf, s.IsPerformance);
        Assert.Equal(appearance, s.IsAppearance);
    }

    [Fact]
    public void String_ComparesExactly()
    {
        Assert.True(new EffectState(Str, "1", true).IsAppearance);
        Assert.True(new EffectState(Str, "0", true).IsPerformance);
    }

    [Fact]
    public void ShowKeepHint_FollowsEffectKeepOn()
    {
        Assert.True(new EffectState(Keep, "2", true).ShowKeepHint);
        Assert.False(new EffectState(Dword, "1", true).ShowKeepHint);
    }

    [Fact]
    public void KeepEffect_Absent_StillReadsAsAppearance()
    {
        var s = new EffectState(Keep, null, false);
        Assert.True(s.IsAppearance);
        Assert.True(s.CanDisable);
    }
}

public class VisualEffectsPlanTests
{
    [Fact]
    public void ForPerformance_CoversEveryEffectOnce()
        => Assert.Equal(VisualEffectsCatalog.Effects.Count,
                        VisualEffectsPlan.ForPerformance(VisualEffectsCatalog.Effects).Count);

    [Fact]
    public void ForPerformance_NonKeepEffects_GetPerformanceValue()
        => Assert.All(VisualEffectsPlan.ForPerformance(VisualEffectsCatalog.Effects).Where(w => !w.Effect.KeepOn),
                      w => Assert.Equal(w.Effect.PerformanceValue, w.Value));

    [Fact]
    public void ForPerformance_KeepEffects_StayAtAppearance_ClearTypePreserved()
    {
        var plan = VisualEffectsPlan.ForPerformance(VisualEffectsCatalog.Effects);
        Assert.All(plan.Where(w => w.Effect.KeepOn), w => Assert.Equal(w.Effect.AppearanceValue, w.Value));
        var fs = plan.Single(w => w.Effect.Id == "font-smoothing");
        Assert.Equal("2", fs.Value);   // ClearType is NOT turned off by the performance preset
    }

    [Fact]
    public void ForAppearance_EveryEffect_GetsAppearanceValue()
        => Assert.All(VisualEffectsPlan.ForAppearance(VisualEffectsCatalog.Effects),
                      w => Assert.Equal(w.Effect.AppearanceValue, w.Value));
}

public class VisualEffectsReportTests
{
    private static readonly VisualEffect NonKeep =
        new("nk", "L", "A", "HKCU", "K", "V1", RegistryValueType.DWord, "1", "0", false);
    private static readonly VisualEffect Keep =
        new("k", "L", "A", "HKCU", "K", "V2", RegistryValueType.String, "2", "0", true);

    private static VisualEffectsReport Report(VisualFxMode mode, bool known, params EffectState[] states)
        => new(mode, known, states);

    [Fact]
    public void Counts_TallyEachBucket()
    {
        var r = Report(VisualFxMode.Custom, true,
            new EffectState(NonKeep, "0", true),    // performance
            new EffectState(NonKeep, "1", true),    // appearance
            new EffectState(NonKeep, "9", true));   // custom
        Assert.Equal(1, r.PerformanceCount);
        Assert.Equal(1, r.AppearanceCount);
        Assert.Equal(1, r.CustomCount);
        Assert.Equal(3, r.Total);
    }

    [Fact]
    public void AllPerformance_IgnoresKeepEffects()
    {
        // Non-keep at performance + ClearType (keep) left at appearance ⇒ "optimised" is still true.
        var r = Report(VisualFxMode.BestPerformance, true,
            new EffectState(NonKeep, "0", true),
            new EffectState(Keep, "2", true));
        Assert.True(r.AllPerformance);
        Assert.False(r.AllAppearance);
    }

    [Fact]
    public void AllPerformance_False_WhenANonKeepEffectIsStillOn()
    {
        var r = Report(VisualFxMode.Custom, true,
            new EffectState(NonKeep, "1", true));   // still appearance
        Assert.False(r.AllPerformance);
    }

    [Fact]
    public void AllAppearance_True_WhenEverythingPretty()
    {
        var r = Report(VisualFxMode.BestAppearance, true,
            new EffectState(NonKeep, "1", true),
            new EffectState(Keep, "2", true));
        Assert.True(r.AllAppearance);
        Assert.False(r.AllPerformance);
    }

    [Fact]
    public void ModeDisplay_IsInconnu_WhenModeNotKnown()
        => Assert.Equal("Inconnu", Report(VisualFxMode.Unknown, false).ModeDisplay);

    [Fact]
    public void ModeDisplay_IsLabel_WhenKnown()
        => Assert.Equal(VisualFxModeInfo.Label(VisualFxMode.BestPerformance),
                        Report(VisualFxMode.BestPerformance, true).ModeDisplay);
}

public class VisualEffectsServiceTests
{
    private const string ModePath =
        "HKCU\\" + VisualEffectsCatalog.VisualFxKey + "\\" + VisualEffectsCatalog.VisualFxValue;

    private static (VisualEffectsService svc, FakeRegistryService reg) New()
    {
        var reg = new FakeRegistryService(new EventLog());
        return (new VisualEffectsService(reg), reg);
    }

    private static string PathOf(VisualEffect e) => $"{e.Hive}\\{e.Key}\\{e.ValueName}";

    [Fact]
    public async Task GetReport_ReadsSeededModeAndEffectState()
    {
        var (svc, reg) = New();
        reg.Seed("HKCU", VisualEffectsCatalog.VisualFxKey, VisualEffectsCatalog.VisualFxValue, "2");
        var ta = VisualEffectsCatalog.Find("taskbar-animations")!;
        reg.Seed(ta.Hive, ta.Key, ta.ValueName, "0");   // performance

        var r = await svc.GetReportAsync();

        Assert.True(r.ModeKnown);
        Assert.Equal(VisualFxMode.BestPerformance, r.Mode);
        var st = r.Effects.First(e => e.Id == "taskbar-animations");
        Assert.True(st.IsPresent);
        Assert.True(st.IsPerformance);
    }

    [Fact]
    public async Task GetReport_NothingSeeded_ModeUnknown_AllAbsentDefaultAppearance()
    {
        var (svc, _) = New();
        var r = await svc.GetReportAsync();

        Assert.False(r.ModeKnown);
        Assert.Equal(VisualFxMode.Unknown, r.Mode);
        Assert.All(r.Effects, e => Assert.False(e.IsPresent));
        Assert.All(r.Effects, e => Assert.True(e.IsAppearance));   // absent ⇒ default appearance, never fabricated off
    }

    [Fact]
    public async Task SetEffect_Disable_WritesPerformanceValue_AndFlipsModeToCustom()
    {
        var (svc, reg) = New();
        var ta = VisualEffectsCatalog.Find("taskbar-animations")!;

        var ok = await svc.SetEffectAsync("taskbar-animations", appearance: false);

        Assert.True(ok);
        Assert.Equal("0", reg.Store[PathOf(ta)]);
        Assert.Equal("3", reg.Store[ModePath]);   // Custom
    }

    [Fact]
    public async Task SetEffect_Enable_WritesAppearanceValue()
    {
        var (svc, reg) = New();
        var drag = VisualEffectsCatalog.Find("drag-full-windows")!;

        var ok = await svc.SetEffectAsync("drag-full-windows", appearance: true);

        Assert.True(ok);
        Assert.Equal("1", reg.Store[PathOf(drag)]);
        Assert.Equal("3", reg.Store[ModePath]);
    }

    [Fact]
    public async Task SetEffect_UnknownId_ReturnsFalse_AndWritesNothing()
    {
        var (svc, reg) = New();
        var ok = await svc.SetEffectAsync("does-not-exist", appearance: false);

        Assert.False(ok);
        Assert.Empty(reg.Store);   // not even the mode is touched on an unknown id
    }

    [Fact]
    public async Task ApplyPreset_Performance_DisablesAll_ButKeepsClearType_AndRecordsMode()
    {
        var (svc, reg) = New();

        var ok = await svc.ApplyPresetAsync(performance: true);

        Assert.True(ok);
        foreach (var e in VisualEffectsCatalog.Effects)
        {
            var expected = e.KeepOn ? e.AppearanceValue : e.PerformanceValue;
            Assert.Equal(expected, reg.Store[PathOf(e)]);
        }
        var fs = VisualEffectsCatalog.Find("font-smoothing")!;
        Assert.Equal("2", reg.Store[PathOf(fs)]);   // ClearType preserved
        Assert.Equal("2", reg.Store[ModePath]);     // BestPerformance
    }

    [Fact]
    public async Task ApplyPreset_Appearance_RestoresAll_AndRecordsMode()
    {
        var (svc, reg) = New();

        var ok = await svc.ApplyPresetAsync(performance: false);

        Assert.True(ok);
        foreach (var e in VisualEffectsCatalog.Effects)
            Assert.Equal(e.AppearanceValue, reg.Store[PathOf(e)]);
        Assert.Equal("1", reg.Store[ModePath]);     // BestAppearance
    }
}
