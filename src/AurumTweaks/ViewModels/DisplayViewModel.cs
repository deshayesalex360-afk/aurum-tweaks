using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

public partial class DisplayViewModel : ObservableObject
{
    private readonly IDisplayService _service;

    /// <summary>The last read, kept so « Copier le rapport » renders the real monitors/verdicts, not a re-read.</summary>
    private DisplayReport? _lastReport;

    public ObservableCollection<MonitorState> Monitors { get; } = new();

    [ObservableProperty] private string _headline = "Analyse des écrans…";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    private bool _hasMonitors;

    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    public DisplayViewModel(IDisplayService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { await LoadAsync(); }
        finally { IsBusy = false; }
    }

    private async Task LoadAsync()
    {
        var report = await _service.GetReportAsync();
        _lastReport = report;
        Monitors.Clear();
        foreach (var m in report.Monitors) Monitors.Add(m);
        HasMonitors = report.Any;
        Headline = report.Headline;
    }

    /// <summary>One-click fix: raise this monitor to the highest advertised rate at its current resolution.</summary>
    [RelayCommand]
    private Task SetMaxAsync(MonitorState? monitor)
        => monitor is { CanRaiseRefresh: true } m ? ApplyAsync(m.RaiseTarget) : Task.CompletedTask;

    /// <summary>Power-user path: apply one specific advertised rate.</summary>
    [RelayCommand]
    private Task SetRefreshAsync(RefreshOption? option)
        => option is { IsSelectable: true } o ? ApplyAsync(o) : Task.CompletedTask;

    private async Task ApplyAsync(RefreshOption option)
    {
        if (IsBusy) return; // guard against overlapping changes; the row buttons aren't CanExecute-gated
        IsBusy = true;
        try
        {
            var outcome = await _service.SetRefreshRateAsync(option.DeviceName, option.Width, option.Height, option.Hz);
            await LoadAsync(); // re-read so the UI reflects the MEASURED state
            Status = outcome.Summary;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenDisplaySettings() => ShellLauncher.OpenLink("ms-settings:display");

    [RelayCommand]
    private void OpenAdvancedDisplay() => ShellLauncher.OpenLink("ms-settings:display-advanced");

    /// <summary>
    /// Copy the last read as a shareable text report — a « 60 Hz on a 144 Hz panel » diagnostic gets triaged on forums by a
    /// paste. The render is the tested pure <see cref="DisplayTextReport"/>; this is thin clipboard glue, self-disabled until
    /// a real read exists so it never copies an empty sheet.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasMonitors))]
    private void CopyReport()
    {
        if (_lastReport is not { } report) return;
        var text = DisplayTextReport.Render(report, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = "Rapport d'affichage copié — colle-le où tu veux (forum, ticket de support).";
        }
        catch
        {
            // The clipboard can be momentarily locked by another process — non-fatal, just say so.
            Status = "Copie impossible (presse-papiers occupé). Réessaie dans un instant.";
        }
    }
}
