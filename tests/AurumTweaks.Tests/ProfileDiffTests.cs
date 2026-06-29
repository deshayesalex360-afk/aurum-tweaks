using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="ProfileDiff"/> — the set-difference behind the Profiles page's "Comparer deux profils" tool.
/// The three buckets (shared / left-only / right-only) must partition the inputs honestly, in input order, with
/// case-insensitive de-duplication matching the catalogue's identity rule; "identical" means neither side holds
/// anything the other lacks. The human summary names both profiles and tells the truth about the tally.
/// </summary>
public class ProfileDiffTests
{
    [Fact]
    public void Compare_PartitionsIntoShared_LeftOnly_RightOnly_InInputOrder()
    {
        var diff = ProfileDiff.Compare(new[] { "a", "b", "c" }, new[] { "b", "c", "d" });

        Assert.Equal(new[] { "a" }, diff.OnlyInLeft);
        Assert.Equal(new[] { "d" }, diff.OnlyInRight);
        Assert.Equal(new[] { "b", "c" }, diff.Shared);
        Assert.False(diff.Identical);
    }

    [Fact]
    public void Compare_SameSetInAnyOrder_IsIdentical_WithEverythingShared()
    {
        var diff = ProfileDiff.Compare(new[] { "a", "b", "c" }, new[] { "c", "a", "b" });

        Assert.True(diff.Identical);
        Assert.Empty(diff.OnlyInLeft);
        Assert.Empty(diff.OnlyInRight);
        Assert.Equal(new[] { "a", "b", "c" }, diff.Shared);   // shared keeps the LEFT input order
    }

    [Fact]
    public void Compare_IsCaseInsensitive_AndDropsDuplicatesWithinASide()
    {
        var diff = ProfileDiff.Compare(new[] { "A", "a", "b" }, new[] { "B", "c" });

        Assert.Equal(new[] { "A" }, diff.OnlyInLeft);     // "a" is the same id as "A", de-duped to the first seen
        Assert.Equal(new[] { "b" }, diff.Shared);          // matches "B" case-insensitively
        Assert.Equal(new[] { "c" }, diff.OnlyInRight);
    }

    [Fact]
    public void Compare_IgnoresNullAndWhitespaceIds()
    {
        var diff = ProfileDiff.Compare(new[] { "a", "", "  ", null! }, new[] { "a" });

        Assert.True(diff.Identical);
        Assert.Equal(new[] { "a" }, diff.Shared);
    }

    [Fact]
    public void Compare_OneEmptySide_PutsEverythingInTheOther()
    {
        var diff = ProfileDiff.Compare(new[] { "a", "b" }, System.Array.Empty<string>());

        Assert.Equal(new[] { "a", "b" }, diff.OnlyInLeft);
        Assert.Empty(diff.Shared);
        Assert.Empty(diff.OnlyInRight);
        Assert.False(diff.Identical);
    }

    [Fact]
    public void Compare_TwoEmptySides_IsIdentical()
    {
        var diff = ProfileDiff.Compare(System.Array.Empty<string>(), System.Array.Empty<string>());
        Assert.True(diff.Identical);
    }

    [Fact]
    public void Summarize_WhenIdentical_SaysSo_WithTheSharedCount()
    {
        var diff = ProfileDiff.Compare(new[] { "a", "b" }, new[] { "a", "b" });
        var text = ProfileDiff.Summarize(diff, "Setup A", "Setup B");

        Assert.Contains("exactement les mêmes", text);
        Assert.Contains("Setup A", text);
        Assert.Contains("Setup B", text);
        Assert.Contains("2", text);
    }

    [Fact]
    public void Summarize_WhenDifferent_TalliesSharedAndEachSidesExtras()
    {
        var diff = ProfileDiff.Compare(new[] { "a", "b" }, new[] { "b", "c" });
        var text = ProfileDiff.Summarize(diff, "Setup A", "Setup B");

        Assert.Contains("1 en commun", text);
        Assert.Contains("1 propre(s) à « Setup A »", text);
        Assert.Contains("1 propre(s) à « Setup B »", text);
    }
}
