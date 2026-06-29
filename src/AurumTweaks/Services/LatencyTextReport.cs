using System;
using System.Globalization;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Latence système (DPC/ISR) » report — the plain-text block a user pastes on a forum
/// or a support ticket when chasing micro-stutter or audio crackle. No I/O: it lays out the REAL measurement
/// (<see cref="LatencyReport"/>) the page already took — the verdict, the aggregate synthesis, and the per-core
/// breakdown (each core's own honest one-line <see cref="ProcessorLoad.SummaryDisplay"/>). It keeps the page's honesty
/// line in the paste: the report measures HOW MUCH CPU time goes to DPC/ISR, not WHICH driver causes it, and a failed
/// measurement prints « Mesure impossible » rather than a fabricated zero-load sheet. The level label is the shared
/// <see cref="LatencyVerdict.Label"/> so the paste and the on-screen badge can't drift. Mirrors <see cref="SystemReport"/>;
/// the clipboard write is thin glue in the VM.
/// </summary>
public static class LatencyTextReport
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const int LabelWidth = 20;

    public static string Render(LatencyReport report, DateTime generatedUtc, TimerResolutionReading? timer = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Latence système (DPC/ISR)");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        if (!report.QueryOk)
        {
            sb.AppendLine();
            sb.AppendLine("Mesure impossible — les compteurs de temps DPC/ISR du noyau n'ont pas pu être lus");
            sb.AppendLine("(droits insuffisants ou source indisponible).");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine($"Fenêtre : {report.MeasurementSeconds.ToString("0.#", Fr)} s sur {report.CpuCount} cœur(s) logique(s)");

        var verdict = report.Verdict;
        sb.AppendLine();
        sb.AppendLine($"VERDICT : {LatencyVerdict.Label(verdict.Level)}");
        sb.AppendLine($"  {verdict.Message}");

        sb.AppendLine();
        sb.AppendLine("SYNTHÈSE");
        sb.AppendLine(Row("DPC max", report.MaxDpcDisplay));
        sb.AppendLine(Row("ISR max", report.MaxInterruptDisplay));
        sb.AppendLine(Row("Interruptions", report.TotalInterruptRateDisplay));
        sb.AppendLine(Row("Cœur le plus chargé", report.WorstDpcDisplay));

        // Optional companion readout: the live timer resolution the page now shows (omitted if it couldn't be read).
        if (timer is { QueryOk: true })
        {
            sb.AppendLine();
            sb.AppendLine("MINUTEUR SYSTÈME");
            sb.AppendLine(Row("Résolution actuelle", timer.CurrentDisplay));
            sb.AppendLine(Row("Maximum supporté", timer.BestDisplay));
            sb.AppendLine(Row("Défaut Windows", timer.DefaultDisplay));
            sb.AppendLine($"  {timer.Headline}");
            sb.AppendLine("  Lecture seule ; depuis Windows 10 (2004) la résolution est gérée par processus (valeur globale indicative).");
        }

        sb.AppendLine();
        sb.AppendLine("DÉTAIL PAR CŒUR LOGIQUE");
        foreach (var p in report.PerCpu)
            sb.AppendLine($"  {p.SummaryDisplay}");

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("Diagnostic informatif : il indique COMBIEN de temps CPU part en DPC/ISR, pas QUEL pilote en est");
        sb.AppendLine("responsable (cela demande une trace ETW : LatencyMon, DPC Latency Checker). Seuils indicatifs,");
        sb.AppendLine("mesuré localement et jamais envoyé.");
        return sb.ToString();
    }

    private static string Row(string label, string value) => $"  {label.PadRight(LabelWidth)}: {value}";
}
