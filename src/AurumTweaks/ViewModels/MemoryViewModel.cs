using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Mémoire vive » page — a live composition view, an honest ISLC/RAMMap-style manual flush, and an opt-in
/// « auto-nettoyage » (the Mem Reduct-style feature: flush automatically on an interval and/or at a memory-pressure
/// ceiling). The composition is read from the kernel page lists (exact, language-immune); every flush is a real
/// NtSetSystemInformation call whose effect is re-measured, so the figure shown is the standby that genuinely
/// disappeared. No dead button, no invented metric: auto-clean is OFF by default, states plainly that purging the
/// standby cache does not raise « disponible » memory nor boost FPS, and fires the SAME measured kernel command the
/// manual buttons do — only on a schedule the user chose.
/// </summary>
public partial class MemoryViewModel : ObservableObject
{
    private readonly IMemoryManagementService _service;
    private readonly IMemoryAutoCleanStore _autoCleanStore;

    // Real ticking only under a running WPF app; in a headless/test context the timer is never created and the
    // decision (EvaluateAutoCleanAsync + the pure MemoryAutoClean core) is invokable directly — so the auto-clean
    // rule is unit-testable without a UI message pump. Sample cadence is well under any useful interval/cooldown.
    private const int AutoCleanSampleSeconds = 15;
    private DispatcherTimer? _autoCleanTimer;
    private DateTime? _lastAutoCleanUtc;
    private bool _autoCleanRunning;   // reentrancy guard: a flush can outlive one tick
    private bool _suppressPersist;    // true while loading persisted values into the properties in the ctor

    /// <summary>The curated flush actions, bound directly from the pure catalog.</summary>
    public IReadOnlyList<MemoryFlushAction> Flushes => MemoryFlushCatalog.Actions;

    /// <summary>Auto-clean trigger choices for the combo. « Off » is intentionally excluded — the enable toggle owns on/off.</summary>
    public IReadOnlyList<MemoryAutoCleanTriggerOption> AutoCleanTriggerOptions { get; } = new[]
    {
        new MemoryAutoCleanTriggerOption(MemoryAutoCleanTrigger.Threshold, "Au seuil de mémoire utilisée"),
        new MemoryAutoCleanTriggerOption(MemoryAutoCleanTrigger.Interval, "À intervalle régulier"),
        new MemoryAutoCleanTriggerOption(MemoryAutoCleanTrigger.Both, "Seuil ou intervalle"),
    };

    /// <summary>Which flush the auto-clean runs — the same two real kernel commands the manual page offers.</summary>
    public IReadOnlyList<MemoryFlushKindOption> AutoCleanKindOptions { get; } = new[]
    {
        new MemoryFlushKindOption(MemoryFlushKind.StandbyList, MemoryFlushCatalog.Label(MemoryFlushKind.StandbyList)),
        new MemoryFlushKindOption(MemoryFlushKind.WorkingSets, MemoryFlushCatalog.Label(MemoryFlushKind.WorkingSets)),
    };

    public IReadOnlyList<int> AutoCleanIntervalOptions { get; } = new[] { 5, 10, 15, 30, 60, 120 };

    [ObservableProperty] private MemoryComposition _composition = MemoryComposition.Empty;
    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlushCommand))]
    private bool _isBusy;

    // Auto-clean settings, two-way bound. Changing any of them persists + re-arms the timer (guarded by _suppressPersist).
    [ObservableProperty] private bool _autoCleanEnabled;
    [ObservableProperty] private MemoryAutoCleanTrigger _autoCleanTrigger = MemoryAutoCleanTrigger.Threshold;
    [ObservableProperty] private int _thresholdPercent = 85;
    [ObservableProperty] private int _intervalMinutes = 30;
    [ObservableProperty] private MemoryFlushKind _autoCleanKind = MemoryFlushKind.StandbyList;

    /// <summary>One honest line describing the active policy (or « désactivé »).</summary>
    [ObservableProperty] private string _autoCleanPolicy = "Auto-nettoyage désactivé.";

    /// <summary>The measured result of the last automatic clean, or null until one runs.</summary>
    [ObservableProperty] private string? _autoCleanStatus;

    private bool NotBusy => !IsBusy;

    public MemoryViewModel(IMemoryManagementService service, IMemoryAutoCleanStore autoCleanStore)
    {
        _service = service;
        _autoCleanStore = autoCleanStore;
        LoadAutoCleanSettings();
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

    // ── Auto-clean ───────────────────────────────────────────────────────────────────────────────────────────────

    private void LoadAutoCleanSettings()
    {
        _suppressPersist = true;
        var s = _autoCleanStore.Load();
        AutoCleanEnabled = s.Enabled;
        AutoCleanTrigger = s.Trigger == MemoryAutoCleanTrigger.Off ? MemoryAutoCleanTrigger.Threshold : s.Trigger;
        ThresholdPercent = s.ThresholdPercent;
        IntervalMinutes = s.IntervalMinutes;
        AutoCleanKind = s.Kind;
        _suppressPersist = false;

        AutoCleanPolicy = MemoryAutoClean.Describe(s);
        if (s.Enabled)
        {
            _lastAutoCleanUtc = DateTime.UtcNow;   // don't fire an interval clean the instant the page opens
            StartAutoCleanTimer();
        }
    }

    private MemoryAutoCleanSettings BuildSettings() =>
        new(AutoCleanEnabled, AutoCleanTrigger, ThresholdPercent, IntervalMinutes, AutoCleanKind);

    // The generated OnXxxChanged hooks — any settings edit persists and re-arms the timer.
    partial void OnAutoCleanEnabledChanged(bool value)
    {
        if (value) _lastAutoCleanUtc = DateTime.UtcNow;   // start the interval clock fresh on enable
        PersistAndRearm();
    }

    partial void OnAutoCleanTriggerChanged(MemoryAutoCleanTrigger value) => PersistAndRearm();
    partial void OnThresholdPercentChanged(int value) => PersistAndRearm();
    partial void OnIntervalMinutesChanged(int value) => PersistAndRearm();
    partial void OnAutoCleanKindChanged(MemoryFlushKind value) => PersistAndRearm();

    private void PersistAndRearm()
    {
        if (_suppressPersist) return;
        var s = BuildSettings();
        _autoCleanStore.Save(s);
        AutoCleanPolicy = MemoryAutoClean.Describe(s);
        if (s.Enabled) StartAutoCleanTimer();
        else
        {
            StopAutoCleanTimer();
            AutoCleanStatus = null;
        }
    }

    private void StartAutoCleanTimer()
    {
        if (Application.Current is null) return;   // headless/tests: EvaluateAutoCleanAsync is called directly instead
        _autoCleanTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutoCleanSampleSeconds) };
        _autoCleanTimer.Tick -= OnAutoCleanTick;
        _autoCleanTimer.Tick += OnAutoCleanTick;
        _autoCleanTimer.Start();
    }

    private void StopAutoCleanTimer() => _autoCleanTimer?.Stop();

    private async void OnAutoCleanTick(object? sender, EventArgs e) => await EvaluateAutoCleanAsync();

    /// <summary>
    /// One auto-clean sample: refresh the live composition, and — if the pure rule says so — fire the chosen flush and
    /// report the MEASURED result. Internal so a test can drive it deterministically (seed <see cref="LastAutoCleanUtc"/>,
    /// point the fake service at a high-pressure composition) without a UI timer. Skips while a manual op is busy, and is
    /// reentrancy-guarded so a flush that outlives a tick can't overlap the next.
    /// </summary>
    internal async Task EvaluateAutoCleanAsync()
    {
        if (_autoCleanRunning || IsBusy) return;
        var settings = BuildSettings();
        if (!settings.Enabled || settings.Trigger == MemoryAutoCleanTrigger.Off) return;

        _autoCleanRunning = true;
        try
        {
            var comp = await _service.GetCompositionAsync();
            Composition = comp;   // keep the live view fresh while auto-clean watches

            double minutesSinceLast = _lastAutoCleanUtc is { } last
                ? (DateTime.UtcNow - last).TotalMinutes
                : double.MaxValue;

            if (!MemoryAutoClean.ShouldClean(settings, comp, minutesSinceLast))
            {
                Status = MemoryAdvice.Summarize(comp);
                return;
            }

            var outcome = await _service.FlushAsync(settings.Kind);
            _lastAutoCleanUtc = DateTime.UtcNow;
            Composition = await _service.GetCompositionAsync();
            AutoCleanStatus = "Auto-nettoyage — " + DescribeOutcome(outcome);
            Status = MemoryAdvice.Summarize(Composition);
        }
        finally
        {
            _autoCleanRunning = false;
        }
    }

    /// <summary>Test seam (InternalsVisibleTo): the "last clean" clock the interval/cooldown gate reads.</summary>
    internal DateTime? LastAutoCleanUtc
    {
        get => _lastAutoCleanUtc;
        set => _lastAutoCleanUtc = value;
    }

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

/// <summary>Trigger choice + its French label, for the auto-clean combo.</summary>
public sealed record MemoryAutoCleanTriggerOption(MemoryAutoCleanTrigger Value, string Label);

/// <summary>Flush-kind choice + its French label, for the auto-clean combo.</summary>
public sealed record MemoryFlushKindOption(MemoryFlushKind Value, string Label);
