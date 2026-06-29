using System.Collections.Generic;

namespace AurumTweaks.Services;

/// <summary>
/// The session navigation trail behind the palette's "recent first" ordering. Pure in-memory and tiny: a bounded,
/// newest-first, de-duplicated list of page keys. No persistence (recency resets each launch — the honest scope,
/// it never claims to remember across sessions) and no I/O, so its list behaviour is unit-tested directly.
/// </summary>
public sealed class NavigationHistory : INavigationHistory
{
    // Small cap: enough to cover "the handful of pages I'm bouncing between" without the recent block dominating
    // the full, empty-query palette list.
    private const int Capacity = 8;

    private readonly List<string> _recent = new();

    public IReadOnlyList<string> Recent => _recent;

    public void Record(string pageKey)
    {
        if (string.IsNullOrWhiteSpace(pageKey)) return;
        _recent.Remove(pageKey);          // promote on re-visit instead of duplicating
        _recent.Insert(0, pageKey);
        if (_recent.Count > Capacity) _recent.RemoveRange(Capacity, _recent.Count - Capacity);
    }
}
