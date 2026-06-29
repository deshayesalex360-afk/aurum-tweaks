using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins how a profile resolves to its concrete tweak set (<see cref="ProfileComposition.Resolve"/>) — the data
/// that the Profiles page's "Charger" button now actually applies. The load-bearing assertion is the
/// competitive-safety one: <c>preset-gaming-safe</c> must resolve to ZERO tweaks carrying any anti-cheat
/// concern (the same ban-risk promise <see cref="ProfilePresetsTests"/> guards on the metadata side, here
/// enforced on the real apply set). The rest pin the honesty edges: Stock and unknown ids resolve to nothing
/// (never a guessed batch), and a user profile's stale ids are skipped rather than crashed on.
/// </summary>
public class ProfileCompositionTests
{
    private static Tweak Tw(string id, TweakTier tier = TweakTier.Tranquille, RiskLevel risk = RiskLevel.None,
                            TweakCategory cat = TweakCategory.PerformanceMultimedia, bool acConcern = false)
        => new()
        {
            Id = id,
            Tier = tier,
            Risk = risk,
            Category = cat,
            AntiCheat = acConcern ? new AntiCheatMatrix { Vanguard = AntiCheatStatus.Risky } : new AntiCheatMatrix()
        };

    private static Profile Preset(string id) => new() { Id = id, IsBuiltIn = true };

    // ---- The honesty edges: nothing fabricated ----

    [Fact]
    public void Stock_ResolvesToNothing()
    {
        var catalog = new[] { Tw("a"), Tw("b", TweakTier.Extreme) };
        Assert.Empty(ProfileComposition.Resolve(Preset("preset-stock"), catalog));
    }

    [Fact]
    public void UnknownPresetId_ResolvesToNothing()
    {
        // An id we don't define must apply nothing — never a guessed batch.
        var catalog = new[] { Tw("a"), Tw("b") };
        Assert.Empty(ProfileComposition.Resolve(Preset("preset-does-not-exist"), catalog));
    }

    [Fact]
    public void Extreme_ResolvesToEveryTweak()
    {
        var catalog = new[] { Tw("a"), Tw("b", TweakTier.Extreme, RiskLevel.High), Tw("c", acConcern: true) };
        var ids = ProfileComposition.Resolve(Preset("preset-extreme"), catalog).Select(t => t.Id);
        Assert.Equal(new[] { "a", "b", "c" }, ids);
    }

    [Fact]
    public void Tranquille_ResolvesToExactlyTheTranquilleTier()
    {
        var catalog = new[]
        {
            Tw("t1", TweakTier.Tranquille),
            Tw("a1", TweakTier.Avance),
            Tw("t2", TweakTier.Tranquille),
            Tw("e1", TweakTier.Extreme)
        };
        var ids = ProfileComposition.Resolve(Preset("preset-tranquille"), catalog).Select(t => t.Id);
        Assert.Equal(new[] { "t1", "t2" }, ids);
    }

    // ---- The ban-risk promise: the competitive-safe preset never carries an anti-cheat concern ----

    [Fact]
    public void GamingSafe_ExcludesEveryAntiCheatConcern()
    {
        // A tweak that would otherwise qualify (Tranquille, no risk) but touches an anti-cheat MUST NOT appear.
        var catalog = new[]
        {
            Tw("clean", TweakTier.Tranquille),
            Tw("risky", TweakTier.Tranquille, acConcern: true)
        };
        var ids = ProfileComposition.Resolve(Preset("preset-gaming-safe"), catalog).Select(t => t.Id).ToList();
        Assert.Equal(new[] { "clean" }, ids);
        Assert.DoesNotContain("risky", ids);
    }

    [Fact]
    public void GamingSafe_ExcludesHighRiskAndExtremeTier()
    {
        var catalog = new[]
        {
            Tw("ok", TweakTier.Avance, RiskLevel.Medium),
            Tw("highrisk", TweakTier.Avance, RiskLevel.High),
            Tw("extreme", TweakTier.Extreme, RiskLevel.Low)
        };
        var ids = ProfileComposition.Resolve(Preset("preset-gaming-safe"), catalog).Select(t => t.Id);
        Assert.Equal(new[] { "ok" }, ids);
    }

    // ---- The full gaming set: anti-cheat-risky allowed (it warns), hardware-damaging never ----

    [Fact]
    public void FullGaming_IncludesAntiCheatRisky_ButNotHardwareDamage_NorOffTopicCategories()
    {
        var catalog = new[]
        {
            Tw("g-risky", TweakTier.Avance, RiskLevel.High, TweakCategory.Gaming, acConcern: true),
            Tw("net", TweakTier.Avance, RiskLevel.Low, TweakCategory.NetworkLatency),
            Tw("danger", TweakTier.Extreme, RiskLevel.HardwareDamage, TweakCategory.Gaming),
            Tw("privacy", TweakTier.Tranquille, RiskLevel.None, TweakCategory.PrivacyTelemetry)
        };
        var ids = ProfileComposition.Resolve(Preset("preset-gaming"), catalog).Select(t => t.Id).ToList();
        Assert.Contains("g-risky", ids);                 // anti-cheat-risky is allowed in the FULL gaming set
        Assert.Contains("net", ids);
        Assert.DoesNotContain("danger", ids);            // hardware-damaging is never one-click applied
        Assert.DoesNotContain("privacy", ids);           // not a gaming-bearing category
    }

    // ---- User profile: explicit ids, catalogue order, stale ids skipped ----

    [Fact]
    public void UserProfile_ResolvesExplicitIds_InCatalogOrder_SkippingMissing()
    {
        var catalog = new[] { Tw("a"), Tw("b"), Tw("c") };
        var profile = new Profile
        {
            IsBuiltIn = false,
            TweakIds = { "c", "missing", "a" }      // out of order + a stale id that no longer exists
        };
        var ids = ProfileComposition.Resolve(profile, catalog).Select(t => t.Id);
        Assert.Equal(new[] { "a", "c" }, ids);       // catalogue order, stale id silently dropped
    }

    // ---- Composition summary: the honest "what's inside" each card shows before « Charger » ----

    [Fact]
    public void Summarize_CountsResolvedMembers_SplitByTier()
    {
        var catalog = new[]
        {
            Tw("t1", TweakTier.Tranquille),
            Tw("t2", TweakTier.Tranquille),
            Tw("a1", TweakTier.Avance),
            Tw("e1", TweakTier.Extreme)
        };
        var summary = ProfileComposition.Summarize(Preset("preset-extreme"), catalog);

        Assert.Equal(4, summary.Total);
        Assert.Equal(2, summary.Tranquille);
        Assert.Equal(1, summary.Avance);
        Assert.Equal(1, summary.Extreme);
        Assert.False(summary.IsEmpty);
    }

    [Fact]
    public void Summarize_Label_NamesOnlyTheTiersThatOccur()
    {
        // preset-tranquille resolves to exactly the two tranquille tweaks — no « avancé »/« extrême » clause invented.
        var catalog = new[] { Tw("t1", TweakTier.Tranquille), Tw("t2", TweakTier.Tranquille) };
        var summary = ProfileComposition.Summarize(Preset("preset-tranquille"), catalog);

        Assert.Equal("2 tweak(s) · 2 tranquille", summary.Label);
        Assert.DoesNotContain("avancé", summary.Label);
        Assert.DoesNotContain("extrême", summary.Label);
    }

    [Fact]
    public void Summarize_Label_ListsEveryPresentTier_InTierOrder()
    {
        var catalog = new[]
        {
            Tw("t1", TweakTier.Tranquille),
            Tw("a1", TweakTier.Avance),
            Tw("e1", TweakTier.Extreme)
        };
        var summary = ProfileComposition.Summarize(Preset("preset-extreme"), catalog);

        Assert.Equal("3 tweak(s) · 1 tranquille / 1 avancé / 1 extrême", summary.Label);
    }

    [Fact]
    public void Summarize_EmptyProfile_IsHonestlyEmpty_NotAFabricatedSet()
    {
        // Stock resolves to nothing — the card must say so, never imply a phantom batch.
        var summary = ProfileComposition.Summarize(Preset("preset-stock"), new[] { Tw("a"), Tw("b") });

        Assert.True(summary.IsEmpty);
        Assert.Equal(0, summary.Total);
        Assert.Equal("Aucun tweak", summary.Label);
    }

    [Fact]
    public void Summarize_MembersOverload_TalliesAnAlreadyResolvedSet()
    {
        // The members overload exists so the Profiles page can Resolve ONCE and feed the same list to Summarize and
        // to ProfileApplyRisk.Assess — it must tally exactly what it's handed (no re-resolution against a catalogue).
        var members = new[] { Tw("t1", TweakTier.Tranquille), Tw("a1", TweakTier.Avance), Tw("e1", TweakTier.Extreme) };
        var summary = ProfileComposition.Summarize(members);

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Tranquille);
        Assert.Equal(1, summary.Avance);
        Assert.Equal(1, summary.Extreme);
    }
}
