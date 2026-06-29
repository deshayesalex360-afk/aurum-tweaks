using System;
using System.Security.Cryptography;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Proves the offline licence check is REAL cryptography, not a rubber stamp: a token signed by the seller's private
/// key verifies and grants its edition; a token tampered by one byte, signed by the wrong key, malformed, or expired
/// grants NOTHING (always Free). Uses an ephemeral P-256 keypair and verifies through the PUBLIC half only — exactly
/// what ships — so the test mirrors the deployed trust boundary. The private key never leaves this process.
/// </summary>
public class LicenseVerifierTests
{
    private static readonly DateTime Now = new(2026, 06, 26, 12, 00, 00, DateTimeKind.Utc);

    private static ECDsa PublicHalf(ECDsa signer)
    {
        var pub = ECDsa.Create();
        pub.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _);
        return pub;
    }

    private static LicensePayload Premium(DateTime? expires = null)
        => new(AppEdition.Premium, "joueur@example.com", Now.AddDays(-1), expires);

    [Fact]
    public void ValidToken_VerifiesThroughThePublicKey_AndGrantsItsEdition()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var pub = PublicHalf(signer);

        string token = LicenseIssuer.Issue(Premium(), signer);
        var result = LicenseVerifier.Verify(token, pub, Now);

        Assert.True(result.IsValid);
        Assert.Equal(AppEdition.Premium, result.Edition);
        Assert.Equal("ok", result.Reason);
        Assert.Equal("joueur@example.com", result.Payload!.LicensedTo);
    }

    [Fact]
    public void TamperedPayload_FailsTheSignature_AndGrantsFree()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var pub = PublicHalf(signer);

        string token = LicenseIssuer.Issue(Premium(), signer);

        // Flip a byte of the (still validly-base64) payload, keep the original signature → decodes fine, but the
        // signature no longer matches the bytes. This is the « forge a Premium licence » attack; it must be caught.
        string[] parts = token.Split('.');
        byte[] payload = Convert.FromBase64String(parts[0]);
        payload[0] ^= 0xFF;
        string forged = Convert.ToBase64String(payload) + "." + parts[1];

        var result = LicenseVerifier.Verify(forged, pub, Now);

        Assert.False(result.IsValid);
        Assert.Equal("signature", result.Reason);
        Assert.Equal(AppEdition.Free, result.Edition);
    }

    [Fact]
    public void TokenSignedByAnotherKey_FailsTheSignature()
    {
        using var seller = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var sellerPub = PublicHalf(seller);

        // The attacker mints a "Premium" token with THEIR key; verified against the seller's public key it's worthless.
        string token = LicenseIssuer.Issue(Premium(), attacker);
        var result = LicenseVerifier.Verify(token, sellerPub, Now);

        Assert.False(result.IsValid);
        Assert.Equal("signature", result.Reason);
        Assert.Equal(AppEdition.Free, result.Edition);
    }

    [Fact]
    public void ExpiredLicence_RevertsToFree()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var pub = PublicHalf(signer);

        string token = LicenseIssuer.Issue(Premium(expires: Now.AddDays(-1)), signer);
        var result = LicenseVerifier.Verify(token, pub, Now);

        Assert.False(result.IsValid);
        Assert.Equal("expired", result.Reason);
        Assert.Equal(AppEdition.Free, result.Edition);
    }

    [Fact]
    public void NotYetExpiredLicence_IsValid()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var pub = PublicHalf(signer);

        string token = LicenseIssuer.Issue(Premium(expires: Now.AddDays(30)), signer);
        var result = LicenseVerifier.Verify(token, pub, Now);

        Assert.True(result.IsValid);
        Assert.Equal(AppEdition.Premium, result.Edition);
    }

    [Fact]
    public void PerpetualLicence_NoExpiry_IsValid()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var pub = PublicHalf(signer);

        string token = LicenseIssuer.Issue(Premium(expires: null), signer);
        Assert.True(LicenseVerifier.Verify(token, pub, Now).IsValid);
    }

    [Fact]
    public void FreeEditionToken_RoundTripsAsFree()
    {
        // A signed Free licence is legitimately valid — it just grants Free. (Useful as an explicit "downgrade" key.)
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var pub = PublicHalf(signer);

        string token = LicenseIssuer.Issue(new LicensePayload(AppEdition.Free, "x", Now, null), signer);
        var result = LicenseVerifier.Verify(token, pub, Now);

        Assert.True(result.IsValid);
        Assert.Equal(AppEdition.Free, result.Edition);
    }

    [Theory]
    [InlineData(null, "empty")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("onlyonepart", "malformed")]
    [InlineData("a.b.c", "malformed")]
    [InlineData("not!base64.also!bad", "malformed")]
    public void GarbageTokens_FailSafeToFree(string? token, string expectedReason)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var pub = PublicHalf(signer);

        var result = LicenseVerifier.Verify(token, pub, Now);

        Assert.False(result.IsValid);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(AppEdition.Free, result.Edition);
    }
}
