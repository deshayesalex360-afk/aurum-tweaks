using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services.Interop;

namespace AurumTweaks.Services;

/// <summary>
/// GPU overclocking abstraction. On NVIDIA it is backed by the real, user-mode NVAPI wrapper
/// (<see cref="NvApi"/>) and applies <b>core/memory clock offsets only</b> — frequency deltas
/// the driver clamps to the card's allowed V/F range, non-persistent across reboot. Power-limit,
/// temperature target and voltage are intentionally <i>not</i> applied natively (their NVAPI struct
/// layout is not confident enough to write safely), and AMD is reported honestly as "use Adrenalin".
/// Nothing here ships a kernel driver, touches the vBIOS, or unlocks voltage.
/// </summary>
public interface IGpuOcService
{
    /// <summary>Returns the current detected GPU and whether the native OC backend is available.</summary>
    Task<GpuOcBackendStatus> GetStatusAsync();

    /// <summary>Apply a GPU OC profile. Validated first; returns false + error when out of range or backend unavailable.</summary>
    Task<GpuOcApplyResult> ApplyAsync(GpuOcProfile profile);

    /// <summary>Read back the current applied core/memory offsets, or null when unavailable.</summary>
    Task<GpuOcProfile?> ReadCurrentAsync();

    /// <summary>Reset core/memory offsets back to 0.</summary>
    Task<GpuOcApplyResult> ResetAsync();
}

public sealed record GpuOcBackendStatus(
    GpuVendor Vendor,
    string GpuName,
    bool BackendAvailable,
    string? BackendVersion = null,
    string? Message = null);

public sealed record GpuOcProfile(
    int CoreOffsetMhz,
    int MemoryOffsetMhz,
    int PowerLimitPct,
    int TempLimitC,
    int TargetVoltageMv);

/// <summary><see cref="Applied"/> is a short human summary of what was actually written (e.g. "core +150 MHz · mém +1200 MHz").</summary>
public sealed record GpuOcApplyResult(bool Success, string? Error = null, string? Applied = null);

/// <summary>
/// Pure, side-effect-free validation and clamping for GPU OC profiles. Deliberately split out of
/// <see cref="GpuOcService"/> so it can be unit-tested exhaustively without ever touching native
/// NVAPI — a native call with an in-range profile would really overclock the test machine's GPU.
/// </summary>
public static class GpuOcValidation
{
    public const int CoreMin = -500;
    public const int CoreMax = 1000;
    public const int MemMin = -2000;
    public const int MemMax = 3000;
    public const int PowerMin = 50;
    public const int PowerMax = 150;
    public const int TempMin = 50;
    public const int TempMax = 95;

    /// <summary>Returns a human-readable error when the profile is out of range, or null when valid.</summary>
    public static string? Validate(GpuOcProfile p)
    {
        if (p.CoreOffsetMhz < CoreMin || p.CoreOffsetMhz > CoreMax)
            return $"Offset core hors limites ({CoreMin} à +{CoreMax} MHz).";
        if (p.MemoryOffsetMhz < MemMin || p.MemoryOffsetMhz > MemMax)
            return $"Offset mémoire hors limites ({MemMin} à +{MemMax} MHz).";
        if (p.PowerLimitPct < PowerMin || p.PowerLimitPct > PowerMax)
            return $"Power limit hors limites ({PowerMin}–{PowerMax} %).";
        if (p.TempLimitC < TempMin || p.TempLimitC > TempMax)
            return $"Limite de température hors limites ({TempMin}–{TempMax} °C).";
        return null;
    }

    /// <summary>Defense-in-depth: clamp every axis into range before a native write (the driver clamps too).</summary>
    public static GpuOcProfile Clamp(GpuOcProfile p) => p with
    {
        CoreOffsetMhz = Math.Clamp(p.CoreOffsetMhz, CoreMin, CoreMax),
        MemoryOffsetMhz = Math.Clamp(p.MemoryOffsetMhz, MemMin, MemMax),
        PowerLimitPct = Math.Clamp(p.PowerLimitPct, PowerMin, PowerMax),
        TempLimitC = Math.Clamp(p.TempLimitC, TempMin, TempMax)
    };
}

/// <summary>
/// Pure honesty helper for the OC page. The native backend (<see cref="GpuOcService"/>) writes core/memory
/// frequency offsets ONLY; power limit, voltage and temperature are deliberately never applied. The page still
/// shows power-limit/voltage sliders (they're genuinely useful as the numbers to dial into MSI Afterburner /
/// Adrenalin), so this single-sources the disclosure that keeps them from reading as Aurum-applied — turning a
/// would-be dead control into an honest "set this elsewhere" reference. Side-effect-free → unit-testable.
/// </summary>
public static class GpuOcDisclosure
{
    /// <summary>Stock power limit (%): no change requested, so that axis has nothing to disclose.</summary>
    public const int NeutralPowerLimitPct = 100;

    /// <summary>Always-visible note under the sliders — states plainly what Aurum applies vs. what it never does.</summary>
    public const string SlidersNote =
        "Aurum applique nativement les offsets core et mémoire (NVAPI). Le power limit et le voltage ne sont pas "
      + "appliqués par Aurum — règle-les dans MSI Afterburner (NVIDIA) ou Adrenalin (AMD).";

    /// <summary>
    /// The honest "these were not applied" clause to append after a successful apply, or empty when the user
    /// left the non-applied axes at neutral (nothing to disclose). Power limit counts only when moved off 100 %
    /// (stock); voltage counts whenever a target is set, since Aurum never writes voltage. Keeps the apply
    /// status from implying the power/voltage sliders did anything.
    /// </summary>
    public static string IgnoredAxesNote(GpuOcProfile p)
    {
        var axes = new List<string>();
        if (p.PowerLimitPct != NeutralPowerLimitPct) axes.Add("power limit");
        if (p.TargetVoltageMv > 0) axes.Add("voltage");
        return axes.Count == 0
            ? string.Empty
            : $"{string.Join(" et ", axes)} non appliqué(s) par Aurum (à régler dans Afterburner).";
    }
}

/// <summary>
/// Default implementation. NVIDIA offsets go through <see cref="NvApi"/>; everything else is reported
/// honestly rather than faked. The NVAPI handle is probed lazily and cached so a non-NVIDIA machine
/// pays the lookup once and then simply reports "unavailable".
/// </summary>
public sealed class GpuOcService : IGpuOcService
{
    private readonly IHardwareService _hardware;

    private bool _nvProbed;
    private IntPtr _nvGpu = IntPtr.Zero;
    private string _nvName = string.Empty;

    public GpuOcService(IHardwareService hardware)
    {
        _hardware = hardware;
    }

    /// <summary>Lazily resolve (and cache) the first NVAPI-drivable NVIDIA GPU handle. Never throws.</summary>
    private bool EnsureNvidia()
    {
        if (_nvProbed) return _nvGpu != IntPtr.Zero;
        _nvProbed = true;
        if (NvApi.TryGetFirstGpu(out var gpu, out var name) && gpu != IntPtr.Zero)
        {
            _nvGpu = gpu;
            _nvName = name;
            return true;
        }
        return false;
    }

    public async Task<GpuOcBackendStatus> GetStatusAsync()
    {
        var hw = await _hardware.DetectAsync();
        switch (hw.GpuVendor)
        {
            case GpuVendor.Nvidia:
                if (EnsureNvidia())
                {
                    return new GpuOcBackendStatus(
                        GpuVendor.Nvidia,
                        string.IsNullOrEmpty(_nvName) ? hw.GpuPrimary : _nvName,
                        BackendAvailable: true,
                        BackendVersion: hw.GpuDriverVersion,
                        Message: "NVAPI actif — offsets core/mémoire (P0) applicables. Deltas de fréquence "
                               + "bornés par le driver, non-persistants (remis à zéro au redémarrage), aucun "
                               + "déverrouillage de voltage. Power limit / température / voltage : non appliqués "
                               + "nativement pour l'instant.");
                }
                return new GpuOcBackendStatus(
                    GpuVendor.Nvidia, hw.GpuPrimary,
                    BackendAvailable: false,
                    BackendVersion: hw.GpuDriverVersion,
                    Message: "GPU NVIDIA détecté mais NVAPI n'a pas pu être initialisé (pilote absent ou trop ancien).");

            case GpuVendor.Amd:
                return new GpuOcBackendStatus(
                    GpuVendor.Amd, hw.GpuPrimary,
                    BackendAvailable: false,
                    BackendVersion: hw.GpuDriverVersion,
                    Message: "OC AMD non implémenté nativement — utiliser AMD Adrenalin (Performances › Tuning) "
                           + "pour appliquer les offsets.");

            default:
                return new GpuOcBackendStatus(
                    GpuVendor.Unknown, hw.GpuPrimary, false, null,
                    "Aucun GPU NVIDIA ou AMD pilotable détecté — overclocking désactivé.");
        }
    }

    public Task<GpuOcApplyResult> ApplyAsync(GpuOcProfile profile)
    {
        // 1. Validate BEFORE touching anything native — an out-of-range profile never reaches the driver.
        var error = GpuOcValidation.Validate(profile);
        if (error is not null)
            return Task.FromResult(new GpuOcApplyResult(false, error));

        // 2. Native apply is NVIDIA-only for now.
        if (!EnsureNvidia())
            return Task.FromResult(new GpuOcApplyResult(false,
                "Application native indisponible : aucun GPU NVIDIA pilotable via NVAPI (AMD : utiliser Adrenalin)."));

        // 3. Defense-in-depth clamp, then write core/memory offsets only.
        var clamped = GpuOcValidation.Clamp(profile);
        if (!NvApi.TrySetOffsets(_nvGpu, clamped.CoreOffsetMhz, clamped.MemoryOffsetMhz, out var nvError))
            return Task.FromResult(new GpuOcApplyResult(false, nvError));

        var applied = $"core {clamped.CoreOffsetMhz:+#;-#;0} MHz · mém {clamped.MemoryOffsetMhz:+#;-#;0} MHz";
        Serilog.Log.Information("GPU OC applied via NVAPI: {Applied}", applied);
        return Task.FromResult(new GpuOcApplyResult(true, null, applied));
    }

    public Task<GpuOcProfile?> ReadCurrentAsync()
    {
        if (EnsureNvidia() && NvApi.TryReadOffsets(_nvGpu, out int core, out int mem))
            // Power/temp/voltage are not read natively; report the two offsets we can actually trust.
            return Task.FromResult<GpuOcProfile?>(new GpuOcProfile(core, mem, 100, 0, 0));
        return Task.FromResult<GpuOcProfile?>(null);
    }

    public Task<GpuOcApplyResult> ResetAsync()
    {
        if (!EnsureNvidia())
            return Task.FromResult(new GpuOcApplyResult(false,
                "Réinitialisation native indisponible : aucun GPU NVIDIA pilotable via NVAPI."));

        if (!NvApi.TrySetOffsets(_nvGpu, 0, 0, out var nvError))
            return Task.FromResult(new GpuOcApplyResult(false, nvError));

        Serilog.Log.Information("GPU OC reset to 0/0 via NVAPI.");
        return Task.FromResult(new GpuOcApplyResult(true, null, "offsets remis à 0"));
    }
}
