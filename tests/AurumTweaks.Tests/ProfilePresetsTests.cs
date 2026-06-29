using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the safety-bearing contract of the built-in preset catalogue (<see cref="ProfilePresets"/>,
/// extracted from <see cref="ProfileService"/>). The load-bearing promise is the
/// <see cref="Profile.IsCompetitiveSafe"/> flag: the competitive filter trusts it to tell a player a preset
/// is safe to run under Vanguard/FACEIT/EAC. So exactly ONE preset may carry it, it must be the
/// gaming-safe one, and its human-readable description must back the claim — while the *full* gaming preset
/// must NOT carry it and must warn about anti-cheat. A drifted flag or a description that no longer matches
/// the flag is an anti-cheat honesty regression, which is exactly what these tests catch.
/// </summary>
public class ProfilePresetsTests
{
    // ---- Catalogue shape: six built-in presets, all flagged built-in, unique ids ----

    [Fact]
    public void BuiltIn_HasExactlySixPresets_AllMarkedBuiltIn()
    {
        var presets = ProfilePresets.BuiltIn();
        Assert.Equal(6, presets.Count);
        Assert.All(presets, p => Assert.True(p.IsBuiltIn));
    }

    [Fact]
    public void BuiltIn_AllIdsUniqueAndNonEmpty()
    {
        var ids = ProfilePresets.BuiltIn().Select(p => p.Id).ToList();
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void BuiltIn_EveryPresetHasANameAndDescription()
        => Assert.All(ProfilePresets.BuiltIn(), p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
        });

    // ---- The anti-cheat safety flag: exactly one, and it's the gaming-safe preset ----

    [Fact]
    public void BuiltIn_ExactlyOnePresetIsCompetitiveSafe_AndItIsGamingSafe()
    {
        // Single() throws if zero or >1 carry the flag — pins "exactly one" in one shot.
        var safe = ProfilePresets.BuiltIn().Single(p => p.IsCompetitiveSafe);
        Assert.Equal("preset-gaming-safe", safe.Id);
    }

    [Fact]
    public void BuiltIn_CompetitiveSafePreset_DescriptionBacksTheClaim()
    {
        // The flag and the human-readable promise must stay in sync: the preset that claims
        // competitive-safe must name the anti-cheats it claims compatibility with.
        var safe = ProfilePresets.BuiltIn().Single(p => p.IsCompetitiveSafe);
        Assert.Contains("Vanguard", safe.Description);
        Assert.Contains("FACEIT", safe.Description);
        Assert.Contains("EAC", safe.Description);
    }

    [Fact]
    public void BuiltIn_FullGamingPreset_IsNotCompetitiveSafe_AndWarnsAboutAntiCheat()
    {
        // The honesty counterpart: the *complete* gaming preset must NOT masquerade as safe, and must
        // say so. (Its id 'preset-gaming' is distinct from the safe 'preset-gaming-safe'.)
        var full = ProfilePresets.BuiltIn().Single(p => p.Id == "preset-gaming");
        Assert.False(full.IsCompetitiveSafe);
        Assert.Contains("incompatibles", full.Description);
        Assert.Contains("anti-cheat", full.Description);
    }

    // ---- Stock = the honest "nothing applied" baseline ------------------------------

    [Fact]
    public void BuiltIn_StockPreset_ClaimsNoTweaks_AndCarriesNone()
    {
        var stock = ProfilePresets.BuiltIn().Single(p => p.Id == "preset-stock");
        Assert.False(stock.IsCompetitiveSafe);
        Assert.Empty(stock.TweakIds);
        Assert.Contains("aucun tweak", stock.Description);
    }

    // ---- The fresh-copy contract documented on BuiltIn() ----------------------------

    [Fact]
    public void BuiltIn_ReturnsFreshInstancesEachCall_NotASharedMutableList()
    {
        var a = ProfilePresets.BuiltIn();
        var b = ProfilePresets.BuiltIn();

        Assert.NotSame(a, b);          // different list instances
        Assert.NotSame(a[0], b[0]);    // and different element instances

        // Mutating one call's result must not bleed into another's.
        a[0].IsCompetitiveSafe = true;
        Assert.False(b[0].IsCompetitiveSafe);
    }
}
