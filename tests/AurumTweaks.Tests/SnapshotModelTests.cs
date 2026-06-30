using AurumTweaks.Models;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure display-only computed labels on <see cref="SystemSnapshot"/> — small functions of stored state that
/// the View binds (no change notification: a snapshot is immutable after capture). <see cref="SystemSnapshot.CaptureVersionSuffix"/>
/// is the honesty-bearing one: it surfaces the build that captured the snapshot AS A BADGE in the list, reusing the same
/// frozen AppVersion the exported report stamps (so the in-app and the pasted provenance can't disagree), and renders to
/// an EMPTY string — no orphan « · » separator — when the record carries no version, so an older / foreign snapshot
/// shows nothing rather than a guessed build.
/// </summary>
public class SnapshotModelTests
{
    [Theory]
    [InlineData("0.1.0", " · v0.1.0")]
    [InlineData("1.2.3-rc", " · v1.2.3-rc")]
    [InlineData("  0.1.0  ", " · v0.1.0")]   // a padded stored value is trimmed before badging
    public void CaptureVersionSuffix_BadgesAKnownVersion_WithSeparator(string appVersion, string expected)
        => Assert.Equal(expected, new SystemSnapshot { AppVersion = appVersion }.CaptureVersionSuffix);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CaptureVersionSuffix_IsEmpty_ForAnUnrecordedVersion_NoOrphanSeparator(string appVersion)
        => Assert.Equal(string.Empty, new SystemSnapshot { AppVersion = appVersion }.CaptureVersionSuffix);
}
