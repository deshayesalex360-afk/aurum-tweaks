using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

/// <summary>
/// Formats a link speed given in bits per second the way NICs are actually rated — base-1000 (1 Gb/s = 1e9 bps),
/// NOT the base-1024 used for byte sizes. Split out of the service (and culture-pinned to fr-FR like
/// <see cref="ByteSize"/>) so the boundary case is testable: Windows reports a non-positive speed for a down or
/// unknown link, and that must render « — », never a fabricated « 0 b/s ».
/// </summary>
public static class NetworkLinkSpeed
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Format(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "—";
        if (bitsPerSecond >= 1_000_000_000L)
            return (bitsPerSecond / 1_000_000_000.0).ToString("0.##", Fr) + " Gb/s";
        if (bitsPerSecond >= 1_000_000L)
            return (bitsPerSecond / 1_000_000L).ToString(Fr) + " Mb/s";
        if (bitsPerSecond >= 1_000L)
            return (bitsPerSecond / 1_000L).ToString(Fr) + " kb/s";
        return bitsPerSecond.ToString(Fr) + " b/s";
    }
}

/// <summary>The small, display-oriented adapter family we collapse the big <see cref="NetworkInterfaceType"/> enum into.</summary>
public enum NetworkAdapterKind { Ethernet, WiFi, Loopback, Tunnel, Other }

/// <summary>
/// Maps a <see cref="NetworkInterfaceType"/> to a <see cref="NetworkAdapterKind"/> and gives each a stable French
/// label. Pure so the mapping is pinned by tests; the report uses <see cref="NetworkAdapterKind.Loopback"/> to drop
/// the loopback pseudo-adapter, so getting that classification right is load-bearing.
/// </summary>
public static class NetworkAdapterClassification
{
    public static NetworkAdapterKind Classify(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx => NetworkAdapterKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => NetworkAdapterKind.WiFi,
        NetworkInterfaceType.Loopback => NetworkAdapterKind.Loopback,
        NetworkInterfaceType.Tunnel => NetworkAdapterKind.Tunnel,
        _ => NetworkAdapterKind.Other
    };

    public static string Describe(NetworkAdapterKind kind) => kind switch
    {
        NetworkAdapterKind.Ethernet => "Ethernet",
        NetworkAdapterKind.WiFi => "Wi-Fi",
        NetworkAdapterKind.Loopback => "Boucle locale",
        NetworkAdapterKind.Tunnel => "Tunnel",
        _ => "Autre"
    };
}

/// <summary>
/// A plain, immutable snapshot of one adapter built from <see cref="NetworkInterface"/> by the service, so all the
/// display/verdict logic (and its tests) work on data and never need a live NIC. Every list is non-null.
/// </summary>
public sealed record NetworkAdapterInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public NetworkAdapterKind Kind { get; init; } = NetworkAdapterKind.Other;
    public bool IsUp { get; init; }
    public long SpeedBps { get; init; }
    public string Mac { get; init; } = "";
    public IReadOnlyList<string> IPv4 { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Gateways { get; init; } = Array.Empty<string>();
}

/// <summary>French, display-ready projection of one <see cref="NetworkAdapterInfo"/>. Every field shows « — » rather than a blank or a fabricated value when the underlying data is absent.</summary>
public sealed record NetworkAdapterRow(NetworkAdapterInfo Adapter)
{
    public string Name => string.IsNullOrWhiteSpace(Adapter.Name) ? "Carte inconnue" : Adapter.Name.Trim();
    public string Description => Adapter.Description?.Trim() ?? "";

    /// <summary>Show the driver description only when it adds something the name doesn't already say.</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Adapter.Description)
        && !string.Equals(Adapter.Description.Trim(), Name, StringComparison.OrdinalIgnoreCase);

    public string TypeDisplay => NetworkAdapterClassification.Describe(Adapter.Kind);
    public bool IsUp => Adapter.IsUp;
    public string StatusDisplay => Adapter.IsUp ? "Connectée" : "Déconnectée";
    public string SpeedDisplay => NetworkLinkSpeed.Format(Adapter.SpeedBps);

    public bool HasIPv4 => Adapter.IPv4.Count > 0;
    public string IPv4Display => HasIPv4 ? string.Join(", ", Adapter.IPv4) : "—";
    public bool HasDns => Adapter.DnsServers.Count > 0;
    public string DnsDisplay => HasDns ? string.Join(", ", Adapter.DnsServers) : "—";
    public bool HasGateway => Adapter.Gateways.Count > 0;
    public string GatewayDisplay => HasGateway ? string.Join(", ", Adapter.Gateways) : "—";
    public string MacDisplay => string.IsNullOrWhiteSpace(Adapter.Mac) ? "—" : Adapter.Mac;

    /// <summary>
    /// The adapter actually carrying the default route — up, non-loopback, and holding a gateway. The honest
    /// "this is your internet connection" signal: a heuristic, but a truthful one, since an adapter with no default
    /// gateway is not on the active path. Used to highlight one row and to summarise the report.
    /// </summary>
    public bool IsActive => Adapter.IsUp && Adapter.Kind != NetworkAdapterKind.Loopback && Adapter.Gateways.Count > 0;
}

/// <summary>The full adapter list plus an honest one-line summary of the active connection.</summary>
public sealed record NetworkAdaptersReport(IReadOnlyList<NetworkAdapterRow> Adapters)
{
    public int AdapterCount => Adapters.Count;
    public bool HasAdapters => Adapters.Count > 0;
    public int ActiveCount => Adapters.Count(a => a.IsActive);
    public NetworkAdapterRow? Active => Adapters.FirstOrDefault(a => a.IsActive);

    public string CountDisplay
    {
        get
        {
            if (!HasAdapters) return "—";
            string cartes = AdapterCount > 1 ? "cartes" : "carte";
            if (ActiveCount == 0) return $"{AdapterCount} {cartes}";
            string actives = ActiveCount > 1 ? "actives" : "active";
            return $"{AdapterCount} {cartes} · {ActiveCount} {actives}";
        }
    }

    public string Headline
    {
        get
        {
            if (!HasAdapters) return "Aucune carte réseau détectée";
            var active = Active;
            return active is null
                ? "Aucune carte active (pas de passerelle par défaut)"
                : $"{active.Name} — {active.TypeDisplay} · {active.SpeedDisplay}";
        }
    }

    public string Detail
    {
        get
        {
            if (!HasAdapters) return "Aucune interface réseau rapportée par Windows.";
            var active = Active;
            return active is null
                ? "Aucune carte ne porte de passerelle par défaut — vérifie ta connexion."
                : $"IPv4 {active.IPv4Display} · DNS {active.DnsDisplay}";
        }
    }

    /// <summary>
    /// Build the report from raw snapshots: drop the loopback pseudo-adapter (never user-actionable), then order
    /// active connection first, other live adapters next, the rest last, alphabetically within each band.
    /// </summary>
    public static NetworkAdaptersReport From(IEnumerable<NetworkAdapterInfo> adapters)
    {
        var rows = adapters
            .Where(a => a.Kind != NetworkAdapterKind.Loopback)
            .Select(a => new NetworkAdapterRow(a))
            .OrderByDescending(r => r.IsActive)
            .ThenByDescending(r => r.IsUp)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new NetworkAdaptersReport(rows);
    }
}

/// <summary>The safe network-maintenance actions this page can run.</summary>
public enum NetworkAction { FlushDns, RenewDhcp }

/// <summary>
/// The exact (executable, args) for each <see cref="NetworkAction"/>, isolated as a pure value so a wrong switch
/// can't silently fire the wrong ipconfig command. Both are standard, self-healing operations: /flushdns clears the
/// resolver cache (Windows rebuilds it on demand) and /renew re-requests the DHCP lease.
/// </summary>
public static class NetworkActionCommand
{
    public static (string FileName, string Args) Build(NetworkAction action) => action switch
    {
        NetworkAction.FlushDns => ("ipconfig.exe", "/flushdns"),
        NetworkAction.RenewDhcp => ("ipconfig.exe", "/renew"),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}

/// <summary>
/// The result of a maintenance action. Success is decided by the process EXIT CODE (ipconfig returns 0 on success),
/// never by parsing its localized stdout — the same code-page-immunity decision as Power Plan and Scheduled Tasks —
/// so the verdict is correct regardless of the OS display language. The message is a fixed French string.
/// </summary>
public sealed record NetworkActionOutcome(bool Ok, string Message)
{
    public static NetworkActionOutcome FromExitCode(int exitCode, string okMessage, string failMessage) =>
        exitCode == 0 ? new NetworkActionOutcome(true, okMessage) : new NetworkActionOutcome(false, failMessage);
}

/// <summary>
/// Reads the live network adapters and runs the two safe maintenance commands. A read-mostly front-end: the only
/// writes are ipconfig /flushdns and /renew, both standard self-healing operations. Adapter enumeration and the
/// CLI calls are mechanical I/O ("test the decision, not the world") — every honesty-bearing decision (speed/IP/DNS
/// formatting, active-adapter heuristic, command bytes, exit-code verdict) lives in a pinned pure core above.
/// </summary>
public sealed class NetworkAdaptersService : INetworkAdaptersService
{
    public Task<NetworkAdaptersReport> GetReportAsync() => Task.Run(() =>
    {
        var list = new List<NetworkAdapterInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try { list.Add(Snapshot(ni)); }
                catch { /* a single quirky adapter must not sink the whole enumeration */ }
            }
        }
        catch { /* GetAllNetworkInterfaces itself failed — return honestly empty rather than throw */ }
        return NetworkAdaptersReport.From(list);
    });

    public Task<NetworkActionOutcome> FlushDnsAsync() =>
        RunAsync(NetworkAction.FlushDns, "Cache DNS vidé.", "Échec du vidage du cache DNS.");

    // "terminé" not "renouvelé": on a static-IP adapter /renew completes successfully with nothing to renew —
    // the exit code can't tell us a lease was actually obtained, so we don't claim one was.
    public Task<NetworkActionOutcome> RenewDhcpAsync() =>
        RunAsync(NetworkAction.RenewDhcp, "Renouvellement DHCP terminé.", "Échec du renouvellement DHCP (aucun serveur DHCP joignable ?).");

    private static Task<NetworkActionOutcome> RunAsync(NetworkAction action, string okMsg, string failMsg) => Task.Run(() =>
    {
        var (file, args) = NetworkActionCommand.Build(action);
        var (exit, _) = ProcessRunner.Capture(file, args, 20_000);
        return NetworkActionOutcome.FromExitCode(exit, okMsg, failMsg);
    });

    private static NetworkAdapterInfo Snapshot(NetworkInterface ni)
    {
        long speed;
        try { speed = ni.Speed; } catch { speed = -1; }   // some virtual adapters throw on Speed

        var ipv4 = new List<string>();
        var dns = new List<string>();
        var gw = new List<string>();
        try
        {
            var props = ni.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    ipv4.Add(ua.Address.ToString());
            foreach (var d in props.DnsAddresses)
                if (d.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    dns.Add(d.ToString());
            foreach (var g in props.GatewayAddresses)
                if (g.Address is not null && !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any))
                    gw.Add(g.Address.ToString());   // drop 0.0.0.0 / :: so a placeholder can't fake an active route
        }
        catch { /* some adapters refuse GetIPProperties — keep the basics, lists stay empty */ }

        string mac = "";
        try
        {
            var bytes = ni.GetPhysicalAddress().GetAddressBytes();
            if (bytes.Length > 0) mac = string.Join(":", bytes.Select(b => b.ToString("X2")));
        }
        catch { /* no hardware address (tunnel/loopback) — « — » */ }

        return new NetworkAdapterInfo
        {
            Name = ni.Name ?? "",
            Description = ni.Description ?? "",
            Kind = NetworkAdapterClassification.Classify(ni.NetworkInterfaceType),
            IsUp = ni.OperationalStatus == OperationalStatus.Up,
            SpeedBps = speed,
            Mac = mac,
            IPv4 = ipv4,
            DnsServers = dns,
            Gateways = gw
        };
    }
}
