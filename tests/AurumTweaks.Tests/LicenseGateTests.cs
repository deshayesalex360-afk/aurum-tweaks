using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the product-level gate that composes « is licensing even active » with the pure entitlement policy. The
/// load-bearing rule: a build with NO embedded key (licensing not configured) runs FULLY unlocked — we never paywall
/// a feature that has no checkout — while the instant a key is configured, the Free promise (Tranquille only) applies.
/// Both halves are proven here so neither can regress silently.
/// </summary>
public class LicenseGateTests
{
    // ---- Not configured ⇒ everything is unlocked, regardless of edition or tier. ----

    [Theory]
    [InlineData(AppEdition.Free, TweakTier.Tranquille)]
    [InlineData(AppEdition.Free, TweakTier.Avance)]
    [InlineData(AppEdition.Free, TweakTier.Extreme)]
    [InlineData(AppEdition.Premium, TweakTier.Extreme)]
    public void NotConfigured_UnlocksEveryTier(AppEdition edition, TweakTier tier)
        => Assert.True(LicenseGate.IsTierUnlocked(licensingConfigured: false, edition, tier));

    [Theory]
    [InlineData(AppEdition.Free, PremiumFeature.AdvancedTweaks)]
    [InlineData(AppEdition.Free, PremiumFeature.ExtremeTweaks)]
    [InlineData(AppEdition.Free, PremiumFeature.GpuOverclocking)]
    public void NotConfigured_UnlocksEveryFeature(AppEdition edition, PremiumFeature feature)
        => Assert.True(LicenseGate.IsFeatureUnlocked(licensingConfigured: false, edition, feature));

    // ---- Configured + Free ⇒ the real gate: Tranquille only, every premium surface locked. ----

    [Fact]
    public void Configured_Free_UnlocksTranquilleOnly()
    {
        Assert.True(LicenseGate.IsTierUnlocked(true, AppEdition.Free, TweakTier.Tranquille));
        Assert.False(LicenseGate.IsTierUnlocked(true, AppEdition.Free, TweakTier.Avance));
        Assert.False(LicenseGate.IsTierUnlocked(true, AppEdition.Free, TweakTier.Extreme));
    }

    [Theory]
    [InlineData(PremiumFeature.AdvancedTweaks)]
    [InlineData(PremiumFeature.ExtremeTweaks)]
    [InlineData(PremiumFeature.GpuOverclocking)]
    public void Configured_Free_LocksEveryFeature(PremiumFeature feature)
        => Assert.False(LicenseGate.IsFeatureUnlocked(true, AppEdition.Free, feature));

    // ---- Configured + Premium ⇒ all unlocked, the same as the policy core. ----

    [Theory]
    [InlineData(TweakTier.Tranquille)]
    [InlineData(TweakTier.Avance)]
    [InlineData(TweakTier.Extreme)]
    public void Configured_Premium_UnlocksEveryTier(TweakTier tier)
        => Assert.True(LicenseGate.IsTierUnlocked(true, AppEdition.Premium, tier));

    [Theory]
    [InlineData(PremiumFeature.AdvancedTweaks)]
    [InlineData(PremiumFeature.ExtremeTweaks)]
    [InlineData(PremiumFeature.GpuOverclocking)]
    public void Configured_Premium_UnlocksEveryFeature(PremiumFeature feature)
        => Assert.True(LicenseGate.IsFeatureUnlocked(true, AppEdition.Premium, feature));
}
