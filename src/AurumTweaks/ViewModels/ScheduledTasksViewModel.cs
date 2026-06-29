using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Tâches planifiées » page — a curated, reversible telemetry/privacy task manager. Everything here is genuinely
/// wired: it reads the live state of well-known Windows tasks via <c>Get-ScheduledTask</c> and toggles them with a
/// real <c>schtasks /Change</c> (exact inverses). A toggle Windows rejects comes back as the unchanged real state
/// after the re-read — never a fake "done". The page is honest about scope: this is confidentialité/télémétrie, not
/// a magic FPS lever, and the one useful maintenance task is flagged « à conserver » rather than offered as bloat.
/// </summary>
public partial class ScheduledTasksViewModel : ObservableObject
{
    private readonly IScheduledTaskService _service;

    public ObservableCollection<ScheduledTaskEntry> Tasks { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;

    public ScheduledTasksViewModel(IScheduledTaskService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture de l'état des tâches planifiées…";
        var report = await _service.GetReportAsync();

        Tasks.Clear();
        foreach (var t in report.Entries) Tasks.Add(t);

        Status = report.QueryOk
            ? $"{report.EnabledRecommendedCount} tâche(s) de confidentialité encore active(s) · {report.PresentCount} tâche(s) connue(s) présente(s) sur ce PC."
            : "Impossible de lire l'état des tâches planifiées (PowerShell / Get-ScheduledTask).";
        IsBusy = false;
    }

    /// <summary>Flip one task, then re-read so the list reflects the real machine — never a fabricated success.</summary>
    [RelayCommand]
    private async Task ToggleAsync(ScheduledTaskEntry? entry)
    {
        if (entry is null || !entry.IsPresent) return;   // absent tasks have no toggle to honour
        IsBusy = true;
        await _service.SetEnabledAsync(entry.FullPath, !entry.IsEnabled);
        await RefreshAsync();
    }

    /// <summary>Open Windows' own Task Scheduler — we don't pretend our curated list is the whole picture.</summary>
    [RelayCommand]
    private void OpenTaskScheduler() => ShellLauncher.OpenLocal("taskschd.msc");
}
