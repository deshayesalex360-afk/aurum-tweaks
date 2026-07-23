using System;
using System.Collections.Generic;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure matching for per-game GPU-OC profiles: given the user's bindings and the name of the game that
/// just became active (or went away), decide which binding — if any — applies. Kept side-effect-free so
/// the decision that drives a real GPU write is unit-testable without a running game or a live card;
/// the process launch/exit watcher and the apply/revert wiring live in the service layer.
/// </summary>
public static class GameOcMatching
{
    /// <summary>
    /// The enabled binding whose game name matches <paramref name="activeGameName"/> (case- and
    /// whitespace-insensitive), or null when nothing is bound / the name is empty. First match wins, so a
    /// caller that allows duplicates gets deterministic behavior. A disabled binding never matches.
    /// </summary>
    public static GameOcBinding? ForActiveGame(IEnumerable<GameOcBinding> bindings, string? activeGameName)
    {
        if (bindings is null || string.IsNullOrWhiteSpace(activeGameName)) return null;
        string target = activeGameName.Trim();
        foreach (var b in bindings)
        {
            if (!b.Enabled || string.IsNullOrWhiteSpace(b.GameName)) continue;
            if (string.Equals(b.GameName.Trim(), target, StringComparison.OrdinalIgnoreCase))
                return b;
        }
        return null;
    }

    /// <summary>True when the binding actually asks for a non-stock overclock — so the watcher never fires a
    /// no-op apply (all axes neutral) just because a bound game launched. Voltage is never a factor.</summary>
    public static bool IsNonStock(GameOcBinding b) =>
        b.CoreOffsetMhz != 0 || b.MemoryOffsetMhz != 0 || b.AmdMaxFreqMhz != 0 || b.AmdMaxVramFreqMhz != 0
        || b.PowerLimitPct != 100;
}
