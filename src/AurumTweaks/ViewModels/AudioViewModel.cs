using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Son » — surfaces the Communications ducking preference and the two reversible writes (apply « Ne rien faire » for
/// gaming, restore the Windows default 80 %). The busy-disable idiom gates every command, and each write's CanExecute
/// folds in its can-flag (<see cref="CanApplyRecommended"/> / <see cref="CanRestoreDefault"/>) so a button is never a
/// no-op — never bound via IsEnabled, which would override CanExecute. Exclusive mode / sample rate / spatial / per-app
/// volume are honestly handed off to Windows' own panels rather than faked.
/// </summary>
public partial class AudioViewModel : ObservableObject
{
    private readonly IAudioService _service;

    /// <summary>The last read, kept so « Copier le rapport » renders the real preference/devices, not a re-read.</summary>
    private AudioReport? _lastReport;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecommendedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreDefaultCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    private bool _hasReport;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecommendedCommand))]
    private bool _canApplyRecommended;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreDefaultCommand))]
    private bool _canRestoreDefault;

    [ObservableProperty] private string _duckingDisplay = "—";
    [ObservableProperty] private string _headline = "—";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _verdictOk;
    [ObservableProperty] private bool _verdictWarn;
    [ObservableProperty] private bool _verdictInfo;
    [ObservableProperty] private bool _isRecommended;
    [ObservableProperty] private string _schemeDisplay = "—";
    [ObservableProperty] private bool _systemSoundsSilent;
    [ObservableProperty] private bool _hasDevices;
    [ObservableProperty] private string? _status;

    public ObservableCollection<AudioDevice> Devices { get; } = new();

    public AudioViewModel(IAudioService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    private bool CanRun() => !IsBusy;
    private bool CanApply() => !IsBusy && CanApplyRecommended;
    private bool CanRestore() => !IsBusy && CanRestoreDefault;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { await LoadAsync(); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyRecommendedAsync() => await SetDuckingAsync(AudioDucking.DoNothing);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreDefaultAsync() => await SetDuckingAsync(AudioDucking.ReduceOther80);

    private async Task SetDuckingAsync(AudioDucking desired)
    {
        IsBusy = true;
        try
        {
            var outcome = await _service.SetDuckingAsync(desired);
            Status = outcome.Message;
            await LoadAsync();   // reflect the new, VERIFIED state (each button disables itself once its target is live)
        }
        finally { IsBusy = false; }
    }

    // Exclusive mode, sample rate / bit depth, spatial audio and the Communications tab itself live in the classic
    // Sound applet — handed off honestly (mmsys.cpl is a separator-free .cpl that ShellLauncher.OpenLocal allows).
    [RelayCommand]
    private void OpenSoundPanel() => ShellLauncher.OpenLocal("mmsys.cpl");

    [RelayCommand]
    private void OpenSoundSettings() => ShellLauncher.OpenLink("ms-settings:sound");

    [RelayCommand]
    private void OpenAppVolume() => ShellLauncher.OpenLink("ms-settings:apps-volume");

    /// <summary>
    /// Copy the last read as a shareable text report — « pourquoi le son du jeu baisse quand Discord parle ? » gets triaged
    /// on Discord/forums by a paste. The render is the tested pure <see cref="AudioTextReport"/>; this is thin clipboard
    /// glue, self-disabled until a real read exists so it never copies an empty sheet.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasReport))]
    private void CopyReport()
    {
        if (_lastReport is not { } report) return;
        var text = AudioTextReport.Render(report, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = "Rapport audio copié — colle-le où tu veux (forum, ticket de support).";
        }
        catch
        {
            // The clipboard can be momentarily locked by another process — non-fatal, just say so.
            Status = "Copie impossible (presse-papiers occupé). Réessaie dans un instant.";
        }
    }

    private async Task LoadAsync()
    {
        var report = await _service.GetReportAsync();
        _lastReport = report;
        HasReport = true;

        Devices.Clear();
        foreach (var device in report.Devices) Devices.Add(device);

        DuckingDisplay = report.DuckingDisplay;
        Headline = report.Headline;
        Detail = report.Detail;
        VerdictOk = report.VerdictOk;
        VerdictWarn = report.VerdictWarn;
        VerdictInfo = report.VerdictInfo;
        IsRecommended = report.IsRecommended;
        SchemeDisplay = report.SchemeDisplay;
        SystemSoundsSilent = report.SystemSoundsSilent;
        HasDevices = report.HasDevices;
        CanApplyRecommended = report.CanApplyRecommended;
        CanRestoreDefault = report.CanRestoreDefault;
    }
}
