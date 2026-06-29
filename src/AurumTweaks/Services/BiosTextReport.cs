using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Configuration &amp; conseils BIOS » report — the plain-text block a user pastes on a
/// forum or a Discord when asking the classic « est-ce que mon BIOS est bien réglé pour le jeu / l'OC ? ». No I/O: it
/// lays out the REAL per-PC verdict the <see cref="BiosAdvisorService"/> already built (platform summary, the same
/// ranked <see cref="BiosRecommendation"/> list the page shows, with the state Windows could read back), grouped by the
/// SAME tri+one state the advisor assigned. The honesty contract rides into the paste:
/// <list type="bullet">
/// <item>A <see cref="BiosCheckState.Verify"/> setting lands under « À VÉRIFIER » and the footer spells out it is NOT a
///   confirmed state — Windows simply couldn't read it, so the user must check it in the BIOS — and is NEVER folded
///   into « À CHANGER » or « DÉJÀ OK ».</item>
/// <item>An empty group prints no heading (an « À CHANGER (0) » would imply work that doesn't exist); the header still
///   carries the totals so a 0-row group never erases the count.</item>
/// <item>The footer repeats that menu names vary by vendor/version (the paths are indicative), that Aurum applies
///   NOTHING to the BIOS on the user's behalf (only the universal reboot-to-UEFI), and that the detected state was read
///   at generation time and may have drifted.</item>
/// </list>
/// A null report (hardware scan not finished) says so rather than printing empty headings. Counts are small integers,
/// so the text is locale-independent. Mirrors <see cref="DriveHealthTextReport"/>; the clipboard copy / file write is
/// thin glue in the VM.
/// </summary>
public static class BiosTextReport
{
    private const int LabelWidth = 11;

    public static string Render(BiosAdvisorReport? report, TweakTier tier, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Configuration & conseils BIOS");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");

        if (report is null)
        {
            sb.AppendLine(new string('=', 48));
            sb.AppendLine();
            sb.AppendLine("Détection matérielle pas encore terminée — relance le scan puis réessaie.");
            return sb.ToString();
        }

        sb.AppendLine(string.IsNullOrWhiteSpace(report.PlatformSummary)
            ? "Profil détecté : matériel non identifié"
            : $"Profil détecté : {report.PlatformSummary}");
        sb.AppendLine($"Niveau de recommandation : {TierLabel(tier)}");
        // Mirrors the report's own three tallies — the header keeps the totals even where a 0-row group is omitted.
        sb.AppendLine($"{report.ActionNeededCount} à changer · {report.VerifyCount} à vérifier · {report.OptimalCount} déjà OK");
        sb.AppendLine(new string('=', 48));

        if (report.Recommendations.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Aucune recommandation BIOS applicable au matériel détecté.");
            return sb.ToString();
        }

        AppendSection(sb, "À CHANGER", report.Recommendations, BiosCheckState.ActionNeeded);
        AppendSection(sb, "À VÉRIFIER", report.Recommendations, BiosCheckState.Verify);
        AppendSection(sb, "DÉJÀ OK", report.Recommendations, BiosCheckState.Optimal);
        AppendSection(sb, "GUIDE", report.Recommendations, BiosCheckState.Unknown);

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("« À VÉRIFIER » = état non lisible depuis Windows → à contrôler toi-même dans le BIOS, PAS un état confirmé.");
        sb.AppendLine("Les noms de menus varient selon le fabricant et la version du BIOS — les chemins sont indicatifs.");
        sb.AppendLine("Aurum ne modifie aucun réglage BIOS à ta place (hormis le redémarrage vers l'UEFI) : conseils indicatifs, un changement BIOS comporte des risques.");
        sb.AppendLine("État détecté au moment de la génération — il a pu changer depuis.");
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string heading, IEnumerable<BiosRecommendation> all, BiosCheckState state)
    {
        // Preserve the advisor's ranking (priority desc → category → name) within the group by keeping list order.
        var rows = all.Where(r => r.State == state).ToList();
        if (rows.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine($"{heading} ({rows.Count}) :");
        foreach (var r in rows)
            AppendRecommendation(sb, r);
    }

    private static void AppendRecommendation(StringBuilder sb, BiosRecommendation r)
    {
        sb.AppendLine();
        var title = $"  • {r.Name}";
        if (!string.IsNullOrWhiteSpace(r.Category))
            title += $" — {r.Category}";
        if (r.IsCritical)
            title += "   ⚠ SÉCURITÉ HARDWARE";
        sb.AppendLine(title);

        if (r.HasDetectedState)
            sb.AppendLine(Row("Détecté", r.DetectedStateText));
        if (!string.IsNullOrWhiteSpace(r.TierRecommendation))
            sb.AppendLine(Row("Conseil", r.TierRecommendation));
        if (!string.IsNullOrWhiteSpace(r.VendorPath))
            sb.AppendLine(Row("Où", r.VendorPath));
        if (!string.IsNullOrWhiteSpace(r.VendorAlias))
            sb.AppendLine(Row("Alias", r.VendorAlias));
        if (!string.IsNullOrWhiteSpace(r.ExpectedGain))
            sb.AppendLine(Row("Gain", r.ExpectedGain));
        if (!string.IsNullOrWhiteSpace(r.ValidationTool))
            sb.AppendLine(Row("Validation", r.ValidationTool));
        if (!string.IsNullOrWhiteSpace(r.Notes))
            sb.AppendLine(Row("Note", r.Notes));
    }

    private static string TierLabel(TweakTier tier) => tier switch
    {
        TweakTier.Tranquille => "Tranquille",
        TweakTier.Avance => "Avancé",
        TweakTier.Extreme => "Extrême",
        _ => tier.ToString()
    };

    private static string Row(string label, string value) => $"    {label.PadRight(LabelWidth)}: {value}";
}
