using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the maths behind the Gaming page's network readout — <see cref="NetworkRouteMath.Aggregate"/>,
/// the pure core extracted from <see cref="NetworkOptiService"/>. The live service only opens sockets;
/// every number the user actually sees (average latency, jitter, packet loss %) is computed here, so
/// this is the honesty net for "no fabricated numbers": a wrong average or a mis-scaled loss % would be
/// a lie on screen. A negative sample denotes a failed/timed-out probe (a lost packet).
///
/// Floats are asserted to 3 decimals to stay robust against benign rounding in the average/sqrt.
/// </summary>
public class NetworkRouteMathTests
{
    [Fact]
    public void Aggregate_StableLink_NoLoss_ZeroJitter()
    {
        // Eight identical RTTs — the real burst size — with no drops: mean = the value, jitter = 0, loss = 0.
        var snap = NetworkRouteMath.Aggregate(new long[] { 20, 20, 20, 20, 20, 20, 20, 20 });

        Assert.Equal(20f, snap.PingMs, 3);
        Assert.Equal(0f, snap.JitterMs, 3);
        Assert.Equal(0f, snap.PacketLossPct, 3);
        Assert.Equal(0, snap.HopCount);
    }

    [Fact]
    public void Aggregate_AverageAndJitter_ArePopulationStdDev()
    {
        // {10, 20}: mean = 15; variance = ((10-15)^2 + (20-15)^2)/2 = 25; jitter = sqrt(25) = 5.
        var snap = NetworkRouteMath.Aggregate(new long[] { 10, 20 });

        Assert.Equal(15f, snap.PingMs, 3);
        Assert.Equal(5f, snap.JitterMs, 3);   // population (÷N), not sample (÷N-1), std-dev
        Assert.Equal(0f, snap.PacketLossPct, 3);
    }

    [Fact]
    public void Aggregate_CountsLostPackets_AndExcludesThemFromLatency()
    {
        // Two good (30 ms) + two failed of four → 50% loss; latency/jitter from the good ones only.
        var snap = NetworkRouteMath.Aggregate(new long[] { 30, -1, 30, -1 });

        Assert.Equal(30f, snap.PingMs, 3);
        Assert.Equal(0f, snap.JitterMs, 3);
        Assert.Equal(50f, snap.PacketLossPct, 3);
    }

    [Fact]
    public void Aggregate_FractionalLoss_IsNotRounded()
    {
        // 3 lost of 8 = 37.5% — must not be truncated to an integer.
        var snap = NetworkRouteMath.Aggregate(new long[] { 10, 10, 10, 10, 10, -1, -1, -1 });

        Assert.Equal(10f, snap.PingMs, 3);
        Assert.Equal(37.5f, snap.PacketLossPct, 3);
    }

    [Fact]
    public void Aggregate_SingleGoodSample_HasZeroJitter_NotNaN()
    {
        // One usable sample → std-dev is undefined; we report 0, never NaN.
        var snap = NetworkRouteMath.Aggregate(new long[] { -1, 50, -1, -1 });

        Assert.Equal(50f, snap.PingMs, 3);
        Assert.Equal(0f, snap.JitterMs, 3);
        Assert.Equal(75f, snap.PacketLossPct, 3);
    }

    [Fact]
    public void Aggregate_AllProbesFailed_ReportsTotalLoss_NoLatency()
    {
        var snap = NetworkRouteMath.Aggregate(new long[] { -1, -1, -1, -1 });

        Assert.Equal(0f, snap.PingMs, 3);
        Assert.Equal(0f, snap.JitterMs, 3);
        Assert.Equal(100f, snap.PacketLossPct, 3);
        Assert.Equal(0, snap.HopCount);
    }

    [Fact]
    public void Aggregate_EmptyInput_IsSafe_NoDivideByZero()
    {
        // Degenerate (the live service never sends 0 probes): must not produce NaN/∞ loss.
        var snap = NetworkRouteMath.Aggregate(System.Array.Empty<long>());

        Assert.Equal(0f, snap.PingMs, 3);
        Assert.Equal(0f, snap.JitterMs, 3);
        Assert.Equal(0f, snap.PacketLossPct, 3);
        Assert.False(float.IsNaN(snap.PacketLossPct));
    }

    [Fact]
    public void Aggregate_NeverInventsAHopCount()
    {
        // No traceroute is performed anywhere — HopCount stays 0 regardless of the samples.
        var snap = NetworkRouteMath.Aggregate(new long[] { 12, 14, 13, 200 });
        Assert.Equal(0, snap.HopCount);
    }
}
