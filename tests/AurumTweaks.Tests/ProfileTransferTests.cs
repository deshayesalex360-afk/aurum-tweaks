using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the export/import trust boundary. A profile file is untrusted input (another machine, a forum, a
/// hand-edit), so <see cref="ProfileTransfer.Parse"/> must reconcile it against the LIVE catalogue and never let
/// a crafted payload claim more than it earned: no built-in/AC-safe masquerade, no reused id silently
/// overwriting a local profile, no storing ids this build doesn't know. The portable payload is also minimal —
/// it carries no local bookkeeping. Pure (no I/O, no dialog).
/// </summary>
public class ProfileTransferTests
{
    private static Tweak T(string id) => new() { Id = id, Name = new() { ["fr"] = id } };
    private static IReadOnlyList<Tweak> Catalog(params string[] ids) => ids.Select(T).ToList();

    [Fact]
    public void Serialize_ThenParse_RoundTripsName_Description_AndRecognizedIds()
    {
        var original = new Profile { Name = "Mon setup", Description = "FPS", TweakIds = { "a", "b" } };

        var json = ProfileTransfer.Serialize(original);
        var import = ProfileTransfer.Parse(json, Catalog("a", "b"));

        Assert.True(import.Ok);
        Assert.NotNull(import.Profile);
        Assert.Equal("Mon setup", import.Profile!.Name);
        Assert.Equal("FPS", import.Profile.Description);
        Assert.Equal(new[] { "a", "b" }, import.Profile.TweakIds);
    }

    [Fact]
    public void Serialize_CarriesNoLocalBookkeeping()
    {
        // The shared format is name/description/tweakIds ONLY — never the local id, flags or timestamps.
        var json = ProfileTransfer.Serialize(new Profile { Name = "X", TweakIds = { "a" } });

        Assert.DoesNotContain("isBuiltIn", json);
        Assert.DoesNotContain("isCompetitiveSafe", json);
        Assert.DoesNotContain("createdUtc", json);
        Assert.DoesNotContain("\"id\"", json);
    }

    [Fact]
    public void Parse_KeepsRecognizedIds_DropsUnknown_AndReportsThem()
    {
        var json = ProfileTransfer.Serialize(new Profile { Name = "Partagé", TweakIds = { "a", "ghost1", "b", "ghost2" } });

        var import = ProfileTransfer.Parse(json, Catalog("a", "b"));   // catalogue has only a, b

        Assert.True(import.Ok);
        Assert.Equal(new[] { "a", "b" }, import.Profile!.TweakIds);    // stale ids never stored
        Assert.Equal(2, import.RecognizedCount);
        Assert.Equal(new[] { "ghost1", "ghost2" }, import.UnknownIds);
        Assert.Contains("2 ignoré(s)", import.Summary);                // and the drop is admitted, not hidden
    }

    [Fact]
    public void Parse_AllUnknownIds_IsRefused_WithNothingToImport()
    {
        var json = ProfileTransfer.Serialize(new Profile { Name = "Étranger", TweakIds = { "x", "y" } });

        var import = ProfileTransfer.Parse(json, Catalog("a", "b"));

        Assert.False(import.Ok);                                       // never save a profile that applies nothing here
        Assert.Null(import.Profile);
        Assert.Contains("rien à importer", import.Summary);
    }

    [Fact]
    public void Parse_NeverTrustsBuiltInOrCompetitiveSafeFlags()
    {
        // A hand-crafted file claims to be a trusted, AC-safe preset. The importer must strip both: an imported
        // profile is always a plain user bundle, so it can't masquerade as a preset nor wear an unearned badge.
        const string json = "{ \"name\": \"Faux preset\", \"tweakIds\": [\"a\"], \"isBuiltIn\": true, \"isCompetitiveSafe\": true }";

        var import = ProfileTransfer.Parse(json, Catalog("a"));

        Assert.True(import.Ok);
        Assert.False(import.Profile!.IsBuiltIn);
        Assert.False(import.Profile.IsCompetitiveSafe);
    }

    [Fact]
    public void Parse_MintsFreshId_NeverReusesPayloadId()
    {
        const string json = "{ \"id\": \"shared-123\", \"name\": \"X\", \"tweakIds\": [\"a\"] }";

        var import = ProfileTransfer.Parse(json, Catalog("a"));

        Assert.True(import.Ok);
        Assert.NotEqual("shared-123", import.Profile!.Id);             // a fresh local id can't overwrite an existing one
        Assert.False(string.IsNullOrWhiteSpace(import.Profile.Id));
    }

    [Fact]
    public void Parse_MalformedJson_IsRejectedHonestly()
    {
        var import = ProfileTransfer.Parse("{ this is not json", Catalog("a"));

        Assert.False(import.Ok);
        Assert.Null(import.Profile);
        Assert.Contains("invalide", import.Summary);
    }

    [Fact]
    public void Parse_BlankName_IsRejected()
    {
        const string json = "{ \"name\": \"   \", \"tweakIds\": [\"a\"] }";

        var import = ProfileTransfer.Parse(json, Catalog("a"));

        Assert.False(import.Ok);
    }
}
