using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Applications préinstallées » page — curated AppX debloat plus read-only hidden AppX reveal and winget
/// install/upgrade management. Every destructive AppX removal is marked as non-inversible in Aurum; hidden AppX
/// packages are read-only unless curated; winget actions only run IDs already listed in the UI.
/// </summary>
public partial class AppxDebloatViewModel : ObservableObject
{
    private readonly IAppxDebloatService _service;

    public ObservableCollection<AppxEntry> Apps { get; } = new();
    public ObservableCollection<HiddenAppxEntry> HiddenApps { get; } = new();
    public ObservableCollection<WingetInstallChoice> WingetInstallOptions { get; } = new();
    public ObservableCollection<WingetUpgradeEntry> WingetUpgrades { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _wingetStatus;
    [ObservableProperty] private bool _hasHiddenApps;
    [ObservableProperty] private bool _hasWinget;
    [ObservableProperty] private bool _hasWingetUpgrades;
    [ObservableProperty] private bool _hasSelectedWingetInstalls;
    [ObservableProperty] private bool _isBusy;

    public AppxDebloatViewModel(IAppxDebloatService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    partial void OnIsBusyChanged(bool value)
    {
        InstallSelectedWingetCommand.NotifyCanExecuteChanged();
        UpgradeListedWingetCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasSelectedWingetInstallsChanged(bool value) =>
        InstallSelectedWingetCommand.NotifyCanExecuteChanged();

    partial void OnHasWingetUpgradesChanged(bool value) =>
        UpgradeListedWingetCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { await LoadAsync(); }
        finally { IsBusy = false; }
    }

    private async Task LoadAsync()
    {
        Status = "Lecture des applications préinstallées et AppX cachées…";
        var report = await _service.GetReportAsync();

        Apps.Clear();
        foreach (var a in report.Entries) Apps.Add(a);

        HiddenApps.Clear();
        foreach (var h in report.HiddenPackageRows) HiddenApps.Add(h);
        HasHiddenApps = HiddenApps.Count > 0;

        Status = report.QueryOk
            ? $"{report.RemovableRecommendedCount} application(s) superflue(s) encore installée(s) · {report.InstalledCount} application(s) connue(s) présente(s) · {report.HiddenCount} AppX non cataloguée(s) révélée(s)."
            : "Impossible de lire les applications installées (PowerShell / Get-AppxPackage).";

        WingetStatus = "Lecture de winget…";
        var winget = await _service.GetWingetReportAsync();
        HasWinget = winget.WingetAvailable;
        WingetStatus = winget.Message;

        ClearWingetInstallOptions();
        foreach (var option in winget.InstallOptions)
        {
            var row = new WingetInstallChoice(option);
            row.PropertyChanged += WingetChoiceChanged;
            WingetInstallOptions.Add(row);
        }
        RefreshWingetSelection();

        WingetUpgrades.Clear();
        foreach (var upgrade in winget.UpgradeCandidates) WingetUpgrades.Add(upgrade);
        HasWingetUpgrades = winget.HasUpgrades;
    }

    /// <summary>Uninstall one app, then re-read so the list reflects the real machine — never a fabricated success.</summary>
    [RelayCommand]
    private async Task RemoveAsync(AppxEntry? entry)
    {
        if (entry is null || !entry.ShowRemove) return;   // absent / protected apps have no uninstall to offer
        IsBusy = true;
        try
        {
            await _service.RemoveAsync(entry.PackageFullName);
            await LoadAsync();
        }
        finally { IsBusy = false; }
    }

    private bool CanInstallSelectedWinget() => !IsBusy && HasSelectedWingetInstalls;

    [RelayCommand(CanExecute = nameof(CanInstallSelectedWinget))]
    private async Task InstallSelectedWingetAsync()
    {
        var ids = WingetInstallOptions
            .Where(o => o.IsSelected && o.CanSelect)
            .Select(o => o.Id)
            .ToList();
        if (ids.Count == 0) return;

        IsBusy = true;
        WingetStatus = $"Installation winget listée : {string.Join(", ", ids)}";
        WingetActionReport? result = null;
        try
        {
            result = await _service.InstallWingetAsync(ids);
            await LoadAsync();
        }
        finally
        {
            if (result is not null) WingetStatus = result.Summary;
            IsBusy = false;
        }
    }

    private bool CanUpgradeListedWinget() => !IsBusy && HasWingetUpgrades;

    [RelayCommand(CanExecute = nameof(CanUpgradeListedWinget))]
    private async Task UpgradeListedWingetAsync()
    {
        var ids = WingetPlan.ListedUpgradeIds(WingetUpgrades, WingetUpgrades.Select(u => u.Id));
        if (ids.Count == 0) return;

        IsBusy = true;
        WingetStatus = $"Mise à jour winget des paquets listés : {string.Join(", ", ids)}";
        WingetActionReport? result = null;
        try
        {
            result = await _service.UpgradeWingetAsync(ids);
            await LoadAsync();
        }
        finally
        {
            if (result is not null) WingetStatus = result.Summary;
            IsBusy = false;
        }
    }

    /// <summary>Open Windows' own « Applications installées » page — we don't pretend our curated list is the whole picture.</summary>
    [RelayCommand]
    private void OpenAppsSettings() => ShellLauncher.OpenLink("ms-settings:appsfeatures");

    private void WingetChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WingetInstallChoice.IsSelected)) RefreshWingetSelection();
    }

    private void RefreshWingetSelection() =>
        HasSelectedWingetInstalls = WingetInstallOptions.Any(o => o.IsSelected && o.CanSelect);

    private void ClearWingetInstallOptions()
    {
        foreach (var row in WingetInstallOptions)
            row.PropertyChanged -= WingetChoiceChanged;
        WingetInstallOptions.Clear();
    }
}

public partial class WingetInstallChoice : ObservableObject
{
    public WingetInstallChoice(WingetInstallOption option) => Option = option;

    public WingetInstallOption Option { get; }
    public string Id => Option.Id;
    public string Label => Option.Label;
    public string CategoryDisplay => Option.CategoryDisplay;
    public string StateDisplay => Option.StateDisplay;
    public bool CanSelect => Option.CanInstall;

    [ObservableProperty] private bool _isSelected;
}
