using System;
using System.Collections.Generic;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Which edition the running app is licensed for. <see cref="Free"/> is value 0 ON PURPOSE: it is the fail-safe
/// default, so an absent, expired, or unverifiable licence ALWAYS reads as Free and NEVER accidentally as Premium —
/// the honesty mandate cuts hardest here (the app must not hand out paid features it can't prove were paid for). Free
/// is a genuinely useful product on its own (the full Tranquille tier + monitoring + the system report), not a
/// crippled teaser; Premium adds the power-user surfaces enumerated in <see cref="PremiumFeature"/>.
/// </summary>
public enum AppEdition
{
    Free = 0,
    Premium = 1
}

/// <summary>
/// The catalogue of surfaces a Premium licence unlocks beyond the free experience. An explicit enum (not a scatter of
/// bools) so adding a gated surface is one deliberate, reviewable edit, and — critically — so the « ce que débloque
/// Premium » list shown on the upgrade screen is generated from the SAME source the runtime gates read. That single
/// source is the <see cref="TweakCategoryLabels"/> anti-drift discipline applied to entitlements: the promise on the
/// paywall and the actual lock can never word or scope the offer differently.
/// </summary>
public enum PremiumFeature
{
    AdvancedTweaks,
    ExtremeTweaks,
    GpuOverclocking
}

/// <summary>
/// The pure, side-effect-free decision layer for freemium gating: given an <see cref="AppEdition"/>, what is unlocked.
/// Deliberately split from all licence I/O so the « what does this edition allow » rules are exhaustively unit-testable
/// without a stored licence, a key file, or a network call — the same pure-core pattern as GpuOcValidation /
/// OptimizationScore / ScoreHistory. The gate is REAL: a locked feature genuinely returns false, and the only path to
/// true is <see cref="AppEdition.Premium"/>, which the licence service upstream grants solely after a verified token.
/// </summary>
public static class EntitlementPolicy
{
    /// <summary>The full premium catalogue, in declaration order — the single list the upgrade screen renders so the
    /// advertised perks are exactly the gated ones (no hand-kept second copy to drift).</summary>
    public static readonly IReadOnlyList<PremiumFeature> AllPremiumFeatures =
        (PremiumFeature[])Enum.GetValues(typeof(PremiumFeature));

    /// <summary>
    /// Is a premium capability unlocked for this edition? Today every <see cref="PremiumFeature"/> is included in
    /// Premium, so this is <c>edition == Premium</c> — but routing each call through the NAMED feature keeps the gate
    /// self-documenting at the call site and lets the catalogue drive the honest « what's included » list; a future
    /// tiered edition (e.g. a cheaper plan without GPU OC) would branch here without touching any call site.
    /// </summary>
    public static bool IsUnlocked(AppEdition edition, PremiumFeature feature)
        => edition == AppEdition.Premium;

    /// <summary>
    /// May this edition APPLY tweaks of the given tier? The free promise, made concrete: the whole
    /// <see cref="TweakTier.Tranquille"/> tier works without paying. <see cref="TweakTier.Avance"/> and
    /// <see cref="TweakTier.Extreme"/> are Premium, each routed through its own <see cref="PremiumFeature"/> so the
    /// tier gate and the capability gate cannot disagree. An unrecognised tier is treated as locked — fail safe, never
    /// silently free.
    /// </summary>
    public static bool IsTierUnlocked(AppEdition edition, TweakTier tier)
        => tier switch
        {
            TweakTier.Tranquille => true,
            TweakTier.Avance => IsUnlocked(edition, PremiumFeature.AdvancedTweaks),
            TweakTier.Extreme => IsUnlocked(edition, PremiumFeature.ExtremeTweaks),
            _ => false
        };
}

/// <summary>
/// Composes the « is licensing even active » product decision with the pure <see cref="EntitlementPolicy"/>. The app
/// ships with NO embedded public key (licensing not configured), and in that state it MUST run fully unlocked — we
/// never lock a feature behind a paywall that doesn't exist yet, and we never strip capabilities from a build that has
/// no way to buy them back. The instant the seller embeds their public key, the real edition policy takes over and the
/// Free promise (Tranquille-only) applies. Keeping this rule in ONE pure, tested place means every gated surface treats
/// « not configured » identically — there is no second copy to forget. (This does not, and cannot, defend against a
/// cracker patching the binary to report « not configured »; that is the same honest limit all client-side licensing
/// has, stated plainly elsewhere — it only guards against forging a licence.)
/// </summary>
public static class LicenseGate
{
    public static bool IsTierUnlocked(bool licensingConfigured, AppEdition edition, TweakTier tier)
        => !licensingConfigured || EntitlementPolicy.IsTierUnlocked(edition, tier);

    public static bool IsFeatureUnlocked(bool licensingConfigured, AppEdition edition, PremiumFeature feature)
        => !licensingConfigured || EntitlementPolicy.IsUnlocked(edition, feature);
}

/// <summary>
/// Splits a chosen set of tweaks into what the current edition may APPLY and what is locked behind Premium, by tier —
/// the freemium gate at the one place it must genuinely bite: the elevated apply path (and the preview that mirrors
/// it). Pure (the licence verdict is passed in) so the « which tweaks are refused » rule is unit-testable without a
/// licence service, and it routes every tweak through <see cref="LicenseGate.IsTierUnlocked"/> so the page-level gate
/// and the per-tweak gate cannot disagree. In the as-shipped, not-configured build EVERYTHING lands in Allowed — there
/// is no paid tier to withhold — so this is a no-op until a seller embeds their key.
/// </summary>
public static class TweakGate
{
    public static (IReadOnlyList<Tweak> Allowed, IReadOnlyList<Tweak> Locked) Partition(
        bool licensingConfigured, AppEdition edition, IEnumerable<Tweak> selected)
    {
        var allowed = new List<Tweak>();
        var locked = new List<Tweak>();
        foreach (var tweak in selected)
        {
            if (LicenseGate.IsTierUnlocked(licensingConfigured, edition, tweak.Tier))
                allowed.Add(tweak);
            else
                locked.Add(tweak);
        }
        return (allowed, locked);
    }
}

/// <summary>French display labels for premium features — the shared source for the upgrade screen, mirroring
/// <see cref="TweakCategoryLabels"/> so the paywall copy and any other listing of the offer can't drift apart.</summary>
public static class PremiumFeatureLabels
{
    public static string French(PremiumFeature feature) => feature switch
    {
        PremiumFeature.AdvancedTweaks => "Tweaks Avancés",
        PremiumFeature.ExtremeTweaks => "Tweaks Extrême",
        PremiumFeature.GpuOverclocking => "Overclocking GPU",
        _ => feature.ToString()
    };
}

/// <summary>
/// The French strings the freemium gate speaks when an apply path is refused or trimmed by the licence: the
/// count-based tweak messages (<see cref="AllLocked"/> / <see cref="LockedSuffix"/>) for the tier gate, and the
/// feature-based <see cref="FeatureLocked"/> for a whole Premium surface (GPU overclocking). Every gated apply
/// surface — Tweaks, Dashboard, Profiles, Snapshot, GPU OC — routes its lock copy through HERE so no two pages can
/// word the same refusal differently. This is the <see cref="PremiumFeatureLabels"/> anti-drift discipline applied to
/// the gate's runtime messages, not just its catalogue labels. Every string names the Licence onglet as the single
/// fix, so a refused user always knows the one place to act.
/// </summary>
public static class PremiumGateText
{
    /// <summary>Said when EVERY chosen tweak is Premium-only on a configured Free build, so nothing ran — honest about
    /// the count refused and where to unlock, never a silent no-op.</summary>
    public static string AllLocked(int lockedCount) =>
        $"{lockedCount} tweak(s) réservé(s) à Premium — activez une clé dans l'onglet Licence pour les appliquer.";

    /// <summary>Appended to a normal apply outcome when SOME picks were withheld, so the pinned « N appliqué(s) »
    /// contract stays intact in the common (and as-shipped) case where nothing is reserved.</summary>
    public static string LockedSuffix(int lockedCount) =>
        $" · {lockedCount} réservé(s) à Premium";

    /// <summary>
    /// Said when a whole Premium FEATURE (not a count of tweaks) is locked on a configured Free build — e.g. GPU
    /// overclocking. Pulls the feature NAME from <see cref="PremiumFeatureLabels"/> and keeps the « réservé à Premium —
    /// activez une clé dans l'onglet Licence pour … » skeleton in one place, so a renamed feature or a reworded lock
    /// can't drift between the apply-refusal and the pre-emptive banner that both speak it. <paramref name="actionClause"/>
    /// is the site-specific tail completing « …pour {clause}. » (« l'appliquer » at the refusal, « appliquer un profil »
    /// on the banner) — the part that legitimately differs by context, while the shared skeleton above cannot.
    /// </summary>
    public static string FeatureLocked(PremiumFeature feature, string actionClause) =>
        $"{PremiumFeatureLabels.French(feature)} réservé à Premium — activez une clé dans l'onglet Licence pour {actionClause}.";
}
