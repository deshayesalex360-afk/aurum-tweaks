using System;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Affichage » report — the plain-text block a user pastes on a forum or a support
/// ticket when chasing the classic missed setting (a 144/240 Hz panel left at 60 Hz). No I/O: it lays out the REAL state
/// the page already read from the Win32 display API — the synthesis headline, then every attached monitor with its live
/// mode, orientation, the best advertised rate at its current resolution, and the same honest per-monitor
/// <see cref="MonitorState.Verdict"/> shown on screen. It keeps the page's honesty in the paste: an unreadable mode prints
/// « illisible », a monitor whose modes couldn't be enumerated prints « non énumérée » (never a fake « you are at max »),
/// and the footer repeats that a higher rate smooths motion but does NOT raise the FPS the GPU produces, and that the
/// report is read-only and never sent. Mirrors <see cref="PowerPlanTextReport"/>; the clipboard write is thin glue in the VM.
/// </summary>
public static class DisplayTextReport
{
    private const int LabelWidth = 14;

    public static string Render(DisplayReport report, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Affichage");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        sb.AppendLine();
        sb.AppendLine(report.Headline);

        sb.AppendLine();
        sb.AppendLine($"ÉCRANS ({report.Count})");
        if (!report.Any)
        {
            sb.AppendLine("  Aucun écran actif lu — l'API d'affichage Windows n'a rien renvoyé.");
        }
        else
        {
            foreach (var m in report.Monitors)
            {
                sb.AppendLine();
                sb.AppendLine($"  • {m.DisplayName}{(m.IsPrimary ? "  (principal)" : string.Empty)}");
                sb.AppendLine(Row("Mode courant", m.CurrentLabel));
                sb.AppendLine(Row("Orientation", m.OrientationLabel));
                sb.AppendLine(Row("Fréquence max", MaxRefresh(m)));
                sb.AppendLine(Row("Périphérique", m.DeviceName));
                sb.AppendLine($"    {m.Verdict}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("Lu localement via l'API d'affichage Windows et jamais envoyé. Une fréquence plus élevée fluidifie");
        sb.AppendLine("le mouvement et réduit la latence d'affichage — elle n'augmente pas les FPS que le GPU produit.");
        sb.AppendLine("Rapport en lecture seule — changer la fréquence se fait dans Aurum ou les paramètres Windows.");
        return sb.ToString();
    }

    // The best advertised rate at the monitor's CURRENT resolution, or an honest « non énumérée » when no mode matched.
    private static string MaxRefresh(MonitorState m) =>
        m.MaxRefreshAtCurrent > 0 ? $"{m.MaxRefreshAtCurrent} Hz à {m.Current.ResolutionLabel}" : "non énumérée";

    private static string Row(string label, string value) => $"    {label.PadRight(LabelWidth)}: {value}";
}
