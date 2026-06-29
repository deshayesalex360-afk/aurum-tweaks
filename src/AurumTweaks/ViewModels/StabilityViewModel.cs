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
/// Drives the RAM stability page. It closes the loop with the timings calculator: tighten timings there,
/// validate them here. All the real work (allocation, moving-inversions, parallel sweeps) lives in
/// <see cref="IMemoryStabilityService"/>; this VM only orchestrates, streams live progress, and renders
/// an honest verdict. It never fabricates a pass — a cancelled or short-allocation run says exactly that.
/// </summary>
public partial class StabilityViewModel : ObservableObject
{
    private readonly IMemoryStabilityService _service;
    private readonly IHardwareService _hardware;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private int _sizeMb = 1024;
    [ObservableProperty] private int _passes = 2;
    [ObservableProperty] private int _threads = Math.Max(1, Environment.ProcessorCount / 2);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressPhase = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThroughputDisplay))]
    private double _throughputMbps;

    [ObservableProperty] private int _liveErrors;
    [ObservableProperty] private int _currentPass;

    /// <summary>Memory bandwidth is usually GB/s — show Go/s once we clear 1000 Mo/s, else Mo/s.</summary>
    public string ThroughputDisplay => ThroughputMbps >= 1000
        ? $"{ThroughputMbps / 1000d:0.0} Go/s"
        : $"{ThroughputMbps:0} Mo/s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    [NotifyPropertyChangedFor(nameof(ShowCancelledNotice))]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand))]
    private MemoryTestResult? _result;

    [ObservableProperty] private string _statusText =
        "Prêt — un test rapide qui écrit puis relit des motifs en RAM pour débusquer une instabilité grossière (timings trop serrés, VDIMM trop basse).";

    [ObservableProperty] private string _detectionNote = string.Empty;

    /// <summary>One-click test sizes, in gibibytes.</summary>
    public int[] QuickSizesGb { get; } = { 1, 2, 4, 8, 16 };

    /// <summary>Upper bound for the worker-thread picker — never offer more than the box has.</summary>
    public int MaxThreads { get; } = Environment.ProcessorCount;

    public bool HasResult => Result is { HasRun: true };
    public bool HasErrors => Result is { ErrorCount: > 0 };
    public bool HasNotes  => Result is { Notes.Count: > 0 };

    /// <summary>Show the "interrompu" card only when cancelled <em>without</em> errors — real errors take priority.</summary>
    public bool ShowCancelledNotice => Result is { Cancelled: true, ErrorCount: 0 };

    public StabilityViewModel(IMemoryStabilityService service, IHardwareService hardware)
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
            long totalBytes = hw?.TotalRamBytes ?? 0;
            if (totalBytes > 0)
            {
                int totalMb = (int)(totalBytes / (1024 * 1024));
                SizeMb = Math.Clamp(totalMb / 2, 512, 8192);
                double gb = totalBytes / 1024d / 1024d / 1024d;
                DetectionNote =
                    $"{gb:0.#} Go détectés — par défaut on teste {SizeMb} Mo (≈ la moitié, pour laisser respirer Windows). " +
                    "Ferme tes applis lourdes : plus la RAM libre est grande, plus la couverture est bonne.";
            }
            else
            {
                DetectionNote = "RAM non détectée — règle la taille à la main (laisse de la marge pour le système).";
            }
        }
        catch
        {
            DetectionNote = "RAM non détectée — règle la taille à la main (laisse de la marge pour le système).";
        }
    }

    [RelayCommand]
    private void SetSizeGb(int gb) => SizeMb = gb * 1024;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Result = null;
        ProgressPercent = 0;
        ThroughputMbps = 0;
        LiveErrors = 0;
        CurrentPass = 0;
        ProgressPhase = "Allocation…";
        StatusText = $"Test en cours — {SizeMb} Mo · {Passes} passe(s) · {Threads} thread(s). Évite de lancer un truc lourd en parallèle.";

        var progress = new Progress<MemoryTestProgress>(p =>
        {
            ProgressPercent = p.Percent;
            ProgressPhase = p.Phase;
            ThroughputMbps = p.ThroughputMbps;
            LiveErrors = p.Errors;
            CurrentPass = p.Pass;
        });

        try
        {
            var cfg = new MemoryTestConfig { SizeMb = SizeMb, Passes = Passes, Threads = Threads };
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
            ProgressPhase = string.Empty;
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

    // Errors-first classification lives in StabilityVerdict, and the verdict SENTENCE in MemoryStabilityVerdict, so a
    // caught bit-flip can never be masked as a mere "interrompu" and the status line, the cards, and the shareable
    // report all read from one source — no drift.
    private static string BuildVerdict(MemoryTestResult r) => MemoryStabilityVerdict.Describe(r);

    /// <summary>
    /// Copy the last run as a shareable text report — the way a « 6000 CL30 stable » claim is backed up on an
    /// overclocking thread is a paste. The render is the tested pure <see cref="MemoryStabilityTextReport"/>, which
    /// keeps the « ce test rapide ne remplace pas TM5/Karhu » caveat in the paste so a « STABLE » never overclaims.
    /// Thin clipboard glue, self-disabled until a real run exists so it never copies an empty sheet.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void CopyReport()
    {
        if (Result is not { HasRun: true } result) return;
        var text = MemoryStabilityTextReport.Render(result, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusText = "Rapport de stabilité RAM copié — colle-le où tu veux (forum, thread OC).";
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
            Title = "Exporter le rapport de stabilité RAM",
            FileName = $"aurum-stabilite-ram-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var text = MemoryStabilityTextReport.Render(result, DateTime.UtcNow);
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, text);
            StatusText = $"Rapport de stabilité RAM exporté — {result.SizeMbTested} Mo, {result.ErrorCount} erreur(s).";
        }
        catch (IOException ex) { StatusText = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { StatusText = $"Export impossible : {ex.Message}"; }
    }
}
