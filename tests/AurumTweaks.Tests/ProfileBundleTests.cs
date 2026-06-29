using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="ProfileBundle"/> — the one-file backup / migration / share of every user profile. Round-trip
/// (Serialize → Parse) must preserve each profile's recognizable content, and import must obey the SAME catalogue
/// trust as the single-file path: a profile is kept only when it still resolves to a recognized tweak, unknown ids
/// are dropped, and the result is a clean user profile (fresh id, never the payload's flags). The honesty edges —
/// garbage in stays out, an all-unimportable bundle yields nothing and says so — are pinned alongside.
/// </summary>
public class ProfileBundleTests
{
    private static Tweak Tw(string id) => new() { Id = id };

    private static Profile User(string name, params string[] ids)
        => new() { Name = name, IsBuiltIn = false, TweakIds = ids.ToList() };

    [Fact]
    public void Serialize_ThenParse_RoundTripsEveryProfile_AgainstTheCatalogue()
    {
        var catalog = new[] { Tw("a"), Tw("b"), Tw("c") };
        var json = ProfileBundle.Serialize(new[] { User("Setup A", "a", "b"), User("Setup B", "b", "c") });

        var result = ProfileBundle.Parse(json, catalog);

        Assert.True(result.Ok);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(new[] { "Setup A", "Setup B" }, result.Profiles.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "a", "b" }, result.Profiles[0].TweakIds);
        Assert.Equal(new[] { "b", "c" }, result.Profiles[1].TweakIds);
    }

    [Fact]
    public void Parse_RebuildsCleanUserProfiles_WithFreshIds()
    {
        var source = User("Setup A", "a");
        var json = ProfileBundle.Serialize(new[] { source });

        var imported = Assert.Single(ProfileBundle.Parse(json, new[] { Tw("a") }).Profiles);

        Assert.False(imported.IsBuiltIn);                 // an imported profile is always a local user profile
        Assert.NotEqual(source.Id, imported.Id);          // a fresh id — never the source's local bookkeeping
        Assert.False(string.IsNullOrWhiteSpace(imported.Id));
    }

    [Fact]
    public void Parse_SkipsProfilesWithNoRecognizedTweak_ButKeepsTheRest()
    {
        // "Bad" names only ids the catalogue lacks → dropped; "Good" survives. The tally is honest about both.
        var json = ProfileBundle.Serialize(new[] { User("Good", "a"), User("Bad", "ghost1", "ghost2") });

        var result = ProfileBundle.Parse(json, new[] { Tw("a") });

        var kept = Assert.Single(result.Profiles);
        Assert.Equal("Good", kept.Name);
        Assert.Equal(1, result.SkippedCount);
        Assert.Contains("1 ignoré", result.Summary);
    }

    [Fact]
    public void Parse_DropsUnknownIdsWithinAProfile_KeepingTheRecognizedOnes()
    {
        var json = ProfileBundle.Serialize(new[] { User("Mixed", "a", "ghost", "b") });

        var kept = Assert.Single(ProfileBundle.Parse(json, new[] { Tw("a"), Tw("b") }).Profiles);

        Assert.Equal(new[] { "a", "b" }, kept.TweakIds);  // the stale id is silently dropped, the real ones survive
    }

    [Fact]
    public void Parse_AllProfilesUnimportable_YieldsNothing_AndSaysSo()
    {
        var json = ProfileBundle.Serialize(new[] { User("Bad", "ghost"), User("AlsoBad", "phantom") });

        var result = ProfileBundle.Parse(json, new[] { Tw("a") });

        Assert.False(result.Ok);
        Assert.Empty(result.Profiles);
        Assert.Equal(2, result.SkippedCount);
        Assert.Contains("Aucun profil", result.Summary);
    }

    [Fact]
    public void Parse_NeverTrustsThePayloadsBuiltInFlag()
    {
        // A hand-crafted bundle claiming a built-in profile must still import as an ordinary, editable user profile.
        const string json = """
        { "format":"aurum-profiles-bundle", "version":1,
          "profiles":[ { "name":"Sneaky", "isBuiltIn":true, "tweakIds":["a"] } ] }
        """;

        var imported = Assert.Single(ProfileBundle.Parse(json, new[] { Tw("a") }).Profiles);
        Assert.False(imported.IsBuiltIn);
    }

    [Fact]
    public void Parse_GarbageJson_IsInvalid_NotACrash()
    {
        var result = ProfileBundle.Parse("ceci n'est pas du json", new[] { Tw("a") });
        Assert.False(result.Ok);
        Assert.Contains("invalide", result.Summary);
    }

    [Fact]
    public void Parse_ProfilelessBundle_IsInvalidOrEmpty()
    {
        const string json = """{ "format":"aurum-profiles-bundle", "version":1, "profiles":[] }""";
        var result = ProfileBundle.Parse(json, new[] { Tw("a") });
        Assert.False(result.Ok);
        Assert.Empty(result.Profiles);
    }
}
