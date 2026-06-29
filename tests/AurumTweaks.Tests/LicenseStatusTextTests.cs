using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Guards the licence vocabulary's French face: every internal reason code the verifier/service can emit must map to a
/// real, non-empty sentence (no blank cell, no leaked English token on the Licence page), an unknown code falls back
/// safely, and the three page-headline states say the honest thing — « not configured » must NOT imply a paywall.
/// </summary>
public class LicenseStatusTextTests
{
    // The exhaustive set of codes the rest of the licence stack produces: LicenseValidation.Valid ("ok"),
    // LicenseService's "not-loaded"/"not-configured"/"bad-key", LicenseVerifier's "empty"/"malformed"/"signature"/
    // "payload"/"expired", and DeactivateAsync's "deactivated". If a new code is added without French, this misses it
    // and the maintainer's eye on this list is the prompt — but the default-case test below still keeps the UI safe.
    [Theory]
    [InlineData("ok")]
    [InlineData("not-configured")]
    [InlineData("not-loaded")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("signature")]
    [InlineData("payload")]
    [InlineData("expired")]
    [InlineData("deactivated")]
    [InlineData("bad-key")]
    public void French_EveryKnownReason_IsNonEmpty(string reason)
        => Assert.False(string.IsNullOrWhiteSpace(LicenseStatusText.French(reason)));

    [Fact]
    public void French_UnknownReason_FallsBackSafely_NeverLeaksTheCode()
    {
        var text = LicenseStatusText.French("some-future-code");
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("some-future-code", text);
    }

    [Theory]
    [InlineData(AppEdition.Free, "Gratuite")]
    [InlineData(AppEdition.Premium, "Premium")]
    public void FrenchEdition_NamesTheEdition(AppEdition edition, string expected)
        => Assert.Equal(expected, LicenseStatusText.FrenchEdition(edition));

    [Fact]
    public void FrenchSummary_NotConfigured_SaysEverythingUnlocked_AndNeverImpliesAPaywall()
    {
        var text = LicenseStatusText.FrenchSummary(configured: false, AppEdition.Free);
        Assert.Contains("toutes les fonctionnalités", text);
        // The honesty point: with no checkout, we never tell the user to buy or activate anything.
        Assert.DoesNotContain("Activez", text);
    }

    [Fact]
    public void FrenchSummary_ConfiguredFree_NamesWhatPremiumAdds()
    {
        var text = LicenseStatusText.FrenchSummary(configured: true, AppEdition.Free);
        Assert.Contains("Gratuite", text);
        Assert.Contains("Activez", text);
    }

    [Fact]
    public void FrenchSummary_ConfiguredPremium_IsThePaidStory()
    {
        var text = LicenseStatusText.FrenchSummary(configured: true, AppEdition.Premium);
        Assert.Contains("Premium", text);
    }
}
