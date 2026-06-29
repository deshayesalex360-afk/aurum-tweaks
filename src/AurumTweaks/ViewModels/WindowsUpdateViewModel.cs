using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Windows Update » page — a curated front-end over documented Update policy values that govern the behaviours most
/// hostile to gaming (P2P upload of updates, forced mid-session reboots, drivers silently replaced). Each toggle is a
/// genuine, reversible HKLM policy write the page RE-READS, so a refused write (managed/enterprise device) shows the
/// unchanged truth, never a fake « fait » — and the bulk action reports exactly how many writes were accepted. An
/// absent key is shown as the Windows default, never invented. Browsing/installing updates is handed off to Windows.
/// </summary>
public partial class WindowsUpdateViewModel : ObservableObject
{
    private readonly IWindowsUpdateService _service;

    public ObservableCollection<WindowsUpdateTweakState> Tweaks { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;

    public WindowsUpdateViewModel(IWindowsUpdateService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture des réglages Windows Update…";
        var report = await _service.GetReportAsync();

        Tweaks.Clear();
        foreach (var t in report.Tweaks) Tweaks.Add(t);

        Status = report.AllOptimized
            ? $"Optimisé · {report.OptimizedCount}/{report.Total} réglage(s) appliqué(s)."
            : $"{report.OptimizedCount}/{report.Total} réglage(s) optimisé(s) · {report.DefaultCount} au défaut Windows.";
        IsBusy = false;
    }

    /// <summary>« Tout optimiser » — set every curated toggle to its gaming-friendly value, then re-read and report acceptance.</summary>
    [RelayCommand]
    private async Task OptimizeAllAsync()
    {
        IsBusy = true;
        var outcome = await _service.ApplyAllAsync(optimize: true);
        await RefreshAsync();
        Status = outcome.Summary;   // honest: surfaces refused HKLM policy writes, never a blanket success
    }

    /// <summary>« Tout rétablir » — restore every toggle to its Windows default value, then re-read and report acceptance.</summary>
    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        IsBusy = true;
        var outcome = await _service.ApplyAllAsync(optimize: false);
        await RefreshAsync();
        Status = outcome.Summary;
    }

    [RelayCommand]
    private Task Optimize(WindowsUpdateTweakState? tweak) => SetOptimizedAsync(tweak, optimize: true);

    [RelayCommand]
    private Task Restore(WindowsUpdateTweakState? tweak) => SetOptimizedAsync(tweak, optimize: false);

    private async Task SetOptimizedAsync(WindowsUpdateTweakState? tweak, bool optimize)
    {
        if (tweak is null) return;
        IsBusy = true;
        var ok = await _service.SetOptimizedAsync(tweak.Id, optimize);
        await RefreshAsync();   // re-read so the row reflects the real registry, never a fabricated success
        if (!ok)
            Status = $"« {tweak.Label} » : écriture refusée par le système (valeur inchangée). Stratégie gérée ou périphérique d'entreprise ?";
    }

    /// <summary>Open Windows' own Update page so the user can check for / install updates — deliberately not faked in-app (honest hand-off).</summary>
    [RelayCommand]
    private void OpenWindowsUpdate() => ShellLauncher.OpenLink("ms-settings:windowsupdate");

    /// <summary>Open the Delivery Optimization settings so the user can cross-check the P2P sharing scope (honest hand-off).</summary>
    [RelayCommand]
    private void OpenDeliveryOptimization() => ShellLauncher.OpenLink("ms-settings:delivery-optimization");

    /// <summary>Open the active-hours settings so the user can bound when Windows is allowed to reboot (honest hand-off).</summary>
    [RelayCommand]
    private void OpenActiveHours() => ShellLauncher.OpenLink("ms-settings:windowsupdate-activehours");
}
