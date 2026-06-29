using System;
using System.Text;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Périphériques (entrée) » report — the plain-text block a user pastes when asking the
/// FR scene's « quelle est ma config souris/clavier, l'accélération est-elle off ? ». No I/O: it lays out the REAL state
/// the page already read — connected HID devices and how they're wired, the vendor app that actually owns the polling
/// rate, and the Windows pointer-acceleration flag — leading with the page's own <see cref="InputTuningReport.Summary"/>.
/// It carries this page's signature honesty into the paste: the TRUE USB polling rate (125/500/1000/4000/8000 Hz) is NOT
/// readable from Windows without a kernel filter driver, so no Hz figure is ever invented — the guidance says so plainly
/// and the footer repeats it. Read-only, never sent, no guaranteed FPS gain. Mirrors <see cref="DriveHealthTextReport"/>;
/// the clipboard write is thin glue in the VM.
/// </summary>
public static class InputDeviceTextReport
{
    private const int LabelWidth = 12;

    public static string Render(InputTuningReport report, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Périphériques (entrée)");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        sb.AppendLine();
        sb.AppendLine(report.Summary);

        sb.AppendLine();
        if (report.Devices.Count == 0)
        {
            sb.AppendLine("PÉRIPHÉRIQUES (0)");
            sb.AppendLine("  Aucun périphérique HID détecté par Windows.");
        }
        else
        {
            sb.AppendLine($"PÉRIPHÉRIQUES ({report.Devices.Count})");
            foreach (var d in report.Devices)
            {
                sb.AppendLine();
                sb.AppendLine($"  • {d.Name}");
                sb.AppendLine($"    {d.Summary}");
                if (!string.IsNullOrWhiteSpace(d.Manufacturer))
                    sb.AppendLine(Row("Fabricant", d.Manufacturer.Trim()));
            }
        }

        sb.AppendLine();
        sb.AppendLine("LOGICIELS CONSTRUCTEUR");
        if (report.HasDetectedSoftware)
            foreach (var s in report.DetectedSoftware)
                sb.AppendLine($"  • {s}");
        else
            sb.AppendLine("  Aucun logiciel constructeur détecté en cours d'exécution.");

        sb.AppendLine();
        sb.AppendLine("ACCÉLÉRATION SOURIS");
        sb.AppendLine($"  {report.MouseAccelerationText}");

        if (report.Guidance.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("POLLING RATE / LATENCE");
            foreach (var g in report.Guidance)
                sb.AppendLine($"  • {g}");
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("État lu localement et jamais envoyé. Le vrai polling rate USB n'est pas lisible sans pilote noyau :");
        sb.AppendLine("aucun chiffre de Hz n'est inventé. Diagnostic d'entrée — aucun gain de FPS garanti.");
        return sb.ToString();
    }

    private static string Row(string label, string value) => $"    {label.PadRight(LabelWidth)}: {value}";
}
