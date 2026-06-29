using System;
using System.Globalization;
using System.Text;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Benchmark (frame-times) » report — the plain-text block a user pastes on Discord,
/// a forum, or an overclocking thread to back up a « +12 % de 1% low » claim. No I/O: it lays out the REAL frame-time
/// metrics the page already computed (<see cref="BenchmarkResult"/>) and, when an A/B run exists, the before→after
/// movement (<see cref="BenchmarkComparison"/>). Honesty-bearing and therefore unit-tested:
/// <list type="bullet">
/// <item>a result with no frames prints « Aucune donnée » rather than a sheet of zeros that reads as a real run;</item>
/// <item>the provenance line is the result's own <see cref="BenchmarkResult.Source"/> (ETW capture vs CSV import) and
/// its honest <see cref="BenchmarkResult.Notes"/> travel with the paste, so « capturé » is never confused with
/// « importé » and a differenced/short-run caveat isn't dropped;</item>
/// <item>a regression in the comparison is labelled « régression », never buffed into a win — the sign and the
/// percent come straight from the tested <see cref="MetricDelta"/>, and the comparability « Réserves » are kept.</item>
/// </list>
/// Numbers are formatted in fr-FR (the shipping culture) so the output is deterministic regardless of the machine's
/// locale. Mirrors <see cref="LatencyTextReport"/> / <see cref="NetworkDiagnosticReport"/>; the clipboard / file write
/// is thin glue in the VM.
/// </summary>
public static class BenchmarkTextReport
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const int LabelWidth = 18;

    public static string Render(BenchmarkResult result, BenchmarkComparison? comparison, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Benchmark (frame-times)");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));

        if (!result.HasData)
        {
            sb.AppendLine();
            sb.AppendLine("Aucune donnée — lance une capture ou importe un CSV de frame-times,");
            sb.AppendLine("puis copie le rapport.");
            return sb.ToString();
        }

        var s = result.Stats;

        sb.AppendLine();
        sb.AppendLine("SOURCE");
        sb.AppendLine(Row("Source", Val(result.Source)));
        if (!string.IsNullOrWhiteSpace(result.TargetProcess))
            sb.AppendLine(Row("Process cible", result.TargetProcess.Trim()));
        sb.AppendLine(Row("Capturé le", result.CapturedAt.ToString("dd/MM/yyyy HH:mm", Fr)));

        sb.AppendLine();
        sb.AppendLine("RÉSULTAT");
        sb.AppendLine(Row("Frames", $"{s.FrameCount} sur {Num(s.DurationSec, "0.0")} s"));
        sb.AppendLine(Row("FPS moyen", Num(s.AvgFps, "0.0")));
        sb.AppendLine(Row("FPS min / max", $"{Num(s.MinFps, "0.0")} / {Num(s.MaxFps, "0.0")}"));
        sb.AppendLine(Row("1% low", Num(s.P1LowFps, "0.0")));
        sb.AppendLine(Row("0,1% low", Num(s.P01LowFps, "0.0")));
        sb.AppendLine(Row("Frame-time moy.", $"{Num(s.AvgFrameTimeMs, "0.00")} ms"));
        sb.AppendLine(Row("Frame-time médian", $"{Num(s.MedianFrameTimeMs, "0.00")} ms"));
        sb.AppendLine(Row("P99 / P99.9", $"{Num(s.P99FrameTimeMs, "0.00")} / {Num(s.P999FrameTimeMs, "0.00")} ms"));
        sb.AppendLine(Row("Écart-type", $"{Num(s.StdDevMs, "0.00")} ms"));
        sb.AppendLine(Row("Stutter", $"{Num(s.StutterPct, "0.0")} %"));
        sb.AppendLine(Row("Var. img-à-img", $"{Num(s.ConsecutiveDeltaMs, "0.00")} ms"));

        // Honest hedge tied to the lows just printed: 1%/0,1% low are tail percentiles, so on too few frames they
        // rest on ~1 sample. Same threshold as the A/B comparer (FrameSampleAdequacy), so the single-run paste and a
        // comparison can't disagree on when the lows are thin.
        if (FrameSampleAdequacy.TailLowsAreThin(s.FrameCount))
            sb.AppendLine($"  Peu d'images ({s.FrameCount}) : « 1% low » / « 0,1% low » reposent sur très peu de frames — vise ≥ {FrameSampleAdequacy.MinFramesForTailLows} pour qu'ils soient fiables.");

        // Régularité de diffusion — the page's honest interpretation (1% low vs moyenne + saccades). The label is the
        // shared FrameConsistencyVerdict.Label so the paste and the on-screen badge can't drift; it judges smoothness,
        // not whether the FPS is high enough (spelled out in the footer).
        var consistency = FrameConsistencyVerdict.Evaluate(s);
        sb.AppendLine();
        sb.AppendLine("RÉGULARITÉ (indicatif)");
        sb.AppendLine(Row("Verdict", FrameConsistencyVerdict.Label(consistency.Level)));
        sb.AppendLine($"  {consistency.Message}");

        if (result.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("NOTES");
            foreach (var note in result.Notes)
                sb.AppendLine($"  - {note}");
        }

        if (comparison is { } cmp)
        {
            sb.AppendLine();
            sb.AppendLine("COMPARAISON (Avant → Après)");
            sb.AppendLine(MetricLine(cmp.Headline));
            foreach (var m in cmp.Metrics)
                sb.AppendLine(MetricLine(m));
            if (cmp.Caveats.Count > 0)
            {
                sb.AppendLine("  Réserves :");
                foreach (var c in cmp.Caveats)
                    sb.AppendLine($"    - {c}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("Benchmark informatif : frame-times du process choisi, mesurés localement et jamais envoyés.");
        sb.AppendLine("« 1% low » / « 0,1% low » suivent la convention centile (FPS au frame-time P99 / P99.9).");
        sb.AppendLine("La « régularité » juge la régularité de diffusion des images (1% low vs moyenne, saccades), pas si le niveau de FPS est suffisant.");
        return sb.ToString();
    }

    /// <summary>One before→after metric line. The verdict word disambiguates the sign (an FPS rising and a frame-time
    /// falling are both improvements), and it comes straight from <see cref="MetricDelta"/>'s tested Improved/Regressed
    /// — never re-derived. Public so <see cref="EvidenceReport"/> renders the same movement identically, never drifting
    /// from this page's own paste.</summary>
    public static string MetricLine(MetricDelta m)
    {
        string verdict = m.Improved ? "amélioration" : m.Regressed ? "régression" : "stable";
        return $"  {Label(m.Label)}: {Num(m.Before, Fmt(m.Unit))} → {Num(m.After, Fmt(m.Unit))} {m.Unit}"
             + $"   ({Signed(m.Delta, m.Unit)} {m.Unit} · {Signed(m.PercentChange, "%")} % · {verdict})";
    }

    private static string Row(string label, string value) => $"  {Label(label)}: {value}";

    private static string Label(string label) => label.PadRight(LabelWidth);

    private static string Num(double v, string format) => v.ToString(format, Fr);

    // Frame-time metrics carry sub-millisecond detail; FPS / percentages read better at one decimal.
    private static string Fmt(string unit) => unit == "ms" ? "0.00" : "0.0";

    private static string Signed(double v, string unit)
        => v.ToString(unit == "ms" ? "+0.00;-0.00;0.00" : "+0.0;-0.0;0.0", Fr);

    private static string Val(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();
}
