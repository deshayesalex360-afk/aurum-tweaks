using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

/// <summary>
/// Pure, side-effect-free aggregation of ping round-trip samples into the user-facing
/// <see cref="NetworkRouteSnapshot"/>. Deliberately split out of <see cref="NetworkOptiService"/>
/// (the same pattern as <c>GpuOcValidation</c>) so the numbers shown on the Gaming page — average
/// latency, jitter and packet loss — can be pinned by tests without ever opening a socket.
/// </summary>
public static class NetworkRouteMath
{
    /// <summary>
    /// Reduce raw ping round-trip times (ms) to a snapshot. A <b>negative</b> entry denotes a
    /// failed/timed-out probe (a lost packet). <see cref="NetworkRouteSnapshot.PingMs"/> is the mean of
    /// the successful probes, <see cref="NetworkRouteSnapshot.JitterMs"/> is their population standard
    /// deviation, and <see cref="NetworkRouteSnapshot.PacketLossPct"/> is failures ÷ samples × 100.
    /// HopCount is always 0 — no traceroute is performed, and we never invent one.
    /// </summary>
    public static NetworkRouteSnapshot Aggregate(IReadOnlyList<long> roundTripTimesMs)
    {
        int samples = roundTripTimesMs.Count;
        var good = roundTripTimesMs.Where(t => t >= 0).ToArray();
        int failures = samples - good.Length;

        // No usable sample: report 100% loss when probes were actually sent, but never divide by zero
        // on an empty input (a degenerate case the live service — fixed 8 samples — never produces).
        if (good.Length == 0)
            return new NetworkRouteSnapshot(0, 0, samples == 0 ? 0f : 100f, 0);

        float avg = (float)good.Average();
        float jitter = good.Length > 1
            ? (float)System.Math.Sqrt(good.Average(v => (v - avg) * (v - avg)))
            : 0f;
        float lossPct = (float)failures / samples * 100f;
        return new NetworkRouteSnapshot(avg, jitter, lossPct, 0);
    }
}

/// <summary>The competitive-stability rating of a measured path; worst component sets the overall grade.</summary>
public enum NetworkQuality { Excellent, Good, Fair, Poor }

/// <summary>
/// Pure, honest interpretation of a <see cref="NetworkRouteSnapshot"/> as a connection-STABILITY grade for
/// competitive play (same split-out pattern as <see cref="NetworkRouteMath"/>, the same decision-core idea as
/// <c>BiosApplyAdvisor</c>/<c>ProfileApplyRisk</c>). The grade is the WORST of three component ratings — packet
/// loss, then jitter, then latency — so one bad dimension can never hide behind two good ones (a 100%-loss path is
/// never "Excellent" just because its ping rounds to 0). Thresholds are fixed and documented; the verdict is
/// explicitly INDICATIVE — it rates the measured path to the CHOSEN TARGET, not the latency to a game server, and
/// it is never a promise about FPS. Loss and jitter dominate deliberately: they are the line-stability signals that
/// generalize, whereas absolute ping depends on which target the user picked.
/// </summary>
public sealed record NetworkQualityGrade(NetworkQuality Quality, string Label, string Detail)
{
    public static NetworkQualityGrade Assess(NetworkRouteSnapshot s)
    {
        var loss = GradeLoss(s.PacketLossPct);
        var jitter = GradeJitter(s.JitterMs);
        var ping = GradePing(s.PingMs);

        var overall = Worst(loss, Worst(jitter, ping));   // the limiting factor sets the grade
        return new NetworkQualityGrade(overall, LabelFor(overall), DetailFor(overall, loss, jitter, ping, s));
    }

    // Any loss hurts a real-time game; zero is the only "excellent".
    private static NetworkQuality GradeLoss(float pct) =>
        pct <= 0f ? NetworkQuality.Excellent
        : pct <= 1f ? NetworkQuality.Good
        : pct <= 5f ? NetworkQuality.Fair
        : NetworkQuality.Poor;

    // Jitter is what causes rubber-banding — it weighs as heavily as loss.
    private static NetworkQuality GradeJitter(float ms) =>
        ms < 2f ? NetworkQuality.Excellent
        : ms < 8f ? NetworkQuality.Good
        : ms < 16f ? NetworkQuality.Fair
        : NetworkQuality.Poor;

    // Absolute latency to the chosen target — context, not a game-server promise.
    private static NetworkQuality GradePing(float ms) =>
        ms < 20f ? NetworkQuality.Excellent
        : ms < 50f ? NetworkQuality.Good
        : ms < 100f ? NetworkQuality.Fair
        : NetworkQuality.Poor;

    private static NetworkQuality Worst(NetworkQuality a, NetworkQuality b) =>
        (NetworkQuality)System.Math.Max((int)a, (int)b);

    private static string LabelFor(NetworkQuality q) => q switch
    {
        NetworkQuality.Excellent => "Excellent",
        NetworkQuality.Good => "Bon",
        NetworkQuality.Fair => "Moyen",
        _ => "Médiocre"
    };

    private const string Caveat = " (indicatif : mesuré vers la cible, pas le serveur de jeu)";

    private static string DetailFor(NetworkQuality overall, NetworkQuality loss, NetworkQuality jitter, NetworkQuality ping,
                                    NetworkRouteSnapshot s)
    {
        if (overall == NetworkQuality.Excellent)
            return $"Aucune perte, gigue {s.JitterMs:F1} ms, ping {s.PingMs:F0} ms{Caveat}.";

        // Name the dominant limiting factor — the first, by impact order (loss → jitter → ping), matching the grade.
        string cause =
            loss == overall ? $"perte de paquets {s.PacketLossPct:F0} %"
            : jitter == overall ? $"gigue {s.JitterMs:F1} ms"
            : $"latence {s.PingMs:F0} ms";
        return $"Limité par la {cause}{Caveat}.";
    }
}

/// <summary>
/// Network latency measurement. Real ExitLag-style multipath routing would require a TUN/WFP driver
/// shipped separately — this service is the diagnostic layer underneath. It sends a fixed burst of
/// ICMP probes and hands the raw samples to <see cref="NetworkRouteMath.Aggregate"/> for the maths.
/// </summary>
public sealed class NetworkOptiService : INetworkOptiService
{
    public async Task<NetworkRouteSnapshot> MeasureAsync(string host)
    {
        using var ping = new Ping();   // Ping holds a native socket handle — dispose it, don't leak per measure.
        const int samples = 8;
        var times = new long[samples];
        for (int i = 0; i < samples; i++)
        {
            try
            {
                var r = await ping.SendPingAsync(host, 1500);
                times[i] = r.Status == IPStatus.Success ? r.RoundtripTime : -1;
            }
            catch
            {
                times[i] = -1;
            }
        }
        return NetworkRouteMath.Aggregate(times);
    }

    /// <summary>
    /// Trace the route to <paramref name="host"/> with real increasing-TTL ICMP probes — the same technique
    /// as <c>tracert</c>. Each intermediate router answers <c>TtlExpired</c> with its own address; a
    /// non-responding hop is left as a "*" (never an invented address or latency). Stops once the destination
    /// replies or the hop budget is spent. The per-hop classification + counting live in the pure
    /// <see cref="TracerouteMath"/>, so what the user sees on the Gaming page is unit-testable without a socket.
    /// </summary>
    public async Task<TracerouteReport> TraceRouteAsync(string host)
    {
        const int maxHops = 30;
        const int timeoutMs = 1000;

        if (string.IsNullOrWhiteSpace(host))
            return TracerouteMath.Failed("Aucune cible à tracer.");

        // Resolve once up front (prefer IPv4) so a DNS failure is reported immediately instead of being
        // retried 30× against an unresolvable name, and every hop targets the same address.
        IPAddress? target;
        try
        {
            target = (await Dns.GetHostAddressesAsync(host))
                .OrderByDescending(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .FirstOrDefault();
        }
        catch
        {
            target = null;
        }
        if (target is null)
            return TracerouteMath.Failed($"Hôte « {host} » introuvable (résolution DNS échouée).");

        var buffer = new byte[32];
        var probes = new List<TracerouteProbe>(maxHops);

        using var ping = new Ping();
        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            try
            {
                var reply = await ping.SendPingAsync(target, timeoutMs, buffer, new PingOptions(ttl, true));
                probes.Add(new TracerouteProbe(reply.Status, reply.Address?.ToString(), reply.RoundtripTime));
                if (reply.Status == IPStatus.Success)
                    break;   // reached the destination — no point probing further hops
            }
            catch
            {
                // A single failed probe is just a non-responding hop; keep tracing the rest of the route.
                probes.Add(new TracerouteProbe(IPStatus.Unknown, null, 0));
            }
        }

        return TracerouteMath.Build(probes);
    }

    /// <summary>
    /// Benchmark the curated public resolvers by timing real DNS A-record queries against each (a fresh
    /// connected UDP socket per query, the txn-id checked on the reply so a stray packet can't be mistaken
    /// for an answer), then hand the raw per-resolver latencies to <see cref="DnsBenchmarkMath"/> to rank.
    /// Resolvers are independent servers so they're probed in parallel (DNS datagrams are tiny — sharing the
    /// uplink doesn't skew per-server RTT), but each resolver's own queries stay sequential for clean timings.
    /// </summary>
    public async Task<DnsBenchmarkReport> BenchmarkDnsAsync()
    {
        // The same real, well-known names hit every resolver → identical workload, fair comparison.
        string[] domains = { "google.com", "youtube.com", "cloudflare.com", "wikipedia.org" };
        const int timeoutMs = 1000;

        // Add the user's own system resolver so the verdict is actionable ("switch to save X ms"). If it's
        // already one of the curated entries, flag that one instead of probing the same address twice.
        var resolvers = new List<DnsResolver>(DnsBenchmarkMath.DefaultResolvers);
        var systemDns = GetSystemDnsResolver();
        if (systemDns is not null)
        {
            int existing = resolvers.FindIndex(r => r.Address == systemDns);
            if (existing >= 0)
                resolvers[existing] = resolvers[existing] with { IsCurrent = true };
            else
                resolvers.Insert(0, new DnsResolver("DNS actuel", systemDns, IsCurrent: true));
        }

        var results = await Task.WhenAll(resolvers.Select(r => MeasureResolverAsync(r, domains, timeoutMs)));
        return DnsBenchmarkMath.Rank(results);
    }

    /// <summary>
    /// The first non-loopback IPv4 DNS server Windows is actually configured to use, or null if none is readable.
    /// Best-effort and never throws — a missing current resolver just drops the "vs your DNS" comparison.
    /// </summary>
    private static string? GetSystemDnsResolver()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(ni => ni.GetIPProperties().DnsAddresses)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                ?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<DnsProbeResult> MeasureResolverAsync(DnsResolver resolver, string[] domains, int timeoutMs)
    {
        var latencies = new List<double>(domains.Length);
        if (IPAddress.TryParse(resolver.Address, out var ip))
        {
            foreach (var domain in domains)
            {
                double? ms = await QueryOnceAsync(ip, domain, timeoutMs);
                if (ms.HasValue) latencies.Add(ms.Value);
            }
        }
        return DnsBenchmarkMath.Summarize(resolver, latencies, domains.Length);
    }

    private static async Task<double?> QueryOnceAsync(IPAddress resolver, string domain, int timeoutMs)
    {
        try
        {
            ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            byte[] query = DnsQueryPacket.Build(domain, id);

            using var udp = new UdpClient(resolver.AddressFamily);
            udp.Connect(resolver, 53);   // connected UDP → only this resolver's replies reach us

            using var cts = new CancellationTokenSource(timeoutMs);
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(query, cts.Token);
            var reply = await udp.ReceiveAsync(cts.Token);
            sw.Stop();

            // Only count it when the reply is genuinely ours (matching id + QR bit) — never a fabricated time.
            return DnsQueryPacket.IsValidResponse(reply.Buffer, id) ? sw.Elapsed.TotalMilliseconds : null;
        }
        catch
        {
            // Timeout, port 53 blocked, host unreachable — honestly "no answer", never an invented latency.
            return null;
        }
    }
}

/// <summary>One raw traceroute probe straight off the socket — the input to <see cref="TracerouteMath.Build"/>.</summary>
public sealed record TracerouteProbe(IPStatus Status, string? Address, long RttMs);

/// <summary>What a single increasing-TTL probe turned out to be.</summary>
public enum TracerouteHopStatus
{
    /// <summary>An intermediate router replied (TTL expired in transit) — we have its address + round-trip.</summary>
    Replied,

    /// <summary>The destination itself answered — the route is complete at this hop.</summary>
    Destination,

    /// <summary>No usable reply — rendered "*", never given an invented address or latency.</summary>
    Timeout
}

/// <summary>One hop on the traced route. A <see cref="TracerouteHopStatus.Timeout"/> hop carries no address and <c>RttMs == -1</c>.</summary>
public sealed record TracerouteHop(int Ttl, string? Address, long RttMs, TracerouteHopStatus Status)
{
    /// <summary>Router address, or "*" when the hop didn't answer (we never show a fabricated address).</summary>
    public string AddressDisplay => Status == TracerouteHopStatus.Timeout ? "*" : (Address ?? "*");

    /// <summary>Round-trip text, or "—" for a non-responding hop (no invented latency).</summary>
    public string RttDisplay => Status == TracerouteHopStatus.Timeout ? "—" : $"{RttMs} ms";
}

/// <summary>The largest single-hop latency increase along the route (indicative only — RTTs aren't monotonic).</summary>
public sealed record LatencyJump(int Ttl, string? Address, long DeltaMs);

/// <summary>The full traced route plus an honest one-line summary.</summary>
public sealed record TracerouteReport(IReadOnlyList<TracerouteHop> Hops, bool DestinationReached, int HopCount, string Summary);

/// <summary>
/// Pure, socket-free reduction of raw traceroute probes into the user-facing <see cref="TracerouteReport"/>
/// (the same split-out pattern as <see cref="NetworkRouteMath"/>). This is the honesty boundary: a
/// non-responding hop becomes a "*" with no address and no latency, so the route shown can never contain an
/// invented hop, and the hop count / destination-reached verdict can be pinned by tests.
/// </summary>
public static class TracerouteMath
{
    public static TracerouteHopStatus Classify(IPStatus status) => status switch
    {
        IPStatus.Success => TracerouteHopStatus.Destination,
        IPStatus.TtlExpired => TracerouteHopStatus.Replied,
        _ => TracerouteHopStatus.Timeout
    };

    public static TracerouteReport Build(IReadOnlyList<TracerouteProbe> probes)
    {
        var hops = new List<TracerouteHop>(probes.Count);
        int destinationTtl = 0;
        for (int i = 0; i < probes.Count; i++)
        {
            int ttl = i + 1;
            var status = Classify(probes[i].Status);
            hops.Add(status == TracerouteHopStatus.Timeout
                ? new TracerouteHop(ttl, null, -1, status)                       // drop any socket-reported 0.0.0.0 / bogus time
                : new TracerouteHop(ttl, probes[i].Address, probes[i].RttMs, status));
            if (status == TracerouteHopStatus.Destination && destinationTtl == 0)
                destinationTtl = ttl;
        }

        bool reached = destinationTtl > 0;
        int hopCount = reached ? destinationTtl : hops.Count;
        return new TracerouteReport(hops, reached, hopCount, BuildSummary(reached, hopCount, hops));
    }

    /// <summary>
    /// The largest positive latency step between two consecutive RESPONDING hops, or null when fewer than two
    /// answered. Indicative only: intermediate-router ICMP is de-prioritised so RTTs along a route are NOT
    /// monotonic — this names where latency rises most, it does not prove a culprit (the caller labels it so).
    /// </summary>
    public static LatencyJump? BiggestLatencyJump(IReadOnlyList<TracerouteHop> hops)
    {
        LatencyJump? best = null;
        long prevRtt = -1;
        foreach (var h in hops)
        {
            if (h.Status == TracerouteHopStatus.Timeout || h.RttMs < 0)
                continue;
            if (prevRtt >= 0)
            {
                long delta = h.RttMs - prevRtt;
                if (delta > 0 && (best is null || delta > best.DeltaMs))
                    best = new LatencyJump(h.Ttl, h.Address, delta);
            }
            prevRtt = h.RttMs;
        }
        return best;
    }

    /// <summary>An honest empty report carrying the reason it couldn't run (bad target, DNS failure).</summary>
    public static TracerouteReport Failed(string reason) =>
        new(Array.Empty<TracerouteHop>(), false, 0, reason);

    private static string BuildSummary(bool reached, int hopCount, IReadOnlyList<TracerouteHop> hops)
    {
        if (hops.Count == 0)
            return "Aucun saut mesuré.";
        int responding = hops.Count(h => h.Status != TracerouteHopStatus.Timeout);
        return reached
            ? $"Destination atteinte en {hopCount} saut(s) — {responding} ont répondu."
            : $"Destination non atteinte après {hops.Count} saut(s) — {responding} ont répondu.";
    }
}

/// <summary>
/// A DNS resolver the benchmark probes — a friendly name and its IPv4 address. <see cref="IsCurrent"/> marks
/// the user's own system resolver when it's added to the run, so the UI can flag it and the verdict can say
/// whether switching is worth it.
/// </summary>
public sealed record DnsResolver(string Name, string Address, bool IsCurrent = false);

/// <summary>
/// One resolver's benchmark outcome: the median round-trip of the queries it actually answered, plus how many
/// of the attempts succeeded. A resolver that never answered has <see cref="Responded"/> false and
/// <see cref="MedianMs"/> −1 — the UI shows "—", never an invented latency.
/// </summary>
public sealed record DnsProbeResult(DnsResolver Resolver, bool Responded, double MedianMs, int Successes, int Attempts)
{
    /// <summary>Median latency text, or "—" when the resolver didn't answer (no fabricated number).</summary>
    public string LatencyDisplay => Responded ? $"{MedianMs:F0} ms" : "—";

    /// <summary>How many probes answered out of how many were sent (e.g. "4/4") — honest about partial loss.</summary>
    public string ReliabilityDisplay => $"{Successes}/{Attempts}";
}

/// <summary>The ranked benchmark: resolvers fastest-first (silent ones last), the winner, and a one-line verdict.</summary>
public sealed record DnsBenchmarkReport(IReadOnlyList<DnsProbeResult> Ranked, DnsResolver? Fastest, string Summary);

/// <summary>
/// Pure builder/validator for a minimal DNS-over-UDP A-record query (RFC 1035). Split out of the socket glue
/// so the exact wire bytes — and the rule for what counts as a valid reply — are unit-testable without a
/// network. <see cref="Build"/> emits a single recursion-desired question; <see cref="IsValidResponse"/> only
/// accepts a datagram whose transaction id matches and whose QR bit is set, so a stray/late packet can never
/// be counted as the resolver's answer (which would fabricate a latency).
/// </summary>
public static class DnsQueryPacket
{
    /// <summary>
    /// Build the query for <paramref name="domain"/> with transaction id <paramref name="id"/>: 12-byte header
    /// (RD set, one question) + QNAME (length-prefixed ASCII labels, root-terminated) + QTYPE A + QCLASS IN.
    /// Throws on an empty/over-long (&gt;63) / non-ASCII label rather than emit a corrupt packet.
    /// </summary>
    public static byte[] Build(string domain, ushort id)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("A domain is required.", nameof(domain));

        var labels = domain.Trim().Trim('.').Split('.');
        int qnameLen = labels.Sum(l => 1 + l.Length) + 1;   // each label: 1 length byte + bytes; + root 0x00
        var packet = new byte[12 + qnameLen + 4];

        packet[0] = (byte)(id >> 8);
        packet[1] = (byte)(id & 0xFF);
        packet[2] = 0x01;   // flags hi: RD (recursion desired) = 1, QR = 0 (query)
        // packet[3] flags lo = 0
        packet[5] = 0x01;   // QDCOUNT = 1 (packet[4] high byte stays 0); AN/NS/AR counts stay 0

        int pos = 12;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
                throw new ArgumentException($"Invalid DNS label '{label}'.", nameof(domain));
            packet[pos++] = (byte)label.Length;
            foreach (char c in label)
            {
                if (c > 0x7F)   // non-ASCII would need punycode; never silently truncate to a wrong byte
                    throw new ArgumentException($"Non-ASCII DNS label '{label}'.", nameof(domain));
                packet[pos++] = (byte)c;
            }
        }
        packet[pos++] = 0x00;          // root label terminator
        packet[pos++] = 0x00; packet[pos++] = 0x01;   // QTYPE  = A
        packet[pos++] = 0x00; packet[pos++] = 0x01;   // QCLASS = IN
        return packet;
    }

    /// <summary>True only when <paramref name="response"/> is a well-formed reply to id <paramref name="expectedId"/> (QR bit set).</summary>
    public static bool IsValidResponse(byte[]? response, ushort expectedId)
    {
        if (response is null || response.Length < 12)
            return false;
        ushort id = (ushort)((response[0] << 8) | response[1]);
        if (id != expectedId)
            return false;
        return (response[2] & 0x80) != 0;   // QR = 1 → it's a response, not our query echoed back
    }
}

/// <summary>
/// Pure ranking maths for the DNS benchmark (same split-out pattern as <see cref="NetworkRouteMath"/>): turns
/// each resolver's raw answered-query latencies into a <see cref="DnsProbeResult"/> (median over what answered)
/// and orders them fastest-first with non-responders honestly kept last. The recommendation is simply the
/// lowest measured median — no fabrication, and switching DNS stays a manual step the user performs.
/// </summary>
public static class DnsBenchmarkMath
{
    /// <summary>The curated public resolvers probed by the benchmark — one well-known entry per provider.</summary>
    public static IReadOnlyList<DnsResolver> DefaultResolvers { get; } = new[]
    {
        new DnsResolver("Cloudflare", "1.1.1.1"),
        new DnsResolver("Google", "8.8.8.8"),
        new DnsResolver("Quad9", "9.9.9.9"),
        new DnsResolver("OpenDNS", "208.67.222.222"),
        new DnsResolver("AdGuard", "94.140.14.14"),
    };

    /// <summary>Median of the values (mean of the two middle ones for an even count); 0 on empty (callers guard).</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Reduce one resolver's answered-query latencies (ms) to its result; empty ⇒ did not respond.</summary>
    public static DnsProbeResult Summarize(DnsResolver resolver, IReadOnlyList<double> latenciesMs, int attempts)
    {
        bool responded = latenciesMs.Count > 0;
        return new DnsProbeResult(resolver, responded, responded ? Median(latenciesMs) : -1, latenciesMs.Count, attempts);
    }

    /// <summary>Order resolvers fastest-first (responders by median asc), keep non-responders last, name the winner.</summary>
    public static DnsBenchmarkReport Rank(IReadOnlyList<DnsProbeResult> results)
    {
        var responders = results.Where(r => r.Responded).OrderBy(r => r.MedianMs).ToList();
        var silent = results.Where(r => !r.Responded).ToList();
        var ranked = responders.Concat(silent).ToList();

        var fastest = responders.FirstOrDefault();
        string summary = fastest is null
            ? "Aucun résolveur DNS n'a répondu — vérifie ta connexion ou un pare-feu bloquant le port 53 (UDP)."
            : $"Le plus rapide : {fastest.Resolver.Name} ({fastest.Resolver.Address}) à {fastest.MedianMs:F0} ms médian. "
              + "Définis-le comme DNS dans les propriétés de ta carte réseau pour l'appliquer.";
        return new DnsBenchmarkReport(ranked, fastest?.Resolver, summary);
    }

    /// <summary>
    /// The actionable one-liner: how the user's current system resolver (the <see cref="DnsResolver.IsCurrent"/>
    /// entry) compares to the fastest measured one. Returns null when the run carried no current resolver, says
    /// so honestly when the current one didn't answer (never inventing a delta), and only suggests switching
    /// when a measured resolver actually beat it.
    /// </summary>
    public static string? CompareToCurrent(DnsBenchmarkReport report)
    {
        var current = report.Ranked.FirstOrDefault(r => r.Resolver.IsCurrent);
        if (current is null)
            return null;
        if (!current.Responded)
            return $"Ton DNS actuel ({current.Resolver.Address}) n'a pas répondu au test.";

        var fastest = report.Ranked.FirstOrDefault(r => r.Responded);
        double delta = fastest is null ? 0 : current.MedianMs - fastest.MedianMs;
        if (fastest is null || fastest.Resolver.IsCurrent || delta <= 0)
            return $"Ton DNS actuel ({current.Resolver.Address}) est déjà le plus rapide ({current.MedianMs:F0} ms médian).";

        return $"{fastest.Resolver.Name} ({fastest.Resolver.Address}) est {delta:F0} ms plus rapide que ton DNS "
             + $"actuel ({current.MedianMs:F0} ms) — envisage de le changer.";
    }
}
