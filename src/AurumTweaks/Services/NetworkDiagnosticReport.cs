using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Diagnostic réseau » — the plain-text block a user pastes on Discord or a forum
/// when asking « est-ce que ma connexion est le problème ? ». No I/O: it lays out the REAL measurements the Gaming page
/// already took (latency snapshot, traced route, DNS benchmark) and is honest about absence — a step the user hasn't run
/// yet prints « non mesuré » with the button to run it, never a fabricated zero or an empty table that reads as
/// « parfait ». The stability line reuses the tested <see cref="NetworkQualityGrade"/>, so the worst-component verdict and
/// its « mesuré vers la cible, pas le serveur de jeu » caveat travel with the paste. Numbers are formatted in fr-FR (the
/// shipping culture) so the output is deterministic regardless of the machine's locale. Mirrors <see cref="SystemReport"/>;
/// the clipboard write is thin glue in the VM.
/// </summary>
public static class NetworkDiagnosticReport
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const int LabelWidth = 10;

    public static string Render(
        string? target,
        NetworkRouteSnapshot? route,
        IReadOnlyList<TracerouteHop> hops,
        IReadOnlyList<DnsProbeResult> dns,
        DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Diagnostic réseau");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Cible : {Val(target)}");
        sb.AppendLine(new string('=', 48));

        sb.AppendLine();
        sb.AppendLine("LATENCE (vers la cible)");
        if (route is { } r)
        {
            var grade = NetworkQualityGrade.Assess(r);
            sb.AppendLine(Row("Ping", $"{r.PingMs.ToString("0.0", Fr)} ms"));
            sb.AppendLine(Row("Gigue", $"{r.JitterMs.ToString("0.0", Fr)} ms"));
            sb.AppendLine(Row("Perte", $"{r.PacketLossPct.ToString("0", Fr)} %"));
            sb.AppendLine(Row("Stabilité", $"{grade.Label} — {grade.Detail}"));
        }
        else
        {
            sb.AppendLine("  non mesuré — lance « Mesurer ».");
        }

        sb.AppendLine();
        sb.AppendLine("ROUTE (traceroute)");
        if (hops.Count > 0)
            foreach (var h in hops)
                sb.AppendLine($"  {h.Ttl,3}  {Pad(h.AddressDisplay, 22)}{h.RttDisplay}");
        else
            sb.AppendLine("  non tracée — lance « Tracer la route ».");

        sb.AppendLine();
        sb.AppendLine("DNS (résolveurs publics, du plus rapide au plus lent)");
        if (dns.Count > 0)
            foreach (var d in dns)
            {
                string current = d.Resolver.IsCurrent ? "  (actuel)" : "";
                sb.AppendLine(
                    $"  {Pad(d.Resolver.Name, 18)}{Pad(d.Resolver.Address, 16)}{Pad(d.LatencyDisplay, 9)}{d.ReliabilityDisplay}{current}");
            }
        else
            sb.AppendLine("  non mesuré — lance « Benchmark DNS ».");

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("Diagnostic informatif, mesuré localement vers la cible choisie (pas le serveur de jeu) et");
        sb.AppendLine("jamais envoyé. « non mesuré » = l'étape n'a pas encore été lancée.");
        return sb.ToString();
    }

    private static string Row(string label, string value) => $"  {label.PadRight(LabelWidth)}: {value}";
    private static string Pad(string? s, int width) => (s ?? string.Empty).PadRight(width);
    private static string Val(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
}
