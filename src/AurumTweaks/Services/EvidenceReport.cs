using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// The three honest before/after surfaces, collected into one immutable snapshot so the unified « preuve » report can
/// be rendered from a single value. Each slot is independently optional: the user may have measured FPS but never
/// snapshotted their settings, scored their machine but never benchmarked, or any mix. A null slot is an absence to
/// disclose, never a delta to fabricate.
/// <list type="bullet">
/// <item><see cref="Settings"/> — the diff between a « before » snapshot and now (which tweaks went on / silently fell off);</item>
/// <item><see cref="Performance"/> — the A/B frame-time movement (FPS, 1% low, stutter);</item>
/// <item><see cref="Score"/> + <see cref="ScoreTrend"/> — the current optimization score and where it moved.</item>
/// </list>
/// Published into the <see cref="IEvidenceLedger"/> by each owning page; read back as one unit by the Dashboard export.
/// </summary>
public sealed record EvidenceInputs(
    SnapshotComparison? Settings,
    string? SettingsBaselineLabel,
    string? SettingsTargetLabel,
    BenchmarkComparison? Performance,
    OptimizationScorecard? Score,
    ScoreProgress? ScoreTrend)
{
    /// <summary>Nothing measured yet — every section will render its honest « non disponible » guidance.</summary>
    public static readonly EvidenceInputs Empty = new(null, null, null, null, null, null);

    public bool HasSettings => Settings is not null;
    public bool HasPerformance => Performance is not null;

    /// <summary>A score counts as evidence only when it rests on at least one verifiable probe — a
    /// <see cref="ScoreGrade.NoData"/> card is an absence, not a « 0/100 » verdict.</summary>
    public bool HasScore => Score is { HasData: true };

    public bool HasAnyEvidence => HasSettings || HasPerformance || HasScore;
}

/// <summary>
/// Pure renderer for the unified « Preuve avant / après » report — the single plain-text block that folds the three
/// separate before/after surfaces (frame-time A/B, settings diff, optimization score) into one paste a user can drop
/// on a forum or Discord to back a « +12 % de 1% low après mes tweaks » claim. No I/O: it lays out only what was
/// actually measured (<see cref="EvidenceInputs"/>) and is the honesty core of the « testable sans peur, preuve à
/// l'appui » promise, so it is thoroughly unit-tested.
/// <list type="bullet">
/// <item>A missing surface prints « Non disponible — comment la produire », never a sheet of zeros or an invented
/// delta that would read as a real measurement.</item>
/// <item>The frame-time movement reuses the EXACT same <see cref="BenchmarkTextReport.MetricLine"/> as the Benchmark
/// page's own paste, so the same run can't be worded two ways across surfaces (the anti-drift mandate). A regression
/// stays labelled « régression » — never buffed into a win.</item>
/// <item>The settings diff leads with the comparison's own honest <see cref="SnapshotComparison.Summary"/> and lists
/// the meaningful buckets (now-active vs silently-reverted); the score reuses <see cref="OptimizationScorecard.GradeLabel"/>,
/// the verifiable/indeterminate counts, and <see cref="ScoreProgress.TrendLine"/> verbatim.</item>
/// </list>
/// Mirrors <see cref="BenchmarkTextReport"/> / <see cref="SystemReport"/> in house style (header, '='/'-' rules, the
/// « généré localement et jamais envoyé » footer); the clipboard / file write is thin glue in the Dashboard VM.
/// </summary>
public static class EvidenceReport
{
    private const int Width = 48;

    // A long bucket is summarised, not dumped: a 60-line settings diff in a forum paste buries the headline. The
    // cap keeps the proof skimmable while « … +N de plus » stays honest about the full count.
    private const int MaxListedPerBucket = 12;

    // The shipping culture, so a run's duration reads « 30,0 s » here exactly as it does on the Benchmark page —
    // deterministic regardless of the machine's locale (same precedent as BenchmarkTextReport).
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Render(EvidenceInputs inputs, DateTime generatedUtc, HardwareInfo? hardware = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Preuve avant / après");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', Width));

        AppendMachine(sb, hardware);
        AppendPerformance(sb, inputs.Performance);
        AppendSettings(sb, inputs);
        AppendScore(sb, inputs.Score, inputs.ScoreTrend);

        sb.AppendLine();
        sb.AppendLine(new string('-', Width));
        sb.AppendLine("Preuve générée localement et jamais envoyée — chaque chiffre vient d'une mesure réelle, aucune valeur n'est simulée.");
        sb.AppendLine("Une section « non disponible » signale une mesure que tu n'as pas encore produite, jamais un échec ni un mauvais résultat.");
        return sb.ToString();
    }

    // Whose machine produced these numbers — the context a « +12 % de 1% low » paste needs to be credible. Reuses
    // SystemReport's exact CPU/GPU/RAM/OS lines (never a second wording), so the proof and the full system report can't
    // disagree on the rig — and the honest tells (SMT off, RAM below rated, dépareillé kit) ride along verbatim.
    // Auto-detected, so unlike the measured surfaces there is no « comment la produire » guidance: a detected rig is
    // shown, a null one (the caller simply didn't pass it) is omitted — never a fabricated « non disponible » spec.
    private static void AppendMachine(StringBuilder sb, HardwareInfo? hw)
    {
        if (hw is null) return;
        sb.AppendLine();
        sb.AppendLine("MACHINE");
        sb.AppendLine($"  CPU : {SystemReport.CpuLine(hw)}");
        sb.AppendLine($"  GPU : {SystemReport.GpuLine(hw)}");
        sb.AppendLine($"  RAM : {SystemReport.RamLine(hw)}");
        sb.AppendLine($"  OS  : {SystemReport.OsLine(hw)}");
    }

    // The punchline first: a forum reader wants the FPS / 1% low movement before the housekeeping. Each line is the
    // Benchmark page's own MetricLine, so a run pasted from here and from there can never disagree on the numbers.
    private static void AppendPerformance(StringBuilder sb, BenchmarkComparison? perf)
    {
        sb.AppendLine();
        sb.AppendLine("PERFORMANCE (frame-times, avant → après)");
        if (perf is null)
        {
            sb.AppendLine("  Non disponible — capture un run « Avant », applique tes tweaks, puis un run « Après » (page Benchmark) pour mesurer le gain.");
            return;
        }

        // Provenance before the deltas: a forum reader's first question is « mesuré comment, sur quoi, combien de
        // temps ? ». The Source token (« ETW DXGI · game.exe » vs « CSV · … ») and the run length come straight from
        // the two captures the ledger published, so the proof discloses the methodology behind the before→after — a
        // 3-second hand-made CSV can't masquerade as a 30-second capture. The comparability « Réserves » below still
        // flag a mismatch; these lines disclose the affirmative provenance even when there's nothing to flag.
        string before = RunProvenance(perf.Before);
        string after = RunProvenance(perf.After);
        if (before.Length > 0) sb.AppendLine($"  Avant : {before}");
        if (after.Length > 0) sb.AppendLine($"  Après : {after}");

        sb.AppendLine(BenchmarkTextReport.MetricLine(perf.Headline));
        foreach (var m in perf.Metrics)
            sb.AppendLine(BenchmarkTextReport.MetricLine(m));

        if (perf.Caveats.Count > 0)
        {
            sb.AppendLine("  Réserves :");
            foreach (var c in perf.Caveats)
                sb.AppendLine($"    - {c}");
        }
    }

    // One run's compact provenance: the rich Source token when the capture supplied it (it already embeds the method
    // and usually the process), else the bare process name; plus the run length, the honest « est-ce assez long pour
    // être fiable ? » signal. Both facts are printed verbatim from the capture — no re-derivation, no invented value.
    private static string RunProvenance(BenchmarkResult r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Source)) parts.Add(r.Source.Trim());
        else if (!string.IsNullOrWhiteSpace(r.TargetProcess)) parts.Add(r.TargetProcess.Trim());
        if (r.HasData)
        {
            parts.Add($"{r.Stats.FrameCount} images sur {r.Stats.DurationSec.ToString("0.0", Fr)} s");
            // WHEN the run was captured. The A/B is the one durable proof (it survives a restart), so a comparison
            // reloaded days later must not read as fresh under today's « Généré le » header — the date keeps it honest.
            parts.Add($"le {r.CapturedAt.ToLocalTime():dd/MM/yyyy}");
        }
        return string.Join(" — ", parts);
    }

    // What the tweaks actually changed on the machine. The comparison's own Summary is the honest headline; the two
    // detail lists answer « did my tweaks take, and did anything silently fall off? » — the question the diff exists for.
    private static void AppendSettings(StringBuilder sb, EvidenceInputs inputs)
    {
        string baseline = LabelOr(inputs.SettingsBaselineLabel, "Avant");
        string target = LabelOr(inputs.SettingsTargetLabel, "Après");

        sb.AppendLine();
        sb.AppendLine($"RÉGLAGES ({baseline} → {target})");

        if (inputs.Settings is not { } diff)
        {
            sb.AppendLine("  Non disponible — prends un instantané « Avant », applique tes tweaks, puis compare-le à « Maintenant » (page Instantanés).");
            return;
        }

        if (!string.IsNullOrWhiteSpace(diff.Summary))
            sb.AppendLine($"  {diff.Summary}");
        AppendChanges(sb, "Optimisations désormais actives :", diff.Improvements);
        AppendChanges(sb, "Régressions (étaient actives, ne le sont plus) :", diff.Regressions);
    }

    private static void AppendScore(StringBuilder sb, OptimizationScorecard? score, ScoreProgress? trend)
    {
        sb.AppendLine();
        sb.AppendLine("SCORE D'OPTIMISATION");

        if (score is not { HasData: true })
        {
            sb.AppendLine("  Non disponible — ouvre le Tableau de bord pour lancer la détection d'état et calculer ton score.");
            return;
        }

        sb.AppendLine($"  {score.Score} / 100 — {score.GradeLabel}");
        sb.AppendLine($"  {score.AppliedCount} / {score.VerifiableCount} optimisation(s) recommandée(s) vérifiable(s) active(s)");
        if (score.IndeterminateCount > 0)
            sb.AppendLine($"  {score.IndeterminateCount} non vérifiable(s) (valeur non relisible par Windows) — exclue(s) du score");

        string line = trend?.TrendLine ?? string.Empty;
        if (!string.IsNullOrEmpty(line))
            sb.AppendLine($"  {line}");
    }

    private static void AppendChanges(StringBuilder sb, string header, IReadOnlyList<SnapshotChange> changes)
    {
        if (changes.Count == 0) return;

        sb.AppendLine($"  {header}");
        foreach (var c in changes.Take(MaxListedPerBucket))
            sb.AppendLine($"    - {c.TweakName}");
        int extra = changes.Count - MaxListedPerBucket;
        if (extra > 0)
            sb.AppendLine($"    … +{extra} de plus");
    }

    private static string LabelOr(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

/// <summary>
/// The in-memory rendez-vous for the three before/after surfaces. Each owning page PUBLISHES its latest comparison as
/// it's produced (the Snapshot page when a diff is shown/closed, the Benchmark page when an A/B is built, the Dashboard
/// when it scores), and the Dashboard export READS them back as one <see cref="EvidenceInputs"/> to render the unified
/// proof. This decouples the surfaces — no page reaches into another's view-state — while keeping the report honest:
/// the ledger holds only what was actually published, and a null clears a slot so a stale « before/after » can't linger.
/// Singleton, accessed only on the UI thread (publishes come from VM property hooks), so it needs no locking.
///
/// <para>One slot is also DURABLE: the frame-time A/B is written through to <see cref="IEvidenceStore"/> and
/// rehydrated on construction, so a proof built before a reboot survives it. The settings diff (« maintenant » is a
/// live probe) and the live score stay in-memory by design — see <see cref="IEvidenceStore"/> for why reloading
/// either would risk pasting a pre-reboot reading as current.</para>
/// </summary>
public sealed class EvidenceLedger : IEvidenceLedger
{
    private readonly IEvidenceStore _store;

    private SnapshotComparison? _settings;
    private string? _settingsBaseline;
    private string? _settingsTarget;
    private BenchmarkComparison? _performance;
    private OptimizationScorecard? _score;
    private ScoreProgress? _scoreTrend;

    // Default to the no-op store so a bare `new EvidenceLedger()` (unit tests) stays pure in-memory; production wires
    // the file-backed EvidenceStore. Rehydrate the one durable slot — the A/B — straight away.
    public EvidenceLedger(IEvidenceStore? store = null)
    {
        _store = store ?? NullEvidenceStore.Instance;
        _performance = _store.LoadPerformance();
    }

    public void PublishSettings(SnapshotComparison? comparison, string? baselineLabel, string? targetLabel)
    {
        _settings = comparison;
        _settingsBaseline = baselineLabel;
        _settingsTarget = targetLabel;
    }

    // Write-through so the durable copy tracks the live slot: a published null deletes the file, the restart-time
    // twin of clearing the in-memory slot, so a closed A/B can't reappear on the next launch.
    public void PublishPerformance(BenchmarkComparison? comparison)
    {
        _performance = comparison;
        _store.SavePerformance(comparison);
    }

    public void PublishScore(OptimizationScorecard? scorecard, ScoreProgress? trend)
    {
        _score = scorecard;
        _scoreTrend = trend;
    }

    public EvidenceInputs Current()
        => new(_settings, _settingsBaseline, _settingsTarget, _performance, _score, _scoreTrend);
}
