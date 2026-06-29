using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Latence système (DPC/ISR) » page — a LatencyMon-adjacent diagnostic. It samples the kernel's own per-processor
/// time counters twice and reports how much CPU time each logical core spends in Deferred Procedure Calls and hardware
/// interrupts — the classic cause of micro-stutter and audio crackle. Everything shown is real and measured; the
/// thresholds are labelled « indicatif » and the page states plainly that it measures HOW MUCH DPC/ISR load there is,
/// not WHICH driver causes it (that needs a full ETW trace — LatencyMon / DPC Latency Checker, which it links to). It's
/// read-only: a measurement, never a « fix » button, and it promises no FPS.
/// </summary>
public partial class LatencyDiagnosticsViewModel : ObservableObject
{
    private readonly ILatencyDiagnosticsService _service;
    private readonly ITimerResolutionService _timer;

    /// <summary>The last measurement, kept so « Copier le rapport » renders the real numbers, not a re-measure.</summary>
    private LatencyReport? _lastReport;

    /// <summary>Per-core load, in natural CPU order — the honest hardware map (CPU 0..N).</summary>
    public ObservableCollection<ProcessorLoad> PerCpu { get; } = new();

    [ObservableProperty] private string _headline = "Mesure de la latence DPC/ISR…";
    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureCommand))]
    [NotifyCanExecuteChangedFor(nameof(MeasureLongCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _queryFailed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    private bool _hasResults;

    [ObservableProperty] private string _verdictLabel = string.Empty;
    [ObservableProperty] private string _verdictMessage = string.Empty;
    [ObservableProperty] private bool _showLow;
    [ObservableProperty] private bool _showModerate;
    [ObservableProperty] private bool _showHigh;
    [ObservableProperty] private bool _showUnknown;

    [ObservableProperty] private string _maxDpcDisplay = "—";
    [ObservableProperty] private string _maxInterruptDisplay = "—";
    [ObservableProperty] private string _totalInterruptRateDisplay = "—";
    [ObservableProperty] private string _worstDpcDisplay = "—";

    /// <summary>Live system timer resolution — a separate read-only readout, refreshed on every measurement.</summary>
    [ObservableProperty] private TimerResolutionReading? _timerResolution;
    [ObservableProperty] private bool _hasTimerResolution;

    public LatencyDiagnosticsViewModel(ILatencyDiagnosticsService service, ITimerResolutionService timer)
    {
        _service = service;
        _timer = timer;
        _ = MeasureAsync();
    }

    private bool CanMeasure() => !IsBusy;

    /// <summary>Quick 2-second measurement — the default, run on open and on « Mesurer ».</summary>
    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private Task MeasureAsync() => RunAsync(2000, "Mesure rapide (2 s) en cours…");

    /// <summary>A longer 10-second measurement — averages out a passing spike before trusting a « modérée » verdict.</summary>
    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private Task MeasureLongAsync() => RunAsync(10000, "Mesure longue (10 s) en cours — patiente…");

    /// <summary>Open Windows' Performance Monitor — the built-in tool that can graph DPC/interrupt time over time.</summary>
    [RelayCommand]
    private void OpenPerfMon() =>
        ShellLauncher.OpenLocal(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "perfmon.exe"));

    /// <summary>Open Windows' Resource Monitor — the honest complement for live per-process CPU/interrupt activity.</summary>
    [RelayCommand]
    private void OpenResMon() =>
        ShellLauncher.OpenLocal(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "resmon.exe"));

    private async Task RunAsync(int sampleMs, string busyStatus)
    {
        if (IsBusy) return;   // a measurement is sampling — never re-enter and corrupt the window
        IsBusy = true;
        Status = busyStatus;

        // Timer resolution is an instantaneous ntdll read — refresh it alongside each DPC/ISR sample.
        var timer = _timer.Read();
        TimerResolution = timer;
        HasTimerResolution = timer.QueryOk;

        var report = await _service.MeasureAsync(sampleMs);
        _lastReport = report;

        PerCpu.Clear();
        foreach (var load in report.PerCpu) PerCpu.Add(load);

        QueryFailed = !report.QueryOk;
        HasResults = report.QueryOk && report.CpuCount > 0;
        Headline = report.Headline;

        var verdict = report.Verdict;
        VerdictMessage = verdict.Message;
        VerdictLabel = LatencyVerdict.Label(verdict.Level);
        ShowLow = verdict.Level == LatencyLevel.Low;
        ShowModerate = verdict.Level == LatencyLevel.Moderate;
        ShowHigh = verdict.Level == LatencyLevel.High;
        ShowUnknown = verdict.Level == LatencyLevel.Unknown;

        MaxDpcDisplay = report.MaxDpcDisplay;
        MaxInterruptDisplay = report.MaxInterruptDisplay;
        TotalInterruptRateDisplay = report.TotalInterruptRateDisplay;
        WorstDpcDisplay = report.WorstDpcDisplay;

        Status = report.QueryOk
            ? $"Mesuré sur {report.CpuCount} cœur(s) logique(s). Relance une mesure longue pour confirmer un verdict « modérée »."
            : "Mesure impossible — les compteurs de performance par processeur du noyau n'ont pas pu être lus.";
        IsBusy = false;
    }

    /// <summary>
    /// Copy the last measurement as a shareable text report — the way a stutter/audio-dropout problem is triaged on a
    /// forum is a paste. The render is the tested pure <see cref="LatencyTextReport"/>; this is thin clipboard glue,
    /// self-disabled until a real measurement exists so it never copies an empty or failed sheet.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasResults))]
    private void CopyReport()
    {
        if (_lastReport is not { } report) return;
        var text = LatencyTextReport.Render(report, DateTime.UtcNow, TimerResolution);
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = "Rapport de latence copié — colle-le où tu veux (forum, ticket de support).";
        }
        catch
        {
            // The clipboard can be momentarily locked by another process — non-fatal, just say so.
            Status = "Copie impossible (presse-papiers occupé). Réessaie dans un instant.";
        }
    }
}
