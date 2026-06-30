using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Confidentialité » page — a curated front-end over Windows' consent/telemetry switches. Each toggle is a genuine,
/// reversible registry write the page RE-READS, so a refused write shows the unchanged truth, never a fake « done ».
/// An absent key is shown as that setting's default/not-configured state, never invented as protected. The honest
/// framing: this REDUCES the data Windows collects — it does not make the OS private — and the telemetry floor on
/// Famille/Pro is disclosed on the row itself. AI policies and firewall rules disclose their limits; firewall blocking
/// is opt-in, named, and removable, never a hosts/DNS hijack.
/// </summary>
public partial class PrivacyViewModel : ObservableObject
{
    private readonly IPrivacyService _service;

    public ObservableCollection<PrivacySettingState> Settings { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _telemetryFirewallStatus;
    [ObservableProperty] private string _telemetryFirewallNote = PrivacyFirewallPlan.UiLimit;
    [ObservableProperty] private bool _canBlockTelemetryFirewall;
    [ObservableProperty] private bool _canRemoveTelemetryFirewall;
    [ObservableProperty] private bool _isBusy;

    public PrivacyViewModel(IPrivacyService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture des réglages de confidentialité…";
        var reportTask = _service.GetReportAsync();
        var firewallTask = _service.GetTelemetryFirewallReportAsync();
        await Task.WhenAll(reportTask, firewallTask);
        var report = reportTask.Result;
        var firewall = firewallTask.Result;

        Settings.Clear();
        foreach (var s in report.Settings) Settings.Add(s);

        TelemetryFirewallStatus = firewall.StateDisplay;
        CanBlockTelemetryFirewall = firewall.CanBlock;
        CanRemoveTelemetryFirewall = firewall.CanRemove;

        Status = report.AllHardened
            ? $"Renforcé · {report.HardenedCount}/{report.Total} réglage(s) protégé(s)."
            : $"{report.HardenedCount}/{report.Total} réglage(s) protégé(s) · {report.DefaultCount} au défaut Windows.";
        IsBusy = false;
    }

    /// <summary>« Tout renforcer » — set every curated setting to its privacy-protective value, then re-read.</summary>
    [RelayCommand]
    private async Task HardenAllAsync()
    {
        IsBusy = true;
        await _service.ApplyAllAsync(harden: true);
        await RefreshAsync();
    }

    /// <summary>« Tout rétablir » — restore every setting to its Windows default value, then re-read.</summary>
    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        IsBusy = true;
        await _service.ApplyAllAsync(harden: false);
        await RefreshAsync();
    }

    [RelayCommand]
    private Task Harden(PrivacySettingState? setting) => SetHardenedAsync(setting, harden: true);

    [RelayCommand]
    private Task Restore(PrivacySettingState? setting) => SetHardenedAsync(setting, harden: false);

    [RelayCommand]
    private async Task BlockTelemetryFirewallAsync()
    {
        IsBusy = true;
        var ok = await _service.SetTelemetryFirewallBlockedAsync(block: true);
        await RefreshAsync();
        if (!ok)
            Status = "Le pare-feu Windows a refusé la création d'au moins une règle Aurum ; l'état affiché a été relu.";
    }

    [RelayCommand]
    private async Task RemoveTelemetryFirewallAsync()
    {
        IsBusy = true;
        var ok = await _service.SetTelemetryFirewallBlockedAsync(block: false);
        await RefreshAsync();
        if (!ok)
            Status = "Le pare-feu Windows a refusé le retrait d'au moins une règle Aurum ; l'état affiché a été relu.";
    }

    private async Task SetHardenedAsync(PrivacySettingState? setting, bool harden)
    {
        if (setting is null) return;
        IsBusy = true;
        await _service.SetHardenedAsync(setting.Id, harden);
        await RefreshAsync();   // re-read so the row reflects the real registry, never a fabricated success
    }

    /// <summary>Open Windows' own Privacy settings so the user can cross-check the same switches (honest hand-off).</summary>
    [RelayCommand]
    private void OpenPrivacySettings() => ShellLauncher.OpenLink("ms-settings:privacy");
}
