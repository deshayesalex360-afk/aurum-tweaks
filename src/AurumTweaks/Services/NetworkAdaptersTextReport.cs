using System;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Cartes réseau » report — the plain-text block a user pastes when troubleshooting
/// connectivity (« pourquoi ma carte négocie-t-elle 100 Mb/s au lieu de 1 Gb/s ? », « quels DNS ? »). No I/O: it lays
/// out the REAL state the page already read from Windows (NetworkInterface), leading with the page's own
/// <see cref="NetworkAdaptersReport.Headline"/> and <see cref="NetworkAdaptersReport.Detail"/> so the paste matches the
/// screen, then every non-loopback adapter — active connection first — with its type, link state, link speed, IPv4, gateway,
/// DNS, MAC and (when it adds anything) the driver description. It carries the page's honesty into the paste: a down/unknown
/// link speed and an absent IP/DNS/gateway/MAC print « — » (never a fabricated « 0 b/s » or empty-as-OK), the active adapter
/// is the truthful default-route heuristic (up, non-loopback, holds a gateway), and the footer repeats this is read-only,
/// never sent and brings no FPS — plus a deliberate caveat that the paste carries local identifiers (IPv4/MAC/DNS) the user
/// should review before publishing. Mirrors <see cref="DriveHealthTextReport"/>; the clipboard write is thin glue in the VM.
/// </summary>
public static class NetworkAdaptersTextReport
{
    private const int LabelWidth = 16;

    public static string Render(NetworkAdaptersReport report, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Cartes réseau");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        sb.AppendLine();
        sb.AppendLine(report.Headline);
        sb.AppendLine(report.Detail);

        sb.AppendLine();
        if (!report.HasAdapters)
        {
            sb.AppendLine("CARTES (0)");
            sb.AppendLine("  Aucune carte réseau listée par Windows.");
        }
        else
        {
            sb.AppendLine($"CARTES ({report.AdapterCount}) — {report.CountDisplay}");
            foreach (var a in report.Adapters)
            {
                sb.AppendLine();
                sb.AppendLine($"  • {a.Name}  [{StatusLabel(a)}]");
                sb.AppendLine(Row("Type", a.TypeDisplay));
                sb.AppendLine(Row("État", a.StatusDisplay));
                sb.AppendLine(Row("Débit du lien", a.SpeedDisplay));
                sb.AppendLine(Row("IPv4", a.IPv4Display));
                sb.AppendLine(Row("Passerelle", a.GatewayDisplay));
                sb.AppendLine(Row("DNS", a.DnsDisplay));
                sb.AppendLine(Row("MAC", a.MacDisplay));
                if (a.HasDescription)
                    sb.AppendLine(Row("Pilote", a.Description));
            }
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("État lu localement et jamais envoyé. Surveillance réseau : aucun gain de FPS.");
        sb.AppendLine("Ce rapport contient des identifiants locaux (IPv4, MAC, DNS) — vérifie avant de le publier publiquement.");
        return sb.ToString();
    }

    // The active default-route adapter is the signal the user cares about; otherwise fall back to up/down.
    private static string StatusLabel(NetworkAdapterRow a) => a.IsActive ? "Active" : a.StatusDisplay;

    private static string Row(string label, string value) => $"    {label.PadRight(LabelWidth)}: {value}";
}
