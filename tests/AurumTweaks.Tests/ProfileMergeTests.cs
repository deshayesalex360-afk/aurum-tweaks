using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="ProfileMerge"/> — the union behind the Profiles page's "Fusionner" tool, the companion to the
/// "Comparer" diff. <see cref="ProfileMerge.Union"/> must read as "everything in A, plus what B adds": left order
/// first, right's extras appended, no duplicate when both name the same tweak (case-insensitively, matching the
/// catalogue's identity rule), and null/blank ids ignored. <see cref="ProfileMerge.UniqueMergedName"/> names the
/// new card « A + B », disambiguating with a numbered suffix when that name is already taken.
/// </summary>
public class ProfileMergeTests
{
    [Fact]
    public void Union_KeepsLeftOrder_ThenAppendsOnlyWhatTheRightAdds()
    {
        var union = ProfileMerge.Union(new[] { "a", "b", "c" }, new[] { "b", "c", "d", "e" });

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, union);
    }

    [Fact]
    public void Union_IsCaseInsensitive_KeepingTheFirstSpellingSeen()
    {
        // "A" comes first on the left, so the union keeps that casing; "a" on the right is the same id, dropped.
        var union = ProfileMerge.Union(new[] { "A", "b" }, new[] { "a", "B", "c" });

        Assert.Equal(new[] { "A", "b", "c" }, union);
    }

    [Fact]
    public void Union_DropsDuplicatesWithinASide_Too()
    {
        var union = ProfileMerge.Union(new[] { "a", "a", "b" }, new[] { "b", "b" });

        Assert.Equal(new[] { "a", "b" }, union);
    }

    [Fact]
    public void Union_IgnoresNullAndWhitespaceIds()
    {
        var union = ProfileMerge.Union(new[] { "a", "", "  ", null! }, new[] { null!, "b", "   " });

        Assert.Equal(new[] { "a", "b" }, union);
    }

    [Fact]
    public void Union_OneEmptySide_IsJustTheOther()
    {
        Assert.Equal(new[] { "a", "b" }, ProfileMerge.Union(new[] { "a", "b" }, System.Array.Empty<string>()));
        Assert.Equal(new[] { "c", "d" }, ProfileMerge.Union(System.Array.Empty<string>(), new[] { "c", "d" }));
    }

    [Fact]
    public void Union_TwoEmptySides_IsEmpty()
    {
        Assert.Empty(ProfileMerge.Union(System.Array.Empty<string>(), System.Array.Empty<string>()));
    }

    [Fact]
    public void UniqueMergedName_WhenFree_IsPlainAPlusB()
    {
        var name = ProfileMerge.UniqueMergedName("Setup A", "Setup B", new[] { "Autre", "Encore" });

        Assert.Equal("Setup A + Setup B", name);
    }

    [Fact]
    public void UniqueMergedName_WhenTaken_AppendsTheSmallestFreeNumber()
    {
        var existing = new[] { "Setup A + Setup B", "Setup A + Setup B (2)" };

        var name = ProfileMerge.UniqueMergedName("Setup A", "Setup B", existing);

        Assert.Equal("Setup A + Setup B (3)", name);
    }

    [Fact]
    public void UniqueMergedName_IsCaseInsensitiveAgainstExistingNames()
    {
        // The store is one-file-per-name and case-insensitive, so a differently-cased clash still forces a suffix.
        var name = ProfileMerge.UniqueMergedName("Setup A", "Setup B", new[] { "setup a + setup b" });

        Assert.Equal("Setup A + Setup B (2)", name);
    }
}
