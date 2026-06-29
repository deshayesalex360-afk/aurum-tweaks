using System;
using AurumTweaks.Converters;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the coarse French "time since" buckets used for a profile's last-applied stamp. Pure (now is passed in),
/// so every branch — including the future-stamp clamp and the >7-day absolute-date fallback — is deterministic.
/// </summary>
public class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnderOneMinute_ReadsAtInstant()
        => Assert.Equal("à l'instant", RelativeTime.Since(Now.AddSeconds(-30), Now));

    [Fact]
    public void Minutes_AreFloored()
        => Assert.Equal("il y a 5 min", RelativeTime.Since(Now.AddMinutes(-5).AddSeconds(-40), Now));

    [Fact]
    public void Hours_AreFloored()
        => Assert.Equal("il y a 3 h", RelativeTime.Since(Now.AddHours(-3), Now));

    [Fact]
    public void Days_AreFloored()
        => Assert.Equal("il y a 2 j", RelativeTime.Since(Now.AddDays(-2), Now));

    [Fact]
    public void OverAWeek_FallsBackToAbsoluteDate()
        => Assert.Equal("31/05/2026", RelativeTime.Since(new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc), Now));

    [Fact]
    public void FutureStamp_ClampsToInstant_NeverNegative()
        => Assert.Equal("à l'instant", RelativeTime.Since(Now.AddMinutes(5), Now));
}
