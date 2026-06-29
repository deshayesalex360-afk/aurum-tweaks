using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the service start-type vocabulary that the engine's two halves must agree on. The apply path
/// (ServiceManagerService.SetStartupType) writes a Start value; the detection path (TryGetStartupType) reads
/// it back as a canonical string; on-system detection then compares that read-back against the catalog's
/// startupApply. So a catalog value is only honest if it round-trips — applies AND reads back identically.
/// The trap this guards: SetStartupType is lenient (accepts "auto", silently maps unknowns to Manual), so a
/// non-canonical value applies but never detects as applied — a dark "✓ Appliqué" badge over a live service.
/// </summary>
public class ServiceStartupTests
{
    [Theory]
    [InlineData("Boot")]
    [InlineData("System")]
    [InlineData("Automatic")]
    [InlineData("DelayedAuto")]
    [InlineData("Manual")]
    [InlineData("Disabled")]
    [InlineData("disabled")]      // case-insensitive: detection compares OrdinalIgnoreCase
    [InlineData("AUTOMATIC")]
    public void Canonical_RoundTrippableTargets_AreAccepted(string startupType)
        => Assert.True(ServiceStartup.IsCanonical(startupType));

    [Theory]
    // "auto" is the headline trap: SetStartupType maps it to Start=2, but TryGetStartupType reads that back
    // as "Automatic" — so a tweak written with "auto" applies yet detects as not-applied. Forbidden.
    [InlineData("auto")]
    [InlineData("Disbaled")]      // a typo would silently apply as Manual (SetStartupType's _ => 3 fallback)
    [InlineData("Unknown")]       // a read-only sentinel for an unexpected Start value, never a valid target
    [InlineData("2")]             // the raw registry number is not what the catalog speaks
    [InlineData("")]
    [InlineData(null)]
    public void NonCanonical_OrAliasedOrEmpty_AreRejected(string? startupType)
        => Assert.False(ServiceStartup.IsCanonical(startupType));
}
