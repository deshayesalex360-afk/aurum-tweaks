using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AurumTweaks.ViewModels;

public partial class NetworkAdaptersViewModel : ObservableObject
{
    private readonly INetworkAdaptersService _service;
    private NetworkAdaptersReport? _lastReport;

    // One busy gate disables every command while any one of them runs (CanExecute idiom, not a Visibility-to-IsEnabled bind).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlushDnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenewDhcpCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _hasAdapters;
    [ObservableProperty] private string _countDisplay = "—";
    [ObservableProperty] private string _headline = "—";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string? _status;

    // Gates « Copier le rapport »: enabled only once a real read has produced a report, so the button is never a no-op.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    private bool _hasReport;

    public ObservableCollection<NetworkAdapterRow> Adapters { get; } = new();

    public NetworkAdaptersViewModel(INetworkAdaptersService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { await LoadAsync(); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task FlushDnsAsync()
    {
        IsBusy = true;
        try { Status = (await _service.FlushDnsAsync()).Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RenewDhcpAsync()
    {
        IsBusy = true;
        try
        {
            Status = (await _service.RenewDhcpAsync()).Message;
            await LoadAsync();   // a renew can change the IPv4/gateway — re-read so the page shows the truth
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenNetworkConnections() => ShellLauncher.OpenLocal("ncpa.cpl");

    [RelayCommand]
    private void OpenNetworkSettings() => ShellLauncher.OpenLink("ms-settings:network-status");

    [RelayCommand]
    private void GoToGaming() => App.Services.GetRequiredService<MainViewModel>().Navigate("Gaming");

    /// <summary>Copy the shareable adapter inventory — the real read-back state, never sent anywhere — to the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(HasReport))]
    private void CopyReport()
    {
        if (_lastReport is null) return;
        var text = NetworkAdaptersTextReport.Render(_lastReport, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = "Rapport copié dans le presse-papiers.";
        }
        catch (Exception)
        {
            Status = "Impossible d'accéder au presse-papiers pour l'instant.";
        }
    }

    private async Task LoadAsync()
    {
        var report = await _service.GetReportAsync();
        _lastReport = report;
        Adapters.Clear();
        foreach (var a in report.Adapters) Adapters.Add(a);
        HasAdapters = report.HasAdapters;
        CountDisplay = report.CountDisplay;
        Headline = report.Headline;
        Detail = report.Detail;
        HasReport = true;
    }
}
