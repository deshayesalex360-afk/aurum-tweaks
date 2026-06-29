using System;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the command-palette ranking core. The point of extracting <see cref="CommandPaletteSearch"/> from the UI
/// is that match quality is asserted here, not eyeballed in a running window — so every score tier, the French
/// accent-folding, and the deterministic tie-breaking are nailed down by value tables rather than by feel.
/// </summary>
public class CommandPaletteSearchTests
{
    private static PaletteEntry P(string title, string keywords = "")
        => new(title, title, "G", PaletteEntryKind.Page, keywords);

    // --- Normalization: lower-case + strip diacritics so a French user's un-accented typing still matches. ---

    [Theory]
    [InlineData("Réseau", "reseau")]
    [InlineData("PRIO", "prio")]
    [InlineData("Priorité & affinité", "priorite & affinite")]
    [InlineData("  Dns  ", "dns")]
    [InlineData("Mémoire virtuelle", "memoire virtuelle")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize_LowersAndStripsDiacritics(string? input, string expected)
        => Assert.Equal(expected, CommandPaletteSearch.Normalize(input));

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = CommandPaletteSearch.Normalize("Mémoire Virtuelle");
        Assert.Equal(once, CommandPaletteSearch.Normalize(once));
    }

    // --- Score tiers: each match class resolves to its exact constant (accent-insensitively). ---

    [Theory]
    [InlineData("Dns", "", "dns", CommandPaletteSearch.ScoreExact)]
    [InlineData("Réseau", "", "reseau", CommandPaletteSearch.ScoreExact)]
    [InlineData("Dashboard", "", "dash", CommandPaletteSearch.ScorePrefix)]
    [InlineData("Priorité & affinité", "", "affi", CommandPaletteSearch.ScoreWordPrefix)]
    [InlineData("Cartes réseau", "", "tes", CommandPaletteSearch.ScoreSubstring)]
    [InlineData("Dns", "cloudflare google", "google", CommandPaletteSearch.ScoreKeywordSubstring)]
    [InlineData("Cartes réseau", "", "crt", CommandPaletteSearch.ScoreSubsequence)]
    [InlineData("Dns", "resolveur cloudflare", "rslv", CommandPaletteSearch.ScoreKeywordSubsequence)]
    [InlineData("Dns", "reseau", "xyzzy", CommandPaletteSearch.ScoreNone)]
    public void Score_AssignsExpectedTier(string title, string keywords, string query, int expected)
        => Assert.Equal(expected, CommandPaletteSearch.Score(title, keywords, query));

    [Fact]
    public void Score_EmptyQuery_IsNone()
        => Assert.Equal(CommandPaletteSearch.ScoreNone, CommandPaletteSearch.Score("Dns", "", "   "));

    // --- Rank: launcher mode, filtering, and fully deterministic ordering. ---

    [Fact]
    public void Rank_EmptyQuery_ReturnsEverythingInOriginalOrder()
    {
        var entries = new[] { P("Beta"), P("Alpha"), P("Gamma") };
        var result = CommandPaletteSearch.Rank(entries, "");
        Assert.Equal(new[] { "Beta", "Alpha", "Gamma" }, result.Select(e => e.Title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rank_BlankQuery_IsLauncherMode(string? query)
    {
        var entries = new[] { P("Beta"), P("Alpha") };
        Assert.Equal(entries.Length, CommandPaletteSearch.Rank(entries, query).Count);
    }

    [Fact]
    public void Rank_DropsNonMatches()
    {
        var entries = new[] { P("Dns"), P("Power"), P("Audio") };
        var result = CommandPaletteSearch.Rank(entries, "dns");
        Assert.Equal(new[] { "Dns" }, result.Select(e => e.Title));
    }

    [Fact]
    public void Rank_ExactBeatsPrefix()
    {
        var entries = new[] { P("Dnssec"), P("Dns") };   // prefix listed first, exact second
        var result = CommandPaletteSearch.Rank(entries, "dns");
        Assert.Equal(new[] { "Dns", "Dnssec" }, result.Select(e => e.Title));
    }

    [Fact]
    public void Rank_PrefixBeatsSubstring()
    {
        var entries = new[] { P("Mydns"), P("Dnsx") };   // substring, then prefix
        var result = CommandPaletteSearch.Rank(entries, "dns");
        Assert.Equal(new[] { "Dnsx", "Mydns" }, result.Select(e => e.Title));
    }

    [Fact]
    public void Rank_SameTier_ShorterTitleFirst()
    {
        var entries = new[] { P("Powershell"), P("Power") };  // both prefix-match "pow"
        var result = CommandPaletteSearch.Rank(entries, "pow");
        Assert.Equal(new[] { "Power", "Powershell" }, result.Select(e => e.Title));
    }

    [Fact]
    public void Rank_SameTierAndLength_KeepsOriginalOrder()
    {
        var entries = new[] { P("Disk"), P("Disc") };   // both prefix "dis", both length 4
        var result = CommandPaletteSearch.Rank(entries, "dis");
        Assert.Equal(new[] { "Disk", "Disc" }, result.Select(e => e.Title));
    }

    [Theory]
    [InlineData("reseau")]
    [InlineData("réseau")]
    [InlineData("RESEAU")]
    public void Rank_IsAccentAndCaseInsensitive(string query)
    {
        var entries = new[] { P("Cartes réseau") };
        Assert.Single(CommandPaletteSearch.Rank(entries, query));
    }

    [Fact]
    public void Rank_EmptyEntries_ReturnsEmpty()
        => Assert.Empty(CommandPaletteSearch.Rank(Array.Empty<PaletteEntry>(), "dns"));

    [Fact]
    public void Rank_MatchesKeywords_WhenTitleMisses()
    {
        // "resolveur" lives only in the keywords; recall must reach it so a French synonym still finds the page.
        var entries = new[] { P("Serveurs DNS", "dns resolveur cloudflare") };
        Assert.Single(CommandPaletteSearch.Rank(entries, "resolveur"));
    }
}
