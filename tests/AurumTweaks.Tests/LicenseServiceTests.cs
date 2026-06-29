using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Proves the runtime licence service is honest end to end: it defaults to Free, only ever reads Premium from a token
/// the embedded PUBLIC key genuinely verifies, persists a licence solely when valid, reverts to Free on deactivate or
/// expiry, and — when no key is embedded (the as-shipped placeholder) — fails safe to "not-configured" Free instead of
/// faking an unlock. Drives the SAME verify chain production does (ImportSubjectPublicKeyInfo → LicenseVerifier) from an
/// ephemeral keypair, so nothing is stubbed past the trust boundary; the private half never leaves the test.
/// </summary>
public class LicenseServiceTests
{
    // An ephemeral seller keypair: the private half signs test tokens, the public half (base64 SPKI) feeds the ring —
    // exactly the split that ships (only the public key is ever embedded).
    private static (string publicB64, ECDsa priv) NewSellerKey()
    {
        var priv = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(priv.ExportSubjectPublicKeyInfo()), priv);
    }

    private static string PremiumToken(ECDsa signer, DateTime? expires = null)
        => LicenseIssuer.Issue(
            new LicensePayload(AppEdition.Premium, "joueur@example.com", DateTime.UtcNow.AddDays(-1), expires), signer);

    private static LicenseService Service(string? publicKeyB64, FakeLicenseStore store)
        => new(store, new FakeLicenseKeyRing(publicKeyB64));

    [Fact]
    public async Task NotConfigured_NoEmbeddedKey_StaysFree_EvenWithARealLookingToken()
    {
        var (_, priv) = NewSellerKey();
        using var _dispose = priv;
        var store = new FakeLicenseStore();
        var svc = Service("", store);   // empty ring = the as-shipped placeholder

        var result = await svc.ActivateAsync(PremiumToken(priv));

        Assert.False(svc.IsConfigured);
        Assert.False(result.IsValid);
        Assert.Equal("not-configured", result.Reason);
        Assert.Equal(AppEdition.Free, svc.CurrentEdition);
        Assert.Equal(0, store.SaveCount);   // nothing persisted — there is no fake unlock to remember
    }

    [Fact]
    public async Task Initialise_NoStoredToken_IsConfiguredButFree()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var svc = Service(pub, new FakeLicenseStore());

        await svc.InitialiseAsync();

        Assert.True(svc.IsConfigured);
        Assert.Equal(AppEdition.Free, svc.CurrentEdition);
        Assert.Null(svc.CurrentPayload);
    }

    [Fact]
    public async Task Initialise_StoredValidPremiumToken_GrantsPremium()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var store = new FakeLicenseStore(PremiumToken(priv));
        var svc = Service(pub, store);

        await svc.InitialiseAsync();

        Assert.Equal(AppEdition.Premium, svc.CurrentEdition);
        Assert.Equal("ok", svc.StatusReason);
        Assert.Equal("joueur@example.com", svc.CurrentPayload!.LicensedTo);
    }

    [Fact]
    public async Task Activate_ValidPremiumToken_GrantsPremium_AndPersistsExactlyOnce()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var store = new FakeLicenseStore();
        var svc = Service(pub, store);

        var token = PremiumToken(priv);
        var result = await svc.ActivateAsync(token);

        Assert.True(result.IsValid);
        Assert.Equal(AppEdition.Premium, svc.CurrentEdition);
        Assert.Equal(token, store.Token);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Activate_TamperedToken_StaysFree_AndPersistsNothing()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var store = new FakeLicenseStore();
        var svc = Service(pub, store);

        // The « forge a Premium licence » attack: flip a payload byte, keep the signature → decodes, doesn't verify.
        var parts = PremiumToken(priv).Split('.');
        var payload = Convert.FromBase64String(parts[0]);
        payload[0] ^= 0xFF;
        var forged = Convert.ToBase64String(payload) + "." + parts[1];

        var result = await svc.ActivateAsync(forged);

        Assert.False(result.IsValid);
        Assert.Equal("signature", result.Reason);
        Assert.Equal(AppEdition.Free, svc.CurrentEdition);
        Assert.Null(store.Token);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Activate_TokenSignedByAnotherKey_StaysFree()
    {
        var (pub, seller) = NewSellerKey();
        using var _s = seller;
        var (_, attacker) = NewSellerKey();
        using var _a = attacker;
        var svc = Service(pub, new FakeLicenseStore());

        // Minted with the attacker's key, verified against the seller's public key → worthless.
        var result = await svc.ActivateAsync(PremiumToken(attacker));

        Assert.False(result.IsValid);
        Assert.Equal("signature", result.Reason);
        Assert.Equal(AppEdition.Free, svc.CurrentEdition);
    }

    [Fact]
    public async Task Deactivate_RevertsToFree_AndClearsTheStore()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var store = new FakeLicenseStore();
        var svc = Service(pub, store);
        await svc.ActivateAsync(PremiumToken(priv));
        Assert.Equal(AppEdition.Premium, svc.CurrentEdition);

        await svc.DeactivateAsync();

        Assert.Equal(AppEdition.Free, svc.CurrentEdition);
        Assert.Null(store.Token);
        Assert.Equal(1, store.ClearCount);
    }

    [Fact]
    public async Task Initialise_StoredExpiredToken_RevertsToFree()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var store = new FakeLicenseStore(PremiumToken(priv, expires: DateTime.UtcNow.AddDays(-1)));
        var svc = Service(pub, store);

        await svc.InitialiseAsync();

        Assert.Equal(AppEdition.Free, svc.CurrentEdition);
        Assert.Equal("expired", svc.StatusReason);
    }

    [Fact]
    public async Task BadEmbeddedKey_PresentButNotKeyMaterial_FailsSafeToFree_YetIsConfigured()
    {
        // A present-but-garbage key: IsConfigured is true (someone DID set a key), yet every verification fails safe —
        // the honest distinction between « no paid tier configured » and « the configured key is broken ».
        var svc = Service("not-a-real-key", new FakeLicenseStore());

        await svc.InitialiseAsync();
        var activate = await svc.ActivateAsync("anything.anything");

        Assert.True(svc.IsConfigured);
        Assert.Equal(AppEdition.Free, svc.CurrentEdition);
        Assert.Equal("bad-key", activate.Reason);
    }

    [Fact]
    public async Task EditionChanged_FiresOnRealTransitionsOnly()
    {
        var (pub, priv) = NewSellerKey();
        using var _dispose = priv;
        var svc = Service(pub, new FakeLicenseStore());
        var fires = 0;
        svc.EditionChanged += (_, _) => fires++;

        var token = PremiumToken(priv);
        await svc.ActivateAsync(token);     // Free → Premium : fires
        await svc.ActivateAsync(token);     // Premium → Premium : no fire (change-detection guard)
        await svc.DeactivateAsync();        // Premium → Free : fires

        Assert.Equal(2, fires);
    }
}
