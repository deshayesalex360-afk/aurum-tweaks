using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

public partial class OverclockingViewModel : ObservableObject
{
    private readonly IHardwareService _hardware;
    private readonly IGpuOcService _ocService;
    private readonly ILicenseService _license;

    [ObservableProperty] private HardwareInfo? _hardwareInfo;
    [ObservableProperty] private GpuOcBackendStatus? _backendStatus;
    [ObservableProperty] private int _gpuCoreOffsetMhz;
    [ObservableProperty] private int _gpuMemOffsetMhz;
    [ObservableProperty] private int _gpuPowerLimitPct = 100;
    [ObservableProperty] private int _gpuTempLimitC = 83;
    [ObservableProperty] private int _gpuTargetVoltageMv = 900;
    [ObservableProperty] private string _autoOcStatus = "Prêt";
    [ObservableProperty] private string _stressTestProgress = string.Empty;

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

    /// <summary>Always-visible disclosure under the sliders: names exactly which axes Aurum applies on THIS
    /// machine (offsets via NVAPI; power via NVAPI or ADLX; temp when verified) and which it never does
    /// (voltage). Bound by the view so the non-applied sliders read as references, not dead controls.</summary>
    public string GpuSlidersNote => GpuOcDisclosure.SlidersNote(
        OffsetsNative, BackendStatus?.PowerBackend ?? GpuPowerBackendKind.None, TempLimitNative);

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
    }

    public OverclockingViewModel(IHardwareService hardware, IGpuOcService ocService, ILicenseService license)
    {
        _hardware = hardware;
        _ocService = ocService;
        _license = license;
        // Re-evaluate the lock the moment a licence is activated/removed elsewhere, so the banner appears or clears
        // without a relaunch (both this VM and the service are singletons → same lifetime, no leak).
        _license.EditionChanged += (_, _) => RefreshLockState();
        RefreshLockState();
        _ = LoadAsync();
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

        // If the card already has offsets applied (e.g. from a previous session), reflect the real
        // values over the suggestion above — show what's actually on the GPU, not a guess.
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

        if (current is not null && TempLimitNative && current.TempLimitC > 0)
            GpuTempLimitC = current.TempLimitC;
        else if (current is null && TempLimitNative)
            GpuTempLimitC = BackendStatus!.TempLimitDefaultC;
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
        var profile = new GpuOcProfile(GpuCoreOffsetMhz, GpuMemOffsetMhz, GpuPowerLimitPct, GpuTempLimitC, GpuTargetVoltageMv);
        var result = await _ocService.ApplyAsync(profile);
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
    /// Explains how to validate a GPU OC. Honest: no GPU stress engine is integrated, so this never
    /// pretends a GPU test ran — it points to the real tools (FurMark / OCCT / Heaven) and to the
    /// genuinely built-in « Stabilité RAM » / « Stabilité CPU » tabs.
    /// </summary>
    [RelayCommand]
    private void RunStabilityTest()
    {
        StressTestProgress = "Pas de stress GPU intégré : valide l'OC GPU avec FurMark / OCCT / Heaven en "
                           + "parallèle du monitoring. Côté RAM et CPU, des tests intégrés existent → onglets "
                           + "« Stabilité RAM » et « Stabilité CPU ».";
    }

    [RelayCommand]
    private async Task ResetOcAsync()
    {
        // Run the native reset FIRST. On failure the card still holds the previous OC, so the sliders
        // must keep showing it — mutating them up-front would make the page claim a reset that didn't
        // happen and lose the values the user needs to see what's still applied.
        var r = await _ocService.ResetAsync();
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

        // Surface the service's per-axis summary (what was actually restored on which axes) rather than
        // a fixed "offsets remis à zéro" that would be wrong on AMD (no offsets) or hide power/temp reverts.
        AutoOcStatus = string.IsNullOrEmpty(r.Applied)
            ? "Réinitialisation GPU effectuée."
            : $"Réinitialisé : {r.Applied}";
    }
}
