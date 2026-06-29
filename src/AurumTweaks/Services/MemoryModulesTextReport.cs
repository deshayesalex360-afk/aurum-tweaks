using System;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Barrettes mémoire » report — the plain-text block a user pastes when asking the FR
/// scene's classic « ma RAM tourne-t-elle en double canal à la bonne vitesse ? ». No I/O: it lays out the REAL state the
/// page already read from Windows (Win32_PhysicalMemory), leading with the page's own
/// <see cref="MemoryModulesReport.ProfileHeadline"/> so the paste matches the screen, then the synthesis (type, total,
/// slots, configured/rated speed, channel HINT) and every populated module with its slot, capacity and speed. It carries
/// the page's honesty into the paste: the XMP/EXPO verdict is explicitly indicative (Windows' « nominale » is often the
/// JEDEC base, not the kit's XMP/EXPO note), the channel mode is a probability inferred from the module count (Windows
/// can't read the real channel mode), an unreported capacity/speed prints « — » (never a fabricated 0), and the footer
/// repeats this is read-only, never sent, brings no FPS, and that the profile is toggled in the BIOS — not here. Mirrors
/// <see cref="DriveHealthTextReport"/>; the clipboard write is thin glue in the VM.
/// </summary>
public static class MemoryModulesTextReport
{
    private const int LabelWidth = 18;

    public static string Render(MemoryModulesReport report, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Barrettes mémoire");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        sb.AppendLine();
        sb.AppendLine(report.ProfileHeadline);

        sb.AppendLine();
        sb.AppendLine("SYNTHÈSE");
        sb.AppendLine(Row("Type", report.TypeDisplay));
        sb.AppendLine(Row("Total installé", report.TotalDisplay));
        sb.AppendLine(Row("Barrettes / slots", report.SlotsDisplay));
        sb.AppendLine(Row("Vitesse configurée", report.SpeedDisplay));
        sb.AppendLine(Row("Vitesse nominale", report.RatedDisplay));
        sb.AppendLine(Row("Canaux (indicatif)", report.ChannelDisplay));

        sb.AppendLine();
        if (!report.HasModules)
        {
            sb.AppendLine("BARRETTES (0)");
            sb.AppendLine("  Aucune barrette listée par Windows.");
        }
        else
        {
            sb.AppendLine($"BARRETTES ({report.ModuleCount})");
            foreach (var m in report.Modules)
            {
                sb.AppendLine();
                sb.AppendLine($"  • {m.Identity}  [{m.Slot}]");
                sb.AppendLine(Row("Type", m.Type));
                sb.AppendLine(Row("Capacité", m.Capacity));
                sb.AppendLine(Row("Vitesse", m.SpeedDisplay));
                if (m.HasBankLabel)
                    sb.AppendLine(Row("Banque", m.BankLabel));
                if (m.BelowRated)
                    sb.AppendLine("    Sous la vitesse nominale rapportée par Windows.");
            }
        }

        sb.AppendLine();
        sb.AppendLine(report.ProfileDetail);

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("État lu localement (Win32_PhysicalMemory) et jamais envoyé. Lecture seule : aucune écriture, aucun gain de FPS.");
        sb.AppendLine("Le profil XMP/EXPO s'active dans le BIOS, pas ici — et Windows peut sous-estimer la vitesse réelle du kit.");
        return sb.ToString();
    }

    private static string Row(string label, string value) => $"    {label.PadRight(LabelWidth)}: {value}";
}
