using System.Threading.Tasks;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the « Activer la protection » one-click fix on the Points-de-restauration page: « un bouton = une action » — the
/// app ENABLES System Restore itself (no hand-off to the Windows dialog), then RE-READS so the honest QueryOk confirms
/// it. A genuine enable Windows accepts clears the « impossible » state; one it blocks must keep the page honestly
/// impossible (never a faked success). Driven through FakeRestoreManagerService; no PowerShell, no registry.
/// </summary>
public class RestoreManagerViewModelTests
{
    [Fact]
    public async Task EnableProtection_EnablesThenReReads_ClearingTheImpossibleStateWhenItTook()
    {
        var service = new FakeRestoreManagerService { OverviewQueryOk = false }; // protection reads off ⇒ « impossible »
        service.OnEnableProtection = () => service.OverviewQueryOk = true;        // Windows accepts the enable
        var vm = new RestoreManagerViewModel(service);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.ProtectionOff);   // baseline: the « Activer la protection » action is offered

        await vm.EnableProtectionCommand.ExecuteAsync(null);

        Assert.Equal(1, service.EnableProtectionCalls);  // the app DID the action (one selection = one action)...
        Assert.True(vm.QueryOk);                         // ...and the RE-READ (not the command's return) confirms it took
        Assert.False(vm.ProtectionOff);                  // the now-satisfied offer is withdrawn
    }

    [Fact]
    public async Task EnableProtection_WhenWindowsBlocksIt_StaysHonestlyImpossible()
    {
        var service = new FakeRestoreManagerService { OverviewQueryOk = false }; // stays off: policy-blocked
        var vm = new RestoreManagerViewModel(service);                           // no OnEnableProtection ⇒ nothing changes
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.EnableProtectionCommand.ExecuteAsync(null);

        Assert.Equal(1, service.EnableProtectionCalls);  // the attempt genuinely ran...
        Assert.False(vm.QueryOk);                        // ...but nothing was faked — the read still says « impossible »
        Assert.True(vm.ProtectionOff);                   // and the offer remains, honestly
    }
}
