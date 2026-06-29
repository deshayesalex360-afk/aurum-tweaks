using System;
using System.Collections.Generic;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="NetworkDiagnosticReport"/> — the shareable « Diagnostic réseau » paste. The honesty contract: a step
/// the user hasn't run prints « non mesuré » with its action (never a fabricated zero), a non-responding hop/resolver
/// shows « * » / « — » (never an invented address or time), and the stability line carries the worst-component grade and
/// its indicative caveat. Numbers are fr-FR-formatted by the renderer, so the asserted decimals are locale-independent.
/// </summary>
public class NetworkDiagnosticReportTests
{
    private static readonly DateTime When = new(2026, 6, 21, 14, 30, 0, DateTimeKind.Utc);

    private static TracerouteHop Replied(int ttl, string addr, long rtt) => new(ttl, addr, rtt, TracerouteHopStatus.Replied);
    private static TracerouteHop Timeout(int ttl) => new(ttl, null, -1, TracerouteHopStatus.Timeout);

    private static DnsProbeResult Dns(string name, string addr, bool responded, double median, int ok, int total, bool current = false) =>
        new(new DnsResolver(name, addr, current), responded, median, ok, total);

    private static string Render(
        string? target = "1.1.1.1",
        NetworkRouteSnapshot? route = null,
        IReadOnlyList<TracerouteHop>? hops = null,
        IReadOnlyList<DnsProbeResult>? dns = null) =>
        NetworkDiagnosticReport.Render(target, route, hops ?? Array.Empty<TracerouteHop>(),
            dns ?? Array.Empty<DnsProbeResult>(), When);

    [Fact]
    public void Header_CarriesTitleAndTarget()
    {
        var text = Render(target: "8.8.8.8");
        Assert.Contains("Aurum Tweaks — Diagnostic réseau", text);
        Assert.Contains("Cible : 8.8.8.8", text);
    }

    [Fact]
    public void BlankTarget_RendersDash() => Assert.Contains("Cible : —", Render(target: "   "));

    // --- Latency section ---

    [Fact]
    public void Latency_FormatsMetricsInFrench_AndCarriesStabilityGrade()
    {
        var text = Render(route: new NetworkRouteSnapshot(12.3f, 1.2f, 0f, 0));
        Assert.Contains("LATENCE (vers la cible)", text);
        Assert.Contains("12,3 ms", text);   // fr-FR decimal comma, deterministic across machine locales
        Assert.Contains("1,2 ms", text);
        Assert.Contains("0 %", text);
        Assert.Contains("Stabilité", text);
        Assert.Contains("Excellent", text);
        Assert.Contains("indicatif : mesuré vers la cible, pas le serveur de jeu", text);
    }

    [Fact]
    public void Latency_TotalLoss_GradesMediocre_NamingPacketLoss()
    {
        var text = Render(route: new NetworkRouteSnapshot(0f, 0f, 100f, 0));
        Assert.Contains("Médiocre", text);
        Assert.Contains("perte de paquets", text);
    }

    [Fact]
    public void Latency_NotMeasured_SaysSoWithTheAction() =>
        Assert.Contains("non mesuré — lance « Mesurer »", Render(route: null));

    // --- Route section ---

    [Fact]
    public void Route_ListsRespondingHops_AndHonestTimeout()
    {
        var text = Render(hops: new[] { Replied(1, "192.168.1.1", 1), Timeout(2) });
        Assert.Contains("ROUTE (traceroute)", text);
        Assert.Contains("192.168.1.1", text);
        Assert.Contains("1 ms", text);
        Assert.Contains("*", text);   // a non-responding hop — never a fabricated address
    }

    [Fact]
    public void Route_NotTraced_SaysSoWithTheAction() =>
        Assert.Contains("non tracée — lance « Tracer la route »", Render(hops: Array.Empty<TracerouteHop>()));

    // --- DNS section ---

    [Fact]
    public void Dns_ListsResolvers_WithReliability_AndCurrentMarker()
    {
        var text = Render(dns: new[]
        {
            Dns("Cloudflare", "1.1.1.1", responded: true, median: 8, ok: 4, total: 4, current: true),
            Dns("FAI", "203.0.113.5", responded: false, median: -1, ok: 0, total: 4),
        });
        Assert.Contains("DNS (résolveurs publics", text);
        Assert.Contains("Cloudflare", text);
        Assert.Contains("1.1.1.1", text);
        Assert.Contains("8 ms", text);
        Assert.Contains("4/4", text);
        Assert.Contains("(actuel)", text);
        // The silent resolver is honest: shown with its address and 0/4 successes, never a fabricated latency.
        Assert.Contains("203.0.113.5", text);
        Assert.Contains("0/4", text);
    }

    [Fact]
    public void Dns_NotMeasured_SaysSoWithTheAction() =>
        Assert.Contains("non mesuré — lance « Benchmark DNS »", Render(dns: Array.Empty<DnsProbeResult>()));

    // --- Footer / honesty ---

    [Fact]
    public void Footer_StatesItIsLocalAndIndicative()
    {
        var text = Render();
        Assert.Contains("mesuré localement vers la cible choisie (pas le serveur de jeu)", text);
        Assert.Contains("jamais envoyé", text);
    }

    [Fact]
    public void EmptyEverything_StillRendersAllThreeSectionsAsNotMeasured()
    {
        var text = Render(target: "1.1.1.1");
        Assert.Contains("non mesuré — lance « Mesurer »", text);
        Assert.Contains("non tracée", text);
        Assert.Contains("non mesuré — lance « Benchmark DNS »", text);
    }
}
