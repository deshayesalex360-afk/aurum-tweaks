using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the risky-apply confirmation rule. A profile applies in one click, so this gate is what stands between a
/// blind tap on "Charger" and the heaviest tweaks (hardware-risk, anti-cheat-risky, Extreme-tier). It must trip on
/// exactly those three axes — never on an ordinary safe set — and name what tripped it, in honest French. Pure.
/// </summary>
public class ProfileApplyRiskTests
{
    private static Tweak Tw(string id, TweakTier tier = TweakTier.Tranquille, RiskLevel risk = RiskLevel.None, bool ac = false)
        => new()
        {
            Id = id,
            Name = new() { ["fr"] = id },
            Tier = tier,
            Risk = risk,
            AntiCheat = ac ? new AntiCheatMatrix { Vanguard = AntiCheatStatus.Risky } : new AntiCheatMatrix()
        };

    [Fact]
    public void SafeSet_RequiresNoConfirmation_AndHasNoSummary()
    {
        var risk = ProfileApplyRisk.Assess(new[] { Tw("a"), Tw("b") });

        Assert.False(risk.RequiresConfirmation);
        Assert.Equal(0, risk.HardwareCount);
        Assert.Equal(0, risk.AntiCheatCount);
        Assert.Equal(0, risk.ExtremeCount);
        Assert.Equal(string.Empty, risk.Summary);
    }

    // ---- ShortLabel: the same disclosure surfaced on the profile card, before « Charger » ----

    [Fact]
    public void SafeSet_HasNoShortLabel_SoTheCardShowsNoCaution()
    {
        var risk = ProfileApplyRisk.Assess(new[] { Tw("a"), Tw("b") });
        Assert.Equal(string.Empty, risk.ShortLabel);
    }

    [Fact]
    public void ShortLabel_NamesEveryRiskAxis_WithoutTheConfirmCallToAction()
    {
        // The card caution must enumerate the same axes as the gate Summary, but never the "Confirme pour appliquer"
        // prompt — the card isn't a gate, so re-using that wording would imply an action the card can't perform.
        var risk = ProfileApplyRisk.Assess(new[]
        {
            Tw("hw", risk: RiskLevel.HardwareDamage),
            Tw("ac", ac: true),
            Tw("ex", tier: TweakTier.Extreme)
        });

        Assert.StartsWith("Attention :", risk.ShortLabel);
        Assert.Contains("matériel", risk.ShortLabel);
        Assert.Contains("anti-cheat", risk.ShortLabel);
        Assert.Contains("Extrême", risk.ShortLabel);
        Assert.DoesNotContain("Confirme", risk.ShortLabel);
    }

    [Fact]
    public void ShortLabel_AndSummary_DescribeTheSameRisks()
    {
        // Both come from one enumeration, so the card caution can never name a risk the gate omits (or vice-versa).
        var risk = ProfileApplyRisk.Assess(new[] { Tw("ac", ac: true), Tw("ex", tier: TweakTier.Extreme) });

        foreach (var token in new[] { "anti-cheat", "Extrême" })
        {
            Assert.Contains(token, risk.ShortLabel);
            Assert.Contains(token, risk.Summary);
        }
    }

    [Fact]
    public void ExtremeTier_TripsConfirmation()
    {
        var risk = ProfileApplyRisk.Assess(new[] { Tw("x", tier: TweakTier.Extreme) });

        Assert.True(risk.RequiresConfirmation);
        Assert.Equal(1, risk.ExtremeCount);
        Assert.Contains("Extrême", risk.Summary);
    }

    [Fact]
    public void AntiCheatConcern_TripsConfirmation()
    {
        var risk = ProfileApplyRisk.Assess(new[] { Tw("x", ac: true) });

        Assert.True(risk.RequiresConfirmation);
        Assert.Equal(1, risk.AntiCheatCount);
        Assert.Contains("anti-cheat", risk.Summary);
    }

    [Fact]
    public void HardwareDamageRisk_TripsConfirmation()
    {
        var risk = ProfileApplyRisk.Assess(new[] { Tw("x", risk: RiskLevel.HardwareDamage) });

        Assert.True(risk.RequiresConfirmation);
        Assert.Equal(1, risk.HardwareCount);
        Assert.Contains("matériel", risk.Summary);
    }

    [Fact]
    public void Counts_AreIndependent_AcrossCategories()
    {
        // One tweak per risky axis plus two innocuous ones: each axis counts on its own, and the summary names all three.
        var risk = ProfileApplyRisk.Assess(new[]
        {
            Tw("hw", risk: RiskLevel.HardwareDamage),
            Tw("ac", ac: true),
            Tw("ex", tier: TweakTier.Extreme),
            Tw("safe1"),
            Tw("safe2")
        });

        Assert.True(risk.RequiresConfirmation);
        Assert.Equal(1, risk.HardwareCount);
        Assert.Equal(1, risk.AntiCheatCount);
        Assert.Equal(1, risk.ExtremeCount);
        Assert.Contains("matériel", risk.Summary);
        Assert.Contains("anti-cheat", risk.Summary);
        Assert.Contains("Extrême", risk.Summary);
    }
}
