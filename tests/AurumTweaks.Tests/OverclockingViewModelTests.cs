using System;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.Services.Interop;
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
    private static OverclockingViewModel NewVm(FakeGpuOcService? oc = null, FakeLicenseService? license = null,
                                               RecordingApplyJournal? journal = null, FakeMonitoringService? monitoring = null,
                                               FakeGpuStressLoad? stress = null, StubGpuTdrProbe? tdr = null,
                                               FakeGpuFanService? fan = null)
        => new(new FakeHardwareService(new HardwareInfo { GpuPrimary = "NVIDIA Test GPU 0000" }),
               oc ?? new FakeGpuOcService(),
               license ?? new FakeLicenseService(),
               journal ?? new RecordingApplyJournal(),
               monitoring ?? new FakeMonitoringService(),
               stress ?? new FakeGpuStressLoad(),
               tdr ?? new StubGpuTdrProbe(),
               fan ?? new FakeGpuFanService());

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

    // ---- RunStabilityTest: runs a REAL integrated GPU load when available (see Phase 3 section below) ----

    [Fact]
    public void RunStabilityTest_GpuLoadAvailable_StartsARealRun()
    {
        var stress = new FakeGpuStressLoad { Available = true };
        var vm = NewVm(stress: stress);

        vm.RunStabilityTestCommand.Execute(null);

        Assert.Equal(1, stress.StartCount);                 // a real GPU load was started, not a canned message
        Assert.True(vm.StabilityTestRunning);
        Assert.Contains("Charge GPU réelle", vm.StressTestProgress);
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
        // The status surfaces the service's per-axis summary, not a fixed string.
        Assert.Contains("remis à 0", vm.AutoOcStatus);
    }

    [Fact]
    public async Task ApplyGpuOc_NativeWriteFails_ShowsErreur_NeverAppliqué()
    {
        // The honesty-critical failure branch: a failed native write must read as an error, never as a
        // successful apply. Now testable because the fake can return Success=false.
        var oc = new FakeGpuOcService(succeed: false);
        var vm = NewVm(oc);
        vm.GpuCoreOffsetMhz = 150;

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.StartsWith("Erreur", vm.AutoOcStatus);
        Assert.DoesNotContain("Appliqué", vm.AutoOcStatus);
    }

    [Fact]
    public async Task ResetOc_NativeResetFails_ShowsErreur_AndDoesNotZeroTheSliders()
    {
        // On a failed reset the card still holds the OC, so the sliders must keep showing it — zeroing
        // them would make the page claim a reset that never happened.
        var oc = new FakeGpuOcService(succeed: false);
        var vm = NewVm(oc);
        vm.GpuCoreOffsetMhz = 150;
        vm.GpuMemOffsetMhz = 1200;

        await vm.ResetOcCommand.ExecuteAsync(null);

        Assert.StartsWith("Erreur", vm.AutoOcStatus);
        Assert.DoesNotContain("remis", vm.AutoOcStatus);
        Assert.Equal(150, vm.GpuCoreOffsetMhz);   // still shows the on-card OC, not a fabricated 0
        Assert.Equal(1200, vm.GpuMemOffsetMhz);
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
    public void GpuSlidersNote_NoVerifiedBackend_ClaimsNothing_AndPointsToTheRealTools()
    {
        // The default fake reports BackendAvailable=false — claiming "Aurum applies core/mem natively"
        // on a machine with no verified backend would itself be an overclaim.
        var vm = NewVm();
        Assert.Contains("Aucun backend", vm.GpuSlidersNote);
        Assert.Contains("Afterburner", vm.GpuSlidersNote);
    }

    [Fact]
    public void GpuSlidersNote_NvidiaBackendUp_ClaimsCoreMem_AndDisownsPowerAndVoltage()
    {
        var vm = NewVm(new FakeGpuOcService(new GpuOcBackendStatus(GpuVendor.Nvidia, "Test GPU", BackendAvailable: true)));
        Assert.Contains("core et mémoire", vm.GpuSlidersNote);   // names what Aurum genuinely applies
        Assert.Contains("power limit", vm.GpuSlidersNote);       // and what it doesn't (unverified here)
        Assert.Contains("voltage", vm.GpuSlidersNote);
        Assert.Contains("Afterburner", vm.GpuSlidersNote);
    }

    [Fact]
    public void GpuSlidersNote_AmdAdlxBackend_ClaimsPowerViaDocumentedApi_NotOffsets()
    {
        var vm = NewVm(new FakeGpuOcService(new GpuOcBackendStatus(
            GpuVendor.Amd, "Radeon Test", BackendAvailable: true,
            PowerBackend: GpuPowerBackendKind.AdlxDocumented,
            PowerLimitMinPct: -10, PowerLimitMaxPct: 15, PowerLimitDefaultPct: 0)));

        Assert.Contains("power limit", vm.GpuSlidersNote);
        Assert.Contains("ADLX", vm.GpuSlidersNote);
        Assert.Contains("officielle", vm.GpuSlidersNote);         // AMD's documented API, not "undocumented"
        Assert.DoesNotContain("pas documentée", vm.GpuSlidersNote);
        Assert.Contains("offsets core/mémoire", vm.GpuSlidersNote); // referred, since NVAPI offsets don't apply on AMD
    }

    [Fact]
    public void PowerSliderBounds_AreTheVerifiedBackendWindow_WhenNative_ElseTheGenericRange()
    {
        Assert.Equal(50, NewVm().PowerSliderMinPct);
        Assert.Equal(133, NewVm().PowerSliderMaxPct);

        var native = NewVm(new FakeGpuOcService(NativePowerStatus()));
        Assert.Equal(47, native.PowerSliderMinPct);   // the card's real window, not the generic 50–133
        Assert.Equal(120, native.PowerSliderMaxPct);
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

    // ---- Verified-native power limit: the disclosure flips exactly with the backend's verification ----

    private static GpuOcBackendStatus NativePowerStatus()
        => new(GpuVendor.Nvidia, "Test GPU", BackendAvailable: true,
               PowerBackend: GpuPowerBackendKind.NvapiCommunity, PowerLimitMinPct: 47, PowerLimitMaxPct: 120);

    [Fact]
    public async Task ApplyGpuOc_PowerNative_NoLongerDisclaimsThePowerLimit_ButStillDisclaimsVoltage()
    {
        var oc = new FakeGpuOcService(NativePowerStatus());
        var vm = NewVm(oc);
        vm.GpuCoreOffsetMhz = 150;
        vm.GpuPowerLimitPct = 120;       // genuinely applied on a verified card…
        vm.GpuTargetVoltageMv = 875;     // …voltage never is

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.StartsWith("Appliqué", vm.AutoOcStatus);
        // Assert on the disclaimer CLAUSE (after " — "), not the whole string: the power limit genuinely
        // applied on a verified card, so it must never appear in the "non appliqué" clause. Checking the
        // clause specifically means the test can't be satisfied merely by the fake omitting power from
        // its Applied summary — it pins the real invariant.
        var idx = vm.AutoOcStatus.IndexOf(" — ", System.StringComparison.Ordinal);
        Assert.True(idx >= 0, "expected a disclaimer clause for the voltage Aurum does not apply");
        var disclaimer = vm.AutoOcStatus[(idx + 3)..];
        Assert.Contains("voltage", disclaimer);
        Assert.Contains("non appliqué", disclaimer);
        Assert.DoesNotContain("power limit", disclaimer);
    }

    [Fact]
    public void GpuSlidersNote_PowerNative_ClaimsPowerLimit_WithTheReadBackCaveat()
    {
        var vm = NewVm(new FakeGpuOcService(NativePowerStatus()));
        Assert.Contains("power limit", vm.GpuSlidersNote);
        Assert.Contains("relecture", vm.GpuSlidersNote);      // the confirmation caveat, not a bare claim
    }

    [Fact]
    public void PowerLimitRowLabel_FollowsTheBackendVerification()
    {
        Assert.Equal("Power limit (via Afterburner)", NewVm().PowerLimitRowLabel);                       // unverified
        Assert.Equal("Power limit", NewVm(new FakeGpuOcService(NativePowerStatus())).PowerLimitRowLabel); // verified
    }

    [Fact]
    public void Load_PowerNative_PrefillsTheRealCardPowerLimit()
    {
        // The service read 119 % from the card → the slider must show the measured state, not a guess.
        var oc = new FakeGpuOcService(NativePowerStatus(), current: new GpuOcProfile(0, 0, 119, 0, 0));
        Assert.Equal(119, NewVm(oc).GpuPowerLimitPct);
    }

    [Fact]
    public void Load_PowerNotNative_IgnoresTheProfilePowerPlaceholder()
    {
        // Without verification the profile's power field is a neutral placeholder, not a measurement —
        // prefilling from it would fabricate a metric.
        var oc = new FakeGpuOcService(current: new GpuOcProfile(0, 0, 119, 0, 0));
        Assert.Equal(100, NewVm(oc).GpuPowerLimitPct);
    }

    // ---- Verified-native temperature target: the row only exists when it genuinely applies ----

    private static GpuOcBackendStatus NativeThermStatus()
        => new(GpuVendor.Nvidia, "Test GPU", BackendAvailable: true,
               PowerBackend: GpuPowerBackendKind.NvapiCommunity, PowerLimitMinPct: 47, PowerLimitMaxPct: 120,
               TempLimitNative: true, TempLimitMinC: 65, TempLimitMaxC: 88, TempLimitDefaultC: 84);

    [Fact]
    public void TempRow_HiddenByDefault_VisibleOnlyWhenVerifiedNative()
    {
        // A visible-but-unapplied temp slider would be a dead control; a hidden-but-applied axis worse.
        Assert.False(NewVm().TempLimitNative);
        Assert.True(NewVm(new FakeGpuOcService(NativeThermStatus())).TempLimitNative);
    }

    [Fact]
    public void TempSliderBounds_AreTheCardsOwnVerifiedWindow_WhenNative()
    {
        var vm = NewVm(new FakeGpuOcService(NativeThermStatus()));
        Assert.Equal(65, vm.TempSliderMinC);
        Assert.Equal(88, vm.TempSliderMaxC);
    }

    [Fact]
    public void Load_TempNative_PrefillsTheRealCardTempTarget()
    {
        // The service read 88 °C from the card → the slider must show the measured state, not the 83 guess.
        var oc = new FakeGpuOcService(NativeThermStatus(), current: new GpuOcProfile(0, 0, 119, 88, 0));
        Assert.Equal(88, NewVm(oc).GpuTempLimitC);
    }

    [Fact]
    public void Load_TempNotNative_IgnoresTheProfileTempPlaceholder()
    {
        var oc = new FakeGpuOcService(current: new GpuOcProfile(0, 0, 100, 88, 0));
        Assert.Equal(83, NewVm(oc).GpuTempLimitC);   // the untouched generic default
    }

    [Fact]
    public void Load_TempNative_ZeroLiveRead_FallsBackToCapturedDefault_NotTheHardcoded83()
    {
        // A verified temp axis whose live read comes back 0 °C (a momentarily failed thermal read) must
        // NOT leave the hard-coded 83 init showing as if it were the card's current target — that would be
        // a placeholder presented as a measurement (fabricated-metric shape the mandate forbids).
        var oc = new FakeGpuOcService(NativeThermStatus(), current: new GpuOcProfile(0, 0, 119, 0, 0));
        Assert.Equal(84, NewVm(oc).GpuTempLimitC);   // BackendStatus.TempLimitDefaultC, not 83
    }

    [Fact]
    public async Task ResetOc_TempNative_ReturnsTheSliderToTheCardsOwnDefault()
    {
        var vm = NewVm(new FakeGpuOcService(NativeThermStatus(), current: new GpuOcProfile(0, 0, 119, 88, 0)));
        Assert.Equal(88, vm.GpuTempLimitC);           // prefilled from the card

        await vm.ResetOcCommand.ExecuteAsync(null);

        Assert.Equal(84, vm.GpuTempLimitC);           // the card's default, not the generic 83
    }

    [Fact]
    public async Task ApplyGpuOc_PassesTheTempTargetThroughTheProfile()
    {
        var oc = new FakeGpuOcService(NativeThermStatus());
        var vm = NewVm(oc);
        vm.GpuTempLimitC = 70;

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.Single(oc.Applied);
        Assert.Equal(70, oc.Applied[0].TempLimitC);   // the backend receives exactly the dialed target
    }

    // ---- AMD GPU max frequency (ADLX): the row only exists when it genuinely applies ----

    private static GpuOcBackendStatus AmdGfxStatus()
        => new(GpuVendor.Amd, "Radeon Test", BackendAvailable: true,
               PowerBackend: GpuPowerBackendKind.AdlxDocumented, PowerLimitMinPct: -10, PowerLimitMaxPct: 15, PowerLimitDefaultPct: 0,
               GfxTuningNative: true, GfxMaxFreqMinMhz: 500, GfxMaxFreqMaxMhz: 3000, GfxMaxFreqDefaultMhz: 2500);

    [Fact]
    public void GfxRow_HiddenByDefault_VisibleOnlyWhenVerifiedNative()
    {
        Assert.False(NewVm().GfxTuningNative);
        Assert.True(NewVm(new FakeGpuOcService(AmdGfxStatus())).GfxTuningNative);
    }

    [Fact]
    public void GfxSliderBounds_AreTheDriverWindow_WhenNative()
    {
        var vm = NewVm(new FakeGpuOcService(AmdGfxStatus()));
        Assert.Equal(500, vm.GfxSliderMinMhz);
        Assert.Equal(3000, vm.GfxSliderMaxMhz);
    }

    [Fact]
    public void Load_GfxNative_PrefillsTheRealMaxFreq()
    {
        // The service read 2800 MHz from the driver → the slider must show the measured state.
        var oc = new FakeGpuOcService(AmdGfxStatus(), current: new GpuOcProfile(0, 0, 0, 0, 0) { AmdMaxFreqMhz = 2800 });
        Assert.Equal(2800, NewVm(oc).GpuAmdMaxFreqMhz);
    }

    [Fact]
    public void Load_GfxNative_ReadFails_UsesStartupDefault_NotAStrayZero()
    {
        // Read returns null on a verified GFX card → prefill from the captured startup value (2500),
        // never the init 0 that a blind apply would clamp down to the window minimum (a downclock).
        var oc = new FakeGpuOcService(AmdGfxStatus(), current: null);
        Assert.Equal(2500, NewVm(oc).GpuAmdMaxFreqMhz);
    }

    [Fact]
    public async Task ApplyGpuOc_PassesTheAmdMaxFreqThroughTheProfile()
    {
        var oc = new FakeGpuOcService(AmdGfxStatus());
        var vm = NewVm(oc);
        vm.GpuAmdMaxFreqMhz = 2700;

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.Single(oc.Applied);
        Assert.Equal(2700, oc.Applied[0].AmdMaxFreqMhz);   // the backend receives exactly the dialed target
    }

    [Fact]
    public async Task ResetOc_GfxNative_ReturnsTheSliderToTheStartupDefault()
    {
        var vm = NewVm(new FakeGpuOcService(AmdGfxStatus(), current: new GpuOcProfile(0, 0, 0, 0, 0) { AmdMaxFreqMhz = 2800 }));
        Assert.Equal(2800, vm.GpuAmdMaxFreqMhz);          // prefilled from the driver

        await vm.ResetOcCommand.ExecuteAsync(null);

        Assert.Equal(2500, vm.GpuAmdMaxFreqMhz);          // the startup default, not a stray 0
    }

    // ---- AMD memory max frequency (ADLX VRAM): same verified-only contract as GPU frequency ----

    private static GpuOcBackendStatus AmdVramStatus()
        => new(GpuVendor.Amd, "Radeon Test", BackendAvailable: true,
               GfxTuningNative: true, GfxMaxFreqMinMhz: 500, GfxMaxFreqMaxMhz: 3000, GfxMaxFreqDefaultMhz: 2500,
               VramTuningNative: true, VramMaxFreqMinMhz: 1000, VramMaxFreqMaxMhz: 2600, VramMaxFreqDefaultMhz: 2400);

    [Fact]
    public void VramRow_HiddenByDefault_VisibleOnlyWhenVerifiedNative()
    {
        Assert.False(NewVm().VramTuningNative);
        Assert.True(NewVm(new FakeGpuOcService(AmdVramStatus())).VramTuningNative);
    }

    [Fact]
    public void VramSliderBounds_AreTheDriverWindow_WhenNative()
    {
        var vm = NewVm(new FakeGpuOcService(AmdVramStatus()));
        Assert.Equal(1000, vm.VramSliderMinMhz);
        Assert.Equal(2600, vm.VramSliderMaxMhz);
    }

    [Fact]
    public void Load_VramNative_PrefillsTheRealMaxFreq_ElseStartupDefaultOnReadFail()
    {
        var read = new FakeGpuOcService(AmdVramStatus(), current: new GpuOcProfile(0, 0, 0, 0, 0) { AmdMaxVramFreqMhz = 2550 });
        Assert.Equal(2550, NewVm(read).GpuAmdMaxVramFreqMhz);

        var readFail = new FakeGpuOcService(AmdVramStatus(), current: null);
        Assert.Equal(2400, NewVm(readFail).GpuAmdMaxVramFreqMhz);   // startup default, never a stray 0
    }

    [Fact]
    public async Task ApplyGpuOc_PassesTheAmdMaxVramFreqThroughTheProfile()
    {
        var oc = new FakeGpuOcService(AmdVramStatus());
        var vm = NewVm(oc);
        vm.GpuAmdMaxVramFreqMhz = 2500;

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.Single(oc.Applied);
        Assert.Equal(2500, oc.Applied[0].AmdMaxVramFreqMhz);
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

    // ---- Phase 2a: an overclock is journaled like every other system change ----

    [Fact]
    public async Task ApplyGpuOc_RecordsAJournalEntry_SoTheOverclockIsVisibleInWhatChanged()
    {
        var journal = new RecordingApplyJournal();
        var vm = NewVm(journal: journal);
        vm.GpuCoreOffsetMhz = 120;

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.Single(journal.Entries);
        Assert.Equal("Application", journal.Entries[0].Action);
        Assert.Equal(1, journal.Entries[0].Succeeded);
        Assert.Contains("GPU OC", journal.Entries[0].TweakIds[0]);
    }

    [Fact]
    public async Task ResetOc_RecordsARestaurationJournalEntry()
    {
        var journal = new RecordingApplyJournal();
        var vm = NewVm(journal: journal);

        await vm.ResetOcCommand.ExecuteAsync(null);

        Assert.Single(journal.Entries);
        Assert.Equal("Restauration", journal.Entries[0].Action);
    }

    // ---- Phase 2b: auto-revert safety net arms ONLY on risky (frequency) changes ----

    [Fact]
    public async Task ApplyGpuOc_FrequencyChange_ArmsTheAutoRevertCountdown()
    {
        var vm = NewVm();
        vm.GpuCoreOffsetMhz = 150;   // a frequency axis changed vs stock → can black-screen → net armed

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.True(vm.AutoRevertActive);
        Assert.Contains("Conserver", vm.AutoRevertLabel);
    }

    [Fact]
    public async Task ApplyGpuOc_PowerOnlyChange_DoesNotArmAutoRevert()
    {
        // Power is bounded by the card window and can't hang the display → no countdown (would be noise).
        var vm = NewVm();
        vm.GpuCoreOffsetMhz = 0; vm.GpuMemOffsetMhz = 0;
        vm.GpuPowerLimitPct = 130;

        await vm.ApplyGpuOcCommand.ExecuteAsync(null);

        Assert.False(vm.AutoRevertActive);
    }

    [Fact]
    public async Task ConfirmKeepOc_DisarmsTheAutoRevert()
    {
        var vm = NewVm();
        vm.GpuCoreOffsetMhz = 150;
        await vm.ApplyGpuOcCommand.ExecuteAsync(null);
        Assert.True(vm.AutoRevertActive);

        vm.ConfirmKeepOcCommand.Execute(null);

        Assert.False(vm.AutoRevertActive);
        Assert.Contains("conservés", vm.AutoOcStatus);
    }

    [Fact]
    public async Task TriggerAutoRevert_ReAppliesTheCapturedPreviousProfile()
    {
        // Previous on-card state is core +100; the user pushes core +250 (risky) → the timeout must
        // re-apply the previous +100, never leave the unstable +250.
        var oc = new FakeGpuOcService(current: new GpuOcProfile(100, 0, 100, 83, 0));
        var vm = NewVm(oc);
        vm.GpuCoreOffsetMhz = 250;
        await vm.ApplyGpuOcCommand.ExecuteAsync(null);
        Assert.True(vm.AutoRevertActive);

        await vm.TriggerAutoRevertAsync();

        Assert.False(vm.AutoRevertActive);
        Assert.Equal(100, oc.Applied[^1].CoreOffsetMhz);   // last write to the card is the reverted profile
    }

    [Fact]
    public async Task TriggerAutoRevert_NoPreviousRead_FallsBackToReset()
    {
        // The pre-apply read was unavailable (fake returns null) → the safe revert is a Reset to stock.
        var oc = new FakeGpuOcService(current: null);
        var vm = NewVm(oc);
        vm.GpuCoreOffsetMhz = 250;
        await vm.ApplyGpuOcCommand.ExecuteAsync(null);
        Assert.True(vm.AutoRevertActive);

        await vm.TriggerAutoRevertAsync();

        Assert.Equal(1, oc.ResetCount);   // reverted via Reset, not a re-apply of an unknown state
    }

    // ---- Phase 2c: live observed GPU effect (not just the written setting) ----

    [Fact]
    public void LiveEffect_ReflectsTheMonitoringSnapshot_AndShowsDashForUnreadSensors()
    {
        var monitoring = new FakeMonitoringService();
        var vm = NewVm(monitoring: monitoring);

        monitoring.Push(new MonitoringSnapshot { GpuClockMhz = 2790, GpuTempC = 64, GpuUsagePercent = 98 });

        Assert.Equal(2790, vm.LiveGpuClockMhz);
        Assert.Contains("2790 MHz", vm.LiveEffectLabel);
        Assert.Contains("64 °C", vm.LiveEffectLabel);

        monitoring.Push(new MonitoringSnapshot());   // all sensors unread → never a fabricated 0
        Assert.Contains("—", vm.LiveEffectLabel);
    }

    // ---- Phase 3: integrated GPU stability test (real load + verdict) ----

    [Fact]
    public void RunStabilityTest_NoGpuLoadAvailable_FallsBackToHonestReferral_NeverFakesARun()
    {
        var vm = NewVm(stress: new FakeGpuStressLoad { Available = false });

        vm.RunStabilityTestCommand.Execute(null);

        Assert.False(vm.StabilityTestRunning);
        Assert.Contains("indisponible", vm.StressTestProgress);
        Assert.Contains("FurMark", vm.StressTestProgress);   // honest referral, no fabricated verdict
    }

    [Fact]
    public async Task StabilityRun_CollectsSamples_ThenClassifies_AndStopsTheLoad()
    {
        var stress = new FakeGpuStressLoad { Available = true };
        var monitoring = new FakeMonitoringService();
        var vm = NewVm(monitoring: monitoring, stress: stress, tdr: new StubGpuTdrProbe(tdrObserved: false));

        vm.RunStabilityTestCommand.Execute(null);
        Assert.True(vm.StabilityTestRunning);
        Assert.Equal(1, stress.StartCount);

        // Feed a healthy load telemetry stream while the run is active.
        for (int i = 0; i < 10; i++)
            monitoring.Push(new MonitoringSnapshot { GpuClockMhz = 2800 + (i % 3), GpuTempC = 70, GpuUsagePercent = 99 });

        await vm.FinishStabilityRunAsync();

        Assert.False(vm.StabilityTestRunning);
        Assert.Equal(1, stress.StopCount);                    // the real GPU load was stopped
        Assert.Contains("Stable", vm.StressTestProgress);     // healthy stream → stable verdict
    }

    [Fact]
    public async Task StabilityRun_DriverResetDetected_ReportsCrashed_NotStable()
    {
        var stress = new FakeGpuStressLoad { Available = true };
        var monitoring = new FakeMonitoringService();
        // The TDR probe reports a driver reset in the window → the run is unstable regardless of telemetry.
        var vm = NewVm(monitoring: monitoring, stress: stress, tdr: new StubGpuTdrProbe(tdrObserved: true));

        vm.RunStabilityTestCommand.Execute(null);
        for (int i = 0; i < 10; i++)
            monitoring.Push(new MonitoringSnapshot { GpuClockMhz = 2800, GpuTempC = 70, GpuUsagePercent = 99 });

        await vm.FinishStabilityRunAsync();

        Assert.Contains("reset du driver", vm.StressTestProgress);
    }

    // ---- Phase 3: fan control wiring ----

    [Fact]
    public async Task FanControl_Available_LoadsStatus_AndApplyFloorsBelowTheSafetyMinimum()
    {
        var fan = new FakeGpuFanService(new GpuFanStatus(true, GpuVendor.Nvidia, 45, 1500, "ventilateur actif"));
        var vm = NewVm(fan: fan);

        Assert.True(vm.FanAvailable);        // LoadAsync ran synchronously in the ctor (fakes are completed tasks)
        Assert.Equal(45, vm.GpuFanPercent);  // prefilled from the real current %
        Assert.Equal(1500, vm.FanRpm);

        vm.GpuFanPercent = 5;                // below the hard floor
        await vm.SetFanManualCommand.ExecuteAsync(null);

        Assert.Equal(GpuFanSafety.HardFloorPercent, fan.ManualWrites[^1]);   // floored, never reaches the card as 5
    }

    [Fact]
    public void FanControl_Unavailable_HidesTheCard()
    {
        var vm = NewVm(fan: new FakeGpuFanService(new GpuFanStatus(false, GpuVendor.Amd, 0, 0, "→ Adrenalin")));
        Assert.False(vm.FanAvailable);       // the view collapses the whole fan card on this
    }
}
