using System;
using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>
/// The honest result of checking whether tweaks the app once PROVED live are still live now. "Drift" = a tweak that
/// post-apply verification confirmed on (read back, value matched expected) that the machine now reports off —
/// typically undone by a Windows Update, Group Policy, an anti-cheat or another tool, not by the user. Deliberately
/// conservative: only a once-confirmed tweak can drift (a failed or never-confirmed apply has no proven-on baseline,
/// so its being off is no alarm), and a tweak we can't read back NOW is <see cref="UnverifiableCount"/>, never
/// drifted — we refuse to invent a regression we didn't witness. Pure data + display-only labels (the View binds
/// these); built by <see cref="AurumTweaks.Services.DriftAnalysis.Detect"/>. Enum-free, so it stays in Models.
/// </summary>
public sealed record DriftReport(
    IReadOnlyList<string> Drifted,
    int PersistedCount,
    int UnverifiableCount)
{
    /// <summary>The empty result: nothing was ever proven live, so there is nothing to claim about.</summary>
    public static readonly DriftReport None = new(Array.Empty<string>(), 0, 0);

    /// <summary>True only when at least one once-confirmed tweak now reads back off — the sole state the card shows.</summary>
    public bool HasDrift => Drifted.Count > 0;

    public int DriftedCount => Drifted.Count;

    /// <summary>The drifted tweak ids joined for a compact trail (the VM maps ids → localized names for the card).</summary>
    public string DriftedLabel => string.Join(", ", Drifted);
}
