using System;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Son » report — the plain-text block a user pastes on Discord or a forum when triaging
/// « pourquoi le son du jeu baisse quand Discord parle ? ». No I/O: it lays out the REAL state the page already read — the
/// Communications ducking preference with its honest verdict, the active system sound scheme, and the WMI audio-device
/// inventory (each device's status shown verbatim, healthy = ●, anything else = ○). It keeps the page's honesty in the
/// paste: the « Source » line distinguishes a value actually read from the registry, the implicit Windows default when no
/// value is set, and an outright failed read — never a fabricated preference; and the footer states that only the
/// communication preference is written/verified by Aurum (format, mode exclusif, audio spatial stay in Windows' panels)
/// and that the report is read-only and never sent. Mirrors <see cref="DisplayTextReport"/>; the clipboard write is thin
/// glue in the VM.
/// </summary>
public static class AudioTextReport
{
    private const int LabelWidth = 16;

    public static string Render(AudioReport report, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Son");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        sb.AppendLine();
        sb.AppendLine(report.Headline);
        if (!string.IsNullOrWhiteSpace(report.Detail))
            sb.AppendLine($"  {report.Detail}");

        sb.AppendLine();
        sb.AppendLine("COMMUNICATIONS (atténuation)");
        sb.AppendLine(Row("Préférence", report.DuckingDisplay));
        sb.AppendLine(Row("Source", SourceLine(report)));
        sb.AppendLine(Row("Recommandé jeu", report.IsRecommended
            ? "oui — « Ne rien faire »"
            : "non — « Ne rien faire » garde l'audio du jeu constant"));

        sb.AppendLine();
        sb.AppendLine("SONS SYSTÈME");
        sb.AppendLine(Row("Modèle de sons",
            report.SchemeDisplay + (report.SystemSoundsSilent ? "  (silencieux)" : string.Empty)));

        sb.AppendLine();
        sb.AppendLine($"PÉRIPHÉRIQUES AUDIO ({report.DeviceCount})");
        if (!report.HasDevices)
            sb.AppendLine("  Aucun périphérique listé — WMI n'a rien renvoyé.");
        else
            foreach (var d in report.Devices)
                sb.AppendLine($"  {(d.IsOk ? "●" : "○")} {d.NameDisplay}  [{d.ManufacturerDisplay}]  — {d.StatusDisplay}");

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("Lu localement via le registre et WMI, jamais envoyé. Seule la préférence « communication » est");
        sb.AppendLine("écrite et vérifiée par Aurum ; format, mode exclusif et audio spatial se règlent dans Windows.");
        sb.AppendLine("Rapport en lecture seule.");
        return sb.ToString();
    }

    // Honest provenance of the ducking value: a real registry read, the implicit Windows default (no value set), or a
    // failed read. Branches on the same (IsExplicit, Ducking) pair the page itself distinguishes.
    private static string SourceLine(AudioReport r)
    {
        if (r.IsExplicit) return "valeur lue dans le registre";
        return r.Ducking == AudioDucking.Unknown
            ? "non lue — réglages audio illisibles"
            : "implicite — aucune valeur définie (défaut Windows : réduire de 80 %)";
    }

    private static string Row(string label, string value) => $"  {label.PadRight(LabelWidth)}: {value}";
}
