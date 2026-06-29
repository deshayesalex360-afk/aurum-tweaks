using System.Threading.Tasks;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pre-flight I/O glue: it gathers the REAL signals (System Restore readability + pending reboot + the
/// restore-point toggle) and honours the toggle exactly — when the user opts out, it must NOT spawn the System Restore
/// probe (irrelevant work), and must report the honest "opted out" posture rather than a fabricated worry. Driven
/// through the in-memory fakes; no PowerShell, no registry.
/// </summary>
public class PreflightServiceTests
{
    private static PreflightService NewService(
        FakeAppSettingsStore settings, FakeRestoreManagerService restore, FakePendingRebootService reboot)
        => new(restore, reboot, settings, new RecordingRestorePointService(new EventLog()));

    [Fact]
    public async Task RestoreRequested_AndReadable_ProbesIt_AndReportsAllClear()
    {
        var settings = new FakeAppSettingsStore();
        settings.Current.CreateRestorePointBeforeTweaks = true;
        var restore = new FakeRestoreManagerService { OverviewQueryOk = true };
        var svc = NewService(settings, restore, new FakePendingRebootService());

        var v = await svc.EvaluateAsync();

        Assert.Equal(1, restore.OverviewCalls);   // genuinely probed when a point is requested
        Assert.True(v.AllClear);
        Assert.False(v.HasCaution);
    }

    [Fact]
    public async Task RestoreRequested_ButUnreadable_IsACaution()
    {
        var settings = new FakeAppSettingsStore();
        settings.Current.CreateRestorePointBeforeTweaks = true;
        var restore = new FakeRestoreManagerService { OverviewQueryOk = false };
        var svc = NewService(settings, restore, new FakePendingRebootService());

        var v = await svc.EvaluateAsync();

        Assert.Equal(1, restore.OverviewCalls);
        Assert.True(v.HasCaution);
        Assert.Contains(v.Checks, c => c.Title == PreflightEvaluator.RestoreUnavailableTitle);
    }

    [Fact]
    public async Task RestoreOptedOut_DoesNotProbeSystemRestore_AndReportsOptedOut()
    {
        var settings = new FakeAppSettingsStore();
        settings.Current.CreateRestorePointBeforeTweaks = false;          // user turned the net off
        var restore = new FakeRestoreManagerService { OverviewQueryOk = false }; // would be a Caution IF probed
        var svc = NewService(settings, restore, new FakePendingRebootService());

        var v = await svc.EvaluateAsync();

        Assert.Equal(0, restore.OverviewCalls);   // skipped: no wasted PowerShell, readability is irrelevant
        Assert.False(v.HasCaution);               // opting out is not a worry...
        Assert.Contains(v.Checks, c => c.Title == PreflightEvaluator.RestoreOptedOutTitle); // ...it's honestly disclosed
    }

    [Fact]
    public async Task PendingReboot_PropagatesAsACaution()
    {
        var settings = new FakeAppSettingsStore();   // restore-point toggle defaults on
        var reboot = new FakePendingRebootService
        {
            Status = PendingRebootEvaluator.Evaluate(new PendingRebootSignals(true, false, false, false))
        };
        var svc = NewService(settings, new FakeRestoreManagerService { OverviewQueryOk = true }, reboot);

        var v = await svc.EvaluateAsync();

        Assert.True(v.HasCaution);
        Assert.Contains(v.Checks, c => c.Title == PreflightEvaluator.RebootPendingTitle);
    }

    // --- EnableRestoreAndReprobe: the app DOES the action, then re-reads the live state -----------

    [Fact]
    public async Task EnableRestoreAndReprobe_EnablesThenReReadsLiveState_ClearingTheCautionWhenItTook()
    {
        var settings = new FakeAppSettingsStore();
        settings.Current.CreateRestorePointBeforeTweaks = true;
        var restore = new FakeRestoreManagerService { OverviewQueryOk = false };   // unreadable now ⇒ a caution
        var restorePoint = new RecordingRestorePointService(new EventLog())
        {
            OnEnable = () => restore.OverviewQueryOk = true   // simulate: Windows now reads the net back
        };
        var svc = new PreflightService(restore, new FakePendingRebootService(), settings, restorePoint);

        Assert.True((await svc.EvaluateAsync()).HasCaution);   // baseline: the restore caution is up
        var after = await svc.EnableRestoreAndReprobeAsync();

        Assert.Equal(1, restorePoint.EnableCalls);   // it actually RAN the enable — not a dialog hand-off to the user
        Assert.False(after.HasCaution);              // and the RE-PROBE (never the command's return) cleared it
    }

    [Fact]
    public async Task EnableRestoreAndReprobe_WhenItDidntTake_KeepsTheCautionFromTheLiveProbe()
    {
        var settings = new FakeAppSettingsStore();
        settings.Current.CreateRestorePointBeforeTweaks = true;
        var restore = new FakeRestoreManagerService { OverviewQueryOk = false };   // stays unreadable: policy-blocked
        var restorePoint = new RecordingRestorePointService(new EventLog());       // no OnEnable ⇒ nothing changes
        var svc = new PreflightService(restore, new FakePendingRebootService(), settings, restorePoint);

        var after = await svc.EnableRestoreAndReprobeAsync();

        Assert.Equal(1, restorePoint.EnableCalls);   // the attempt genuinely ran...
        Assert.True(after.HasCaution);               // ...but the honest re-probe still reads it off — no fake success
        Assert.Contains(after.Checks, c => c.Title == PreflightEvaluator.RestoreUnavailableTitle);
    }
}
