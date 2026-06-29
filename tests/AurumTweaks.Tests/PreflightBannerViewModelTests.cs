using System.Threading.Tasks;
using AurumTweaks.Services;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the shared pre-flight safety banner — the one child VM injected into every apply surface (Tweaks page,
/// Dashboard one-click) so they all forecast the SAME restore-point/reboot posture from a single probe. A genuine
/// off/pending signal must light the banner; an all-clear machine must keep it quiet (no fabricated alarm); and
/// « Revérifier » must genuinely re-probe, not replay a cached verdict. Driven through FakePreflightService;
/// no registry, no WMI.
/// </summary>
public class PreflightBannerViewModelTests
{
    [Fact]
    public async Task CautionVerdict_SurfacesOnTheBanner()
    {
        var preflight = new FakePreflightService
        {
            Verdict = PreflightEvaluator.Evaluate(new PreflightSignals(
                RestorePointRequested: true, SystemRestoreReadable: false, RebootPending: false))
        };
        var vm = new PreflightBannerViewModel(preflight);
        await vm.Initialization;

        Assert.True(vm.HasCaution);                       // the unreadable-restore caution lights the banner
        Assert.Equal(preflight.Verdict.Summary, vm.Summary);
        Assert.Contains(vm.Checks, c => c.Title == PreflightEvaluator.RestoreUnavailableTitle);
    }

    [Fact]
    public async Task AllClear_KeepsTheBannerQuiet()
    {
        var vm = new PreflightBannerViewModel(new FakePreflightService());   // default = all-clear posture
        await vm.Initialization;

        Assert.False(vm.HasCaution);
        Assert.Equal(2, vm.Checks.Count);                 // restore + reboot, surfaced for transparency
    }

    [Fact]
    public async Task Cautions_ExposesOnlyTheActionableChecks_NotTheReassuringOnes()
    {
        // The banner's detail list (the ItemsControl) binds to Cautions, not Checks: an all-clear probe still
        // produces two Ok checks, but zero of them are cautions, so the list stays empty — the banner says nothing
        // it can't back up. A genuine caution is the ONLY thing that surfaces there.
        var clear = new PreflightBannerViewModel(new FakePreflightService());
        await clear.Initialization;
        Assert.Empty(clear.Cautions);                     // nothing actionable ⇒ the detail list is silent

        var flagged = new PreflightBannerViewModel(new FakePreflightService
        {
            Verdict = PreflightEvaluator.Evaluate(new PreflightSignals(
                RestorePointRequested: true, SystemRestoreReadable: false, RebootPending: false))
        });
        await flagged.Initialization;
        var only = Assert.Single(flagged.Cautions);       // only the genuine caution reaches the detail list
        Assert.Equal(PreflightEvaluator.RestoreUnavailableTitle, only.Title);
        Assert.Equal(PreflightSeverity.Caution, only.Severity);
    }

    [Fact]
    public async Task Refresh_ReProbes_AndPicksUpTheNewPosture()
    {
        var preflight = new FakePreflightService();   // starts all-clear
        var vm = new PreflightBannerViewModel(preflight);
        await vm.Initialization;
        Assert.False(vm.HasCaution);
        var callsAfterInit = preflight.Calls;         // the constructor's initial probe

        // The machine changed (e.g. a reboot got queued); the user hits « Revérifier ».
        preflight.Verdict = PreflightEvaluator.Evaluate(new PreflightSignals(true, true, RebootPending: true));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(callsAfterInit + 1, preflight.Calls);   // genuinely re-probed, not cached
        Assert.True(vm.HasCaution);                          // and the freshly-detected caution is reflected
    }

    // --- The « Activer la Restauration système… » remediation gate --------------------------------

    [Theory]
    [InlineData(true, true, false)]    // all clear ⇒ no remediation offered
    [InlineData(false, false, false)]  // opted out ⇒ Info, not a caution
    [InlineData(true, true, true)]     // a caution, but a pending reboot has no in-app fix
    public async Task CanEnableRestore_IsFalse_WhenThereIsNoUnreadableRestoreCaution(bool req, bool readable, bool pending)
    {
        var vm = new PreflightBannerViewModel(new FakePreflightService
        {
            Verdict = PreflightEvaluator.Evaluate(new PreflightSignals(req, readable, pending))
        });
        await vm.Initialization;
        // With no unreadable-restore caution the offer is never made, so the remediation button can never appear for a
        // caution this action couldn't fix (e.g. a pending reboot has no in-app fix).
        Assert.False(vm.CanEnableRestore);
    }

    [Fact]
    public async Task RestoreUnavailable_OffersTheRemediation()
    {
        var vm = new PreflightBannerViewModel(new FakePreflightService
        {
            Verdict = PreflightEvaluator.Evaluate(new PreflightSignals(
                RestorePointRequested: true, SystemRestoreReadable: false, RebootPending: false))
        });
        await vm.Initialization;
        Assert.True(vm.HasCaution);

        // Machine-independent now: the gate tracks ONLY the verdict's unreadable-restore caution. The button isn't dead
        // even on a locked-down machine — clicking runs the real Enable-ComputerRestore and re-probes; if it can't take,
        // the caution honestly persists (proven by EnableRestore_WhenItDidntTake_KeepsTheCautionHonestly below).
        Assert.True(vm.CanEnableRestore);
    }

    [Fact]
    public async Task EnableRestore_EnablesThenReProbes_AndClearsTheCautionWhenItWorked()
    {
        var preflight = new FakePreflightService
        {
            Verdict = PreflightEvaluator.Evaluate(new PreflightSignals(
                RestorePointRequested: true, SystemRestoreReadable: false, RebootPending: false)),
            // The re-probe after enabling reads an all-clear machine: System Restore is now readable.
            VerdictAfterEnable = PreflightEvaluator.Evaluate(new PreflightSignals(true, true, false))
        };
        var vm = new PreflightBannerViewModel(preflight);
        await vm.Initialization;
        Assert.True(vm.HasCaution);
        Assert.True(vm.CanEnableRestore);
        var callsAfterInit = preflight.Calls;

        await vm.EnableRestoreCommand.ExecuteAsync(null);

        Assert.Equal(1, preflight.EnableCalls);              // the app DID the action (one selection = one action)...
        Assert.Equal(callsAfterInit + 1, preflight.Calls);   // ...then genuinely re-probed the live state
        Assert.False(vm.HasCaution);                         // the freshly-read posture cleared the caution
        Assert.False(vm.CanEnableRestore);                   // and the now-satisfied offer is withdrawn
    }

    [Fact]
    public async Task EnableRestore_WhenItDidntTake_KeepsTheCautionHonestly()
    {
        // Policy-blocked machine: the enable runs but System Restore still won't read back. VerdictAfterEnable left null
        // ⇒ the re-probe returns the same unreadable posture, so the banner must NOT fake a success.
        var preflight = new FakePreflightService
        {
            Verdict = PreflightEvaluator.Evaluate(new PreflightSignals(
                RestorePointRequested: true, SystemRestoreReadable: false, RebootPending: false))
        };
        var vm = new PreflightBannerViewModel(preflight);
        await vm.Initialization;

        await vm.EnableRestoreCommand.ExecuteAsync(null);

        Assert.Equal(1, preflight.EnableCalls);   // the attempt genuinely ran...
        Assert.True(vm.HasCaution);               // ...but nothing was faked — the caution stays
        Assert.True(vm.CanEnableRestore);         // and the offer remains, so the user isn't told a false « fixed »
    }
}
