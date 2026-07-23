using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.Services.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

public partial class OverclockingViewModel : ObservableObject
{
    private readonly IHardwareService _hardware;
    private readonly IGpuOcService _ocService;
    private readonly ILicenseService _license;
    private readonly IApplyJournal _journal;
    private readonly IMonitoringService _monitoring;
    private readonly IGpuStressLoad _stress;
    private readonly IGpuTdrProbe _tdrProbe;
    private readonly IGpuFanService _fanService;

    // Integrated GPU stability test: samples collected from the live monitoring stream while the D3D11
    // compute load runs, plus the run timer.
    private readonly List<GpuStabilitySample> _stabilitySamples = new();
    private DispatcherTimer? _stabilityTimer;
    private int _stabilitySecondsLeft;
    /// <summary>Default stability run length (s) — long enough for the 1 Hz monitoring stream to yield a
    /// verdict-worthy sample count, short enough to stay responsive.</summary>
    private const int StabilityRunSeconds = 20;

    // Auto-revert safety net: the captured pre-apply profile + a 1 s countdown timer. _revertTarget null
    // means "no reliable read of the previous state" → the safe revert is a Reset (stock), not a re-apply.
    private OcAutoRevertCountdown _countdown;
    private DispatcherTimer? _revertTimer;
    private GpuOcProfile? _revertTarget;

    [ObservableProperty] private HardwareInfo? _hardwareInfo;
    [ObservableProperty] private GpuOcBackendStatus? _backendStatus;
    [ObservableProperty] private int _gpuCoreOffsetMhz;
    [ObservableProperty] private int _gpuMemOffsetMhz;
    [ObservableProperty] private int _gpuPowerLimitPct = 100;
    [ObservableProperty] private int _gpuTempLimitC = 83;
    [ObservableProperty] private int _gpuTargetVoltageMv = 900;
    [ObservableProperty] private int _gpuAmdMaxFreqMhz;       // AMD GPU max frequency (ADLX); prefilled from the driver read when native
    [ObservableProperty] private int _gpuAmdMaxVramFreqMhz;   // AMD memory max frequency (ADLX); same contract
    [ObservableProperty] private string _autoOcStatus = "Prêt";
    [ObservableProperty] private string _stressTestProgress = string.Empty;

    // Auto-revert banner: active while the post-apply "Conserver ?" countdown runs (Phase 2b safety net).
    [ObservableProperty] private bool _autoRevertActive;
    [ObservableProperty] private string _autoRevertLabel = string.Empty;

    // GPU stability test: true while the integrated D3D11 load is running.
    [ObservableProperty] private bool _stabilityTestRunning;

    // Fan control (Phase 3): the whole card is shown only when a native fan backend is available.
    [ObservableProperty] private bool _fanAvailable;
    [ObservableProperty] private string _fanStatusMessage = string.Empty;
    [ObservableProperty] private int _gpuFanPercent = GpuFanSafety.HardFloorPercent;   // slider value (never below the floor)
    [ObservableProperty] private int _fanRpm;

    // Live observed GPU state from the monitoring stream (Phase 2c) — 0 = sensor not read.
    [ObservableProperty] private float _liveGpuClockMhz;
    [ObservableProperty] private float _liveGpuTempC;
    [ObservableProperty] private float _liveGpuUsagePercent;

    /// <summary>The EFFECT of an OC as measured by the sensor stream, not merely the setting that was
    /// written — a genuinely stronger corroboration than "confirmé par relecture du réglage". 0 = the
    /// sensor was not read → "—", never a fabricated number.</summary>
    public string LiveEffectLabel
    {
        get
        {
            string clock = LiveGpuClockMhz > 0 ? $"{LiveGpuClockMhz:0} MHz" : "—";
            string temp = LiveGpuTempC > 0 ? $"{LiveGpuTempC:0} °C" : "—";
            string usage = LiveGpuUsagePercent > 0 ? $"{LiveGpuUsagePercent:0} %" : "—";
            return $"Effet observé (live) — fréquence GPU {clock} · température {temp} · charge {usage}";
        }
    }

    // Non-empty ONLY when GPU OC is genuinely locked (a configured build on the Free edition). Bound as an up-front
    // banner so the page never lets the user dial in an OC and only discover at apply-time that it's Premium — the
    // freemium equivalent of "no dead button". Empty (the as-shipped, not-configured case) hides the banner entirely.
    [ObservableProperty] private string _gpuOcLockNote = string.Empty;

    /// <summary>True only after a power-limit backend (NVAPI or ADLX) passed its on-card read verification.</summary>
    private bool PowerLimitNative => BackendStatus?.PowerLimitNative == true;

    /// <summary>Core/mem offsets apply natively only through the NVIDIA NVAPI backend.</summary>
    private bool OffsetsNative => BackendStatus is { Vendor: GpuVendor.Nvidia, BackendAvailable: true };

    /// <summary>Power slider bounds: the verified backend window when native (NVIDIA card window, or the
    /// AMD driver's own Adrenalin-scale window); the generic reference range otherwise.</summary>
    public int PowerSliderMinPct => PowerLimitNative ? BackendStatus!.PowerLimitMinPct : 50;
    public int PowerSliderMaxPct => PowerLimitNative ? BackendStatus!.PowerLimitMaxPct : 133;

    /// <summary>True only after the temperature-target backend passed its on-card read verification.
    /// Public: the view collapses the whole temp slider row on it — the control only exists when it
    /// genuinely applies (no dead control, and no hidden axis silently written either).</summary>
    public bool TempLimitNative => BackendStatus?.TempLimitNative == true;

    /// <summary>Temp slider bounds: the card's own verified window when native; the generic validation
    /// range otherwise (the row is hidden then, these are inert fallbacks).</summary>
    public int TempSliderMinC => TempLimitNative ? BackendStatus!.TempLimitMinC : GpuOcValidation.TempMin;
    public int TempSliderMaxC => TempLimitNative ? BackendStatus!.TempLimitMaxC : GpuOcValidation.TempMax;

    /// <summary>True only after the AMD ADLX GPU max-frequency backend passed its on-card verification.
    /// Public: the view collapses the whole AMD frequency row on it (no dead control, no hidden write).</summary>
    public bool GfxTuningNative => BackendStatus?.GfxTuningNative == true;

    /// <summary>AMD max-frequency slider bounds: the driver's own verified window (MHz).</summary>
    public int GfxSliderMinMhz => GfxTuningNative ? BackendStatus!.GfxMaxFreqMinMhz : 0;
    public int GfxSliderMaxMhz => GfxTuningNative ? BackendStatus!.GfxMaxFreqMaxMhz : 3000;

    /// <summary>True only after the AMD ADLX memory max-frequency backend passed its on-card verification.</summary>
    public bool VramTuningNative => BackendStatus?.VramTuningNative == true;

    /// <summary>AMD memory max-frequency slider bounds: the driver's own verified window (MHz).</summary>
    public int VramSliderMinMhz => VramTuningNative ? BackendStatus!.VramMaxFreqMinMhz : 0;
    public int VramSliderMaxMhz => VramTuningNative ? BackendStatus!.VramMaxFreqMaxMhz : 3000;

    /// <summary>Always-visible disclosure under the sliders: names exactly which axes Aurum applies on THIS
    /// machine (offsets via NVAPI; power via NVAPI or ADLX; temp when verified; AMD GPU/memory max
    /// frequency via ADLX) and which it never does (voltage). Bound by the view so the non-applied
    /// sliders read as references, not dead controls.</summary>
    public string GpuSlidersNote => GpuOcDisclosure.SlidersNote(
        OffsetsNative, BackendStatus?.PowerBackend ?? GpuPowerBackendKind.None, TempLimitNative,
        GfxTuningNative, VramTuningNative);

    /// <summary>Power-limit row label: drops the "(via Afterburner)" suffix once the axis is verified-native,
    /// so the label never claims less than the app actually does (nor more).</summary>
    public string PowerLimitRowLabel => PowerLimitNative ? "Power limit" : "Power limit (via Afterburner)";

    // These all derive from BackendStatus, so a status arriving after the async load must re-announce
    // them — otherwise the view would keep the pre-detection wording/visibility forever.
    partial void OnBackendStatusChanged(GpuOcBackendStatus? value)
    {
        OnPropertyChanged(nameof(GpuSlidersNote));
        OnPropertyChanged(nameof(PowerLimitRowLabel));
        OnPropertyChanged(nameof(PowerSliderMinPct));
        OnPropertyChanged(nameof(PowerSliderMaxPct));
        OnPropertyChanged(nameof(TempLimitNative));
        OnPropertyChanged(nameof(TempSliderMinC));
        OnPropertyChanged(nameof(TempSliderMaxC));
        OnPropertyChanged(nameof(GfxTuningNative));
        OnPropertyChanged(nameof(GfxSliderMinMhz));
        OnPropertyChanged(nameof(GfxSliderMaxMhz));
        OnPropertyChanged(nameof(VramTuningNative));
        OnPropertyChanged(nameof(VramSliderMinMhz));
        OnPropertyChanged(nameof(VramSliderMaxMhz));
    }

    public OverclockingViewModel(IHardwareService hardware, IGpuOcService ocService, ILicenseService license,
                                 IApplyJournal journal, IMonitoringService monitoring,
                                 IGpuStressLoad stress, IGpuTdrProbe tdrProbe, IGpuFanService fanService)
    {
        _hardware = hardware;
        _ocService = ocService;
        _license = license;
        _journal = journal;
        _monitoring = monitoring;
        _stress = stress;
        _tdrProbe = tdrProbe;
        _fanService = fanService;
        // Re-evaluate the lock the moment a licence is activated/removed elsewhere, so the banner appears or clears
        // without a relaunch (both this VM and the service are singletons → same lifetime, no leak).
        _license.EditionChanged += (_, _) => RefreshLockState();
        RefreshLockState();
        // Live-effect readout: reflect the observed GPU clock/temp/load (monitoring is started once at boot).
        _monitoring.SnapshotReady += OnSnapshot;
        _ = LoadAsync();
    }

    private void OnSnapshot(object? sender, MonitoringSnapshot s)
    {
        // Snapshots fire on the monitoring timer thread → marshal to the UI dispatcher when there is one
        // (the same pattern MonitoringViewModel uses); in a headless/test context, update directly.
        var app = Application.Current;
        if (app is not null) app.Dispatcher.Invoke(() => ApplySnapshot(s));
        else ApplySnapshot(s);
    }

    private void ApplySnapshot(MonitoringSnapshot s)
    {
        LiveGpuClockMhz = s.GpuClockMhz;
        LiveGpuTempC = s.GpuTempC;
        LiveGpuUsagePercent = s.GpuUsagePercent;
        OnPropertyChanged(nameof(LiveEffectLabel));

        // While a stability run is in progress, record the observed telemetry for the verdict core.
        if (StabilityTestRunning)
            _stabilitySamples.Add(new GpuStabilitySample(s.GpuClockMhz, s.GpuTempC, s.GpuUsagePercent));
    }

    // The single gate verdict for this page, read fresh so a mid-session upgrade is honored. As-shipped (no embedded
    // key) licensing is "not configured" ⇒ always unlocked, so the gate below is a no-op until a seller embeds a key.
    private bool GpuOcUnlocked =>
        LicenseGate.IsFeatureUnlocked(_license.IsConfigured, _license.CurrentEdition, PremiumFeature.GpuOverclocking);

    private void RefreshLockState() =>
        GpuOcLockNote = GpuOcUnlocked
            ? string.Empty
            : PremiumGateText.FeatureLocked(PremiumFeature.GpuOverclocking, "appliquer un profil");

    private async Task LoadAsync()
    {
        HardwareInfo = await _hardware.DetectAsync();
        BackendStatus = await _ocService.GetStatusAsync();

        // Suggest sensible defaults based on detected GPU.
        if (HardwareInfo?.GpuPrimary.Contains("4090", System.StringComparison.OrdinalIgnoreCase) == true)
        {
            GpuCoreOffsetMhz = 150; GpuMemOffsetMhz = 1200; GpuTargetVoltageMv = 925;
        }
        else if (HardwareInfo?.GpuPrimary.Contains("5090", System.StringComparison.OrdinalIgnoreCase) == true)
        {
            GpuCoreOffsetMhz = 200; GpuMemOffsetMhz = 2000; GpuTargetVoltageMv = 925;
        }
        else if (HardwareInfo?.GpuPrimary.Contains("5080", System.StringComparison.OrdinalIgnoreCase) == true)
        {
            GpuCoreOffsetMhz = 200; GpuMemOffsetMhz = 1800; GpuTargetVoltageMv = 900;
        }
        else if (HardwareInfo?.GpuPrimary.Contains("4080", System.StringComparison.OrdinalIgnoreCase) == true)
        {
            GpuCoreOffsetMhz = 150; GpuMemOffsetMhz = 1200; GpuTargetVoltageMv = 925;
        }
        else if (HardwareInfo?.GpuPrimary.Contains("3080", System.StringComparison.OrdinalIgnoreCase) == true
              || HardwareInfo?.GpuPrimary.Contains("3070", System.StringComparison.OrdinalIgnoreCase) == true)
        {
            GpuCoreOffsetMhz = 100; GpuMemOffsetMhz = 1200; GpuTargetVoltageMv = 875;
        }

        await SyncSlidersToCardAsync();
        await LoadFanStatusAsync();
    }

    private async Task LoadFanStatusAsync()
    {
        var fan = await _fanService.GetStatusAsync();
        FanAvailable = fan.Available;
        FanStatusMessage = fan.Message;
        FanRpm = fan.Rpm;
        // Prefill the slider from the real current fan %, but never below the safety floor.
        if (fan.Available) GpuFanPercent = GpuFanSafety.ClampManualPercent(fan.CurrentPercent);
    }

    /// <summary>Re-read the card's current state and reflect it on the sliders (shared by first load and
    /// the auto-revert). Shows what's actually on the GPU, never a guess or a wrong-scale placeholder.</summary>
    private async Task SyncSlidersToCardAsync()
    {
        // If the card already has offsets applied (e.g. from a previous session), reflect the real
        // values over any suggestion — show what's actually on the GPU, not a guess.
        var current = await _ocService.ReadCurrentAsync();
        if (current is not null && (current.CoreOffsetMhz != 0 || current.MemoryOffsetMhz != 0))
        {
            GpuCoreOffsetMhz = current.CoreOffsetMhz;
            GpuMemOffsetMhz = current.MemoryOffsetMhz;
        }
        // Power/temp prefill only from a verified-native read — otherwise the profile's 100/0 are
        // placeholders, not measurements, and showing them as "current" would be a fabricated metric.
        if (current is not null && PowerLimitNative)
            GpuPowerLimitPct = current.PowerLimitPct;
        else if (current is null && PowerLimitNative)
            // The read failed but the backend is verified: fall back to the value captured at
            // verification (a genuine measurement, on the right scale), NEVER the initial 100 — which
            // on AMD is a wrong-scale placeholder that a blind Apply would clamp to MAX power.
            GpuPowerLimitPct = BackendStatus!.PowerLimitDefaultPct;

        // Temp is the one axis with a >0 sentinel (0 °C is never a real GPU target, unlike 0 % power on
        // AMD or a 0 offset), so the branches must be EXHAUSTIVE: a verified axis whose live read is
        // missing OR zero falls back to the captured default — never the hard-coded 83 init, which would
        // be a placeholder shown as a measurement.
        if (TempLimitNative)
            GpuTempLimitC = current is { TempLimitC: > 0 } ? current.TempLimitC : BackendStatus!.TempLimitDefaultC;

        // AMD GPU max frequency: prefill from the verified read (real measurement, driver scale), or from
        // the captured startup default on read failure — never a stray 0 that a blind apply could clamp
        // to the window's minimum (a downclock the user never asked for).
        if (current is not null && GfxTuningNative)
            GpuAmdMaxFreqMhz = current.AmdMaxFreqMhz;
        else if (current is null && GfxTuningNative)
            GpuAmdMaxFreqMhz = BackendStatus!.GfxMaxFreqDefaultMhz;

        if (current is not null && VramTuningNative)
            GpuAmdMaxVramFreqMhz = current.AmdMaxVramFreqMhz;
        else if (current is null && VramTuningNative)
            GpuAmdMaxVramFreqMhz = BackendStatus!.VramMaxFreqDefaultMhz;
    }

    [RelayCommand]
    private async Task ApplyGpuOcAsync()
    {
        // Freemium gate: GPU overclocking is a Premium feature (and is advertised as one on the Licence page), so the
        // apply path must genuinely refuse on a configured Free build rather than silently writing the offsets.
        if (!GpuOcUnlocked)
        {
            AutoOcStatus = PremiumGateText.FeatureLocked(PremiumFeature.GpuOverclocking, "l'appliquer");
            return;
        }
        // Capture the on-card state BEFORE writing, so a risky apply can auto-revert to exactly it.
        var before = await _ocService.ReadCurrentAsync();

        var profile = new GpuOcProfile(GpuCoreOffsetMhz, GpuMemOffsetMhz, GpuPowerLimitPct, GpuTempLimitC, GpuTargetVoltageMv)
        {
            AmdMaxFreqMhz = GpuAmdMaxFreqMhz,
            AmdMaxVramFreqMhz = GpuAmdMaxVramFreqMhz,
        };
        var result = await _ocService.ApplyAsync(profile);

        // Journal every attempt (success OR failure): an overclock is a real system change and must be
        // visible in "what did Aurum change" — the Journal page, insights, and export.
        await _journal.RecordAsync(GpuOcJournal.ForApply(result, DateTime.UtcNow));

        if (!result.Success)
        {
            AutoOcStatus = $"Erreur : {result.Error}";
            return;
        }

        // Lead with exactly what the backend wrote, then disclose any axis the user set but that Aurum doesn't
        // apply on this card — so a successful apply never implies a slider took effect when it didn't.
        var ignored = GpuOcDisclosure.IgnoredAxesNote(profile, PowerLimitNative);
        AutoOcStatus = ignored.Length == 0
            ? $"Appliqué : {result.Applied}"
            : $"Appliqué : {result.Applied} — {ignored}";

        // Safety net: only when a frequency axis changed (the kind that can black-screen/hang the GPU),
        // arm the "Conserver ?" countdown that auto-reverts to the captured previous state.
        var baseline = before ?? StockProfile;
        if (GpuOcAutoRevert.WarrantsCountdown(baseline, profile))
            ArmAutoRevert(before);
        else
            DisarmAutoRevert();
    }

    /// <summary>Neutral "stock" profile used as the baseline when the pre-apply read is unavailable.</summary>
    private static GpuOcProfile StockProfile => new(0, 0, 100, 83, 0);

    private void ArmAutoRevert(GpuOcProfile? revertTarget)
    {
        _revertTarget = revertTarget;              // null → the revert will be a Reset (stock)
        _countdown = OcAutoRevertCountdown.Start();
        AutoRevertLabel = _countdown.Label;
        AutoRevertActive = true;
        StartRevertTimer();
    }

    private void DisarmAutoRevert()
    {
        StopRevertTimer();
        AutoRevertActive = false;
        _revertTarget = null;
    }

    private void StartRevertTimer()
    {
        // Real ticking only under a running WPF app; in a headless/test context the banner is still armed
        // and the timeout action (TriggerAutoRevertAsync) is invokable directly — so the safety-net
        // DECISION is unit-testable without a UI message pump.
        if (Application.Current is null) return;
        _revertTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _revertTimer.Tick -= OnRevertTick;
        _revertTimer.Tick += OnRevertTick;
        _revertTimer.Start();
    }

    private void StopRevertTimer() => _revertTimer?.Stop();

    private async void OnRevertTick(object? sender, EventArgs e)
    {
        _countdown = _countdown.Tick();
        AutoRevertLabel = _countdown.Label;
        if (_countdown.Expired)
        {
            StopRevertTimer();
            await TriggerAutoRevertAsync();
        }
    }

    /// <summary>Perform the auto-revert: re-apply the captured previous profile, or Reset to stock when the
    /// pre-apply read was unavailable. Internal so a test can drive the timeout outcome without a UI timer.</summary>
    internal async Task TriggerAutoRevertAsync()
    {
        // The captured previous read can carry a placeholder temp of 0 °C (thermal not native), which the
        // service's backend-agnostic validation would reject — sanitize it into the valid window so the
        // safety net can never fail to fire on its own placeholder. Temp isn't applied when non-native, so
        // this is harmless; when native, the real captured value is already in range.
        var target = _revertTarget is null
            ? null
            : _revertTarget with { TempLimitC = Math.Clamp(_revertTarget.TempLimitC, GpuOcValidation.TempMin, GpuOcValidation.TempMax) };
        AutoRevertActive = false;
        var r = target is not null ? await _ocService.ApplyAsync(target) : await _ocService.ResetAsync();
        await _journal.RecordAsync(GpuOcJournal.ForReset(r, DateTime.UtcNow));
        AutoOcStatus = r.Success
            ? "Retour automatique effectué — réglages précédents restaurés."
            : $"Retour automatique — erreur : {r.Error}";
        await SyncSlidersToCardAsync();
        _revertTarget = null;
    }

    /// <summary>« Conserver » — the user confirms the new OC is stable, cancelling the auto-revert.</summary>
    [RelayCommand]
    private void ConfirmKeepOc()
    {
        DisarmAutoRevert();
        AutoOcStatus = "Réglages conservés.";
    }

    /// <summary>Apply the manual fan % (safety-floored + read-back confirmed by the service).</summary>
    [RelayCommand]
    private async Task SetFanManualAsync()
    {
        var r = await _fanService.SetManualAsync(GpuFanPercent);
        FanStatusMessage = r.Success ? $"Appliqué : {r.Applied}" : $"Erreur ventilateur : {r.Error}";
        await RefreshFanReadingAsync();
    }

    /// <summary>Hand the fan back to the driver's automatic curve.</summary>
    [RelayCommand]
    private async Task SetFanAutoAsync()
    {
        var r = await _fanService.SetAutoAsync();
        FanStatusMessage = r.Success ? $"Ventilateur : {r.Applied}" : $"Erreur ventilateur : {r.Error}";
        await RefreshFanReadingAsync();
    }

    private async Task RefreshFanReadingAsync()
    {
        var fan = await _fanService.GetStatusAsync();
        FanRpm = fan.Rpm;
        if (fan.Available) GpuFanPercent = GpuFanSafety.ClampManualPercent(fan.CurrentPercent);
    }

    /// <summary>
    /// Suggest a conservative starting core offset. Honest by name and behaviour: this is NOT an
    /// auto-tuner — no integrated artefact/crash detector runs, so it cannot "detect" stability. It
    /// simply proposes a prudent palier the user then validates with a real benchmark.
    /// </summary>
    [RelayCommand]
    private void RunAutoOc()
    {
        int suggestedCore = System.Math.Min(GpuCoreOffsetMhz > 0 ? GpuCoreOffsetMhz : 150, 200);
        GpuCoreOffsetMhz = suggestedCore;
        AutoOcStatus = $"Palier suggéré : core +{suggestedCore} MHz — à appliquer puis valider avec un test de "
                     + "stabilité (Heaven/OCCT). Aucune stabilité n'est garantie automatiquement.";
    }

    /// <summary>
    /// Integrated GPU stability test: runs a REAL D3D11 compute load, samples the live monitoring stream,
    /// then classifies the run (stable / throttling / hung / driver-reset) — the driver-reset (TDR) being
    /// the definitive instability signal, read from the Windows event log. Honest: if no D3D11 GPU is
    /// accessible it does NOT fake a run (and never uses the CPU/WARP rasterizer), it falls back to the
    /// referral toward FurMark / OCCT.
    /// </summary>
    [RelayCommand]
    private void RunStabilityTest()
    {
        if (StabilityTestRunning) return;
        if (!_stress.Start(out var error))
        {
            StressTestProgress = $"Test de charge GPU intégré indisponible ({error}). Valide l'OC avec FurMark / "
                               + "OCCT / Heaven en parallèle du monitoring ; côté RAM/CPU, onglets « Stabilité ».";
            return;
        }

        _stabilitySamples.Clear();
        StabilityTestRunning = true;
        _stabilitySecondsLeft = StabilityRunSeconds;
        StressTestProgress = $"Charge GPU réelle en cours… {_stabilitySecondsLeft} s (surveille température et throttling).";

        // Real ticking only under a running WPF app; a headless/test context drives the finish explicitly
        // via FinishStabilityRunAsync after pushing samples — so the verdict path is testable without a wait.
        if (Application.Current is null) return;
        _stabilityTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _stabilityTimer.Tick -= OnStabilityTick;
        _stabilityTimer.Tick += OnStabilityTick;
        _stabilityTimer.Start();
    }

    private async void OnStabilityTick(object? sender, EventArgs e)
    {
        _stabilitySecondsLeft--;
        if (_stabilitySecondsLeft > 0)
        {
            StressTestProgress = $"Charge GPU réelle en cours… {_stabilitySecondsLeft} s (surveille température et throttling).";
            return;
        }
        _stabilityTimer?.Stop();
        await FinishStabilityRunAsync();
    }

    /// <summary>Stop the load, probe the run window for a driver-reset (TDR), classify the telemetry, and
    /// report the honest verdict. Internal so a test can drive the outcome without a UI timer or a 20 s wait.</summary>
    internal Task FinishStabilityRunAsync()
    {
        _stress.Stop();
        StabilityTestRunning = false;

        var tdr = _tdrProbe.Probe(StabilityRunSeconds / 60 + 2);
        var verdict = GpuStabilityVerdict.Classify(_stabilitySamples, tdr.TdrObserved, tdr.ProbeFailed);
        StressTestProgress = "Stabilité GPU — " + GpuStabilityVerdict.Describe(verdict, _stabilitySamples);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ResetOcAsync()
    {
        // Run the native reset FIRST. On failure the card still holds the previous OC, so the sliders
        // must keep showing it — mutating them up-front would make the page claim a reset that didn't
        // happen and lose the values the user needs to see what's still applied.
        var r = await _ocService.ResetAsync();

        // A manual reset supersedes any pending auto-revert, and is itself a journaled system change.
        DisarmAutoRevert();
        await _journal.RecordAsync(GpuOcJournal.ForReset(r, DateTime.UtcNow));

        if (!r.Success)
        {
            AutoOcStatus = $"Erreur : {r.Error}";
            return;
        }

        // Only now reflect the reset on the sliders, matching what the service actually restored:
        // card default power / startup AMD value, card default temp — else the generic UI defaults.
        GpuCoreOffsetMhz = 0; GpuMemOffsetMhz = 0; GpuTargetVoltageMv = 900;
        GpuPowerLimitPct = PowerLimitNative ? BackendStatus!.PowerLimitDefaultPct : 100;
        GpuTempLimitC = TempLimitNative ? BackendStatus!.TempLimitDefaultC : 83;
        if (GfxTuningNative) GpuAmdMaxFreqMhz = BackendStatus!.GfxMaxFreqDefaultMhz;
        if (VramTuningNative) GpuAmdMaxVramFreqMhz = BackendStatus!.VramMaxFreqDefaultMhz;

        // Surface the service's per-axis summary (what was actually restored on which axes) rather than
        // a fixed "offsets remis à zéro" that would be wrong on AMD (no offsets) or hide power/temp reverts.
        AutoOcStatus = string.IsNullOrEmpty(r.Applied)
            ? "Réinitialisation GPU effectuée."
            : $"Réinitialisé : {r.Applied}";
    }
}
