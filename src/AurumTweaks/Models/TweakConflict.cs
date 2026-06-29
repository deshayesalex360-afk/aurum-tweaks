using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>One distinct value that some selected tweaks want to write to a shared target, and who asks for it.</summary>
public sealed record ConflictingValue(string Value, IReadOnlyList<string> TweakIds)
{
    /// <summary>Comma-joined tweak ids, for the one-line "← who wants this value" display under the value.</summary>
    public string TweakIdsLabel => string.Join(", ", TweakIds);
}

/// <summary>
/// A genuine selection conflict: two or more selected tweaks set the SAME target — the same registry value, or
/// the same service's startup type — to DIFFERENT values. Because a batch applies its operations in order, the
/// last write silently wins and the outcome depends on an apply order the user can't see. Surfacing this before
/// the batch runs is informed consent, not a guess: only registry/service ops, whose target and value are
/// deterministically identifiable, take part — shell ops we can't read back are never flagged as conflicting.
/// </summary>
public sealed record TweakConflict(string Kind, string Target, IReadOnlyList<ConflictingValue> Values);
