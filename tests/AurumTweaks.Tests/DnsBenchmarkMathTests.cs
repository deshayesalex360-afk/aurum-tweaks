using System;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="DnsBenchmarkMath"/> — the pure ranking core behind the Gaming page's DNS benchmark. The live
/// service only times UDP queries; the median, the fastest-first ordering, and the recommendation the user sees
/// are computed here. The load-bearing assertions: a resolver that never answered is honestly kept (as "—",
/// MedianMs −1) and always sorted last, the winner is simply the lowest measured median (never invented), and
/// an all-silent run yields no recommendation and says so.
/// </summary>
public class DnsBenchmarkMathTests
{
    private static DnsResolver R(string name) => new(name, name.ToLowerInvariant());
    private static DnsResolver Current(string name) => new(name, name.ToLowerInvariant(), IsCurrent: true);

    [Fact]
    public void DefaultResolvers_AreCuratedPublicServers()
    {
        Assert.NotEmpty(DnsBenchmarkMath.DefaultResolvers);
        Assert.Contains(DnsBenchmarkMath.DefaultResolvers, r => r.Address == "1.1.1.1");   // Cloudflare
        Assert.Contains(DnsBenchmarkMath.DefaultResolvers, r => r.Address == "8.8.8.8");   // Google
        Assert.All(DnsBenchmarkMath.DefaultResolvers, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
    }

    [Theory]
    [InlineData(new[] { 5.0 }, 5.0)]
    [InlineData(new[] { 1.0, 2.0, 3.0 }, 2.0)]
    [InlineData(new[] { 1.0, 2.0, 3.0, 4.0 }, 2.5)]   // even count → mean of the two middle values
    [InlineData(new[] { 3.0, 1.0, 2.0 }, 2.0)]        // unsorted input
    public void Median_HandlesOddEvenAndUnsorted(double[] values, double expected)
        => Assert.Equal(expected, DnsBenchmarkMath.Median(values), 6);

    [Fact]
    public void Median_Empty_IsZero_Defensive()
        => Assert.Equal(0, DnsBenchmarkMath.Median(Array.Empty<double>()));

    [Fact]
    public void Summarize_WithAnswers_TakesMedianAndCounts()
    {
        var result = DnsBenchmarkMath.Summarize(R("X"), new[] { 10.0, 20.0, 30.0 }, attempts: 4);

        Assert.True(result.Responded);
        Assert.Equal(20.0, result.MedianMs, 6);
        Assert.Equal(3, result.Successes);
        Assert.Equal(4, result.Attempts);
        Assert.Equal("20 ms", result.LatencyDisplay);
        Assert.Equal("3/4", result.ReliabilityDisplay);
    }

    [Fact]
    public void Summarize_NoAnswers_IsNotResponded_WithNoInventedLatency()
    {
        var result = DnsBenchmarkMath.Summarize(R("X"), Array.Empty<double>(), attempts: 4);

        Assert.False(result.Responded);
        Assert.Equal(-1, result.MedianMs);
        Assert.Equal(0, result.Successes);
        Assert.Equal("—", result.LatencyDisplay);     // never a fabricated number
        Assert.Equal("0/4", result.ReliabilityDisplay);
    }

    [Fact]
    public void Rank_OrdersRespondersFastestFirst_SilentLast()
    {
        var slow   = DnsBenchmarkMath.Summarize(R("Slow"), new[] { 30.0 }, 1);
        var fast   = DnsBenchmarkMath.Summarize(R("Fast"), new[] { 10.0 }, 1);
        var medium = DnsBenchmarkMath.Summarize(R("Medium"), new[] { 20.0 }, 1);
        var silent = DnsBenchmarkMath.Summarize(R("Silent"), Array.Empty<double>(), 1);

        var report = DnsBenchmarkMath.Rank(new[] { slow, silent, fast, medium });

        Assert.Equal(new[] { "Fast", "Medium", "Slow", "Silent" },
                     report.Ranked.Select(r => r.Resolver.Name));
        Assert.Equal("Fast", report.Fastest!.Name);
        Assert.Contains("Fast", report.Summary);
        Assert.Contains("10 ms", report.Summary);
    }

    [Fact]
    public void Rank_AllSilent_NoRecommendation_SaysSo()
    {
        var report = DnsBenchmarkMath.Rank(new[]
        {
            DnsBenchmarkMath.Summarize(R("A"), Array.Empty<double>(), 2),
            DnsBenchmarkMath.Summarize(R("B"), Array.Empty<double>(), 2),
        });

        Assert.Null(report.Fastest);
        Assert.Equal(2, report.Ranked.Count);                 // still listed, honestly, as non-responders
        Assert.Contains("Aucun résolveur", report.Summary);
    }

    [Fact]
    public void Rank_Empty_IsSafe()
    {
        var report = DnsBenchmarkMath.Rank(Array.Empty<DnsProbeResult>());

        Assert.Null(report.Fastest);
        Assert.Empty(report.Ranked);
        Assert.Contains("Aucun résolveur", report.Summary);
    }

    [Fact]
    public void DnsResolver_IsCurrent_DefaultsFalse()
        => Assert.False(new DnsResolver("X", "1.2.3.4").IsCurrent);

    [Fact]
    public void CompareToCurrent_NoCurrentResolver_ReturnsNull()
    {
        var report = DnsBenchmarkMath.Rank(new[]
        {
            DnsBenchmarkMath.Summarize(R("A"), new[] { 10.0 }, 1),
            DnsBenchmarkMath.Summarize(R("B"), new[] { 20.0 }, 1),
        });

        Assert.Null(DnsBenchmarkMath.CompareToCurrent(report));
    }

    [Fact]
    public void CompareToCurrent_CurrentIsFastest_SaysAlreadyFastest()
    {
        var report = DnsBenchmarkMath.Rank(new[]
        {
            DnsBenchmarkMath.Summarize(Current("Mine"), new[] { 10.0 }, 1),
            DnsBenchmarkMath.Summarize(R("Other"), new[] { 30.0 }, 1),
        });

        string? verdict = DnsBenchmarkMath.CompareToCurrent(report);
        Assert.Contains("déjà le plus rapide", verdict);
        Assert.Contains("10 ms", verdict);
    }

    [Fact]
    public void CompareToCurrent_FasterResolverExists_QuantifiesTheGain()
    {
        var report = DnsBenchmarkMath.Rank(new[]
        {
            DnsBenchmarkMath.Summarize(Current("Mine"), new[] { 50.0 }, 1),
            DnsBenchmarkMath.Summarize(R("Cloudflare"), new[] { 12.0 }, 1),
        });

        string? verdict = DnsBenchmarkMath.CompareToCurrent(report);
        Assert.Contains("Cloudflare", verdict);
        Assert.Contains("38 ms plus rapide", verdict);   // 50 − 12
        Assert.Contains("envisage", verdict);
    }

    [Fact]
    public void CompareToCurrent_CurrentDidNotRespond_SaysSo_WithoutInventingADelta()
    {
        var report = DnsBenchmarkMath.Rank(new[]
        {
            DnsBenchmarkMath.Summarize(Current("Mine"), Array.Empty<double>(), 4),
            DnsBenchmarkMath.Summarize(R("Other"), new[] { 20.0 }, 4),
        });

        string? verdict = DnsBenchmarkMath.CompareToCurrent(report);
        Assert.Contains("n'a pas répondu", verdict);
    }
}
