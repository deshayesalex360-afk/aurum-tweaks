using System.Collections.Generic;
using System.Linq;

namespace AurumTweaks.Services;

/// <summary>
/// Reorders an empty-query palette list so the pages the user most recently visited come first — the "recent
/// first" feel of VS Code's Ctrl+P / Linear's palette, fitted to a 37-page app. Pure and UI-free so the ordering
/// is pinned by unit tests. Only <see cref="PaletteEntryKind.Page"/> rows are promotable (recency is page-nav
/// history); action and tweak rows keep their place. Everything not recently visited keeps its original relative
/// order, so the tail of the list is exactly the catalog order it had before.
/// </summary>
public static class PaletteRecency
{
    /// <summary>
    /// Return <paramref name="entries"/> with the recently-visited pages moved to the front in recency order
    /// (<paramref name="recentKeys"/> is newest-first). A recent key with no matching page entry is simply
    /// ignored, so a stale or bogus recorded key can never drop, duplicate, or reorder anything it doesn't name.
    /// The result is always a permutation of the input — same entries, no additions or removals.
    /// </summary>
    public static IReadOnlyList<PaletteEntry> PrioritizeRecent(
        IReadOnlyList<PaletteEntry> entries, IReadOnlyList<string> recentKeys)
    {
        if (entries is null || entries.Count == 0) return entries ?? (IReadOnlyList<PaletteEntry>)new List<PaletteEntry>();
        if (recentKeys is null || recentKeys.Count == 0) return entries;

        // Rank each recent key by recency (0 = most recent); first occurrence wins if a key somehow repeats.
        var rank = new Dictionary<string, int>();
        for (var i = 0; i < recentKeys.Count; i++)
            rank.TryAdd(recentKeys[i], i);

        // Pull out the promotable rows (pages whose id is recent), remembering their source index so the
        // non-recent remainder can be emitted in its original order without any value-equality ambiguity.
        var promotable = entries
            .Select((entry, index) => (entry, index))
            .Where(x => x.entry.Kind == PaletteEntryKind.Page && rank.ContainsKey(x.entry.Id))
            .ToList();

        if (promotable.Count == 0) return entries;

        var promoted = promotable.OrderBy(x => rank[x.entry.Id]).Select(x => x.entry);
        var promotedIndices = promotable.Select(x => x.index).ToHashSet();
        var rest = entries.Where((_, i) => !promotedIndices.Contains(i));

        return promoted.Concat(rest).ToList();
    }
}
