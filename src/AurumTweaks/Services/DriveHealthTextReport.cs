using System;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Santé des disques » report — the plain-text block a user pastes on a forum or a
/// support ticket asking the classic « est-ce que mon SSD est en train de mourir ? ». No I/O: it lays out the REAL
/// state the page already read from Windows (Get-PhysicalDisk joined to Get-StorageReliabilityCounter — Windows' own
/// SMART interpretation), leading with the shared <see cref="DriveHealthReport.Headline"/> and then every physical disk
/// with its medium, bus, capacity, Windows health, and the measured reliability counters. It carries the page's honesty
/// into the paste: a counter Windows didn't expose prints « — » (never a fabricated zero), a USB-bridged drive carries
/// the « les compteurs SMART sont souvent masqués » caveat so empty fields never read as « healthy », a failed query
/// says so plainly rather than inventing an all-clear, and the footer repeats that this is read-only, never sent, brings
/// no FPS, and that even « sain » is a prediction — back up anyway. Mirrors <see cref="DisplayTextReport"/>; the
/// clipboard write is thin glue in the VM.
/// </summary>
public static class DriveHealthTextReport
{
    private const int LabelWidth = 18;

    public static string Render(DriveHealthReport report, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Santé des disques");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        sb.AppendLine();
        sb.AppendLine(report.Headline);

        sb.AppendLine();
        if (!report.QueryOk)
        {
            sb.AppendLine("DISQUES");
            sb.AppendLine("  Lecture impossible — module de stockage Windows indisponible ou accès refusé.");
        }
        else if (report.Count == 0)
        {
            sb.AppendLine("DISQUES (0)");
            sb.AppendLine("  Aucun disque physique listé par Windows.");
        }
        else
        {
            sb.AppendLine($"DISQUES ({report.Count}) — {report.HealthyCount} sain(s), {report.WatchCount} à surveiller, {report.CriticalCount} en alerte");
            foreach (var d in report.Drives)
            {
                sb.AppendLine();
                sb.AppendLine($"  • {d.Name}  [{d.VerdictLabel}]");
                sb.AppendLine(Row("Type", d.MediaDisplay));
                sb.AppendLine(Row("Bus", d.IsUsb ? $"{d.BusDisplay} (externe)" : d.BusDisplay));
                sb.AppendLine(Row("Capacité", d.SizeDisplay));
                sb.AppendLine(Row("État Windows", d.HealthDisplay));
                sb.AppendLine(Row("Température", d.TemperatureDisplay));
                sb.AppendLine(Row("Usure", d.WearDisplay));
                sb.AppendLine(Row("Fonctionnement", d.PowerOnHoursDisplay));
                sb.AppendLine(Row("Erreurs non corr.", d.UncorrectedErrorsDisplay));
                sb.AppendLine($"    {d.VerdictMessage}");
                if (d.IsUsb)
                    sb.AppendLine("    Disque externe (USB) : le pont masque souvent les compteurs SMART — une mesure absente n'est pas « saine ».");
            }
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("État lu localement (interprétation du SMART par Windows) et jamais envoyé. Surveillance matérielle :");
        sb.AppendLine("aucun gain de FPS. Un état « sain » reste une prédiction, pas une garantie — sauvegarde tes données.");
        return sb.ToString();
    }

    private static string Row(string label, string value) => $"    {label.PadRight(LabelWidth)}: {value}";
}
