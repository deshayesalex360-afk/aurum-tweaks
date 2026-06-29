using System.Globalization;
using AurumTweaks.Converters;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the live-tile honesty rule (mirrors the monitoring paste's « 0 = capteur non lu »): a temperature/clock of
/// 0 or below means the sensor wasn't read — a running PC is never 0 °C, an active core never 0 MHz — so the tile
/// shows « — », never a fabricated « 0 ». A strictly-positive reading formats normally. Pure (the format provider
/// is passed), so the threshold and rounding are deterministic regardless of the host locale. A real 0 % LOAD must
/// NOT route through this — idle is a legitimate reading and keeps its own (unfiltered) tile.
/// </summary>
public class SensorDisplayTests
{
    [Theory]
    [InlineData(0, "—")]      // unread sensor — the fabricated-zero we refuse to print
    [InlineData(-1, "—")]     // below zero is impossible for a temp/clock → also « non lu »
    [InlineData(60, "60")]    // a real reading
    [InlineData(72, "72")]
    public void OrDash_DashesZeroAndBelow_FormatsPositive(double value, string expected)
        => Assert.Equal(expected, SensorDisplay.OrDash(value, "F0", CultureInfo.InvariantCulture));

    [Fact]
    public void OrDash_RoundsPerFormat()
        => Assert.Equal("61", SensorDisplay.OrDash(60.6, "F0", CultureInfo.InvariantCulture));

    [Fact]
    public void OrDash_HonoursArbitraryFormat()
        => Assert.Equal("2549.7", SensorDisplay.OrDash(2549.7, "F1", CultureInfo.InvariantCulture));
}
