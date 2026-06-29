using System;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="ProfileDuplicate"/> — the fork that turns any profile (a user profile, or an otherwise-immutable
/// preset) into a fresh, editable copy. The load-bearing assertions are the honesty edges: a duplicate gets its own
/// id (it must never overwrite its source), it is always a user profile (a forked preset is editable), its ids are a
/// detached copy (editing the fork can't mutate the original), and it never inherits the "last applied" stamp (the
/// copy has never been applied — carrying the stamp would assert an apply that never happened). The rest pin the
/// name de-duplication so repeated forks stay distinct and ordered.
/// </summary>
public class ProfileDuplicateTests
{
    private static Profile Source() => new()
    {
        Name = "Mon setup",
        Description = "Réglages perso",
        IsBuiltIn = true,                       // even forking a preset must yield a user profile
        IsCompetitiveSafe = true,
        LastAppliedUtc = DateTime.UtcNow,       // the source has been applied; the copy must NOT claim it has
        TweakIds = { "a", "b" }
    };

    // ---- The honesty edges ----

    [Fact]
    public void Clone_GivesAFreshId_NeverTheSources()
    {
        var src = Source();
        var copy = ProfileDuplicate.Clone(src, new[] { "a", "b" }, Array.Empty<string>());
        Assert.NotEqual(src.Id, copy.Id);
        Assert.False(string.IsNullOrWhiteSpace(copy.Id));
    }

    [Fact]
    public void Clone_IsAlwaysAUserProfile_EvenWhenForkingABuiltInPreset()
    {
        var copy = ProfileDuplicate.Clone(Source(), new[] { "a" }, Array.Empty<string>());
        Assert.False(copy.IsBuiltIn);           // a fork is editable — never a built-in
    }

    [Fact]
    public void Clone_CopiesIdsIntoADetachedList_SoEditingTheForkLeavesTheSourceUntouched()
    {
        var src = Source();
        var copy = ProfileDuplicate.Clone(src, src.TweakIds, Array.Empty<string>());

        Assert.Equal(new[] { "a", "b" }, copy.TweakIds);
        copy.TweakIds.Add("c");                 // mutate the fork…
        Assert.Equal(new[] { "a", "b" }, src.TweakIds);   // …the original is unaffected (not a shared reference)
    }

    [Fact]
    public void Clone_NeverInheritsTheLastAppliedStamp()
    {
        var copy = ProfileDuplicate.Clone(Source(), new[] { "a" }, Array.Empty<string>());
        Assert.Null(copy.LastAppliedUtc);       // the copy has never been applied
    }

    [Fact]
    public void Clone_PreservesDescriptionAndCompetitiveSafeFlag()
    {
        var copy = ProfileDuplicate.Clone(Source(), new[] { "a" }, Array.Empty<string>());
        Assert.Equal("Réglages perso", copy.Description);
        Assert.True(copy.IsCompetitiveSafe);    // identical id set ⇒ the safety property carries over honestly
    }

    [Fact]
    public void Clone_FreezesExactlyTheMembersHandedIn_NotTheSourcesOwnIds()
    {
        // When forking a preset the caller passes the RESOLVED snapshot, which differs from the (empty) preset id list.
        var preset = new Profile { Name = "Preset", IsBuiltIn = true };   // no explicit TweakIds
        var copy = ProfileDuplicate.Clone(preset, new[] { "x", "y", "z" }, Array.Empty<string>());
        Assert.Equal(new[] { "x", "y", "z" }, copy.TweakIds);
    }

    // ---- Name de-duplication ----

    [Fact]
    public void UniqueName_AppendsCopie_WhenNothingCollides()
        => Assert.Equal("Mon setup (copie)", ProfileDuplicate.UniqueName("Mon setup", Array.Empty<string>()));

    [Fact]
    public void UniqueName_NumbersTheCopy_WhenThePlainCopieIsTaken()
        => Assert.Equal("Mon setup (copie 2)",
                        ProfileDuplicate.UniqueName("Mon setup", new[] { "Mon setup (copie)" }));

    [Fact]
    public void UniqueName_FindsTheNextFreeNumber_SkippingEveryTakenOne()
        => Assert.Equal("Mon setup (copie 3)",
                        ProfileDuplicate.UniqueName("Mon setup", new[] { "Mon setup (copie)", "Mon setup (copie 2)" }));

    [Fact]
    public void UniqueName_IsCaseInsensitive_SoACasingVariantStillCounts()
        => Assert.Equal("Mon setup (copie 2)",
                        ProfileDuplicate.UniqueName("Mon setup", new[] { "mon setup (COPIE)" }));
}
