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

    /// <summary>Always-visible disclosure under the sliders: Aurum applies core/mem only, never power/voltage.
    /// Bound by the view so the power-limit and voltage sliders read as Afterburner references, not dead controls.</summary>
    public string GpuSlidersNote => GpuOcDisclosure.SlidersNote;

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

        // Lead with exactly what the backend wrote (core/mem), then disclose any power/voltage the user set but
        // that Aurum doesn't apply — so a successful apply never implies those two sliders took effect.
        var ignored = GpuOcDisclosure.IgnoredAxesNote(profile);
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
        GpuCoreOffsetMhz = 0; GpuMemOffsetMhz = 0; GpuPowerLimitPct = 100; GpuTempLimitC = 83; GpuTargetVoltageMv = 900;
        var r = await _ocService.ResetAsync();
        AutoOcStatus = r.Success ? "Offsets GPU remis à zéro." : $"Erreur : {r.Error}";
    }
}
