using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Points de restauration » page — the user-facing safety net that pairs with the app's "create a restore point before
/// applying tweaks" promise. It lists the REAL points Windows reports, creates a MEASURED checkpoint (the new point must
/// actually appear, otherwise the page says Windows skipped it under its 24h throttle — never a fake « créé »), and
/// exposes the throttle lever as a genuine reversible registry write the page RE-READS. The actual roll-back is handed
/// off to Windows' own <c>rstrui.exe</c> wizard — deliberately not faked in-app: restoring is a reboot-level operation.
/// </summary>
public partial class RestoreManagerViewModel : ObservableObject
{
    private readonly IRestoreManagerService _service;

    public ObservableCollection<RestorePoint> Points { get; } = new();

    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCheckpointCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _hasPoints;
    [ObservableProperty] private bool _hasNoPoints;

    // QueryOk false = « Lecture impossible (protection système désactivée…) ». ProtectionOff mirrors it so the
    // « Activer la protection » action shows up EXACTLY when protection reads off — never a pointless click when it's on.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtectionOff))]
    private bool _queryOk;
    public bool ProtectionOff => !QueryOk;

    [ObservableProperty] private string _checkpointDescription = "Point Aurum";

    // The throttle lever, surfaced for the UI; the Can* gates mean neither button is ever a no-op.
    [ObservableProperty] private string? _frequencyState;
    [ObservableProperty] private bool _canUnthrottle;
    [ObservableProperty] private bool _canRestoreThrottle;

    public RestoreManagerViewModel(IRestoreManagerService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture des points de restauration…";
        var overview = await _service.GetOverviewAsync();

        Points.Clear();
        foreach (var p in overview.Points) Points.Add(p);

        QueryOk = overview.QueryOk;
        HasPoints = overview.HasPoints;
        HasNoPoints = !overview.HasPoints;
        FrequencyState = overview.Frequency.StateDisplay;
        CanUnthrottle = overview.Frequency.CanUnthrottle;
        CanRestoreThrottle = overview.Frequency.CanRestoreThrottle;
        Status = overview.Headline;
        IsBusy = false;
    }

    private bool CanCreateCheckpoint() => !IsBusy;

    /// <summary>Create a checkpoint, then re-read; the honest measured outcome overwrites the refresh headline so the
    /// throttle case ("Windows didn't create one, a point exists in the last 24h") is shown, never a fake success.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateCheckpoint))]
    private async Task CreateCheckpointAsync()
    {
        IsBusy = true;
        Status = "Création du point de restauration… (cela peut prendre un moment)";
        var outcome = await _service.CreateCheckpointAsync(CheckpointDescription);
        await RefreshAsync();
        Status = outcome.Headline;
    }

    /// <summary>« Lever la limite » — write SystemRestorePointCreationFrequency=0 so every request creates a point.</summary>
    [RelayCommand]
    private async Task UnthrottleAsync()
    {
        IsBusy = true;
        await _service.SetUnthrottledAsync(unthrottle: true);
        await RefreshAsync();
    }

    /// <summary>« Rétablir la limite » — remove the override so Windows' default 24h throttle returns (true inverse).</summary>
    [RelayCommand]
    private async Task RestoreThrottleAsync()
    {
        IsBusy = true;
        await _service.SetUnthrottledAsync(unthrottle: false);
        await RefreshAsync();
    }

    /// <summary>Hand off to Windows' own System Restore wizard for the actual roll-back — never faked in-app. The
    /// absolute System32 path is required (ShellLauncher refuses a bare *.exe as an elevation sink).</summary>
    [RelayCommand]
    private void OpenRestoreWizard() =>
        ShellLauncher.OpenLocal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rstrui.exe"));

    /// <summary>« Activer la protection » — actually turn System Restore on (the service runs the elevated
    /// Enable-ComputerRestore), then re-read. One selection = one action: the APP does it, not a hand-off to Windows'
    /// own dialog. RefreshAsync re-reads QueryOk — the honest confirmation: protection clears the « impossible » state
    /// IFF Windows now reads it back; if policy blocks it, the page still honestly shows « impossible », never a faked
    /// success. Surfaced only while protection reads off (ProtectionOff), so it's never a pointless click.</summary>
    [RelayCommand]
    private async Task EnableProtectionAsync()
    {
        IsBusy = true;
        Status = "Activation de la Restauration système…";
        await _service.EnableProtectionAsync();
        await RefreshAsync(); // re-reads QueryOk (the honest confirmation) and clears IsBusy
    }
}
