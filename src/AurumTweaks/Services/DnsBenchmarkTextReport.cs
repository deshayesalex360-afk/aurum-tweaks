using System;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Benchmark DNS » report — the plain-text block a user pastes on a forum or
/// Discord to back up « tel résolveur est le plus rapide chez moi ». No I/O: it lays out the REAL ranked measurement
/// (<see cref="DnsBenchmarkReport"/>) the « Serveurs DNS » page already took. Honesty-bearing and therefore unit-tested:
/// <list type="bullet">
/// <item>a resolver that never answered is shown as « — » with its reliability ratio, never an invented latency (the
/// row reuses the same tested <see cref="DnsProbeResult.LatencyDisplay"/> / <see cref="DnsProbeResult.ReliabilityDisplay"/>
/// the page renders);</item>
/// <item>the « actuel » marker travels with the user's own resolver, and the actionable current-vs-fastest line is the
/// shared <see cref="DnsBenchmarkMath.CompareToCurrent"/>, so the paste reads exactly what the page showed;</item>
/// <item>a run that never executed prints « Aucun benchmark exécuté » rather than an empty table that reads as a
/// result, and the footer keeps the load-bearing caveat — a faster DNS speeds up NAME RESOLUTION, not the in-game
/// ping, and switching the resolver stays a reversible step.</item>
/// </list>
/// Mirrors <see cref="NetworkDiagnosticReport"/>; the clipboard / file write is thin glue in the VM.
/// </summary>
public static class DnsBenchmarkTextReport
{
    private const int NameWidth = 18;
    private const int AddressWidth = 16;
    private const int LatencyWidth = 9;

    public static string Render(DnsBenchmarkReport? report, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Benchmark DNS");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        if (report is null || report.Ranked.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Aucun benchmark exécuté — lance « Comparer et appliquer le plus rapide » puis copie le rapport.");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine("CLASSEMENT (du plus rapide au plus lent, latence médiane)");
        foreach (var d in report.Ranked)
        {
            string current = d.Resolver.IsCurrent ? "  (actuel)" : "";
            sb.AppendLine(
                $"  {Pad(d.Resolver.Name, NameWidth)}{Pad(d.Resolver.Address, AddressWidth)}{Pad(d.LatencyDisplay, LatencyWidth)}{d.ReliabilityDisplay}{current}");
        }

        sb.AppendLine();
        sb.AppendLine("VERDICT");
        sb.AppendLine($"  {report.Summary}");
        var comparison = DnsBenchmarkMath.CompareToCurrent(report);
        if (comparison is not null)
            sb.AppendLine($"  {comparison}");

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("Latence médiane de plusieurs requêtes A, mesurée localement vers chaque résolveur (jamais envoyée).");
        sb.AppendLine("Un DNS plus rapide accélère la RÉSOLUTION DES NOMS (premier chargement, matchmaking), pas le ping en partie.");
        sb.AppendLine("« — » = le résolveur n'a pas répondu. Changer de DNS reste réversible (retour au DHCP automatique à tout moment).");
        return sb.ToString();
    }

    private static string Pad(string? s, int width) => (s ?? string.Empty).PadRight(width);
}
