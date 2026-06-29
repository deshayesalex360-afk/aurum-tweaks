using System;
using System.Globalization;
using System.Linq;
using System.Text;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Test de stabilité CPU » report — the plain-text block a user pastes on an
/// overclocking thread to back up a « stable à 5,2 GHz » claim. No I/O: it lays out the REAL run
/// (<see cref="CpuTestResult"/>). Honesty-bearing and therefore unit-tested:
/// <list type="bullet">
/// <item>the verdict is the shared <see cref="CpuStabilityVerdict.Describe"/>, so the paste reads EXACTLY what the page
/// showed — « STABLE » never appears without its « sur ce test » qualifier, and a cancelled run keeps « ne prouve rien »;</item>
/// <item>the load-bearing caveat (a brief coherence test ≠ hours of Prime95/OCCT, and managed code can't drive an
/// AVX-512 torture) is part of the footer, so a paste can never be read as a hours-long validation;</item>
/// <item>a run that never executed prints « Aucun test exécuté » rather than a fabricated « STABLE », and detected
/// miscalculations are listed as honest evidence (never hidden), with explicit windowing when the detail is capped.</item>
/// </list>
/// Numbers are fr-FR-formatted (the shipping culture) for deterministic output. Mirrors <see cref="LatencyTextReport"/>;
/// the clipboard / file write is thin glue in the VM.
/// </summary>
public static class CpuStabilityTextReport
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const int LabelWidth = 18;

    // The detail list of caught miscalculations is capped in the paste; the count in the section header stays the
    // true total, and the windowing is stated explicitly so the truncation is never silent.
    private const int MaxErrorsShown = 8;

    public static string Render(CpuTestResult result, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Test de stabilité CPU");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        if (!result.HasRun)
        {
            sb.AppendLine();
            sb.AppendLine("Aucun test exécuté — lance un test puis copie le rapport.");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine($"VERDICT : {CpuStabilityVerdict.Describe(result)}");

        sb.AppendLine();
        sb.AppendLine("PARAMÈTRES");
        sb.AppendLine(Row("Durée", $"{result.DurationSec.ToString("0.#", Fr)} s"));
        sb.AppendLine(Row("Threads chargés", result.ThreadsUsed.ToString(Fr)));
        sb.AppendLine(Row("Noyau de calcul", result.Avx2Used ? "AVX2 (256-bit)" : "scalaire"));
        sb.AppendLine(Row("Erreurs de calcul", result.ErrorCount.ToString(Fr)));
        sb.AppendLine(Row("Débit moyen", Throughput(result.AvgIterationsPerSec)));
        sb.AppendLine(Row("Lots vérifiés", result.Batches.ToString("N0", Fr)));

        if (result.ErrorCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"ERREURS DE CALCUL ({result.ErrorCount})");
            if (result.Errors.Count == 0)
            {
                sb.AppendLine("  (détail non conservé)");
            }
            else
            {
                var shown = result.Errors.Take(MaxErrorsShown).ToList();
                foreach (var e in shown)
                    sb.AppendLine(
                        $"  - thread {e.Thread} : attendu 0x{e.Expected:X16}, obtenu 0x{e.Actual:X16} à {e.AtSec.ToString("0.0", Fr)} s");
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
        sb.AppendLine("Test de cohérence rapide : il charge tous les cœurs et vérifie chaque calcul contre une référence.");
        sb.AppendLine("Un « STABLE » ici ne remplace PAS plusieurs heures de Prime95 / OCCT, et le code managé ne peut pas");
        sb.AppendLine("piloter une torture AVX-512 dédiée. Surveille les températures (onglet Monitoring). Mesuré localement.");
        return sb.ToString();
    }

    private static string Row(string label, string value) => $"  {label.PadRight(LabelWidth)}: {value}";

    // Mirrors the page's throughput display: honest workload iterations/s in G/M it/s — never fake "FLOPS".
    private static string Throughput(double itPerSec) => itPerSec >= 1_000_000_000d
        ? $"{(itPerSec / 1e9).ToString("0.00", Fr)} G it/s"
        : $"{(itPerSec / 1e6).ToString("0.0", Fr)} M it/s";
}
