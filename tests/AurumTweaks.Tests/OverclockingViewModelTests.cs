using System;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the honesty contract of the GPU overclocking page's four commands — proving none is a dead button
/// or a fabricator. The stakes are high: an "auto-OC" that *claimed* to have validated stability, or a
/// "stability test" that pretended a GPU stress run happened, would lure a user into locking in an unstable
/// overclock. So this pins that:
///   • RunAutoOc only ever *suggests* a (clamped) starting offset and explicitly disclaims any automatic
///     stability guarantee — it never pretends to have tuned + validated;
///   • RunStabilityTest never pretends a GPU stress ran — it points to the real external tools and to the
///     genuinely built-in RAM/CPU stability tabs;
///   • Apply / Reset really call the OC backend (not dead buttons), and Reset zeroes the offsets.
/// No NVAPI is touched: <see cref="FakeGpuOcService"/> records the calls instead of overclocking the test box.
/// </summary>
public class OverclockingViewModelTests
{
    // GPU name with no known suggestion substring (no 4090/5090/5080/4080/3080/3070) → offsets start at 0,
    // deterministically. The ctor's LoadAsync completes synchronously because every fake returns a completed Task.
    // Default licence: not-configured ⇒ GPU OC unlocked, so the freemium gate is a no-op and every pre-existing
    // apply/reset test behaves exactly as before. Gating tests pass an explicit configured FakeLicenseService.
    private static OverclockingViewModel NewVm(FakeGpuOcService? oc = null, FakeLicenseService? license = null)
        => new(new FakeHardwareService(new HardwareInfo { GpuPrimary = "NVIDIA Test GPU 0000" }),
               oc ?? new FakeGpuOcService(),
               license ?? new FakeLicenseService());

    // ---- RunAutoOc: a suggestion, never an auto-validated overclock ------------------

    [Fact]
    public void RunAutoOc_DisclaimsAnyAutomaticStabilityGuarantee()
    {
        var vm = NewVm();
        vm.RunAutoOcCommand.Execute(null);

        // The load-bearing honesty clause: stability is NOT guaranteed automatically...
        Assert.Contains("Aucune stabilité n'est garantie automatiquement", vm.AutoOcStatus);
        // ...and it sends the user to a real validation step rather than claiming it did one.
        Assert.Contains("valider", vm.AutoOcStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunAutoOc_ClampsTheSuggestedCoreOffset_NeverProposesAnAbsurdValue()
    {
        var vm = NewVm();
        vm.GpuCoreOffsetMhz = 500;            // a sky-high prefill
        vm.RunAutoOcCommand.Execute(null);

        Assert.Equal(200, vm.GpuCoreOffsetMhz); // clamped to the conservative ceiling
        Assert.Contains("+200 MHz", vm.AutoOcStatus);
    }

    [Fact]
    public void RunAutoOc_DefaultsToAConservativeSuggestion_WhenNoOffsetYet()
    {
        var vm = NewVm();                       // offsets start at 0
        vm.RunAutoOcCommand.Execute(null);
        Assert.Equal(150, vm.GpuCoreOffsetMhz);
    }

    // ---- RunStabilityTest: never pretends a GPU stress ran --------------------------

    [Fact]
    public void RunStabilityTest_IsHonestThatNoGpuStressIsIntegrated_AndPointsToRealTools()
    {
        var vm = NewVm();
        vm.RunStabilityTestCommand.Execute(null);

        Assert.Contains("Pas de stress GPU intégré", vm.StressTestProgress);
        Assert.Contains("OCCT", vm.StressTestProgress);     // names a real external validator
        Assert.Contains("Stabilité", vm.StressTestProgress); // routes to the built-in RAM/CPU tabs
    }

    // ---- Apply / Reset: provably wired to the backend (not dead buttons) ------------

    [Fact]
    public async Task ApplyGpuOc_ActuallyCallsTheBackend_WithTheCurrentOffsets()
    {
        var oc = new FakeGpuOcService();
        var vm = NewVm(oc);
        vm.GpuCoreOffsetMhz = 120;
        vm.GpuMemOffsetMhz = 800;

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.Single(oc.Applied);
        Assert.Equal(120, oc.Applied[0].CoreOffsetMhz);
        Assert.Equal(800, oc.Applied[0].MemoryOffsetMhz);
        Assert.StartsWith("Appliqué", vm.AutoOcStatus);
    }

    [Fact]
    public async Task ResetOc_CallsTheBackend_AndZeroesTheOffsets()
    {
        var oc = new FakeGpuOcService();
        var vm = NewVm(oc);
        vm.GpuCoreOffsetMhz = 150;
        vm.GpuMemOffsetMhz = 1200;

        await vm.ResetOcCommand.ExecuteAsync(null);

        Assert.Equal(1, oc.ResetCount);
        Assert.Equal(0, vm.GpuCoreOffsetMhz);
        Assert.Equal(0, vm.GpuMemOffsetMhz);
        Assert.Contains("remis à zéro", vm.AutoOcStatus);
    }

    // ---- Power-limit / voltage sliders are NOT dead controls: a successful apply admits they weren't applied ----

    [Fact]
    public async Task ApplyGpuOc_DisclosesThatPowerAndVoltageWereNotAppliedNatively()
    {
        var oc = new FakeGpuOcService();
        var vm = NewVm(oc);
        vm.GpuCoreOffsetMhz = 150;
        vm.GpuPowerLimitPct = 120;       // user asks to raise the power limit…
        vm.GpuTargetVoltageMv = 875;     // …and sets an undervolt target

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        // The backend only wrote core/mem; the status must admit the two sliders Aurum doesn't apply, so neither
        // reads as a control that silently did nothing.
        Assert.StartsWith("Appliqué", vm.AutoOcStatus);
        Assert.Contains("power limit", vm.AutoOcStatus);
        Assert.Contains("voltage", vm.AutoOcStatus);
        Assert.Contains("non appliqué", vm.AutoOcStatus);
    }

    [Fact]
    public void GpuSlidersNote_IsAStandingDisclosure_ThatPowerAndVoltageArentApplied()
    {
        var vm = NewVm();
        Assert.Contains("core et mémoire", vm.GpuSlidersNote);   // names what Aurum genuinely applies
        Assert.Contains("Afterburner", vm.GpuSlidersNote);       // and where to set the rest
    }

    // ---- Freemium gate: GPU OC is a Premium feature. A configured Free build must refuse to apply (and say so
    //      up-front), Premium applies, and the as-shipped "not configured" build stays fully unlocked. ----

    [Fact]
    public async Task ApplyGpuOc_ConfiguredFree_RefusesToTouchTheBackend_AndPointsToLicence()
    {
        var oc = new FakeGpuOcService();
        var vm = NewVm(oc, new FakeLicenseService(AppEdition.Free, configured: true));
        vm.GpuCoreOffsetMhz = 150;

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.Empty(oc.Applied);                                 // the gate refused — NVAPI path never reached
        Assert.Contains("réservé à Premium", vm.AutoOcStatus);
        Assert.Contains("Licence", vm.AutoOcStatus);
    }

    [Fact]
    public void ConfiguredFree_ShowsTheLockBannerUpFront()
    {
        var vm = NewVm(license: new FakeLicenseService(AppEdition.Free, configured: true));
        Assert.Contains("réservé à Premium", vm.GpuOcLockNote);   // disclosed before the user dials in an OC
    }

    [Fact]
    public async Task ApplyGpuOc_ConfiguredPremium_AppliesNormally()
    {
        var oc = new FakeGpuOcService();
        var vm = NewVm(oc, new FakeLicenseService(AppEdition.Premium, configured: true));
        vm.GpuCoreOffsetMhz = 150;

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.Single(oc.Applied);                                // Premium passes the gate
        Assert.StartsWith("Appliqué", vm.AutoOcStatus);
        Assert.Empty(vm.GpuOcLockNote);                           // no lock banner for a paid edition
    }

    [Fact]
    public void NotConfigured_LeavesGpuOcUnlocked_NoBanner()
    {
        // As-shipped: no embedded key ⇒ the gate is dormant, so even a Free edition has GPU OC fully available.
        var vm = NewVm(license: new FakeLicenseService(AppEdition.Free, configured: false));
        Assert.Empty(vm.GpuOcLockNote);
    }

    [Fact]
    public async Task ActivatingPremium_ClearsTheLockBannerLive_WithoutRelaunch()
    {
        var license = new FakeLicenseService(AppEdition.Free, configured: true);
        var vm = NewVm(license: license);
        Assert.NotEmpty(vm.GpuOcLockNote);                        // locked while Free

        await license.ActivateAsync("any");                       // a key activated elsewhere → Premium + EditionChanged

        Assert.Empty(vm.GpuOcLockNote);                           // banner clears without rebuilding the page
    }

    [Fact]
    public async Task ResetOc_StaysAvailableOnConfiguredFree_SoAnOcCanAlwaysBeUndone()
    {
        // Reset zeroes offsets — a safety/undo action. It must NEVER be gated: a Free user who somehow has an OC
        // applied must always be able to back it out. (The fake doesn't enforce edition, mirroring the VM's gate.)
        var oc = new FakeGpuOcService();
        var vm = NewVm(oc, new FakeLicenseService(AppEdition.Free, configured: true));
        vm.GpuCoreOffsetMhz = 150;

        await vm.ResetOcCommand.ExecuteAsync(null);

        Assert.Equal(1, oc.ResetCount);                           // reset reached the backend despite Free
        Assert.Equal(0, vm.GpuCoreOffsetMhz);
    }
}
