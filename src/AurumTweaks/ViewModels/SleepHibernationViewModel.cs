using System;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Veille &amp; hibernation » page — manages Windows hibernation and the Fast Startup hybrid-shutdown. Both states are
/// read code-page-immune from the registry and RE-READ after every change, so a value Windows rejects comes back as the
/// unchanged truth, never a fabricated « fait ». Hibernation on/off goes through the OS's own <c>powercfg /hibernate</c>
/// (so <c>hiberfil.sys</c> is genuinely created/removed), and the freed disk space shown is the real measured file size.
/// The <c>Can*</c> flags gate every button (bound to <c>IsEnabled</c>) so none is ever a no-op, and Fast Startup is
/// honestly reported as unavailable while hibernation is off. No FPS gain is promised — the payoff is disk space and a
/// true cold shutdown (helpful for dual-boot and for forcing a clean driver reload).
/// </summary>
public partial class SleepHibernationViewModel : ObservableObject
{
    private readonly ISleepHibernationService _service;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;

    // Hibernation row.
    [ObservableProperty] private string _hibernationState = "—";
    [ObservableProperty] private bool _canEnableHibernation;
    [ObservableProperty] private bool _canDisableHibernation;

    // Fast Startup row.
    [ObservableProperty] private string _fastStartupState = "—";
    [ObservableProperty] private bool _canEnableFastStartup;
    [ObservableProperty] private bool _canDisableFastStartup;
    [ObservableProperty] private bool _fastStartupUnavailable;

    // hiberfil.sys size — the honest "this is how much disk you'd free" number.
    [ObservableProperty] private string _hiberfilDisplay = "—";
    [ObservableProperty] private bool _hasHiberfil;

    public SleepHibernationViewModel(ISleepHibernationService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture de l'état de la veille…";
        var report = await _service.GetReportAsync();

        HibernationState = report.Hibernation.StateDisplay;
        CanEnableHibernation = report.Hibernation.CanEnable;
        CanDisableHibernation = report.Hibernation.CanDisable;

        FastStartupState = report.FastStartup.StateDisplay;
        CanEnableFastStartup = report.FastStartup.CanEnable;
        CanDisableFastStartup = report.FastStartup.CanDisable;
        FastStartupUnavailable = !report.FastStartup.IsAvailable;

        HiberfilDisplay = report.HiberfilDisplay;
        HasHiberfil = report.HasHiberfil;

        Status = report.Headline;
        IsBusy = false;
    }

    /// <summary>Enable hibernation via real <c>powercfg /hibernate on</c> (re-creates hiberfil.sys), then re-read.</summary>
    [RelayCommand]
    private async Task EnableHibernationAsync()
    {
        IsBusy = true;
        Status = "Activation de l'hibernation…";
        await _service.SetHibernationAsync(enable: true);
        await RefreshAsync();
    }

    /// <summary>Disable hibernation via real <c>powercfg /hibernate off</c> (removes hiberfil.sys, frees disk), then re-read.</summary>
    [RelayCommand]
    private async Task DisableHibernationAsync()
    {
        IsBusy = true;
        Status = "Désactivation de l'hibernation…";
        await _service.SetHibernationAsync(enable: false);
        await RefreshAsync();
    }

    /// <summary>Turn Fast Startup back on (HiberbootEnabled=1), then re-read.</summary>
    [RelayCommand]
    private async Task EnableFastStartupAsync()
    {
        IsBusy = true;
        await _service.SetFastStartupAsync(enable: true);
        await RefreshAsync();
    }

    /// <summary>Turn Fast Startup off (HiberbootEnabled=0) for a true cold shutdown, then re-read.</summary>
    [RelayCommand]
    private async Task DisableFastStartupAsync()
    {
        IsBusy = true;
        await _service.SetFastStartupAsync(enable: false);
        await RefreshAsync();
    }

    /// <summary>Modern Settings « Alimentation et mise en veille » — the honest place for sleep/screen timeouts.</summary>
    [RelayCommand]
    private void OpenPowerSettings() => ShellLauncher.OpenLink("ms-settings:powersleep");

    /// <summary>Classic Power Options (powercfg.cpl) — where the Fast Startup checkbox lives under « Choisir l'action des
    /// boutons d'alimentation ». A separator-free *.cpl is the allowed shell target.</summary>
    [RelayCommand]
    private void OpenPowerOptions() => ShellLauncher.OpenLocal("powercfg.cpl");
}
