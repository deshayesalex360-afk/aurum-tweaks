using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="SnapshotVersionProvenance.CrossVersionCaveat"/> — the ONLY place a snapshot comparison invokes build
/// versions, and a load-bearing honesty rule: a comparison may warn that its two sides come from different Aurum builds
/// (so a difference might be a version change, not a real drift) ONLY when both versions are known and genuinely differ.
/// An unknown side (older snapshot / foreign file with no recorded version) or identical versions must produce NO caveat
/// — the report must never claim a version gap it can't see. Pure (value in, string-or-null out), locale-independent.
/// </summary>
public class SnapshotVersionProvenanceTests
{
    [Theory]
    [InlineData("0.1.0", "0.2.0")]
    [InlineData("0.1.0", "1.0.0")]
    public void DifferentKnownVersions_Warn_WithBothInArrowOrder(string baseline, string target)
    {
        var caveat = SnapshotVersionProvenance.CrossVersionCaveat(baseline, target);
        Assert.NotNull(caveat);
        Assert.Contains($"({baseline} → {target})", caveat);
        Assert.Contains("versions différentes", caveat);
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0")]      // same build → nothing to warn about
    [InlineData("0.1.0", "0.1.0 ")]     // a whitespace-only difference is trimmed away, not a real gap
    [InlineData("1.2.3", "1.2.3")]
    public void SameVersion_NeverWarns(string baseline, string target)
        => Assert.Null(SnapshotVersionProvenance.CrossVersionCaveat(baseline, target));

    [Theory]
    [InlineData(null, "0.2.0")]
    [InlineData("0.1.0", null)]
    [InlineData("", "0.2.0")]
    [InlineData("0.1.0", "   ")]
    [InlineData(null, null)]
    public void AnUnknownSide_NeverWarns_BecauseAGapCantBeSeen(string? baseline, string? target)
        => Assert.Null(SnapshotVersionProvenance.CrossVersionCaveat(baseline, target));
}
