using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// "Alimentation" page — the Windows power-plan manager. Genuinely wired: it shows the real active scheme
/// powercfg reports and switches it for real. Every action re-reads the live state, so a switch Windows refuses
/// surfaces as the unchanged plan, never a fake success. « Performances ultimes » is created via the sanctioned
/// duplicate-scheme step on editions that hide it. No fabricated FPS/latency numbers — only factual guidance.
/// </summary>
public partial class PowerPlanViewModel : ObservableObject
{
    private readonly IPowerPlanService _power;
    private ProcessorPowerTuning? _baselineTuning;
    private bool _normalizingProcessorTuning;

    /// <summary>The last read, kept so « Copier le rapport » renders the real schemes/detail, not a re-read.</summary>
    private PowerPlanReport? _lastReport;

    public ObservableCollection<PowerScheme> Schemes { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;

    /// <summary>The active plan's live processor knobs (min/max state, core parking) and editable AC draft.</summary>
    [ObservableProperty] private ProcessorPowerDetail? _processorDetail;
    [ObservableProperty] private bool _hasProcessorDetail;
    [ObservableProperty] private bool _hasProcessorTuning;
    [ObservableProperty] private int _processorMinPercent;
    [ObservableProperty] private int _processorMaxPercent;
    [ObservableProperty] private int _coreParkingPercent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    private bool _hasReport;

    public string ProcessorTuningPreview =>
        $"Min {ProcessorMinPercent} % · Max {ProcessorMaxPercent} % · {ProcessorPowerTuningPlan.CoreParkingDraftDisplay(CoreParkingPercent)}";

    public string ProcessorRestorePreview =>
        _baselineTuning is null
            ? "Restauration indisponible tant que powercfg n'a pas lu les trois valeurs."
            : $"Restaure la lecture de départ : min {_baselineTuning.MinThrottlePercent} %, max {_baselineTuning.MaxThrottlePercent} %, cœurs actifs {_baselineTuning.MinUnparkedCoresPercent} %.";

    public PowerPlanViewModel(IPowerPlanService power)
    {
        _power = power;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture des plans d'alimentation…";
        var report = await _power.GetReportAsync();
        _lastReport = report;
        HasReport = report.Schemes.Count > 0;
        Schemes.Clear();
        foreach (var s in report.Schemes) Schemes.Add(s);

        var detail = await _power.GetProcessorDetailAsync();
        ProcessorDetail = detail;
        HasProcessorDetail = detail.QueryOk;   // a total powercfg failure hides the card rather than showing all « — »
        LoadProcessorTuning(ProcessorPowerTuning.FromDetail(detail));

        Status = report.Schemes.Count == 0
            ? "Impossible de lire les plans d'alimentation (powercfg)."
            : $"Plan actif : {report.ActiveName ?? "inconnu"} · {report.Schemes.Count} plan(s) disponible(s).";
        IsBusy = false;
    }

    /// <summary>Activate one scheme, then re-read so the list reflects the real active plan.</summary>
    [RelayCommand]
    private async Task ActivateAsync(PowerScheme? scheme)
    {
        if (scheme is null || scheme.IsActive) return;
        IsBusy = true;
        await _power.ActivateAsync(scheme.Id);
        await RefreshAsync();
    }

    /// <summary>Create (if hidden on this edition) and activate « Performances ultimes », then re-read.</summary>
    [RelayCommand]
    private async Task EnableUltimateAsync()
    {
        IsBusy = true;
        await _power.EnableUltimateAsync();
        await RefreshAsync();
    }

    /// <summary>Apply the slider values through documented powercfg PPM settings, then re-read the real plan.</summary>
    [RelayCommand]
    private async Task ApplyProcessorTuningAsync()
    {
        if (!HasProcessorTuning) return;
        IsBusy = true;
        var ok = await _power.SetProcessorTuningAsync(CurrentProcessorTuning());
        await RefreshAsync();
        Status = ok
            ? "Réglages processeur appliqués via powercfg puis relus."
            : "powercfg a refusé au moins un réglage processeur ; l'état affiché a été relu.";
    }

    /// <summary>Restore the exact processor settings read when the page first got a complete powercfg answer.</summary>
    [RelayCommand]
    private async Task RestoreProcessorTuningAsync()
    {
        if (_baselineTuning is null) return;
        IsBusy = true;
        var ok = await _power.SetProcessorTuningAsync(_baselineTuning);
        await RefreshAsync();
        Status = ok
            ? "Valeurs processeur lues au départ restaurées via powercfg."
            : "powercfg a refusé la restauration des valeurs processeur ; l'état affiché a été relu.";
    }

    /// <summary>Open Windows' own power-options panel — we complement it, we don't pretend to be the only path.</summary>
    [RelayCommand]
    private void OpenWindowsPower() => ShellLauncher.OpenLocal("powercfg.cpl");

    /// <summary>
    /// Copy the last read as a shareable text report — power/throttling problems get triaged on forums by a paste. The
    /// render is the tested pure <see cref="PowerPlanTextReport"/>; this is thin clipboard glue, self-disabled until a
    /// real read exists so it never copies an empty sheet.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasReport))]
    private void CopyReport()
    {
        if (_lastReport is not { } report) return;
        var text = PowerPlanTextReport.Render(report, ProcessorDetail, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = "Rapport d'alimentation copié — colle-le où tu veux (forum, ticket de support).";
        }
        catch
        {
            // The clipboard can be momentarily locked by another process — non-fatal, just say so.
            Status = "Copie impossible (presse-papiers occupé). Réessaie dans un instant.";
        }
    }

    private ProcessorPowerTuning CurrentProcessorTuning() =>
        new(ProcessorMinPercent, ProcessorMaxPercent, CoreParkingPercent);

    private void LoadProcessorTuning(ProcessorPowerTuning? tuning)
    {
        HasProcessorTuning = tuning is not null;
        if (tuning is null) return;

        _baselineTuning ??= tuning;

        _normalizingProcessorTuning = true;
        ProcessorMinPercent = tuning.MinThrottlePercent;
        ProcessorMaxPercent = tuning.MaxThrottlePercent;
        CoreParkingPercent = tuning.MinUnparkedCoresPercent;
        _normalizingProcessorTuning = false;
        NotifyProcessorTuningText();
    }

    partial void OnProcessorMinPercentChanged(int value)
    {
        if (_normalizingProcessorTuning) return;
        _normalizingProcessorTuning = true;
        var clamped = Math.Clamp(value, 0, 100);
        if (ProcessorMinPercent != clamped) ProcessorMinPercent = clamped;
        if (ProcessorMaxPercent < clamped) ProcessorMaxPercent = clamped;
        _normalizingProcessorTuning = false;
        NotifyProcessorTuningText();
    }

    partial void OnProcessorMaxPercentChanged(int value)
    {
        if (_normalizingProcessorTuning) return;
        _normalizingProcessorTuning = true;
        var clamped = Math.Clamp(value, 0, 100);
        if (ProcessorMaxPercent != clamped) ProcessorMaxPercent = clamped;
        if (ProcessorMinPercent > clamped) ProcessorMinPercent = clamped;
        _normalizingProcessorTuning = false;
        NotifyProcessorTuningText();
    }

    partial void OnCoreParkingPercentChanged(int value)
    {
        if (_normalizingProcessorTuning) return;
        _normalizingProcessorTuning = true;
        var clamped = Math.Clamp(value, 0, 100);
        if (CoreParkingPercent != clamped) CoreParkingPercent = clamped;
        _normalizingProcessorTuning = false;
        NotifyProcessorTuningText();
    }

    private void NotifyProcessorTuningText()
    {
        OnPropertyChanged(nameof(ProcessorTuningPreview));
        OnPropertyChanged(nameof(ProcessorRestorePreview));
    }
}
