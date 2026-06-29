using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

/// <summary>How a network adapter currently gets its DNS servers.</summary>
public enum DnsMode
{
    /// <summary>DNS is provided by the router/box over DHCP — the static <c>NameServer</c> entry is empty.</summary>
    Automatic,

    /// <summary>DNS is pinned manually on the adapter — a non-empty static <c>NameServer</c> entry exists.</summary>
    Static,

    /// <summary>The adapter's configuration could not be read — never reported as automatic or static.</summary>
    Unknown
}

/// <summary>The outcome class of a <c>SetDNSServerSearchOrder</c> WMI call, mapped from its return code.</summary>
public enum DnsApplyStatus
{
    /// <summary>The call completed successfully and no reboot is required (return code 0).</summary>
    Succeeded,

    /// <summary>The call completed but a reboot is required to take full effect (return code 1).</summary>
    RebootRequired,

    /// <summary>The call failed or the adapter could not be found (any other return code / exception).</summary>
    Failed,

    /// <summary>No call was made (missing adapter id) — never rendered as a success.</summary>
    NotAttempted
}

/// <summary>
/// A curated public DNS resolver the page can apply: a friendly name plus its primary and (optional) secondary
/// IPv4 addresses. The provider names intentionally mirror <see cref="DnsBenchmarkMath.DefaultResolvers"/> so the
/// benchmark on the Gaming page and the apply step here tell one coherent story.
/// </summary>
public sealed record DnsPreset(string Name, string Primary, string? Secondary, string Description)
{
    /// <summary>The address list to write — primary first, secondary appended only when present.</summary>
    public IReadOnlyList<string> Addresses => string.IsNullOrWhiteSpace(Secondary)
        ? new[] { Primary }
        : new[] { Primary, Secondary };

    /// <summary>Human-readable « primaire · secondaire » (or just the primary when there is no secondary).</summary>
    public string ServersDisplay => string.IsNullOrWhiteSpace(Secondary) ? Primary : $"{Primary} · {Secondary}";
}

/// <summary>
/// The fixed catalog of public resolvers offered for one-click apply. Each is a well-known, documented provider
/// with a primary+secondary pair — nothing is fabricated, and only these vetted addresses are ever written.
/// </summary>
public static class DnsPresetCatalog
{
    public static IReadOnlyList<DnsPreset> Presets { get; } = new[]
    {
        new DnsPreset("Cloudflare", "1.1.1.1", "1.0.0.1",
            "Rapide et axé sur la vie privée — ne revend pas l'historique des requêtes."),
        new DnsPreset("Google Public DNS", "8.8.8.8", "8.8.4.4",
            "Très disponible et présent partout dans le monde."),
        new DnsPreset("Quad9", "9.9.9.9", "149.112.112.112",
            "Bloque les domaines malveillants connus (hameçonnage, logiciels malveillants)."),
        new DnsPreset("OpenDNS", "208.67.222.222", "208.67.220.220",
            "Bonne disponibilité, filtrage optionnel via un compte."),
        new DnsPreset("AdGuard DNS", "94.140.14.14", "94.140.15.15",
            "Bloque publicités et traqueurs au niveau du DNS."),
    };

    /// <summary>Case-insensitive lookup by provider name; null when no preset matches.</summary>
    public static DnsPreset? Find(string name) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Lookup by PRIMARY IPv4 address (trimmed, case-insensitive); null on no match or blank input. This is the
    /// robust bridge from a benchmark winner to an applicable preset: the benchmark's resolver names ("Google",
    /// "AdGuard") deliberately differ from the catalog's ("Google Public DNS", "AdGuard DNS"), so matching by name
    /// would silently miss those two — only the address is a reliable join key across both lists.
    /// </summary>
    public static DnsPreset? FindByPrimary(string? primary)
    {
        if (string.IsNullOrWhiteSpace(primary)) return null;
        var needle = primary.Trim();
        return Presets.FirstOrDefault(p => string.Equals(p.Primary, needle, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Pure helpers for the DNS server lists exchanged with Windows. The static <c>NameServer</c> registry value is
/// comma- or space-separated; these parse/compare/format it without touching the registry so the honesty rules
/// (empty ⇒ automatic, order-sensitive equality, "—" for none) are unit-testable.
/// </summary>
public static class DnsAddresses
{
    private static readonly char[] Separators = { ',', ' ', ';' };

    /// <summary>Split a raw <c>NameServer</c> string into a trimmed list, dropping blanks; empty/null ⇒ no servers.</summary>
    public static IReadOnlyList<string> Parse(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Order-sensitive, case-insensitive equality — primary/secondary order is meaningful for DNS.</summary>
    public static bool Equal(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    /// <summary>« a · b » for display, or « — » when the list is empty (never a fabricated address).</summary>
    public static string Display(IReadOnlyList<string> servers) =>
        servers.Count == 0 ? "—" : string.Join(" · ", servers);
}

/// <summary>
/// The honest rule for classifying an adapter's DNS source, split out so it can be pinned by tests: no readable
/// adapter id ⇒ <see cref="DnsMode.Unknown"/> (never guessed); a non-empty static <c>NameServer</c> ⇒
/// <see cref="DnsMode.Static"/>; otherwise the DNS comes from DHCP ⇒ <see cref="DnsMode.Automatic"/>.
/// </summary>
public static class DnsModeClassifier
{
    public static DnsMode Classify(bool hasSettingId, int staticServerCount) =>
        !hasSettingId ? DnsMode.Unknown
        : staticServerCount > 0 ? DnsMode.Static
        : DnsMode.Automatic;
}

/// <summary>
/// The live DNS state of one IP-enabled network adapter. <see cref="EffectiveServers"/> is what Windows actually
/// resolves through (static OR DHCP-assigned); <see cref="StaticServers"/> is the authoritative static
/// <c>NameServer</c> entry (empty ⇒ DNS comes from DHCP) — keeping the two apart is what lets the page tell
/// "automatic" from "manual" honestly and gate the buttons so none is ever a no-op.
/// </summary>
public sealed record DnsAdapterState(
    string Description,
    string SettingId,
    bool IsConnected,
    DnsMode Mode,
    IReadOnlyList<string> EffectiveServers,
    IReadOnlyList<string> StaticServers)
{
    public string Name => string.IsNullOrWhiteSpace(Description) ? "Carte réseau" : Description.Trim();

    public bool IsStatic => Mode == DnsMode.Static;
    public bool IsAutomatic => Mode == DnsMode.Automatic;

    public string ModeLabel => Mode switch
    {
        DnsMode.Automatic => "Automatique (DHCP)",
        DnsMode.Static    => "Manuel (statique)",
        _                 => "Indéterminé"
    };

    /// <summary>The DNS servers currently in use, however they were obtained, or « — » when none are readable.</summary>
    public string EffectiveDisplay => DnsAddresses.Display(EffectiveServers);

    public string SourceNote => Mode switch
    {
        DnsMode.Automatic => "Fournis par ta box/routeur via DHCP.",
        DnsMode.Static    => "Définis manuellement sur la carte.",
        _                 => "Source indéterminée."
    };

    /// <summary>Reverting only makes sense when DNS is actually pinned static — otherwise it would be a no-op.</summary>
    public bool CanRevertToAutomatic => IsStatic;

    /// <summary>True when this adapter's STATIC config already equals the preset — so applying it would change nothing.</summary>
    public bool IsUsingPreset(DnsPreset preset) => IsStatic && DnsAddresses.Equal(StaticServers, preset.Addresses);

    /// <summary>The apply button for a preset is offered only when it would genuinely change the static config.</summary>
    public bool CanApply(DnsPreset preset) => Mode != DnsMode.Unknown && !IsUsingPreset(preset);
}

/// <summary>
/// The honest verdict that closes the measure→apply loop: given a DNS benchmark and the active adapter, decide
/// whether the fastest measured resolver can be one-click applied here, and to what. <see cref="CanApply"/> is true
/// ONLY when both <see cref="Preset"/> and <see cref="Adapter"/> are set and writing would genuinely change the live
/// config — every other path (nobody answered, the user's own DNS won, the winner has no curated preset, no active
/// adapter, the adapter already runs it, or its state is unreadable) yields a false verdict with a message saying
/// exactly why, so the apply button is never a no-op and never claims an improvement that isn't real.
/// </summary>
public sealed record DnsRecommendation(DnsPreset? Preset, DnsAdapterState? Adapter, string Message, bool CanApply)
{
    public static DnsRecommendation From(DnsBenchmarkReport report, DnsAdapterState? active)
    {
        // 1. Nobody answered — the benchmark already phrased why (port 53 blocked, no connection). Nothing to apply.
        var fastest = report.Fastest;
        if (fastest is null)
            return new DnsRecommendation(null, null, report.Summary, false);

        // The winner's MEASURED median (same resolver Rank() put first) — a real number, never fabricated.
        var winner = report.Ranked.FirstOrDefault(r => r.Responded);
        string latency = winner is null ? "" : $" à {winner.MedianMs:F0} ms médian";

        // 2. The user's own current resolver won — switching would gain nothing.
        if (fastest.IsCurrent)
            return new DnsRecommendation(null, null,
                $"Ton DNS actuel ({fastest.Address}) est déjà le plus rapide mesuré{latency} — rien à changer.", false);

        // 3. The winner isn't a curated resolver we can apply in one click (e.g. an ISP DNS that happened to win).
        var preset = DnsPresetCatalog.FindByPrimary(fastest.Address);
        if (preset is null)
            return new DnsRecommendation(null, null,
                $"Le plus rapide mesuré est {fastest.Name} ({fastest.Address}){latency}, "
                + "sans préréglage applicable en un clic ici.", false);

        // 4. We know the fastest preset but there's no connected adapter to write it to.
        if (active is null)
            return new DnsRecommendation(preset, null,
                $"Le plus rapide est {preset.Name} ({preset.ServersDisplay}){latency}, "
                + "mais aucune carte réseau active n'a été détectée pour l'appliquer.", false);

        // 5. The active adapter already runs exactly this preset — applying it would change nothing.
        if (active.IsUsingPreset(preset))
            return new DnsRecommendation(preset, active,
                $"« {active.Name} » utilise déjà {preset.Name} ({preset.ServersDisplay}), "
                + "le plus rapide mesuré — rien à changer.", false);

        // 6. The adapter's DNS source couldn't be read — never write blind over an indeterminate state.
        if (active.Mode == DnsMode.Unknown)
            return new DnsRecommendation(preset, active,
                $"Le plus rapide est {preset.Name} ({preset.ServersDisplay}), mais l'état DNS de "
                + $"« {active.Name} » est indéterminé — application impossible en toute sécurité.", false);

        // 7. Applicable: name the winner and the adapter the one click would change.
        return new DnsRecommendation(preset, active,
            $"Le plus rapide est {preset.Name} ({preset.ServersDisplay}){latency}. "
            + $"Appliquer sur « {active.Name} » ?", true);
    }
}

/// <summary>
/// The honest result of a DNS change. Success is <see cref="Verified"/> by RE-READING the authoritative static
/// <c>NameServer</c> entry after the call: an apply is verified only when the read-back equals exactly what we
/// asked for; a revert is verified only when the static entry is now empty (DHCP restored). A change the system
/// silently ignored is reported with its measured value, never as a fabricated success.
/// </summary>
public sealed record DnsApplyOutcome(
    string AdapterName,
    bool RevertToAutomatic,
    IReadOnlyList<string> RequestedServers,
    IReadOnlyList<string> MeasuredServers,
    DnsApplyStatus Status)
{
    public bool Verified =>
        Status is DnsApplyStatus.Succeeded or DnsApplyStatus.RebootRequired
        && (RevertToAutomatic
                ? MeasuredServers.Count == 0
                : DnsAddresses.Equal(MeasuredServers, RequestedServers));

    public string Summary
    {
        get
        {
            if (Status == DnsApplyStatus.NotAttempted)
                return "Aucun changement appliqué.";
            if (Status == DnsApplyStatus.Failed)
                return $"Le changement DNS a échoué sur « {AdapterName} » (carte introuvable ou demande refusée).";

            if (Verified)
                return RevertToAutomatic
                    ? $"DNS automatique (DHCP) rétabli et vérifié sur « {AdapterName} »."
                    : $"DNS appliqué et vérifié sur « {AdapterName} » : {DnsAddresses.Display(MeasuredServers)}.";

            // Accepted by Windows but the read-back doesn't match — never claim success.
            return RevertToAutomatic
                ? $"Demande acceptée mais une configuration DNS statique subsiste sur « {AdapterName} » "
                  + $"({DnsAddresses.Display(MeasuredServers)})."
                : $"Demande acceptée mais la configuration lue diffère sur « {AdapterName} » "
                  + $"({DnsAddresses.Display(MeasuredServers)} au lieu de {DnsAddresses.Display(RequestedServers)}).";
        }
    }

    /// <summary>An honest "nothing happened" result for a missing adapter id.</summary>
    public static DnsApplyOutcome NotAttempted(string adapter, bool revert) =>
        new(adapter, revert, Array.Empty<string>(), Array.Empty<string>(), DnsApplyStatus.NotAttempted);
}

/// <summary>The set of adapters with their DNS state, plus whether the configuration could be read at all.</summary>
public sealed record DnsReport(IReadOnlyList<DnsAdapterState> Adapters, bool QueryOk)
{
    public int Count => Adapters.Count;
    public bool Any => Adapters.Count > 0;
    public int StaticCount => Adapters.Count(a => a.IsStatic);
    public int AutomaticCount => Adapters.Count(a => a.IsAutomatic);

    /// <summary>The first connected adapter (the one carrying a default gateway), or null when none is connected.</summary>
    public DnsAdapterState? Active => Adapters.FirstOrDefault(a => a.IsConnected);

    public string Headline =>
        !QueryOk ? "Lecture de la configuration réseau impossible."
        : Count == 0 ? "Aucune carte réseau active détectée."
        : $"{Count} carte(s) réseau · {StaticCount} en DNS manuel, {AutomaticCount} en automatique.";

    /// <summary>An honest empty report carrying that the WMI read failed.</summary>
    public static DnsReport Failed { get; } = new(Array.Empty<DnsAdapterState>(), false);
}

/// <summary>
/// Reads the live per-adapter DNS configuration and applies a curated resolver (or reverts to DHCP) via the WMI
/// <c>Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder</c> method. The WMI/registry calls are mechanical
/// I/O ("test the decision, not the world") — every honesty-bearing decision (the preset catalog, static-vs-auto
/// classification, no-op gating, the MEASURED-by-re-read outcome, the report headline) lives in a pinned pure core
/// above. DNS is read from the authoritative static <c>NameServer</c> registry entry (via <see cref="IRegistryService"/>),
/// not guessed from <c>DHCPEnabled</c>, so "automatic" vs "manual" is never misreported.
/// </summary>
public sealed class DnsService : IDnsService
{
    // The per-interface TCP/IP parameters. NameServer = static DNS (empty ⇒ DHCP-provided); keyed by adapter GUID.
    private const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    private readonly IRegistryService _registry;

    public DnsService(IRegistryService registry) => _registry = registry;

    public Task<DnsReport> GetReportAsync() => Task.Run(GetReport);

    public Task<DnsApplyOutcome> ApplyPresetAsync(string settingId, string adapterName, DnsPreset preset) =>
        Task.Run(() => SetServers(settingId, adapterName, preset.Addresses, revert: false));

    public Task<DnsApplyOutcome> RevertToAutomaticAsync(string settingId, string adapterName) =>
        Task.Run(() => SetServers(settingId, adapterName, Array.Empty<string>(), revert: true));

    private DnsReport GetReport()
    {
        try
        {
            var list = new List<DnsAdapterState>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT Description, SettingID, DNSServerSearchOrder, DefaultIPGateway " +
                "FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");

            foreach (var mo in searcher.Get().Cast<ManagementObject>())
                using (mo)
                {
                    string desc = mo["Description"] as string ?? "";
                    string guid = mo["SettingID"] as string ?? "";
                    var effective = (mo["DNSServerSearchOrder"] as string[]) ?? Array.Empty<string>();
                    bool connected = mo["DefaultIPGateway"] is string[] { Length: > 0 };

                    var staticServers = ReadStaticServers(guid);
                    var mode = DnsModeClassifier.Classify(!string.IsNullOrEmpty(guid), staticServers.Count);

                    list.Add(new DnsAdapterState(desc, guid, connected, mode, effective, staticServers));
                }

            // Connected adapters (those with a default gateway) first, then the rest, each alphabetical.
            var ordered = list.OrderByDescending(a => a.IsConnected).ThenBy(a => a.Name).ToList();
            return new DnsReport(ordered, true);
        }
        catch
        {
            return DnsReport.Failed;
        }
    }

    /// <summary>The static <c>NameServer</c> entry for an adapter GUID — empty when DNS is DHCP-provided.</summary>
    private IReadOnlyList<string> ReadStaticServers(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return Array.Empty<string>();
        return _registry.TryReadValue("HKLM", $@"{InterfacesKey}\{guid}", "NameServer", out var raw)
            ? DnsAddresses.Parse(raw)
            : Array.Empty<string>();
    }

    private DnsApplyOutcome SetServers(string settingId, string adapterName, IReadOnlyList<string> servers, bool revert)
    {
        if (string.IsNullOrEmpty(settingId))
            return DnsApplyOutcome.NotAttempted(adapterName, revert);

        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2");
            scope.Options.EnablePrivileges = true;   // the app runs elevated; let WMI use the held privileges
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{settingId}'"));
            using var mo = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (mo is null)
                return new DnsApplyOutcome(adapterName, revert, servers, Array.Empty<string>(), DnsApplyStatus.Failed);

            using var inParams = mo.GetMethodParameters("SetDNSServerSearchOrder");
            // A null array resets DNS to DHCP-provided; a populated array pins it static.
            inParams["DNSServerSearchOrder"] = revert || servers.Count == 0 ? null : servers.ToArray();
            using var outParams = mo.InvokeMethod("SetDNSServerSearchOrder", inParams, null);

            uint ret = outParams?["ReturnValue"] is uint r ? r : uint.MaxValue;
            var status = MapReturn(ret);

            // MEASURE: re-read the authoritative static config to confirm what actually took effect.
            var measured = ReadStaticServers(settingId);
            return new DnsApplyOutcome(adapterName, revert, servers, measured, status);
        }
        catch
        {
            return new DnsApplyOutcome(adapterName, revert, servers, Array.Empty<string>(), DnsApplyStatus.Failed);
        }
    }

    /// <summary>
    /// Map a <c>SetDNSServerSearchOrder</c> WMI return code to our status: 0 ⇒ success, 1 ⇒ success-needs-reboot,
    /// anything else ⇒ failure (the documented error codes are all &gt; 1).
    /// </summary>
    internal static DnsApplyStatus MapReturn(uint code) => code switch
    {
        0 => DnsApplyStatus.Succeeded,
        1 => DnsApplyStatus.RebootRequired,
        _ => DnsApplyStatus.Failed
    };
}
