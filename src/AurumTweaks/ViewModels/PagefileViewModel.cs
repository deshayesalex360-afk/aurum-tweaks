using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Mémoire virtuelle » — surfaces the live pagefile report and the single safe write. The busy-disable idiom gates
/// both commands, and the restore command's CanExecute folds in <see cref="CanRestoreAutomatic"/> so it is never a
/// no-op (disabled when Windows already auto-manages). Custom sizing is handed off to Windows' own dialog.
/// </summary>
public partial class PagefileViewModel : ObservableObject
{
    private readonly IPagefileService _service;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreAutomaticCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreAutomaticCommand))]
    private bool _canRestoreAutomatic;

    [ObservableProperty] private string _modeDisplay = "—";
    [ObservableProperty] private string _headline = "—";
    [ObservableProperty] private string _totalAllocatedDisplay = "—";
    [ObservableProperty] private string _recommendationHeadline = "";
    [ObservableProperty] private string _recommendationDetail = "";
    [ObservableProperty] private bool _verdictOk;
    [ObservableProperty] private bool _verdictWarn;
    [ObservableProperty] private bool _verdictInfo;
    [ObservableProperty] private bool _automaticManaged;
    [ObservableProperty] private bool _hasEntries;
    [ObservableProperty] private string? _status;

    public ObservableCollection<PagefileEntry> Entries { get; } = new();

    public PagefileViewModel(IPagefileService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    private bool CanRun() => !IsBusy;
    private bool CanRestore() => !IsBusy && CanRestoreAutomatic;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { await LoadAsync(); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAutomaticAsync()
    {
        IsBusy = true;
        try
        {
            var outcome = await _service.RestoreAutomaticAsync();
            Status = outcome.Message;
            await LoadAsync();   // reflect the new, VERIFIED state (the button disables itself once auto-managed)
        }
        finally { IsBusy = false; }
    }

    // The real change is risky in the wrong direction (a bad fixed size, or disabling), so custom sizing is honestly
    // handed to Windows' own Virtual Memory dialog rather than faked here. SystemPropertiesPerformance.exe → Avancé →
    // Mémoire virtuelle → Modifier. The absolute System32 path is required (OpenLocal refuses a bare *.exe).
    [RelayCommand]
    private void OpenVirtualMemoryDialog() => ShellLauncher.OpenLocal(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "SystemPropertiesPerformance.exe"));

    [RelayCommand]
    private void OpenAboutSettings() => ShellLauncher.OpenLink("ms-settings:about");

    [RelayCommand]
    private void GoToMemory() => App.Services.GetRequiredService<MainViewModel>().Navigate("Memory");

    private async Task LoadAsync()
    {
        var report = await _service.GetReportAsync();

        Entries.Clear();
        foreach (var entry in report.Entries) Entries.Add(entry);

        ModeDisplay = report.ModeDisplay;
        Headline = report.Headline;
        TotalAllocatedDisplay = report.TotalAllocatedDisplay;
        RecommendationHeadline = report.Recommendation.Headline;
        RecommendationDetail = report.Recommendation.Detail;
        VerdictOk = report.VerdictOk;
        VerdictWarn = report.VerdictWarn;
        VerdictInfo = report.VerdictInfo;
        AutomaticManaged = report.AutomaticManaged;
        CanRestoreAutomatic = report.CanRestoreAutomatic;
        HasEntries = report.HasEntries;
    }
}
