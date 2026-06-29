using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Confidentialité » page — a curated front-end over Windows' consent/telemetry switches. Each toggle is a genuine,
/// reversible registry write the page RE-READS, so a refused write shows the unchanged truth, never a fake « done ».
/// An absent key is shown as the Windows default (collecte active), never invented. The honest framing: this REDUCES
/// the data Windows collects — it does not make the OS private — and the telemetry floor on Famille/Pro is disclosed
/// on the row itself. The services and scheduled tasks that carry telemetry are managed on their own pages.
/// </summary>
public partial class PrivacyViewModel : ObservableObject
{
    private readonly IPrivacyService _service;

    public ObservableCollection<PrivacySettingState> Settings { get; } = new();

    [ObservableProperty] private string? _status;
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
        var report = await _service.GetReportAsync();

        Settings.Clear();
        foreach (var s in report.Settings) Settings.Add(s);

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
