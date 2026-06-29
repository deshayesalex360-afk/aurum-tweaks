using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>What a palette row points at — a navigable page, a single tweak (which deep-links into the Tweaks
/// page, pre-filtered), or a global action (a command run from anywhere, e.g. exporting the system report). Kept
/// tiny and UI-free so the ranking core stays unit-testable.</summary>
public enum PaletteEntryKind
{
    Page,
    Tweak,
    Action
}

/// <summary>
/// One destination in the command palette. <see cref="Id"/> is the page key (e.g. "Dns") for pages or the
/// tweak id for tweaks; the navigation handler dispatches on <see cref="Kind"/>. <see cref="Keywords"/> are
/// extra, non-displayed search terms (synonyms, English aliases) so a French user typing "résolution" still
/// lands on the DNS page — they widen recall without cluttering the row.
/// </summary>
public sealed record PaletteEntry(string Id, string Title, string Group, PaletteEntryKind Kind, string Keywords = "");

/// <summary>
/// The honest, accent-insensitive ranking core behind the Ctrl+K command palette. Pure and UI-free so the
/// match quality is pinned by unit tests rather than eyeballed in a running window. French is first-class:
/// every comparison runs on a diacritic-stripped, lower-cased form, so "reseau" finds "Cartes réseau" and
/// "prio" finds "Priorité &amp; affinité" — accents never hide a result the user clearly meant.
/// </summary>
public static class CommandPaletteSearch
{
    // Score tiers — deliberately wide gaps and kept free of length math so each tier is an exact, testable
    // constant. Ordering *within* a tier is settled by Rank (shorter title first, then stable original order),
    // not by fudging the score, which keeps "what tier did this match in?" a clean assertion.
    public const int ScoreExact = 1000;
    public const int ScorePrefix = 800;
    public const int ScoreWordPrefix = 600;
    public const int ScoreSubstring = 400;
    public const int ScoreKeywordSubstring = 250;
    public const int ScoreSubsequence = 100;
    public const int ScoreKeywordSubsequence = 60;
    public const int ScoreNone = 0;

    /// <summary>
    /// Rank <paramref name="entries"/> against <paramref name="query"/>, best first, dropping non-matches.
    /// An empty/whitespace query is the "launcher" case: every entry is returned in its original order so the
    /// palette opens as a full, un-reordered list of destinations. Ties (same tier) break by shorter title
    /// then original index, so the ordering is fully deterministic — no churn between identical queries.
    /// </summary>
    public static IReadOnlyList<PaletteEntry> Rank(IReadOnlyList<PaletteEntry> entries, string? query)
    {
        if (entries is null || entries.Count == 0) return Array.Empty<PaletteEntry>();

        var normalizedQuery = Normalize(query ?? string.Empty);
        if (normalizedQuery.Length == 0) return entries.ToList();

        return entries
            .Select((e, index) => (entry: e, score: ScoreNormalized(e, normalizedQuery), index))
            .Where(x => x.score > ScoreNone)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.entry.Title.Length)
            .ThenBy(x => x.index)
            .Select(x => x.entry)
            .ToList();
    }

    /// <summary>Test-friendly overload: scores a single entry against a raw (un-normalized) query.</summary>
    public static int Score(string title, string keywords, string query)
    {
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0) return ScoreNone;
        return ScoreNormalized(new PaletteEntry(string.Empty, title, string.Empty, PaletteEntryKind.Page, keywords), normalizedQuery);
    }

    private static int ScoreNormalized(PaletteEntry entry, string normalizedQuery)
    {
        var title = Normalize(entry.Title);

        if (title.Length == 0) { /* fall through to keywords */ }
        else if (title == normalizedQuery) return ScoreExact;
        else if (title.StartsWith(normalizedQuery, StringComparison.Ordinal)) return ScorePrefix;
        else if (AnyWordStartsWith(title, normalizedQuery)) return ScoreWordPrefix;
        else if (title.Contains(normalizedQuery, StringComparison.Ordinal)) return ScoreSubstring;

        var keywords = Normalize(entry.Keywords);
        if (keywords.Length > 0 && keywords.Contains(normalizedQuery, StringComparison.Ordinal))
            return ScoreKeywordSubstring;

        if (title.Length > 0 && IsSubsequence(title, normalizedQuery)) return ScoreSubsequence;
        if (keywords.Length > 0 && IsSubsequence(keywords, normalizedQuery)) return ScoreKeywordSubsequence;

        return ScoreNone;
    }

    /// <summary>Lower-case and strip diacritics so accented French text matches an un-accented query (and vice
    /// versa). Idempotent, so callers may normalize freely without compounding the transform.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var decomposed = s.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // True when any whitespace/punctuation-delimited word in the (already normalized) haystack starts with the
    // query — so "affi" reaches the second word of "priorite & affinite", not just title-leading matches.
    private static bool AnyWordStartsWith(string normalizedHaystack, string normalizedQuery)
    {
        var atWordStart = true;
        for (var i = 0; i < normalizedHaystack.Length; i++)
        {
            var c = normalizedHaystack[i];
            var isSeparator = !char.IsLetterOrDigit(c);
            if (isSeparator) { atWordStart = true; continue; }
            if (atWordStart)
            {
                if (i + normalizedQuery.Length <= normalizedHaystack.Length &&
                    string.CompareOrdinal(normalizedHaystack, i, normalizedQuery, 0, normalizedQuery.Length) == 0)
                    return true;
                atWordStart = false;
            }
        }
        return false;
    }

    // Two-pointer subsequence test: every char of needle appears in haystack, in order (not necessarily
    // contiguous). The loosest, last-resort tier — it forgives missing letters ("crt" → "Cartes réseau").
    private static bool IsSubsequence(string haystack, string needle)
    {
        if (needle.Length == 0) return false;
        if (needle.Length > haystack.Length) return false;
        var n = 0;
        foreach (var c in haystack)
        {
            if (c == needle[n] && ++n == needle.Length) return true;
        }
        return false;
    }
}
