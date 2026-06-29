using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AurumTweaks.Services;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Proves the Licence page only ever mirrors the real <see cref="LicenseService"/> verdict — it never grants an edition
/// itself — across the three honest states (not-configured, configured-Free, Premium), and that its activate/deactivate
/// commands relay the service's save-only-when-valid crypto truth into French UI. Driven by the genuine service over an
/// ephemeral keypair (same trust chain as production), so a passing test means the page can't show a fake unlock.
/// </summary>
public class LicenseViewModelTests
{
    private static (string publicB64, ECDsa priv) NewSellerKey()
    {
        var priv = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(priv.ExportSubjectPublicKeyInfo()), priv);
    }

    private static string PremiumToken(ECDsa signer)
        => LicenseIssuer.Issue(
            new LicensePayload(AppEdition.Premium, "joueur@example.com", DateTime.UtcNow.AddDays(-1), null), signer);

    private static LicenseService Service(string? publicKeyB64, FakeLicenseStore store)
        => new(store, new FakeLicenseKeyRing(publicKeyB64));

    [Fact]
    public void NotConfigured_ShowsUnlockedNote_HidesActivation()
    {
        var vm = new LicenseViewModel(Service("", new FakeLicenseStore()));

        Assert.False(vm.LicensingConfigured);
        Assert.True(vm.ShowUnlockedNote);
        Assert.False(vm.ShowActivation);
        Assert.False(vm.IsPremium);
        Assert.Equal("Gratuite", vm.EditionLabel);
        Assert.Contains("toutes les fonctionnalités", vm.Summary);
    }

    [Fact]
    public void ConfiguredFree_ShowsActivation_AndLitsThePremiumPerks()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var vm = new LicenseViewModel(Service(pub, new FakeLicenseStore()));

        Assert.True(vm.LicensingConfigured);
        Assert.False(vm.ShowUnlockedNote);
        Assert.True(vm.ShowActivation);
        Assert.False(vm.IsPremium);
        Assert.NotEmpty(vm.PremiumFeatures);
    }

    [Fact]
    public void PremiumFeatures_AreExactlyTheGatedCatalogue_InOrder_SoTheAdvertisedOfferCantDrift()
    {
        // The « Premium débloque : » list IS the paywall promise. Pin it to the SAME source the runtime gates read
        // (AllPremiumFeatures → French labels), in catalogue order, so the page can never over-promise a perk that
        // isn't actually gated, nor omit one that is. A future hand-written copy of this list would diverge here and
        // trip the test — the same anti-drift guarantee EntitlementPolicyTests pins for the catalogue itself, enforced
        // at the page that sells it. (The list is catalogue-driven, not state-driven, so any edition exercises it.)
        var vm = new LicenseViewModel(Service("", new FakeLicenseStore()));
        var expected = EntitlementPolicy.AllPremiumFeatures.Select(PremiumFeatureLabels.French).ToList();
        Assert.Equal(expected, vm.PremiumFeatures);
    }

    [Fact]
    public async Task Activate_ValidToken_GoesPremium_ClearsInput_AndThanks()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var store = new FakeLicenseStore();
        var vm = new LicenseViewModel(Service(pub, store));

        vm.TokenInput = PremiumToken(priv);
        await vm.ActivateCommand.ExecuteAsync(null);

        Assert.True(vm.IsPremium);
        Assert.Equal("Premium", vm.EditionLabel);
        Assert.False(vm.ShowActivation);              // already unlocked → no paste box
        Assert.Equal(string.Empty, vm.TokenInput);    // accepted key is cleared
        Assert.Equal("joueur@example.com", vm.LicensedTo);
        Assert.Contains("activée", vm.ActivationMessage);
        Assert.Equal(1, store.SaveCount);             // persisted exactly once
    }

    [Fact]
    public async Task Activate_InvalidToken_StaysFree_KeepsInput_ShowsReason_PersistsNothing()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var store = new FakeLicenseStore();
        var vm = new LicenseViewModel(Service(pub, store));

        vm.TokenInput = "ceci-nest-pas-une-cle";
        await vm.ActivateCommand.ExecuteAsync(null);

        Assert.False(vm.IsPremium);
        Assert.True(vm.ShowActivation);
        Assert.Equal("ceci-nest-pas-une-cle", vm.TokenInput);   // not cleared — user can fix and retry
        Assert.Contains("refusée", vm.ActivationMessage);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Activate_EmptyToken_Nudges_WithoutTouchingTheService()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var store = new FakeLicenseStore();
        var vm = new LicenseViewModel(Service(pub, store));

        vm.TokenInput = "   ";
        await vm.ActivateCommand.ExecuteAsync(null);

        Assert.False(vm.IsPremium);
        Assert.Contains("Collez", vm.ActivationMessage);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Deactivate_AfterPremium_RevertsToFree_AndSaysSo()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var store = new FakeLicenseStore();
        var vm = new LicenseViewModel(Service(pub, store));
        vm.TokenInput = PremiumToken(priv);
        await vm.ActivateCommand.ExecuteAsync(null);
        Assert.True(vm.IsPremium);

        await vm.DeactivateCommand.ExecuteAsync(null);

        Assert.False(vm.IsPremium);
        Assert.Equal("Gratuite", vm.EditionLabel);
        Assert.True(vm.ShowActivation);
        Assert.Empty(vm.LicensedTo);
        Assert.Contains("retirée", vm.ActivationMessage);
        Assert.Equal(1, store.ClearCount);
    }

    [Fact]
    public async Task ServiceEditionChange_OutsideTheView_RefreshesIt_WithoutFabricatingAMessage()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var svc = Service(pub, new FakeLicenseStore());
        var vm = new LicenseViewModel(svc);
        Assert.False(vm.IsPremium);

        // A transition the VM didn't initiate (e.g. a stored token verified at startup) must still reflect.
        await svc.ActivateAsync(PremiumToken(priv));

        Assert.True(vm.IsPremium);
        Assert.Equal("Premium", vm.EditionLabel);
        Assert.Empty(vm.ActivationMessage);   // no command ran → no user-facing feedback invented
    }
}
