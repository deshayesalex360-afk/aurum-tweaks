using System;
using System.Collections.Generic;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the freemium gating rules — the honesty invariants the whole monetization rests on: the default edition is
/// Free (an unverifiable licence can never read as Premium), the free tier really is the full Tranquille experience,
/// Premium unlocks everything, an unknown tier fails safe to locked, and the advertised premium catalogue is generated
/// from the SAME enum the gates read (so the paywall promise can't drift from the actual lock). Pure — no licence I/O.
/// </summary>
public class EntitlementPolicyTests
{
    [Fact]
    public void DefaultEdition_IsFree_SoAnUnsetOrUnverifiableLicenceNeverReadsAsPremium()
    {
        // The fail-safe that protects the paid features: enum value 0 MUST be Free. If anyone reorders the enum and
        // makes Premium the default, this test screams.
        Assert.Equal(AppEdition.Free, default(AppEdition));
    }

    [Theory]
    [InlineData(AppEdition.Free, TweakTier.Tranquille, true)]   // the free promise: the whole safe tier works unpaid
    [InlineData(AppEdition.Free, TweakTier.Avance, false)]
    [InlineData(AppEdition.Free, TweakTier.Extreme, false)]
    [InlineData(AppEdition.Premium, TweakTier.Tranquille, true)]
    [InlineData(AppEdition.Premium, TweakTier.Avance, true)]
    [InlineData(AppEdition.Premium, TweakTier.Extreme, true)]
    public void IsTierUnlocked_FreeGetsTranquilleOnly_PremiumGetsEveryTier(
        AppEdition edition, TweakTier tier, bool expected)
        => Assert.Equal(expected, EntitlementPolicy.IsTierUnlocked(edition, tier));

    [Fact]
    public void IsTierUnlocked_UnknownTier_FailsSafeToLocked_ForBothEditions()
    {
        var bogus = (TweakTier)999;
        Assert.False(EntitlementPolicy.IsTierUnlocked(AppEdition.Free, bogus));
        Assert.False(EntitlementPolicy.IsTierUnlocked(AppEdition.Premium, bogus));
    }

    [Fact]
    public void Free_LocksEveryPremiumFeature()
    {
        foreach (var f in EntitlementPolicy.AllPremiumFeatures)
            Assert.False(EntitlementPolicy.IsUnlocked(AppEdition.Free, f), $"Free must lock {f}");
    }

    [Fact]
    public void Premium_UnlocksEveryPremiumFeature()
    {
        foreach (var f in EntitlementPolicy.AllPremiumFeatures)
            Assert.True(EntitlementPolicy.IsUnlocked(AppEdition.Premium, f), $"Premium must unlock {f}");
    }

    [Fact]
    public void AllPremiumFeatures_CoversEveryEnumValue_SoTheAdvertisedOfferCantDrift()
    {
        var declared = (PremiumFeature[])Enum.GetValues(typeof(PremiumFeature));
        Assert.Equal(declared.Length, EntitlementPolicy.AllPremiumFeatures.Count);
        foreach (var f in declared)
            Assert.Contains(f, EntitlementPolicy.AllPremiumFeatures);
    }

    [Fact]
    public void FrenchLabel_EveryFeatureHasADistinctNonEmptyLabel()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PremiumFeature f in Enum.GetValues(typeof(PremiumFeature)))
        {
            var label = PremiumFeatureLabels.French(f);
            Assert.False(string.IsNullOrWhiteSpace(label), $"{f} has no French label");
            Assert.True(seen.Add(label), $"duplicate French label: {label}");
        }
    }

    // ---- PremiumGateText: the shared runtime copy every gated apply surface speaks, pinned here so the strings the
    //      VM tests assert by substring also have one authoritative owner. The anti-drift guarantee is the load-bearing
    //      part: a refusal that names a feature must SOURCE that name from PremiumFeatureLabels, never a second copy. ----

    [Fact]
    public void GateText_TweakLocks_NameTheCountAndTheLicenceOnglet()
    {
        // The two count-based messages must carry the refused count AND point at the one place to unlock — the
        // contract the Tweaks/Dashboard/Profiles/Snapshot status assertions lean on.
        Assert.Equal("3 tweak(s) réservé(s) à Premium — activez une clé dans l'onglet Licence pour les appliquer.",
                     PremiumGateText.AllLocked(3));
        Assert.Equal(" · 2 réservé(s) à Premium", PremiumGateText.LockedSuffix(2));
    }

    [Fact]
    public void GateText_FeatureLock_SourcesItsNameFromTheSharedLabel_ForEveryFeature()
    {
        // Anti-drift: FeatureLocked must embed PremiumFeatureLabels.French(feature) verbatim, so renaming a feature in
        // the one label table renames it in every lock message too — no inline second copy can survive this.
        foreach (PremiumFeature f in Enum.GetValues(typeof(PremiumFeature)))
        {
            var msg = PremiumGateText.FeatureLocked(f, "l'appliquer");
            Assert.StartsWith(PremiumFeatureLabels.French(f), msg);
            Assert.Contains("réservé à Premium", msg);
            Assert.Contains("l'onglet Licence", msg);
        }
    }

    [Fact]
    public void GateText_FeatureLock_PlacesTheActionClauseAtTheTail()
    {
        // The site-specific tail (terse refusal vs. descriptive banner) is the only part allowed to vary; the skeleton
        // before it is shared. Pin the exact GPU-OC apply-refusal so the OverclockingViewModel copy stays stable.
        Assert.Equal("Overclocking GPU réservé à Premium — activez une clé dans l'onglet Licence pour l'appliquer.",
                     PremiumGateText.FeatureLocked(PremiumFeature.GpuOverclocking, "l'appliquer"));
        Assert.Equal("Overclocking GPU réservé à Premium — activez une clé dans l'onglet Licence pour appliquer un profil.",
                     PremiumGateText.FeatureLocked(PremiumFeature.GpuOverclocking, "appliquer un profil"));
    }
}
