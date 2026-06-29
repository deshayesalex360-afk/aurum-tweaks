using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace AurumTweaks.Services;

/// <summary>
/// Thin wrapper around LibreHardwareMonitor for live CPU/GPU/RAM/temps.
/// Polls every second on a background timer.
/// </summary>
public sealed class MonitoringService : IMonitoringService, IDisposable
{
    private readonly Computer _computer;
    private Timer? _timer;
    private MonitoringSnapshot _last = new() { CapturedAtUtc = DateTime.UtcNow };

    public event EventHandler<MonitoringSnapshot>? SnapshotReady;

    public MonitoringService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsNetworkEnabled = false,
            IsStorageEnabled = false
        };
    }

    public void Start()
    {
        try
        {
            _computer.Open();
            _timer ??= new Timer(_ => Poll(), null, 0, 1000);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to start LibreHardwareMonitor");
        }
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        try { _computer.Close(); } catch { }
    }

    public MonitoringSnapshot GetSnapshot() => _last;

    private void Poll()
    {
        try
        {
            float cpuUsage = 0, cpuTemp = 0, cpuClock = 0, gpuUsage = 0, gpuTemp = 0, gpuClock = 0, ramUsed = 0, ramTotal = 0;
            float gpuVramUsedMb = 0, gpuVramTotalMb = 0;

            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        cpuUsage = MaxValue(hw, SensorType.Load, name: "CPU Total") ?? cpuUsage;
                        cpuTemp = MaxValue(hw, SensorType.Temperature, prefer: "Tctl") ?? cpuTemp;
                        // Peak boosting core, not core #1 — the headline a user means by « ma fréquence ».
                        cpuClock = MaxNamedValue(hw, SensorType.Clock, "Core") ?? cpuClock;
                        break;
                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        gpuUsage = MaxValue(hw, SensorType.Load, prefer: "GPU Core") ?? gpuUsage;
                        gpuTemp = MaxValue(hw, SensorType.Temperature, prefer: "GPU Core") ?? gpuTemp;
                        // "Core" excludes the GPU memory/shader clocks, which are unrelated to the boost clock.
                        gpuClock = MaxNamedValue(hw, SensorType.Clock, "Core") ?? gpuClock;
                        // Dedicated VRAM (MB). MaxNamedValue, not MaxValue: we never fall back to an unrelated
                        // SmallData sensor (e.g. a per-process D3D counter). Absent → stays 0 → renders « — ».
                        gpuVramUsedMb = MaxNamedValue(hw, SensorType.SmallData, "GPU Memory Used") ?? gpuVramUsedMb;
                        gpuVramTotalMb = MaxNamedValue(hw, SensorType.SmallData, "GPU Memory Total") ?? gpuVramTotalMb;
                        break;
                    case HardwareType.Memory:
                        ramUsed = MaxValue(hw, SensorType.Data, prefer: "Memory Used") ?? ramUsed;
                        ramTotal = MaxValue(hw, SensorType.Data, prefer: "Memory Available") ?? ramTotal;
                        break;
                }
            }

            var ramTotalEstimate = ramUsed + ramTotal;
            float ramPct = ramTotalEstimate > 0 ? (ramUsed / ramTotalEstimate) * 100f : 0;

            float gpuVramUsedGb = gpuVramUsedMb / 1024f;
            float gpuVramTotalGb = gpuVramTotalMb / 1024f;
            float gpuVramPct = gpuVramTotalMb > 0 ? (gpuVramUsedMb / gpuVramTotalMb) * 100f : 0;

            _last = new MonitoringSnapshot
            {
                CpuUsagePercent = cpuUsage,
                CpuTempC = cpuTemp,
                CpuClockMhz = cpuClock,
                GpuUsagePercent = gpuUsage,
                GpuTempC = gpuTemp,
                GpuClockMhz = gpuClock,
                GpuVramUsedGb = gpuVramUsedGb,
                GpuVramTotalGb = gpuVramTotalGb,
                GpuVramUsagePercent = gpuVramPct,
                RamUsedGb = ramUsed,
                RamTotalGb = ramTotalEstimate,
                RamUsagePercent = ramPct,
                CapturedAtUtc = DateTime.UtcNow
            };
            SnapshotReady?.Invoke(this, _last);
        }
        catch { /* never throw from the timer */ }
    }

    private static float? MaxValue(IHardware hw, SensorType type, string? name = null, string? prefer = null)
    {
        var sensors = hw.Sensors.Where(s => s.SensorType == type);
        if (prefer is not null)
        {
            var preferred = sensors.FirstOrDefault(s => s.Name.Contains(prefer, StringComparison.OrdinalIgnoreCase));
            if (preferred?.Value is float pv) return pv;
        }
        if (name is not null)
        {
            var named = sensors.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (named?.Value is float nv) return nv;
        }
        var any = sensors.Select(s => s.Value).Where(v => v.HasValue).Max();
        return any;
    }

    // Like MaxValue but restricted to sensors whose NAME matches — and returns the MAX across all matches, not the
    // first. Needed for clocks: a CPU exposes one Clock sensor per core ("CPU Core #1".."#N"), and the headline a
    // user means by « ma fréquence » is the highest-boosting core, not core #1. Returns null when nothing matches,
    // which the renderer treats as « non lu » (never a fabricated 0 MHz).
    private static float? MaxNamedValue(IHardware hw, SensorType type, string nameContains)
    {
        float? max = null;
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != type) continue;
            if (!s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Value is float v && (max is null || v > max)) max = v;
        }
        return max;
    }

    public void Dispose() => Stop();
}
