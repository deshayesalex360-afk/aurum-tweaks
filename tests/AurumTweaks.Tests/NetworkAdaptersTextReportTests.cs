using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="NetworkAdaptersTextReport"/> — the shareable « cartes réseau » paste used to troubleshoot link speed,
/// DNS and gateway. Honesty contract: it renders only the REAL state the page already read (NetworkInterface), leads with
/// the shared <see cref="NetworkAdaptersReport.Headline"/>/<see cref="NetworkAdaptersReport.Detail"/> so the paste matches
/// the screen, flags the active default-route adapter as « [Active] », prints « — » for a down link speed or an absent
/// IP/DNS/gateway/MAC (never a fabricated « 0 b/s » or empty-as-OK), drops the loopback pseudo-adapter, shows the driver
/// only when it adds something, distinguishes an empty read from a populated one, and keeps the read-only / never-sent /
/// no-FPS footer plus the deliberate « contains local identifiers » caveat.
/// </summary>
public class NetworkAdaptersTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 21, 14, 30, 0, DateTimeKind.Utc);

    private static NetworkAdapterInfo Adapter(
        string name, NetworkAdapterKind kind = NetworkAdapterKind.Ethernet,
        bool up = true, long speed = 1_000_000_000L, string mac = "AA:BB:CC:DD:EE:FF", string? desc = null,
        string[]? ipv4 = null, string[]? dns = null, string[]? gw = null)
        => new()
        {
            Name = name, Description = desc ?? name, Kind = kind, IsUp = up, SpeedBps = speed, Mac = mac,
            IPv4 = ipv4 ?? new[] { "192.168.1.10" },
            DnsServers = dns ?? new[] { "1.1.1.1" },
            Gateways = gw ?? new[] { "192.168.1.1" }
        };

    private static NetworkAdaptersReport Report(params NetworkAdapterInfo[] adapters) =>
        NetworkAdaptersReport.From(adapters);

    [Fact]
    public void Header_AlwaysCarriesTitle()
    {
        var text = NetworkAdaptersTextReport.Render(Report(Adapter("Ethernet")), When);
        Assert.Contains("Aurum Tweaks — Cartes réseau", text);
    }

    [Fact]
    public void Headline_And_Detail_AreRendered()
    {
        var text = NetworkAdaptersTextReport.Render(Report(Adapter("Ethernet")), When);
        Assert.Contains("Ethernet — Ethernet · 1 Gb/s", text);   // the page's own active-connection headline
        Assert.Contains("IPv4 192.168.1.10", text);              // the page's own detail line
    }

    [Fact]
    public void ActiveAdapter_IsBracketedActive_WithCountHeader()
    {
        var text = NetworkAdaptersTextReport.Render(
            Report(Adapter("Ethernet", up: true, gw: new[] { "192.168.1.1" })), When);
        Assert.Contains("CARTES (1)", text);
        Assert.Contains("• Ethernet  [Active]", text);
    }

    [Fact]
    public void DownAdapter_IsBracketedDisconnected()
    {
        var text = NetworkAdaptersTextReport.Render(
            Report(Adapter("Wi-Fi", NetworkAdapterKind.WiFi, up: false, gw: Array.Empty<string>())), When);
        Assert.Contains("• Wi-Fi  [Déconnectée]", text);
    }

    [Fact]
    public void Empty_SaysNoAdapters_NotAFabricatedRow()
    {
        var text = NetworkAdaptersTextReport.Render(Report(), When);
        Assert.Contains("Aucune carte réseau détectée", text);
        Assert.Contains("CARTES (0)", text);
        Assert.Contains("Aucune carte réseau listée par Windows", text);
        Assert.DoesNotContain("  • ", text);   // no fabricated adapter bullet
    }

    [Fact]
    public void AbsentMetrics_RenderDash_NeverFabricatedZeroOrBlank()
    {
        var text = NetworkAdaptersTextReport.Render(
            Report(Adapter("Ethernet", up: true, speed: -1, mac: "",
                ipv4: Array.Empty<string>(), dns: Array.Empty<string>(), gw: Array.Empty<string>())), When);
        Assert.Contains("Débit du lien", text);
        Assert.DoesNotContain("0 b/s", text);   // a down/unknown link is never a fabricated « 0 b/s »
        Assert.Contains("MAC", text);
        Assert.Contains("—", text);             // absent IP/DNS/gateway/MAC all show « — »
    }

    [Fact]
    public void Loopback_IsDropped_NeverInThePaste()
    {
        var text = NetworkAdaptersTextReport.Render(
            Report(Adapter("Loopback", NetworkAdapterKind.Loopback, gw: Array.Empty<string>()), Adapter("Ethernet")), When);
        Assert.DoesNotContain("Boucle locale", text);
        Assert.Contains("CARTES (1)", text);    // only the real adapter is counted
    }

    [Fact]
    public void Driver_AppearsOnlyWhenItAddsSomething()
    {
        var withDriver = NetworkAdaptersTextReport.Render(
            Report(Adapter("Ethernet", desc: "Realtek PCIe GbE Family Controller")), When);
        Assert.Contains("Pilote", withDriver);
        Assert.Contains("Realtek PCIe GbE Family Controller", withDriver);

        var noExtraDriver = NetworkAdaptersTextReport.Render(Report(Adapter("Ethernet", desc: "Ethernet")), When);
        Assert.DoesNotContain("Pilote", noExtraDriver);   // driver == name adds nothing → omitted
    }

    [Fact]
    public void Ipv4AndMac_AreListed_WhenPresent()
    {
        var text = NetworkAdaptersTextReport.Render(
            Report(Adapter("Ethernet", mac: "AA:BB:CC:DD:EE:FF", ipv4: new[] { "10.0.0.5" })), When);
        Assert.Contains("10.0.0.5", text);
        Assert.Contains("AA:BB:CC:DD:EE:FF", text);
    }

    [Fact]
    public void Footer_KeepsReadOnlyNeverSentNoFps_AndTheLocalIdentifiersCaveat()
    {
        var text = NetworkAdaptersTextReport.Render(Report(Adapter("Ethernet")), When);
        Assert.Contains("jamais envoyé", text);
        Assert.Contains("aucun gain de FPS", text);
        Assert.Contains("identifiants locaux", text);          // the deliberate privacy caveat for a network paste
        Assert.Contains("avant de le publier", text);
    }
}
