using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AurumTweaks.ViewModels;

/// <summary>
/// Drives the CPU stability page — the companion to the RAM memtest. All the real work (pegging every core
/// with the deterministic kernel, verifying each batch against a reference) lives in
/// <see cref="ICpuStabilityService"/>; this VM orchestrates, streams live progress, and renders an honest
/// verdict. It never fabricates a pass — a cancelled run says exactly that, and the page is loud about the
/// fact that a quick coherence test is not Prime95/OCCT for hours.
/// </summary>
public partial class CpuStabilityViewModel : ObservableObject
{
    private readonly ICpuStabilityService _service;
    private readonly IHardwareService _hardware;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private int _durationSec = 30;
    [ObservableProperty] private int _threads = Environment.ProcessorCount;

    /// <summary>Run the heavier 256-bit AVX2 kernel. Defaults on where supported, off (and disabled) otherwise.</summary>
    [ObservableProperty] private bool _useAvx2 = CpuWorkload.Avx2Available;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private double _elapsedSec;
    [ObservableProperty] private int _currentThreads;
    [ObservableProperty] private int _liveErrors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThroughputDisplay))]
    private double _iterationsPerSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    [NotifyPropertyChangedFor(nameof(ShowCancelledNotice))]
    [NotifyPropertyChangedFor(nameof(ResultThroughputDisplay))]
    [NotifyPropertyChangedFor(nameof(ResultWorkloadDisplay))]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand))]
    private CpuTestResult? _result;

    [ObservableProperty] private string _statusText =
        "Prêt — charge tous les cœurs et vérifie chaque calcul contre une référence pour débusquer un OC ou un undervolt instable. Garde un œil sur les températures (onglet Monitoring).";

    [ObservableProperty] private string _detectionNote = string.Empty;

    /// <summary>One-click durations, in seconds.</summary>
    public int[] QuickDurations { get; } = { 15, 30, 60, 120 };

    /// <summary>Upper bound for the worker-thread picker.</summary>
    public int MaxThreads { get; } = Environment.ProcessorCount;

    /// <summary>Whether this CPU can run the AVX2 kernel at all — gates the toggle in the UI.</summary>
    public bool Avx2Supported => CpuWorkload.Avx2Available;

    /// <summary>Workload iterations completed per second — shown in G/M it/s, never as fake "FLOPS".</summary>
    public string ThroughputDisplay => IterationsPerSec >= 1_000_000_000d
        ? $"{IterationsPerSec / 1e9:0.00} G it/s"
        : $"{IterationsPerSec / 1e6:0.0} M it/s";

    public bool HasResult => Result is { HasRun: true };
    public bool HasErrors => Result is { ErrorCount: > 0 };
    public bool HasNotes  => Result is { Notes.Count: > 0 };

    /// <summary>Show the "interrompu" card only when cancelled <em>without</em> errors — real errors take priority.</summary>
    public bool ShowCancelledNotice => Result is { Cancelled: true, ErrorCount: 0 };

    /// <summary>Final average throughput, nicely formatted (G/M it/s).</summary>
    public string ResultThroughputDisplay => Result is null
        ? string.Empty
        : Result.AvgIterationsPerSec >= 1_000_000_000d
            ? $"{Result.AvgIterationsPerSec / 1e9:0.00} G it/s"
            : $"{Result.AvgIterationsPerSec / 1e6:0.0} M it/s";

    /// <summary>Which kernel actually ran, for the verdict chip — never lies about an AVX2 fallback.</summary>
    public string ResultWorkloadDisplay => Result is null ? string.Empty : Result.Avx2Used ? "AVX2" : "Scalaire";

    public CpuStabilityViewModel(ICpuStabilityService service, IHardwareService hardware)
    {
        _service = service;
        _hardware = hardware;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var hw = await _hardware.DetectAsync();
            if (hw is not null && !string.IsNullOrWhiteSpace(hw.CpuName) && hw.CpuName != "Unknown")
            {
                DetectionNote =
                    $"{hw.CpuName} — {hw.CpuCores} cœurs / {hw.CpuThreads} threads. Le test charge {Threads} thread(s) ; " +
                    "réduis ce nombre si tu veux garder la machine utilisable pendant le test.";
            }
            else
            {
                DetectionNote = $"CPU non détecté — {Environment.ProcessorCount} threads logiques disponibles.";
            }
        }
        catch
        {
            DetectionNote = $"CPU non détecté — {Environment.ProcessorCount} threads logiques disponibles.";
        }
    }

    [RelayCommand]
    private void SetDuration(int seconds) => DurationSec = seconds;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Result = null;
        ProgressPercent = 0;
        ElapsedSec = 0;
        IterationsPerSec = 0;
        LiveErrors = 0;
        CurrentThreads = Threads;
        StatusText = $"Test en cours — {DurationSec}s sur {Threads} thread(s). Tous les cœurs sont chargés ; surveille les températures.";

        var progress = new Progress<CpuTestProgress>(p =>
        {
            ProgressPercent = p.Percent;
            ElapsedSec = p.ElapsedSec;
            IterationsPerSec = p.IterationsPerSec;
            LiveErrors = p.Errors;
            CurrentThreads = p.Threads;
        });

        try
        {
            var cfg = new CpuTestConfig { DurationSec = DurationSec, Threads = Threads, UseAvx2 = UseAvx2 && Avx2Supported };
            Result = await _service.RunAsync(cfg, progress, _cts.Token);
            StatusText = BuildVerdict(Result);
        }
        catch (Exception ex)
        {
            StatusText = $"Erreur : {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            ProgressPercent = Result is { Completed: true } ? 100 : ProgressPercent;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _cts?.Cancel();
        StatusText = "Annulation… (le verdict ne portera que sur la portion déjà testée)";
    }

    private bool CanStop() => IsRunning;

    // Errors-first classification lives in StabilityVerdict, and the verdict SENTENCE in CpuStabilityVerdict, so a
    // caught miscalculation can never be masked as a mere "interrompu" and the status line, the cards, and the
    // shareable report all read from one source — no drift.
    private static string BuildVerdict(CpuTestResult r) => CpuStabilityVerdict.Describe(r);

    /// <summary>
    /// Copy the last run as a shareable text report — the way a « stable 30s · 16 threads · AVX2 » claim is backed up
    /// on an overclocking thread is a paste. The render is the tested pure <see cref="CpuStabilityTextReport"/>, which
    /// keeps the « ce test bref ne remplace pas Prime95/OCCT » caveat in the paste so a « STABLE » never overclaims.
    /// Thin clipboard glue, self-disabled until a real run exists so it never copies an empty sheet.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void CopyReport()
    {
        if (Result is not { HasRun: true } result) return;
        var text = CpuStabilityTextReport.Render(result, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText = "Rapport de stabilité copié — colle-le où tu veux (forum, thread OC).";
        }
        catch
        {
            // The clipboard can be momentarily locked by another process — non-fatal, point at the file route.
            StatusText = "Copie impossible (presse-papiers occupé). Utilise « Exporter » à la place.";
        }
    }

    /// <summary>Save the same report as a .txt the user can archive with a tuning session. Same tested render as the
    /// copy path; this is the save-dialog + file-write glue, and it honestly reports a write failure.</summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task ExportReportAsync()
    {
        if (Result is not { HasRun: true } result) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exporter le rapport de stabilité CPU",
            FileName = $"aurum-stabilite-cpu-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var text = CpuStabilityTextReport.Render(result, DateTime.UtcNow);
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, text);
            StatusText = $"Rapport de stabilité exporté — {result.DurationSec:0}s, {result.ErrorCount} erreur(s).";
        }
        catch (IOException ex) { StatusText = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusText = $"Export impossible : {ex.Message}"; }
    }
}
