using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>
/// The honest result of re-probing a batch right after an apply or a revert, split into the three claims we can
/// truthfully make. The labels are intentionally outcome-neutral so the one record serves both directions:
/// <list type="bullet">
/// <item><see cref="Confirmed"/> — the machine read back exactly the state we were after (live after an apply via
///   <c>TweakVerifier</c>; gone after a revert via <c>RevertVerifier</c>).</item>
/// <item><see cref="Unconfirmed"/> — the alarming case the engine called a success but the readback contradicts: a
///   change that "didn't stick" after apply, or one "still active" after revert. Never dressed up as a green check.</item>
/// <item><see cref="Unverifiable"/> — can't be read back at all (the shell-only ops), so we make no claim either way.</item>
/// </list>
/// The distinction is the whole point: a fabricated "✓ verified" over a change that didn't take — or a "tout
/// restauré" over one still on the machine — is exactly the dishonest indicator the mandate forbids. Each list
/// preserves the batch order. The two folds that feed this differ on purpose (see <c>TweakDetection.Aggregate</c>
/// vs <c>AggregateAfterRevert</c>) so a revert never lets one off op mask a sibling that is still active.
/// </summary>
public sealed record VerificationReport(
    IReadOnlyList<string> Confirmed,
    IReadOnlyList<string> Unconfirmed,
    IReadOnlyList<string> Unverifiable)
{
    /// <summary>True only when at least one tweak contradicted the engine's report — the alarming case to surface.</summary>
    public bool HasUnconfirmed => Unconfirmed.Count > 0;

    /// <summary>Comma-joined ids of the contradicting tweaks, for the one-line warning under the status.</summary>
    public string UnconfirmedLabel => string.Join(", ", Unconfirmed);
}
