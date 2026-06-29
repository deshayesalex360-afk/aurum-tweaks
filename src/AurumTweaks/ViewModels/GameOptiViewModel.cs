using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Optimisations jeu » page — a curated front-end over documented gaming/responsiveness registry tweaks. Each toggle
/// is a genuine, reversible registry write the page RE-READS, so a refused write shows the unchanged truth, never a fake
/// « fait ». An absent key is shown as the Windows default, never invented. The honest framing: these are documented
/// tweaks whose gain is variable and config-dependent — no FPS figure is promised — and a reboot/relog may be needed for
/// some to take full effect. GPU scheduling (HAGS) and the capture settings are handed off to Windows' own pages.
/// </summary>
public partial class GameOptiViewModel : ObservableObject
{
    private readonly IGameOptiService _service;

    public ObservableCollection<GameTweakState> Tweaks { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;

    // Read-only state for the two settings the page surfaces but never writes (HAGS / MPO). Null until the first read.
    [ObservableProperty] private DisplayGpuState? _displayGpu;
    [ObservableProperty] private bool _hasGpuInfo;

    public GameOptiViewModel(IGameOptiService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture des réglages d'optimisation…";
        var report = await _service.GetReportAsync();

        Tweaks.Clear();
        foreach (var t in report.Tweaks) Tweaks.Add(t);

        DisplayGpu = report.DisplayGpu;
        HasGpuInfo = report.DisplayGpu is not null;

        Status = report.AllOptimized
            ? $"Optimisé · {report.OptimizedCount}/{report.Total} réglage(s) appliqué(s)."
            : $"{report.OptimizedCount}/{report.Total} réglage(s) optimisé(s) · {report.DefaultCount} au défaut Windows.";
        IsBusy = false;
    }

    /// <summary>« Tout optimiser » — set every curated tweak to its performance value, then re-read.</summary>
    [RelayCommand]
    private async Task OptimizeAllAsync()
    {
        IsBusy = true;
        await _service.ApplyAllAsync(optimize: true);
        await RefreshAsync();
    }

    /// <summary>« Tout rétablir » — restore every tweak to its Windows default value, then re-read.</summary>
    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        IsBusy = true;
        await _service.ApplyAllAsync(optimize: false);
        await RefreshAsync();
    }

    [RelayCommand]
    private Task Optimize(GameTweakState? tweak) => SetOptimizedAsync(tweak, optimize: true);

    [RelayCommand]
    private Task Restore(GameTweakState? tweak) => SetOptimizedAsync(tweak, optimize: false);

    private async Task SetOptimizedAsync(GameTweakState? tweak, bool optimize)
    {
        if (tweak is null) return;
        IsBusy = true;
        await _service.SetOptimizedAsync(tweak.Id, optimize);
        await RefreshAsync();   // re-read so the row reflects the real registry, never a fabricated success
    }

    /// <summary>Open Windows' own graphics settings so the user can toggle HAGS — deliberately not faked in-app (honest hand-off).</summary>
    [RelayCommand]
    private void OpenGraphicsSettings() => ShellLauncher.OpenLink("ms-settings:display-advancedgraphics");

    /// <summary>Open Windows' own capture settings (the Game DVR / Game Bar UI) so the user can cross-check (honest hand-off).</summary>
    [RelayCommand]
    private void OpenCaptureSettings() => ShellLauncher.OpenLink("ms-settings:gaming-gamebar");
}
