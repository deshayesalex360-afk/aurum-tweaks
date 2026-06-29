using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="DnsBenchmarkTextReport"/> — the shareable « Benchmark DNS » paste produced on the « Serveurs DNS »
/// page. Honesty contract: a run that never executed prints « Aucun benchmark exécuté » (never an empty table that
/// reads as a result); the ranking is faithfully fastest-first using the tested <see cref="DnsProbeResult"/> display
/// members, so a resolver that did not answer shows « — » and its loss ratio rather than an invented latency; the user's
/// own resolver carries the « (actuel) » marker and the shared <see cref="DnsBenchmarkMath.CompareToCurrent"/> line; and
/// the load-bearing footer caveat — a faster DNS speeds up NAME RESOLUTION, not the in-game ping, and the change stays
/// reversible — always travels with the paste so a « le plus rapide » can never be read as a latency/FPS gain.
/// Latencies are F0 integers (no separators), so the asserted text is locale-independent.
/// </summary>
public class DnsBenchmarkTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 22, 14, 30, 0, DateTimeKind.Utc);

    private static DnsProbeResult Probe(
        string name, string addr, double medianMs, bool current = false,
        bool responded = true, int successes = 4, int attempts = 4)
        => new(new DnsResolver(name, addr, current), responded, responded ? medianMs : -1, successes, attempts);

    private static string Render(DnsBenchmarkReport report) => DnsBenchmarkTextReport.Render(report, When);

    [Fact]
    public void Header_CarriesTitle()
        => Assert.Contains("Aurum Tweaks — Benchmark DNS",
            Render(DnsBenchmarkMath.Rank(new[] { Probe("Cloudflare", "1.1.1.1", 12) })));

    [Fact]
    public void NoRun_SaysSo_WithoutFabricatingARanking()
    {
        var text = DnsBenchmarkTextReport.Render(null, When);
        Assert.Contains("Aucun benchmark exécuté", text);
        Assert.DoesNotContain("CLASSEMENT", text);
        Assert.DoesNotContain("VERDICT", text);
    }

    [Fact]
    public void EmptyRanking_AlsoReadsAsNoRun_NeverAnEmptyTable()
    {
        var empty = new DnsBenchmarkReport(Array.Empty<DnsProbeResult>(), null, "");
        Assert.Contains("Aucun benchmark exécuté", Render(empty));
    }

    [Fact]
    public void Ranking_ListsResolvers_FastestFirst_WithLatencyAndReliability()
    {
        var report = DnsBenchmarkMath.Rank(new[]
        {
            Probe("Google", "8.8.8.8", 30),
            Probe("Cloudflare", "1.1.1.1", 12),
        });
        var text = Render(report);
        Assert.Contains("CLASSEMENT", text);
        Assert.Contains("Cloudflare", text);
        Assert.Contains("1.1.1.1", text);
        Assert.Contains("12 ms", text);
        Assert.Contains("4/4", text);
        // Render preserves the fastest-first order: Cloudflare (12 ms) before Google (30 ms).
        Assert.True(text.IndexOf("Cloudflare", StringComparison.Ordinal)
                  < text.IndexOf("Google", StringComparison.Ordinal));
    }

    [Fact]
    public void NonResponder_ShownAsDash_NeverAFakeTime()
    {
        var report = DnsBenchmarkMath.Rank(new[]
        {
            Probe("Cloudflare", "1.1.1.1", 12),
            Probe("OpenDNS", "208.67.222.222", 0, responded: false, successes: 0, attempts: 4),
        });
        var text = Render(report);
        Assert.Contains("OpenDNS", text);
        Assert.Contains("0/4", text);             // the row rendered, and shows it answered nothing
        Assert.DoesNotContain("-1", text);        // the «didn't answer» sentinel never leaks as a number
        Assert.DoesNotContain("0 ms", text);      // nor a fabricated zero latency
    }

    [Fact]
    public void CurrentResolver_IsMarked_Actuel()
    {
        var report = DnsBenchmarkMath.Rank(new[]
        {
            Probe("Cloudflare", "1.1.1.1", 12),
            Probe("DNS actuel", "192.168.1.1", 40, current: true),
        });
        Assert.Contains("(actuel)", Render(report));
    }

    [Fact]
    public void Verdict_NamesTheWinner_AndCarriesTheCurrentVsFastestComparison()
    {
        var report = DnsBenchmarkMath.Rank(new[]
        {
            Probe("Cloudflare", "1.1.1.1", 12),
            Probe("DNS actuel", "192.168.1.1", 40, current: true),
        });
        var text = Render(report);
        Assert.Contains("VERDICT", text);
        Assert.Contains("Le plus rapide : Cloudflare", text);
        Assert.Contains("plus rapide que ton DNS", text);   // the shared CompareToCurrent line travels with the paste
    }

    [Fact]
    public void Footer_KeepsTheLoadBearingCaveat_NameResolutionNotPing_AndReversible()
    {
        var text = Render(DnsBenchmarkMath.Rank(new[] { Probe("Cloudflare", "1.1.1.1", 12) }));
        Assert.Contains("RÉSOLUTION DES NOMS", text);
        Assert.Contains("pas le ping en partie", text);
        Assert.Contains("réversible", text);
    }
}
