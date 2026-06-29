using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="ProfileNaming.Disambiguate"/> — the shared "pick a name no card already uses" rule behind both
/// the merge (« A + B ») and difference (« A sans B ») tools. A free name is returned untouched; a clash gets the
/// smallest « (N) », N ≥ 2, that isn't taken. Matching is case-insensitive like the one-file-per-name store, so a
/// differently-cased clash still forces a suffix rather than letting one profile overwrite another's file.
/// </summary>
public class ProfileNamingTests
{
    [Fact]
    public void Disambiguate_WhenFree_ReturnsTheBaseNameUntouched()
    {
        Assert.Equal("Setup A + Setup B",
            ProfileNaming.Disambiguate("Setup A + Setup B", new[] { "Autre", "Encore" }));
    }

    [Fact]
    public void Disambiguate_WhenTaken_AppendsTheSmallestFreeNumber()
    {
        var existing = new[] { "Base", "Base (2)", "Base (3)" };

        Assert.Equal("Base (4)", ProfileNaming.Disambiguate("Base", existing));
    }

    [Fact]
    public void Disambiguate_FillsTheLowestGap_NotJustOnePastTheHighest()
    {
        // "Base (3)" exists but "Base (2)" is free → the new card takes (2), the smallest free suffix, not (4).
        Assert.Equal("Base (2)", ProfileNaming.Disambiguate("Base", new[] { "Base", "Base (3)" }));
    }

    [Fact]
    public void Disambiguate_IsCaseInsensitiveAgainstExistingNames()
    {
        Assert.Equal("Base (2)", ProfileNaming.Disambiguate("Base", new[] { "BASE" }));
    }

    [Fact]
    public void Disambiguate_AgainstNoExistingNames_IsAlwaysTheBaseName()
    {
        Assert.Equal("Base", ProfileNaming.Disambiguate("Base", System.Array.Empty<string>()));
    }
}
