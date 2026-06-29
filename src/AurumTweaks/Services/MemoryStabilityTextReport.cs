using System;
using System.Globalization;
using System.Linq;
using System.Text;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Test de stabilité RAM » report — the plain-text block a user pastes on an
/// overclocking thread to back up a « 6000 CL30 stable » claim. No I/O: it lays out the REAL run
/// (<see cref="MemoryTestResult"/>). Honesty-bearing and therefore unit-tested:
/// <list type="bullet">
/// <item>the verdict is the shared <see cref="MemoryStabilityVerdict.Describe"/>, so the paste reads EXACTLY what the
/// page showed — « STABLE » never appears without its « sur ce test » qualifier, and a cancelled run keeps « ne
/// prouve rien »;</item>
/// <item>the load-bearing caveat (a brief coverage test ≠ hours of TM5/Karhu/OCCT, only the FREE memory is covered,
/// and the offset is logical not a physical DIMM address) is part of the footer, so a paste can never be read as an
/// overnight validation;</item>
/// <item>a run that never executed prints « Aucun test exécuté » rather than a fabricated « STABLE », and detected
/// bit-flips are listed as honest evidence (never hidden), with explicit windowing when the detail is capped.</item>
/// </list>
/// Numbers are fr-FR-formatted (the shipping culture) for deterministic output. Mirrors <see cref="CpuStabilityTextReport"/>;
/// the clipboard / file write is thin glue in the VM.
/// </summary>
public static class MemoryStabilityTextReport
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const int LabelWidth = 18;

    // The detail list of caught bit-flips is capped in the paste; the count in the section header stays the true
    // total, and the windowing is stated explicitly so the truncation is never silent.
    private const int MaxErrorsShown = 8;

    public static string Render(MemoryTestResult result, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Test de stabilité RAM");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        if (!result.HasRun)
        {
            sb.AppendLine();
            sb.AppendLine("Aucun test exécuté — lance un test puis copie le rapport.");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine($"VERDICT : {MemoryStabilityVerdict.Describe(result)}");

        sb.AppendLine();
        sb.AppendLine("PARAMÈTRES");
        sb.AppendLine(Row("Durée", $"{result.DurationSec.ToString("0.#", Fr)} s"));
        sb.AppendLine(Row("Mémoire testée", $"{result.SizeMbTested.ToString(Fr)} Mo"));
        sb.AppendLine(Row("Passes terminées", result.PassesCompleted.ToString(Fr)));
        sb.AppendLine(Row("Erreurs", result.ErrorCount.ToString(Fr)));
        sb.AppendLine(Row("Débit moyen", Throughput(result.AvgThroughputMbps)));

        if (result.ErrorCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"ERREURS MÉMOIRE ({result.ErrorCount})");
            if (result.Errors.Count == 0)
            {
                sb.AppendLine("  (détail non conservé)");
            }
            else
            {
                var shown = result.Errors.Take(MaxErrorsShown).ToList();
                foreach (var e in shown)
                {
                    var motif = string.IsNullOrWhiteSpace(e.Pattern) ? "" : $" (motif {e.Pattern})";
                    sb.AppendLine(
                        $"  - offset 0x{e.ByteOffset:X} : attendu 0x{e.Expected:X16}, obtenu 0x{e.Actual:X16}{motif}");
                }
                if (result.Errors.Count > shown.Count)
                    sb.AppendLine($"  … {shown.Count} sur {result.Errors.Count} détaillées.");
            }
        }

        if (result.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("NOTES");
            foreach (var note in result.Notes)
                sb.AppendLine($"  - {note}");
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("Test de couverture rapide : il écrit des motifs en RAM puis les relit pour débusquer une instabilité grossière.");
        sb.AppendLine("Un « STABLE » ici ne remplace PAS plusieurs heures de TM5 / Karhu / OCCT, et il ne couvre que la mémoire");
        sb.AppendLine("LIBRE (pas les zones occupées par Windows). L'offset est logique, pas une adresse DIMM physique. Mesuré localement.");
        return sb.ToString();
    }

    private static string Row(string label, string value) => $"  {label.PadRight(LabelWidth)}: {value}";

    // Mirrors the page's throughput display: Go/s once we clear 1000 Mo/s, else Mo/s.
    private static string Throughput(double mbps) => mbps >= 1000d
        ? $"{(mbps / 1000d).ToString("0.0", Fr)} Go/s"
        : $"{mbps.ToString("0", Fr)} Mo/s";
}
