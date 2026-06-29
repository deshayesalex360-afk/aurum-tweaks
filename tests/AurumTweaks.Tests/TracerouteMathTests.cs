using System;
using System.Linq;
using System.Net.NetworkInformation;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="TracerouteMath"/> — the pure core behind the Gaming page's "Tracer la route" button. The
/// live service only opens sockets (increasing-TTL ICMP); every hop the user sees is built here, so this is
/// the honesty net. The load-bearing assertions: a non-responding hop stays a "*" with NO address and NO
/// latency (never an invented router), the destination-reached flag + hop count are exact, and the
/// "biggest latency jump" hint is honestly indicative — measured across consecutive RESPONDING hops only,
/// never reported when fewer than two answered and never negative.
/// </summary>
public class TracerouteMathTests
{
    private static TracerouteProbe P(IPStatus s, string? addr, long rtt) => new(s, addr, rtt);

    [Theory]
    [InlineData(IPStatus.Success, TracerouteHopStatus.Destination)]
    [InlineData(IPStatus.TtlExpired, TracerouteHopStatus.Replied)]
    [InlineData(IPStatus.TimedOut, TracerouteHopStatus.Timeout)]
    [InlineData(IPStatus.DestinationHostUnreachable, TracerouteHopStatus.Timeout)]
    [InlineData(IPStatus.Unknown, TracerouteHopStatus.Timeout)]
    public void Classify_MapsEachIpStatus(IPStatus status, TracerouteHopStatus expected)
        => Assert.Equal(expected, TracerouteMath.Classify(status));

    [Fact]
    public void Build_ReachedRoute_CountsHopsToDestination_InOrder()
    {
        var r = TracerouteMath.Build(new[]
        {
            P(IPStatus.TtlExpired, "192.168.1.1", 1),
            P(IPStatus.TtlExpired, "10.0.0.1", 8),
            P(IPStatus.Success, "1.1.1.1", 12),
        });

        Assert.True(r.DestinationReached);
        Assert.Equal(3, r.HopCount);
        Assert.Equal(new[] { 1, 2, 3 }, r.Hops.Select(h => h.Ttl).ToArray());
        Assert.Equal(TracerouteHopStatus.Destination, r.Hops[^1].Status);
        Assert.Equal("1.1.1.1", r.Hops[^1].Address);
    }

    [Fact]
    public void Build_TimeoutHop_CarriesNoInventedAddressOrLatency()
    {
        // The load-bearing honesty pin: a hop that didn't answer is "*", never a guessed router or RTT — even
        // when the socket hands back a bogus 0.0.0.0 / non-zero time for the timed-out probe.
        var r = TracerouteMath.Build(new[]
        {
            P(IPStatus.TtlExpired, "192.168.1.1", 1),
            P(IPStatus.TimedOut, "0.0.0.0", 999),
            P(IPStatus.Success, "1.1.1.1", 10),
        });

        var timeout = r.Hops[1];
        Assert.Equal(TracerouteHopStatus.Timeout, timeout.Status);
        Assert.Null(timeout.Address);
        Assert.Equal(-1, timeout.RttMs);
        Assert.Equal("*", timeout.AddressDisplay);
        Assert.Equal("—", timeout.RttDisplay);
    }

    [Fact]
    public void Build_UnreachedRoute_ReportsHopsProbed_NotReached()
    {
        var r = TracerouteMath.Build(new[]
        {
            P(IPStatus.TtlExpired, "192.168.1.1", 1),
            P(IPStatus.TimedOut, null, 0),
            P(IPStatus.TtlExpired, "10.0.0.1", 9),
        });

        Assert.False(r.DestinationReached);
        Assert.Equal(3, r.HopCount);   // probed 3 hops, never reached the destination
    }

    [Fact]
    public void Build_EmptyProbes_IsSafe_NonNullSummary()
    {
        var r = TracerouteMath.Build(Array.Empty<TracerouteProbe>());
        Assert.Empty(r.Hops);
        Assert.False(r.DestinationReached);
        Assert.Equal(0, r.HopCount);
        Assert.False(string.IsNullOrWhiteSpace(r.Summary));
    }

    [Fact]
    public void Failed_ProducesHonestEmptyReport_CarryingTheReason()
    {
        var r = TracerouteMath.Failed("Hôte introuvable.");
        Assert.Empty(r.Hops);
        Assert.False(r.DestinationReached);
        Assert.Equal(0, r.HopCount);
        Assert.Equal("Hôte introuvable.", r.Summary);
    }

    [Fact]
    public void BiggestLatencyJump_FindsLargestConsecutiveIncrease()
    {
        var hops = TracerouteMath.Build(new[]
        {
            P(IPStatus.TtlExpired, "a", 5),
            P(IPStatus.TtlExpired, "b", 8),    // +3
            P(IPStatus.TtlExpired, "c", 40),   // +32  ← biggest
            P(IPStatus.Success,    "d", 45),   // +5
        }).Hops;

        var jump = TracerouteMath.BiggestLatencyJump(hops);
        Assert.NotNull(jump);
        Assert.Equal(3, jump!.Ttl);
        Assert.Equal(32, jump.DeltaMs);
        Assert.Equal("c", jump.Address);
    }

    [Fact]
    public void BiggestLatencyJump_SkipsTimeouts_MeasuringAcrossResponders()
    {
        // The step straddles a timeout: responder b(10) → responder d(40) is the +30 jump, not broken by the gap.
        var hops = TracerouteMath.Build(new[]
        {
            P(IPStatus.TtlExpired, "a", 5),
            P(IPStatus.TtlExpired, "b", 10),
            P(IPStatus.TimedOut,   null, 0),
            P(IPStatus.Success,    "d", 40),
        }).Hops;

        var jump = TracerouteMath.BiggestLatencyJump(hops);
        Assert.NotNull(jump);
        Assert.Equal(4, jump!.Ttl);
        Assert.Equal(30, jump.DeltaMs);
    }

    [Fact]
    public void BiggestLatencyJump_NeedsTwoResponders_ElseNull()
    {
        var oneResponder = TracerouteMath.Build(new[]
        {
            P(IPStatus.TimedOut, null, 0),
            P(IPStatus.Success,  "d", 20),
        }).Hops;
        Assert.Null(TracerouteMath.BiggestLatencyJump(oneResponder));
    }

    [Fact]
    public void BiggestLatencyJump_AllDecreasing_ReturnsNull_NeverNegative()
    {
        // RTTs aren't monotonic along a real route; a route that only gets faster has no positive jump to report.
        var hops = TracerouteMath.Build(new[]
        {
            P(IPStatus.TtlExpired, "a", 40),
            P(IPStatus.TtlExpired, "b", 20),
            P(IPStatus.Success,    "c", 10),
        }).Hops;
        Assert.Null(TracerouteMath.BiggestLatencyJump(hops));
    }
}
