using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Effets visuels » page — the interactive equivalent of Windows' Performance Options. Each toggle is a genuine,
/// reversible HKCU registry write the page RE-READS, so a refused write shows the unchanged truth, never a fake
/// « done ». An absent key is shown as the Windows default (activé), never invented. The honest framing: this makes
/// the desktop snappier and a touch lighter on modest hardware — it is not an in-game FPS lever — and some effects
/// only finish applying after a sign-out or an Explorer restart.
/// </summary>
public partial class VisualEffectsViewModel : ObservableObject
{
    private readonly IVisualEffectsService _service;

    public ObservableCollection<EffectState> Effects { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _modeDisplay;
    [ObservableProperty] private bool _isBusy;

    public VisualEffectsViewModel(IVisualEffectsService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture de l'état des effets visuels…";
        var report = await _service.GetReportAsync();

        Effects.Clear();
        foreach (var e in report.Effects) Effects.Add(e);

        ModeDisplay = $"Mode actuel : {report.ModeDisplay}";
        Status = report.AllPerformance
            ? $"Optimisé · {report.PerformanceCount} effet(s) désactivé(s) pour la performance (ClearType conservé)."
            : $"{report.PerformanceCount} effet(s) déjà désactivé(s) · {report.AppearanceCount} encore actif(s).";
        IsBusy = false;
    }

    /// <summary>Performance preset: disable every effect except those flagged « à conserver » (ClearType).</summary>
    [RelayCommand]
    private async Task ApplyPerformanceAsync()
    {
        IsBusy = true;
        await _service.ApplyPresetAsync(performance: true);
        await RefreshAsync();
    }

    /// <summary>Appearance preset: restore every effect to its pretty/on value.</summary>
    [RelayCommand]
    private async Task ApplyAppearanceAsync()
    {
        IsBusy = true;
        await _service.ApplyPresetAsync(performance: false);
        await RefreshAsync();
    }

    [RelayCommand]
    private Task Enable(EffectState? effect) => SetEffectAsync(effect, appearance: true);

    [RelayCommand]
    private Task Disable(EffectState? effect) => SetEffectAsync(effect, appearance: false);

    private async Task SetEffectAsync(EffectState? effect, bool appearance)
    {
        if (effect is null) return;
        IsBusy = true;
        await _service.SetEffectAsync(effect.Id, appearance);
        await RefreshAsync();   // re-read so the row reflects the real registry, never a fabricated success
    }

    /// <summary>
    /// Open Windows' own Performance Options dialog so the user can cross-check the exact same settings. We pass
    /// the absolute System32 path (ShellLauncher allows an existing file, but refuses a bare *.exe as an elevation
    /// sink) — the same honest hand-off Disk Cleanup uses for cleanmgr.
    /// </summary>
    [RelayCommand]
    private void OpenPerformanceOptions() =>
        ShellLauncher.OpenLocal(Path.Combine(Environment.SystemDirectory, "SystemPropertiesPerformance.exe"));
}
