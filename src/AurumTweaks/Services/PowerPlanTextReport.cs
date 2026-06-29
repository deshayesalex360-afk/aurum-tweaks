using System;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Plan d'alimentation » report — the plain-text block a user pastes on a forum or a
/// support ticket when triaging power/throttling behaviour. No I/O: it lays out the REAL state the page already read —
/// the active plan, every installed scheme (the active one marked exactly as powercfg flagged it), and the active plan's
/// processor detail « sur secteur » (min/max state, core parking) from <see cref="ProcessorPowerDetail"/>. It keeps the
/// page's honesty in the paste: the processor block prints « non lu » rather than a fabricated value when powercfg
/// couldn't read it, and the footer states the report is read-only and never sent. Mirrors <see cref="LatencyTextReport"/>;
/// the clipboard write is thin glue in the VM.
/// </summary>
public static class PowerPlanTextReport
{
    private const int LabelWidth = 18;

    public static string Render(PowerPlanReport plan, ProcessorPowerDetail? detail, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Plan d'alimentation");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        sb.AppendLine();
        sb.AppendLine($"Plan actif : {Val(plan.ActiveName)}");

        sb.AppendLine();
        sb.AppendLine("PLANS INSTALLÉS");
        if (plan.Schemes.Count == 0)
            sb.AppendLine("  Aucun plan lu — powercfg n'a rien renvoyé.");
        else
            foreach (var s in plan.Schemes)
                sb.AppendLine($"  {(s.IsActive ? "●" : " ")} {s.Name}  ({s.IdString})");

        sb.AppendLine();
        sb.AppendLine("DÉTAIL PROCESSEUR (sur secteur)");
        if (detail is { QueryOk: true })
        {
            sb.AppendLine(Row("État minimal", detail.MinStateDisplay));
            sb.AppendLine(Row("État maximal", detail.MaxStateDisplay));
            sb.AppendLine(Row("Parcage des cœurs", detail.CoreParkingDisplay));
            sb.AppendLine($"  {detail.Interpretation}");
        }
        else
        {
            sb.AppendLine("  non lu — powercfg n'a pas pu lire les paramètres processeur du plan actif.");
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("Lu localement via powercfg et jamais envoyé. Le détail processeur est « sur secteur ».");
        sb.AppendLine("Rapport en lecture seule — changer de plan se fait dans Aurum ou les options Windows.");
        return sb.ToString();
    }

    private static string Row(string label, string value) => $"  {label.PadRight(LabelWidth)}: {value}";
    private static string Val(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
}
