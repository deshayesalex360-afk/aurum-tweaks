using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AurumTweaks.ViewModels;

/// <summary>
/// Drives the benchmark page. Two honest entry points, no injection: import a PresentMon/CapFrameX CSV
/// (always available) or — when Aurum runs elevated — capture frame times live from a chosen process via
/// the DXGI ETW provider. All heavy lifting (parsing, metrics, the ETW session) lives in
/// <see cref="IBenchmarkService"/>; this VM only orchestrates and surfaces honest status. It never
/// fabricates results: a failed capture/import shows the service's plain-spoken note, not invented numbers.
/// </summary>
public partial class BenchmarkViewModel : ObservableObject
{
    private readonly IBenchmarkService _service;
    private readonly IBenchmarkHistoryService _history;
    private readonly IEvidenceLedger _evidence;
    private CancellationTokenSource? _cts;
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    [ObservableProperty] private BenchmarkBackendStatus? _backendStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureLiveCommand))]
    private string _targetProcess = string.Empty;

    [ObservableProperty] private int _captureDurationSec = 20;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    [NotifyPropertyChangedFor(nameof(ConsistencyAssessment))]
    [NotifyPropertyChangedFor(nameof(TailLowsThin))]
    [NotifyPropertyChangedFor(nameof(TailLowHint))]
    [NotifyCanExecuteChangedFor(nameof(SetAsBaselineCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportFramesCsvCommand))]
    private BenchmarkResult? _result;

    /// <summary>The "Avant" run pinned for A/B comparison; new captures are measured against it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBaseline))]
    private BenchmarkResult? _baseline;

    /// <summary>The live before→after comparison, recomputed whenever a new result lands against a baseline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison))]
    private BenchmarkComparison? _comparison;

    /// <summary>Frame-time plot for the current run — the real captured frames as a spike-preserving envelope plus an
    /// average reference line. Rebuilt whenever a new result lands; null when there is no usable run to draw.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrameGraph))]
    private FrameGraphVisual? _frameGraph;

    /// <summary>A/B overlay: the baseline and current frame-time envelopes on one shared axis, so a real improvement is
    /// visible (a lower, flatter « Après » line), not just a percentage. Null until a comparison with real frames exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparisonGraph))]
    private FrameOverlayVisual? _comparisonGraph;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureLiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetAsBaselineCommand))]
    private bool _isCapturing;

    [ObservableProperty] private int _captureProgress;

    /// <summary>Whether the persistent run history has any archived runs (drives the list vs. empty-state in the card).</summary>
    [ObservableProperty] private bool _hasHistory;

    [ObservableProperty] private string _statusText =
        "Prêt — importe un CSV PresentMon/CapFrameX, ou (en admin) capture en live.";

    /// <summary>Running apps with a window — the candidate capture targets the user picks from.</summary>
    public ObservableCollection<string> CandidateProcesses { get; } = new();

    /// <summary>Archived runs (newest first). Every captured or imported run is auto-archived so it survives a restart
    /// and can be reopened or pinned as the « Avant » baseline in a later session; the list is bounded to the newest runs.</summary>
    public ObservableCollection<BenchmarkHistoryEntry> History { get; } = new();

    /// <summary>One-click capture lengths.</summary>
    public int[] QuickDurations { get; } = { 10, 20, 30, 60 };

    public bool HasResult => Result?.HasData == true;
    public bool HasNotes => Result is { Notes.Count: > 0 };
    public bool HasBaseline => Baseline?.HasData == true;
    public bool HasComparison => Comparison is not null;
    public bool HasFrameGraph => FrameGraph is not null;
    public bool HasComparisonGraph => ComparisonGraph is not null;

    /// <summary>Honest « régularité de diffusion » verdict for the current run — smoothness only (1% low vs moyenne +
    /// saccades), never a judgement on whether the FPS level is high enough. Null until a real result lands.</summary>
    public FrameConsistencyAssessment? ConsistencyAssessment =>
        Result is { HasData: true } r ? FrameConsistencyVerdict.Evaluate(r.Stats) : null;

    /// <summary>True when the current run sits below the <see cref="FrameSampleAdequacy.MinFramesForTailLows"/> floor, so
    /// its « 1% low » / « 0,1% low » rest on a handful of frames. Drives the live card's hedge so the on-screen surface
    /// carries the same honesty as the shared paste and the A/B comparer — one shared threshold, they can't disagree.</summary>
    public bool TailLowsThin => Result is { HasData: true } r && FrameSampleAdequacy.TailLowsAreThin(r.Stats.FrameCount);

    /// <summary>The honest caption shown beside the lows when <see cref="TailLowsThin"/>; null when the run clears the
    /// floor. Built here (not in XAML) so the frame floor and its wording stay a single source of truth with the paste.</summary>
    public string? TailLowHint => TailLowsThin
        ? $"« 1% low » / « 0,1% low » reposent sur peu d'images ({Result!.Stats.FrameCount}) — vise ≥ {FrameSampleAdequacy.MinFramesForTailLows} images pour qu'ils soient fiables."
        : null;

    public BenchmarkViewModel(IBenchmarkService service, IBenchmarkHistoryService history, IEvidenceLedger evidence)
    {
        _service = service;
        _history = history;
        _evidence = evidence;
        BackendStatus = _service.GetStatus();
        RefreshProcesses();
        _ = RefreshHistoryAsync();
    }

    [RelayCommand]
    private void RefreshProcesses()
    {
        CandidateProcesses.Clear();
        foreach (var name in _service.GetCandidateProcesses())
            CandidateProcesses.Add(name);

        // Don't clobber a real result message with the process count.
        if (!HasResult && !IsCapturing)
            StatusText = CandidateProcesses.Count > 0
                ? $"{CandidateProcesses.Count} application(s) détectée(s). Choisis ta cible puis capture."
                : "Aucune application avec fenêtre détectée — lance le jeu, puis « Rafraîchir ».";
    }

    [RelayCommand]
    private void SetDuration(int seconds) => CaptureDurationSec = seconds;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportCsvAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Importer un CSV de frame-times (PresentMon / CapFrameX)",
            Filter = "CSV de frame-times (*.csv)|*.csv|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;

        var r = _service.AnalyzeCsv(dlg.FileName);
        Result = r;
        StatusText = r.HasData
            ? $"Importé : {r.Stats.FrameCount} frames · {r.Stats.AvgFps:0.0} FPS moy. · 1% low {r.Stats.P1LowFps:0.0}."
            : FirstNoteOr(r, "Import impossible.");
        if (r.HasData)
            await ArchiveAsync(r);
    }

    private bool CanImport() => !IsCapturing;

    [RelayCommand(CanExecute = nameof(CanCaptureLive))]
    private async Task CaptureLiveAsync()
    {
        _cts = new CancellationTokenSource();
        IsCapturing = true;
        CaptureProgress = 0;
        Result = null;
        StatusText = $"Capture de « {TargetProcess} » sur {CaptureDurationSec}s — bascule (alt-tab) vers le jeu…";

        var progress = new Progress<int>(p => CaptureProgress = p);
        try
        {
            var r = await _service.CaptureLiveAsync(
                TargetProcess, TimeSpan.FromSeconds(CaptureDurationSec), progress, _cts.Token);
            Result = r;
            StatusText = r.HasData
                ? $"Capturé : {r.Stats.FrameCount} frames · {r.Stats.AvgFps:0.0} FPS moy. · 1% low {r.Stats.P1LowFps:0.0}."
                : FirstNoteOr(r, "Aucune frame captée.");
            if (r.HasData)
                await ArchiveAsync(r);   // a live capture lives only in memory — archive it so it survives a restart
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur : {ex.Message}";
        }
        finally
        {
            IsCapturing = false;
            CaptureProgress = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // Live capture needs elevation (real-time ETW), a target, and no capture already running.
    private bool CanCaptureLive() =>
        !IsCapturing
        && BackendStatus?.LiveCaptureAvailable == true
        && !string.IsNullOrWhiteSpace(TargetProcess);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelCapture()
    {
        _cts?.Cancel();
        StatusText = "Annulation… (les métriques porteront sur la portion déjà captée)";
    }

    private bool CanCancel() => IsCapturing;

    /// <summary>Pin the current run as the "Avant" reference; the next capture/import is measured against it.</summary>
    [RelayCommand(CanExecute = nameof(CanSetBaseline))]
    private void SetAsBaseline()
    {
        Baseline = Result;
        Comparison = null;   // a baseline on its own is not yet a comparison
        StatusText = $"Référence « Avant » définie ({Baseline!.Stats.AvgFps:0.0} FPS moy.). "
                   + "Applique tes tweaks, relance une capture identique pour mesurer l'écart réel.";
    }

    private bool CanSetBaseline() => HasResult && !IsCapturing;

    /// <summary>Drop the baseline and any comparison (start a fresh A/B).</summary>
    [RelayCommand]
    private void ClearBaseline()
    {
        Baseline = null;
        Comparison = null;
        StatusText = "Référence « Avant » effacée.";
    }

    // ----- Persistent run history -----

    /// <summary>Reload the archived-runs list from disk (newest first). The read is off-thread in the service; this
    /// only marshals the result into the bound collection and flags whether anything is there.</summary>
    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        var entries = await _history.ListAsync();
        History.Clear();
        foreach (var e in entries)
            History.Add(e);
        HasHistory = History.Count > 0;
    }

    /// <summary>Archive a real run to the persistent history, then refresh the list. Silent on failure — the
    /// capture/import status already stands and archiving is a side-record, never a reason the run appears to fail.</summary>
    private async Task ArchiveAsync(BenchmarkResult result)
    {
        await _history.SaveAsync(result);
        await RefreshHistoryAsync();
    }

    /// <summary>Reload a stored run as the CURRENT result — to revisit its graph/stats, copy its report, or pin it as a
    /// baseline. Doesn't re-archive (it's already in history).</summary>
    [RelayCommand]
    private async Task LoadFromHistoryAsync(BenchmarkHistoryEntry? entry)
    {
        if (entry is null) return;
        var loaded = await _history.LoadAsync(entry.FilePath);
        if (loaded is null)
        {
            StatusText = "Run introuvable ou illisible — il a peut-être été supprimé hors de l'application.";
            await RefreshHistoryAsync();
            return;
        }
        Result = loaded;
        StatusText = $"Run rechargé depuis l'historique : {loaded.Stats.FrameCount} frames · {loaded.Stats.AvgFps:0.0} FPS moy.";
    }

    /// <summary>
    /// Pin a stored run as the « Avant » reference — the cross-session A/B: compare a run captured today against one
    /// archived days ago. When a current run is already loaded, the comparison is computed immediately (both are real
    /// captures); otherwise the next capture/import becomes the « Après ».
    /// </summary>
    [RelayCommand]
    private async Task PinFromHistoryAsBaselineAsync(BenchmarkHistoryEntry? entry)
    {
        if (entry is null) return;
        var loaded = await _history.LoadAsync(entry.FilePath);
        if (loaded is null)
        {
            StatusText = "Run introuvable ou illisible — il a peut-être été supprimé hors de l'application.";
            await RefreshHistoryAsync();
            return;
        }

        Baseline = loaded;
        if (Result is { HasData: true } current && !ReferenceEquals(current, loaded))
        {
            Comparison = BenchmarkComparer.Compare(loaded, current);
            StatusText = "Référence « Avant » définie depuis l'historique — comparée à ton run courant.";
        }
        else
        {
            Comparison = null;   // a baseline on its own is not yet a comparison
            StatusText = $"Référence « Avant » définie depuis l'historique ({loaded.Stats.AvgFps:0.0} FPS moy.). "
                       + "Capture ou importe le run « Après » pour mesurer l'écart.";
        }
    }

    /// <summary>Delete a stored run's CSV from history. Doesn't touch the current result or the pinned baseline.</summary>
    [RelayCommand]
    private async Task DeleteFromHistoryAsync(BenchmarkHistoryEntry? entry)
    {
        if (entry is null) return;
        await _history.DeleteAsync(entry.FilePath);
        await RefreshHistoryAsync();
        StatusText = "Run supprimé de l'historique.";
    }

    /// <summary>
    /// Recompute the A/B comparison whenever a genuinely new result lands against a pinned baseline. A failed
    /// import / empty capture (or the null we set while a capture is starting) clears any stale comparison.
    /// </summary>
    partial void OnResultChanged(BenchmarkResult? value)
    {
        if (Baseline is not null && value is { HasData: true } && !ReferenceEquals(value, Baseline))
            Comparison = BenchmarkComparer.Compare(Baseline, value);
        else if (value is not { HasData: true })
            Comparison = null;

        RebuildFrameGraph(value);
    }

    /// <summary>
    /// Rebuild the frame-time plot from the run's REAL frames: a spike-preserving min/max envelope (so a 1-frame hitch
    /// is never averaged away) on a 0 → max-frame-time axis, plus a reference line at the average. Pure geometry lives
    /// in <see cref="FrameTimeGraph"/>; this only converts its points to a frozen <see cref="PointCollection"/> the view
    /// can bind, and labels the axis with the honest real max / average. Null whenever there is nothing real to draw.
    /// </summary>
    private void RebuildFrameGraph(BenchmarkResult? value)
    {
        if (value is not { HasData: true } r || r.FrameTimesMs.Count == 0)
        {
            FrameGraph = null;
            return;
        }

        var frames = r.FrameTimesMs;
        double yMax = 0;
        foreach (double ms in frames)
            if (ms > yMax) yMax = ms;
        if (yMax <= 0)
        {
            FrameGraph = null;
            return;
        }

        double avg = r.Stats.AvgFrameTimeMs;
        FrameGraph = new FrameGraphVisual(
            ToPointCollection(FrameTimeGraph.BuildEnvelope(frames, yMax)),
            FrameTimeGraph.Y(avg, yMax),
            FrameTimeGraph.ViewWidth,
            FrameTimeGraph.ViewHeight,
            $"max {yMax.ToString("0.0", Fr)} ms",
            $"moy. {avg.ToString("0.0", Fr)} ms",
            "Frame-time réel : plus la ligne est basse et plate, plus c'est fluide. Les pics vers le haut sont des saccades.");
    }

    /// <summary>
    /// Rebuild the A/B frame-time overlay whenever the comparison changes. Both runs are drawn on ONE shared axis (the
    /// honest part — see <see cref="FrameTimeGraph.BuildOverlay"/>), so a tweak that really shrank the spikes shows a
    /// visibly lower « Après » line. Declines to draw (null) when neither run carries raw frames — e.g. a comparison
    /// built from stats alone — rather than inventing a flat line.
    /// </summary>
    partial void OnComparisonChanged(BenchmarkComparison? value)
    {
        // Publish the A/B to the shared ledger so the Dashboard's unified « preuve » can fold in this frame-time
        // movement; null flows through too, clearing a closed comparison so a stale before/after isn't pasted as current.
        _evidence.PublishPerformance(value);

        if (value is null)
        {
            ComparisonGraph = null;
            return;
        }

        var overlay = FrameTimeGraph.BuildOverlay(value.Before.FrameTimesMs, value.After.FrameTimesMs);
        if (overlay.YMaxMs <= 0)
        {
            ComparisonGraph = null;
            return;
        }

        ComparisonGraph = new FrameOverlayVisual(
            ToPointCollection(overlay.Before),
            ToPointCollection(overlay.After),
            FrameTimeGraph.ViewWidth,
            FrameTimeGraph.ViewHeight,
            $"max {overlay.YMaxMs.ToString("0.0", Fr)} ms · échelle commune",
            "Même échelle pour les deux : une ligne « Après » plus basse et plus plate = des frame-times réduits.");
    }

    // Convert pure-core points to a frozen (immutable, cross-thread-safe) PointCollection the view binds; the plots
    // don't animate, so freezing is free correctness. Shared by the single-run plot and the A/B overlay.
    private static PointCollection ToPointCollection(IReadOnlyList<GraphPoint> points)
    {
        var pc = new PointCollection(points.Count);
        foreach (var p in points)
            pc.Add(new System.Windows.Point(p.X, p.Y));
        pc.Freeze();
        return pc;
    }

    /// <summary>
    /// Copy the current run as a shareable text report — the way a « +X % de 1% low » claim is actually backed up on
    /// Discord or an overclocking thread is a paste. The render is the tested pure <see cref="BenchmarkTextReport"/>;
    /// this is thin clipboard glue, self-disabled until a real result exists so it never copies an empty sheet. The
    /// A/B comparison rides along when one is pinned, so the before→after proof travels with the numbers.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void CopyReport()
    {
        if (Result is not { HasData: true } result) return;
        var text = BenchmarkTextReport.Render(result, Comparison, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText = "Rapport copié — colle-le où tu veux (Discord, forum, thread OC).";
        }
        catch
        {
            // The clipboard can be momentarily locked by another process — non-fatal, point at the file route.
            StatusText = "Copie impossible (presse-papiers occupé). Utilise « Exporter » à la place.";
        }
    }

    /// <summary>Save the same report as a .txt the user can archive or attach next to the frame-time CSV. Same tested
    /// render as the copy path; this is the save-dialog + file-write glue, and it honestly reports a write failure.</summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task ExportReportAsync()
    {
        if (Result is not { HasData: true } result) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exporter le rapport de benchmark",
            FileName = $"aurum-benchmark-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var text = BenchmarkTextReport.Render(result, Comparison, DateTime.UtcNow);
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, text);
            StatusText = $"Rapport de benchmark exporté — {result.Stats.FrameCount} frames.";
        }
        catch (IOException ex) { StatusText = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusText = $"Export impossible : {ex.Message}"; }
    }

    /// <summary>
    /// Save the captured RAW frame-times as a CSV — the only way a live ETW capture (which otherwise lives only in
    /// memory) is kept for archival, re-opened in CapFrameX, or re-imported here. The render is the tested pure
    /// <see cref="FrameTimeCsv"/>, whose « FrameTime » column + invariant-culture values re-import bit-exact through the
    /// app's own parser; this is the save-dialog + file-write glue, self-disabled until a real result exists.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task ExportFramesCsvAsync()
    {
        if (Result is not { HasData: true } result) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exporter les frame-times bruts (CSV)",
            FileName = $"aurum-frametimes-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV de frame-times (*.csv)|*.csv|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var text = FrameTimeCsv.Render(result);
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, text);
            StatusText = $"Frame-times exportés ({result.Stats.FrameCount} frames) — réimportable ici ou dans CapFrameX.";
        }
        catch (IOException ex) { StatusText = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusText = $"Export impossible : {ex.Message}"; }
    }

    private static string FirstNoteOr(BenchmarkResult r, string fallback) =>
        r.Notes.Count > 0 ? r.Notes[0] : fallback;
}

/// <summary>
/// Everything the frame-time plot needs, in its fixed logical viewport (<see cref="ViewWidth"/> × <see cref="ViewHeight"/>),
/// which the view scales to fill the card via a Viewbox. <see cref="Envelope"/> is the real frames as a spike-preserving
/// line; <see cref="AverageY"/> places the average reference line in the same coordinate space. The labels carry the
/// honest real max / average so the axis is never mislabelled.
/// </summary>
public sealed record FrameGraphVisual(
    PointCollection Envelope,
    double AverageY,
    double ViewWidth,
    double ViewHeight,
    string MaxLabel,
    string AverageLabel,
    string Caption);

/// <summary>
/// The A/B overlay's two envelopes — <see cref="Before"/> and <see cref="After"/> — in the shared logical viewport
/// (<see cref="ViewWidth"/> × <see cref="ViewHeight"/>) on ONE common axis, so the two lines are visually comparable.
/// <see cref="MaxLabel"/> names that shared axis max so the user knows both lines are scaled the same way.
/// </summary>
public sealed record FrameOverlayVisual(
    PointCollection Before,
    PointCollection After,
    double ViewWidth,
    double ViewHeight,
    string MaxLabel,
    string Caption);
