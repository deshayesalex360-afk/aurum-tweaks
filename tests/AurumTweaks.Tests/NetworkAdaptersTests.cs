using System;
using System.Net.NetworkInformation;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

public class NetworkLinkSpeedTests
{
    [Theory]
    [InlineData(1_000_000_000L, "1 Gb/s")]
    [InlineData(2_500_000_000L, "2,5 Gb/s")]   // fr-FR comma, pinned by the formatter's fixed culture
    [InlineData(10_000_000_000L, "10 Gb/s")]
    [InlineData(100_000_000L, "100 Mb/s")]
    [InlineData(1_000_000L, "1 Mb/s")]
    [InlineData(1_000L, "1 kb/s")]
    [InlineData(100L, "100 b/s")]
    [InlineData(0L, "—")]    // a down/unknown link must never read as a fabricated "0 b/s"
    [InlineData(-1L, "—")]
    public void Format_Cases(long bps, string expected)
        => Assert.Equal(expected, NetworkLinkSpeed.Format(bps));
}

public class NetworkAdapterClassificationTests
{
    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, NetworkAdapterKind.Ethernet, "Ethernet")]
    [InlineData(NetworkInterfaceType.GigabitEthernet, NetworkAdapterKind.Ethernet, "Ethernet")]
    [InlineData(NetworkInterfaceType.Wireless80211, NetworkAdapterKind.WiFi, "Wi-Fi")]
    [InlineData(NetworkInterfaceType.Loopback, NetworkAdapterKind.Loopback, "Boucle locale")]
    [InlineData(NetworkInterfaceType.Tunnel, NetworkAdapterKind.Tunnel, "Tunnel")]
    [InlineData(NetworkInterfaceType.Ppp, NetworkAdapterKind.Other, "Autre")]
    public void Classify_And_Describe(NetworkInterfaceType type, NetworkAdapterKind kind, string label)
    {
        Assert.Equal(kind, NetworkAdapterClassification.Classify(type));
        Assert.Equal(label, NetworkAdapterClassification.Describe(kind));
    }
}

public class NetworkActionCommandTests
{
    [Fact]
    public void FlushDns_IsIpconfigFlushdns()
    {
        var (file, args) = NetworkActionCommand.Build(NetworkAction.FlushDns);
        Assert.Equal("ipconfig.exe", file);
        Assert.Equal("/flushdns", args);
    }

    [Fact]
    public void RenewDhcp_IsIpconfigRenew()
    {
        var (file, args) = NetworkActionCommand.Build(NetworkAction.RenewDhcp);
        Assert.Equal("ipconfig.exe", file);
        Assert.Equal("/renew", args);
    }
}

public class NetworkActionOutcomeTests
{
    [Fact]
    public void Zero_IsSuccess()
    {
        var o = NetworkActionOutcome.FromExitCode(0, "ok", "fail");
        Assert.True(o.Ok);
        Assert.Equal("ok", o.Message);
    }

    [Fact]
    public void NonZero_IsFailure()
    {
        var o = NetworkActionOutcome.FromExitCode(1, "ok", "fail");
        Assert.False(o.Ok);
        Assert.Equal("fail", o.Message);
    }
}

public class NetworkAdapterRowTests
{
    private static NetworkAdapterInfo Adapter(
        string name = "Ethernet", NetworkAdapterKind kind = NetworkAdapterKind.Ethernet,
        bool up = true, long speed = 1_000_000_000L, string mac = "AA:BB:CC:DD:EE:FF",
        string[]? ipv4 = null, string[]? dns = null, string[]? gw = null)
        => new()
        {
            Name = name, Description = name, Kind = kind, IsUp = up, SpeedBps = speed, Mac = mac,
            IPv4 = ipv4 ?? new[] { "192.168.1.10" },
            DnsServers = dns ?? new[] { "1.1.1.1" },
            Gateways = gw ?? new[] { "192.168.1.1" }
        };

    [Fact]
    public void Name_FallsBack_WhenBlank()
        => Assert.Equal("Carte inconnue", new NetworkAdapterRow(Adapter(name: "")).Name);

    [Fact]
    public void Status_ReflectsUpDown()
    {
        Assert.Equal("Connectée", new NetworkAdapterRow(Adapter(up: true)).StatusDisplay);
        Assert.Equal("Déconnectée", new NetworkAdapterRow(Adapter(up: false)).StatusDisplay);
    }

    [Fact]
    public void Speed_Formats_AndDashesWhenUnknown()
    {
        Assert.Equal("1 Gb/s", new NetworkAdapterRow(Adapter(speed: 1_000_000_000L)).SpeedDisplay);
        Assert.Equal("—", new NetworkAdapterRow(Adapter(speed: -1)).SpeedDisplay);
    }

    [Fact]
    public void Ipv4_Dns_Gateway_Mac_Dash_WhenEmpty()
    {
        var row = new NetworkAdapterRow(Adapter(
            ipv4: Array.Empty<string>(), dns: Array.Empty<string>(), gw: Array.Empty<string>(), mac: ""));
        Assert.Equal("—", row.IPv4Display);
        Assert.Equal("—", row.DnsDisplay);
        Assert.Equal("—", row.GatewayDisplay);
        Assert.Equal("—", row.MacDisplay);
    }

    [Fact]
    public void Ipv4_Dns_Joined_WhenPresent()
    {
        var row = new NetworkAdapterRow(Adapter(ipv4: new[] { "10.0.0.2", "10.0.0.3" }, dns: new[] { "1.1.1.1", "8.8.8.8" }));
        Assert.Equal("10.0.0.2, 10.0.0.3", row.IPv4Display);
        Assert.Equal("1.1.1.1, 8.8.8.8", row.DnsDisplay);
    }

    // The active-connection heuristic is honest only if it requires all three: up, non-loopback, and a gateway.
    [Fact]
    public void IsActive_RequiresUp_NonLoopback_AndGateway()
    {
        Assert.True(new NetworkAdapterRow(Adapter(up: true, gw: new[] { "192.168.1.1" })).IsActive);
        Assert.False(new NetworkAdapterRow(Adapter(up: true, gw: Array.Empty<string>())).IsActive);   // no default route
        Assert.False(new NetworkAdapterRow(Adapter(up: false, gw: new[] { "192.168.1.1" })).IsActive); // link down
        Assert.False(new NetworkAdapterRow(Adapter(kind: NetworkAdapterKind.Loopback, gw: new[] { "127.0.0.1" })).IsActive);
    }
}

public class NetworkAdaptersReportTests
{
    private static NetworkAdapterInfo Adapter(
        string name, NetworkAdapterKind kind = NetworkAdapterKind.Ethernet,
        bool up = true, long speed = 1_000_000_000L,
        string[]? ipv4 = null, string[]? dns = null, string[]? gw = null)
        => new()
        {
            Name = name, Description = name, Kind = kind, IsUp = up, SpeedBps = speed, Mac = "AA:BB:CC:DD:EE:FF",
            IPv4 = ipv4 ?? new[] { "192.168.1.10" },
            DnsServers = dns ?? new[] { "1.1.1.1" },
            Gateways = gw ?? new[] { "192.168.1.1" }
        };

    [Fact]
    public void Empty_ReportsNoAdapters_Honestly()
    {
        var rep = NetworkAdaptersReport.From(Array.Empty<NetworkAdapterInfo>());
        Assert.False(rep.HasAdapters);
        Assert.Equal(0, rep.AdapterCount);
        Assert.Equal("—", rep.CountDisplay);
        Assert.Equal("Aucune carte réseau détectée", rep.Headline);
        Assert.Null(rep.Active);
    }

    [Fact]
    public void From_DropsLoopback()
    {
        var rep = NetworkAdaptersReport.From(new[]
        {
            Adapter("Loopback", NetworkAdapterKind.Loopback, gw: Array.Empty<string>()),
            Adapter("Ethernet")
        });
        Assert.Equal(1, rep.AdapterCount);
        Assert.Equal("Ethernet", rep.Adapters[0].Name);
    }

    [Fact]
    public void From_OrdersActiveFirst_RegardlessOfInputOrder()
    {
        var rep = NetworkAdaptersReport.From(new[]
        {
            Adapter("Wi-Fi déconnectée", NetworkAdapterKind.WiFi, up: false, gw: Array.Empty<string>()),
            Adapter("Ethernet active", up: true, gw: new[] { "192.168.1.1" })
        });
        Assert.Equal("Ethernet active", rep.Adapters[0].Name);
        Assert.True(rep.Adapters[0].IsActive);
    }

    [Fact]
    public void Active_And_Counts_Summarised()
    {
        var rep = NetworkAdaptersReport.From(new[]
        {
            Adapter("Ethernet", up: true, speed: 1_000_000_000L, ipv4: new[] { "192.168.1.10" }, dns: new[] { "1.1.1.1" }, gw: new[] { "192.168.1.1" }),
            Adapter("Wi-Fi", NetworkAdapterKind.WiFi, up: true, gw: Array.Empty<string>())
        });
        Assert.True(rep.HasAdapters);
        Assert.Equal(2, rep.AdapterCount);
        Assert.Equal(1, rep.ActiveCount);
        Assert.Equal("Ethernet", rep.Active!.Name);
        Assert.Contains("2 cartes", rep.CountDisplay);
        Assert.Contains("1 active", rep.CountDisplay);
        Assert.Contains("Ethernet", rep.Headline);
        Assert.Contains("1 Gb/s", rep.Headline);
        Assert.Contains("192.168.1.10", rep.Detail);
        Assert.Contains("1.1.1.1", rep.Detail);
    }

    [Fact]
    public void NoActive_WhenNoGateway_HonestSummary()
    {
        var rep = NetworkAdaptersReport.From(new[] { Adapter("Ethernet", up: true, gw: Array.Empty<string>()) });
        Assert.Equal(0, rep.ActiveCount);
        Assert.Null(rep.Active);
        Assert.Contains("Aucune carte active", rep.Headline);
        Assert.Equal("1 carte", rep.CountDisplay);   // singular, no "active" suffix
    }
}
