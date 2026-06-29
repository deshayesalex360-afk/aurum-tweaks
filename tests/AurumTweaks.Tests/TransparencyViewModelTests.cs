using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Thin wiring test for the « Transparence &amp; confiance » VM: proves it gathers the LIVE facts (per-tier catalog
/// counts, the integrity gate's refused files, the user's restore-point setting) and renders them through the pure
/// <see cref="AurumTweaks.Services.TransparencyReport"/>. The disclosure's wording is pinned by
/// <see cref="TransparencyReportTests"/>; here we only assert the VM fed the renderer the real numbers — never a
/// value frozen at construction or a fabricated all-clear.
/// </summary>
public class TransparencyViewModelTests
{
    private static Tweak OfTier(TweakTier tier) => new() { Tier = tier };

    private static TransparencyViewModel Build(bool restoreOn, params string[] rejectedFiles)
    {
        var catalog = new[]
        {
            OfTier(TweakTier.Tranquille), OfTier(TweakTier.Tranquille),
            OfTier(TweakTier.Avance),
            OfTier(TweakTier.Extreme),
        };
        var repo = new FakeTweakRepository(catalog, rejectedFiles);
        var settings = new FakeAppSettingsStore();
        settings.Current.CreateRestorePointBeforeTweaks = restoreOn;
        return new TransparencyViewModel(repo, settings);
    }

    [Fact]
    public async Task Initialization_RendersTheDisclosure_WithLiveTierCounts()
    {
        var vm = Build(restoreOn: true);
        await vm.Initialization;

        Assert.Contains("Transparence & confiance", vm.ReportText);
        Assert.Contains("4 tweak(s)", vm.ReportText);     // 2 + 1 + 1 from the injected catalog
        Assert.Contains("2 Tranquille", vm.ReportText);
        Assert.Contains("1 Avancé", vm.ReportText);
        Assert.Contains("1 Extrême", vm.ReportText);
    }

    [Fact]
    public async Task Initialization_ReflectsTheUsersRestorePointSetting_WhenOff()
    {
        // The VM must read the CURRENT setting, not assume the default — a user who opted out reads « DÉSACTIVÉ ».
        var vm = Build(restoreOn: false);
        await vm.Initialization;

        Assert.Contains("DÉSACTIVÉ", vm.ReportText);
        Assert.DoesNotContain("actuellement ACTIVE", vm.ReportText);
    }

    [Fact]
    public async Task Initialization_DisclosesTheIntegrityGateVerdict_WhenAFileWasRefused()
    {
        var vm = Build(restoreOn: true, "extreme/evil.json [Unknown]", "advanced/bad.json [Tampered]");
        await vm.Initialization;

        Assert.Contains("2 fichier(s) REFUSÉ(s)", vm.ReportText);
        Assert.DoesNotContain("État : intègre", vm.ReportText);
    }

    [Fact]
    public async Task RefreshCommand_RepopulatesReportText_AndClearsStatus()
    {
        var vm = Build(restoreOn: true);
        await vm.Initialization;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("Transparence & confiance", vm.ReportText);
        Assert.Equal(string.Empty, vm.Status);
    }
}
