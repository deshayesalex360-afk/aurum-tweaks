using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Mémoire vive » page — a live composition view plus the honest version of an ISLC/RAMMap-style cache flush.
/// The composition is read from the kernel page lists (exact, language-immune); the flush is a real
/// NtSetSystemInformation call whose effect is re-measured, so the figure shown is the standby that genuinely
/// disappeared. The page states plainly that emptying the standby cache does not raise « disponible » memory and is
/// not an FPS boost — Windows keeps that cache on purpose. No dead button: every flush fires a real kernel command.
/// </summary>
public partial class MemoryViewModel : ObservableObject
{
    private readonly IMemoryManagementService _service;

    /// <summary>The curated flush actions, bound directly from the pure catalog.</summary>
    public IReadOnlyList<MemoryFlushAction> Flushes => MemoryFlushCatalog.Actions;

    [ObservableProperty] private MemoryComposition _composition = MemoryComposition.Empty;
    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlushCommand))]
    private bool _isBusy;

    private bool NotBusy => !IsBusy;

    public MemoryViewModel(IMemoryManagementService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture de la composition mémoire…";
        Composition = await _service.GetCompositionAsync();
        Status = MemoryAdvice.Summarize(Composition);
        IsBusy = false;
    }

    /// <summary>Fire a real flush, then re-read so the headline reflects the machine — and report the measured delta.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task FlushAsync(MemoryFlushAction? action)
    {
        if (action is null) return;
        IsBusy = true;
        Status = $"{action.Label}…";
        var outcome = await _service.FlushAsync(action.Kind);
        Composition = await _service.GetCompositionAsync();
        Status = DescribeOutcome(outcome);
        IsBusy = false;
    }

    /// <summary>Open Windows' Resource Monitor (Mémoire) — its graph shows the same standby/free/modified split natively.</summary>
    [RelayCommand]
    private void OpenResourceMonitor() =>
        ShellLauncher.OpenLocal(Path.Combine(Environment.SystemDirectory, "resmon.exe"));

    /// <summary>Open Task Manager (Performance) — the figures here mirror its "in use / available" memory readout.</summary>
    [RelayCommand]
    private void OpenTaskManager() =>
        ShellLauncher.OpenLocal(Path.Combine(Environment.SystemDirectory, "Taskmgr.exe"));

    // Four honest endings: the call failed, it ran but the delta isn't measurable, it ran and nothing moved, or a
    // real measured delta — and for a standby purge we spell out that « disponible » memory does not change.
    private static string DescribeOutcome(MemoryFlushOutcome o)
    {
        string label = MemoryFlushCatalog.Label(o.Kind);

        if (!o.Invoked)
            return $"{label} : l'appel système a échoué (privilège refusé ?).";

        if (o.Kind == MemoryFlushKind.StandbyList && !o.Before.DetailAvailable)
            return $"{label} : effectué — variation non mesurable sur ce système.";

        if (o.DidSomething)
            return o.Kind == MemoryFlushKind.StandbyList
                ? $"{label} : {o.HeadlineDisplay} retirés du cache standby — déplacés vers la mémoire libre. La mémoire disponible, elle, ne change pas."
                : $"{label} : {o.HeadlineDisplay} déplacés vers la mémoire disponible.";

        return $"{label} : effectué — rien à libérer (le cache était déjà au plus bas).";
    }
}
