using System;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services.Interop;

namespace AurumTweaks.Services;

public sealed record GpuFanStatus(
    bool Available,
    GpuVendor Vendor,
    int CurrentPercent,
    int Rpm,
    string Message);

public sealed record GpuFanResult(bool Success, string? Error = null, string? Applied = null);

/// <summary>
/// GPU fan control. On NVIDIA it is backed by the real, user-mode NVAPI client-fan-coolers interface
/// (<see cref="NvApi"/>) — verified on-card, every manual set floored by <see cref="GpuFanSafety"/> and
/// confirmed by a status read-back. AMD fan tuning (ADLX <c>IADLXManualFanTuning</c>) is a documented
/// state-list curve not yet integrated, so AMD is honestly referred to Adrenalin. Manual fan settings are
/// volatile (reset on reboot/driver reload); nothing here touches the vBIOS.
/// </summary>
public interface IGpuFanService
{
    Task<GpuFanStatus> GetStatusAsync();
    Task<GpuFanResult> SetManualAsync(int percent);
    Task<GpuFanResult> SetAutoAsync();
}

public sealed class GpuFanService : IGpuFanService
{
    private readonly IHardwareService _hardware;
    private readonly INvApi _nv;

    private bool _nvProbed;
    private IntPtr _nvGpu = IntPtr.Zero;

    public GpuFanService(IHardwareService hardware, INvApi nv)
    {
        _hardware = hardware;
        _nv = nv;
    }

    private bool EnsureNvidia()
    {
        if (_nvProbed) return _nvGpu != IntPtr.Zero;
        _nvProbed = true;
        if (_nv.TryGetFirstGpu(out var gpu, out _) && gpu != IntPtr.Zero) _nvGpu = gpu;
        return _nvGpu != IntPtr.Zero;
    }

    public async Task<GpuFanStatus> GetStatusAsync()
    {
        var hw = await _hardware.DetectAsync();
        if (hw.GpuVendor == GpuVendor.Nvidia && EnsureNvidia() && _nv.TryReadFanStatus(_nvGpu, out int pct, out int rpm))
        {
            return new GpuFanStatus(
                Available: true, GpuVendor.Nvidia, pct, rpm,
                $"Contrôle ventilateur NVAPI actif — {rpm} tr/min, {pct} % (interface non documentée par NVIDIA mais "
                + $"utilisée par les outils d'OC ; chaque réglage confirmé par relecture, jamais sous {GpuFanSafety.HardFloorPercent} %). "
                + "Réglage non-persistant (auto au redémarrage).");
        }

        if (hw.GpuVendor == GpuVendor.Amd)
            return new GpuFanStatus(false, GpuVendor.Amd, 0, 0,
                "Contrôle ventilateur AMD non implémenté nativement — régler la courbe dans AMD Adrenalin (Performances › Tuning).");

        return new GpuFanStatus(false, hw.GpuVendor, 0, 0,
            "Contrôle ventilateur indisponible (aucun GPU NVIDIA pilotable via NVAPI).");
    }

    public async Task<GpuFanResult> SetManualAsync(int percent)
    {
        var hw = await _hardware.DetectAsync();
        if (hw.GpuVendor != GpuVendor.Nvidia || !EnsureNvidia())
            return new GpuFanResult(false, "Contrôle ventilateur natif indisponible (NVIDIA uniquement — AMD : Adrenalin).");

        // Apply the hard safety floor at the service boundary (the interop floors again, defense-in-depth).
        int applied = GpuFanSafety.ClampManualPercent(percent);
        if (!_nv.TrySetFanManual(_nvGpu, applied, out var error))
            return new GpuFanResult(false, error);

        Serilog.Log.Information("GPU fan set manual via NVAPI: {Pct}%", applied);
        return new GpuFanResult(true, null, applied == percent
            ? $"ventilateur {applied} % (confirmé par relecture)"
            : $"ventilateur {percent} % demandé → {applied} % (plancher de sécurité, confirmé)");
    }

    public async Task<GpuFanResult> SetAutoAsync()
    {
        var hw = await _hardware.DetectAsync();
        if (hw.GpuVendor != GpuVendor.Nvidia || !EnsureNvidia())
            return new GpuFanResult(false, "Contrôle ventilateur natif indisponible (NVIDIA uniquement — AMD : Adrenalin).");

        if (!_nv.TrySetFanAuto(_nvGpu, out var error))
            return new GpuFanResult(false, error);

        Serilog.Log.Information("GPU fan restored to auto via NVAPI.");
        return new GpuFanResult(true, null, "ventilateur remis en mode automatique (courbe du driver)");
    }
}

/// <summary>Test double for the fan service.</summary>
public sealed class FakeGpuFanService : IGpuFanService
{
    private readonly GpuFanStatus _status;
    public System.Collections.Generic.List<int> ManualWrites { get; } = new();
    public int AutoCount { get; private set; }
    public bool WriteSucceeds { get; set; } = true;

    public FakeGpuFanService(GpuFanStatus? status = null)
        => _status = status ?? new GpuFanStatus(false, GpuVendor.Unknown, 0, 0, "indisponible");

    public Task<GpuFanStatus> GetStatusAsync() => Task.FromResult(_status);

    public Task<GpuFanResult> SetManualAsync(int percent)
    {
        int applied = GpuFanSafety.ClampManualPercent(percent);
        ManualWrites.Add(applied);
        return Task.FromResult(WriteSucceeds
            ? new GpuFanResult(true, Applied: $"ventilateur {applied} %")
            : new GpuFanResult(false, Error: "échec ventilateur simulé"));
    }

    public Task<GpuFanResult> SetAutoAsync()
    {
        AutoCount++;
        return Task.FromResult(WriteSucceeds ? new GpuFanResult(true, Applied: "auto") : new GpuFanResult(false, Error: "échec simulé"));
    }
}
