using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="MonitoringSnapshotTextReport"/> — the shareable « Instantané monitoring » paste for the FR scene's
/// « tes temps / ta charge en jeu ? ». Honesty contract: it renders only the REAL live samples the page polled, computes
/// <b>actuel · moyenne · pic</b> over the window (peak is the throttling tell), treats a temperature of 0 as « capteur non
/// lu » — a running PC is never 0 °C — rather than a fabricated « 0 °C », keeps a genuine 0 % load (idle is real), shows
/// « — » for an unread RAM total instead of « 0 / 0 Go », states the window size, handles an empty window without inventing
/// a row, and keeps the read-only / never-sent / no-FPS footer plus the single-instant caveat.
/// </summary>
public class MonitoringSnapshotTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 21, 14, 30, 0, DateTimeKind.Utc);

    private static MonitoringSnapshot Snap(
        float cpuLoad = 40, float cpuTemp = 60, float gpuLoad = 90, float gpuTemp = 65,
        float ramUsed = 14f, float ramTotal = 32f, float ramPct = 44f,
        float cpuClock = 4500, float gpuClock = 2500,
        float vramUsed = 6f, float vramTotal = 8f, float vramPct = 75f)
        => new()
        {
            CpuUsagePercent = cpuLoad, CpuTempC = cpuTemp, CpuClockMhz = cpuClock,
            GpuUsagePercent = gpuLoad, GpuTempC = gpuTemp, GpuClockMhz = gpuClock,
            GpuVramUsedGb = vramUsed, GpuVramTotalGb = vramTotal, GpuVramUsagePercent = vramPct,
            RamUsedGb = ramUsed, RamTotalGb = ramTotal, RamUsagePercent = ramPct,
            CapturedAtUtc = When
        };

    private static string Render(params MonitoringSnapshot[] samples)
        => MonitoringSnapshotTextReport.Render(samples, When);

    [Fact]
    public void Header_AlwaysCarriesTitle()
    {
        Assert.Contains("Aurum Tweaks — Instantané monitoring", Render(Snap()));
    }

    [Fact]
    public void Window_StatesSampleCount()
    {
        var text = Render(Snap(), Snap(), Snap());
        Assert.Contains("Fenêtre : 3 mesure(s)", text);
        Assert.Contains("actuel · moyenne · pic", text);
    }

    [Fact]
    public void Cpu_And_Gpu_SectionsAndLabelsArePresent()
    {
        var text = Render(Snap());
        Assert.Contains("CPU", text);
        Assert.Contains("GPU", text);
        Assert.Contains("Charge", text);
        Assert.Contains("Température", text);
    }

    [Fact]
    public void Peak_IsTheWindowMaximum_NotTheLastSample()
    {
        // CPU temp climbs then dips; the peak must stay the window max (79), current the most recent (70).
        var text = Render(
            Snap(cpuTemp: 60),
            Snap(cpuTemp: 79),
            Snap(cpuTemp: 70));
        Assert.Contains("pic 79 °C", text);
        Assert.Contains("70 °C  ·  moy", text);   // current is the last sample, not the peak
    }

    [Fact]
    public void PeakLoad_IsTheWindowMaximum()
    {
        var text = Render(
            Snap(gpuLoad: 90),
            Snap(gpuLoad: 100),
            Snap(gpuLoad: 95));
        Assert.Contains("pic 100 %", text);
    }

    [Fact]
    public void ZeroTemperature_IsNonLu_NeverFabricatedZeroDegrees()
    {
        // A whole window with no CPU temperature sensor must read « non lu », never « 0 °C ».
        var text = Render(
            Snap(cpuTemp: 0, gpuTemp: 65),
            Snap(cpuTemp: 0, gpuTemp: 66));
        Assert.Contains("non lu", text);
        Assert.DoesNotContain("0 °C", text);   // no fabricated 0 °C anywhere (GPU is a real 65/66)
    }

    [Fact]
    public void TransientZeroTemperature_DoesNotPoisonTheRealReading()
    {
        // A single dropped temperature sample (0) among real ones must not turn the metric into « non lu ».
        var text = Render(
            Snap(cpuTemp: 0, gpuTemp: 67),
            Snap(cpuTemp: 61, gpuTemp: 67),
            Snap(cpuTemp: 0, gpuTemp: 67));
        // The real 61 °C survives the surrounding zeros: the triple only renders from a valid reading, never « non lu ».
        Assert.Contains("61 °C  ·  moy 61 °C  ·  pic 61 °C", text);
    }

    [Fact]
    public void Clock_RowsPresent_ForCpuAndGpu_WithBoostInMhz()
    {
        // The active-core boost clock is the « ma fréquence en jeu ? » headline — rendered in whole MHz
        // (never GHz) so the paste stays locale-independent (no comma/dot decimal separator to trip fr-FR).
        var text = Render(Snap(cpuClock: 4950, gpuClock: 2850));
        Assert.Contains("Fréquence", text);
        Assert.Contains("4950 MHz", text);
        Assert.Contains("2850 MHz", text);
    }

    [Fact]
    public void PeakClock_IsTheWindowMaximum_NotTheLastSample()
    {
        // Boost spikes then settles; the peak must stay the window max (5200), current the most recent (4800).
        var text = Render(
            Snap(cpuClock: 4600),
            Snap(cpuClock: 5200),
            Snap(cpuClock: 4800));
        Assert.Contains("pic 5200 MHz", text);
        Assert.Contains("4800 MHz  ·  moy", text);   // current is the last sample, not the peak
    }

    [Fact]
    public void ZeroClock_IsNonLu_NeverFabricatedZeroMhz()
    {
        // A whole window with no CPU clock sensor must read « non lu », never « 0 MHz ». GPU clock 2847
        // (not the round default) so a real reading can't accidentally satisfy the « no 0 MHz » assertion.
        var text = Render(
            Snap(cpuClock: 0, gpuClock: 2847),
            Snap(cpuClock: 0, gpuClock: 2847));
        Assert.Contains("non lu", text);
        Assert.DoesNotContain("0 MHz", text);   // no fabricated 0 MHz anywhere (GPU is a real 2847)
    }

    [Fact]
    public void TransientZeroClock_DoesNotPoisonTheRealReading()
    {
        // A single dropped clock sample (0) among real ones must not turn the metric into « non lu ».
        var text = Render(
            Snap(cpuClock: 0),
            Snap(cpuClock: 4700),
            Snap(cpuClock: 0));
        Assert.Contains("4700 MHz  ·  moy 4700 MHz  ·  pic 4700 MHz", text);
    }

    [Fact]
    public void ZeroLoad_IsKept_BecauseIdleIsReal()
    {
        // Unlike temperature, a 0 % load is a legitimate idle reading and must be shown, not hidden.
        var text = Render(Snap(cpuLoad: 0, cpuTemp: 40));
        Assert.Contains("0 %  ·  moy 0 %  ·  pic 0 %", text);
    }

    [Fact]
    public void AbsentRamTotal_RendersDash_NeverFabricatedZeroOverZero()
    {
        var text = Render(Snap(ramUsed: 0, ramTotal: 0, ramPct: 0, vramUsed: 0, vramTotal: 0, vramPct: 0, cpuTemp: 55, gpuTemp: 60));
        Assert.Contains("Utilisée", text);
        Assert.Contains("—", text);
        Assert.DoesNotContain("Go", text);   // no fabricated « 0 / 0 Go » for RAM or VRAM when neither total was read
    }

    [Fact]
    public void Vram_RowPresent_WithUsedTotalAndPercent()
    {
        // « ma VRAM est pleine ? » — a saturated VRAM thrashes textures and stutters, so we surface used/total + %.
        var text = Render(Snap(vramUsed: 7, vramTotal: 8, vramPct: 88));
        Assert.Contains("VRAM", text);
        Assert.Contains("(88 %)", text);   // current usage %, in F0 so it stays locale-independent
        Assert.Contains("Go", text);
    }

    [Fact]
    public void PeakVram_IsTheWindowMaximum()
    {
        // VRAM fills then frees; the peak is the saturation tell and must stay the window max (95), not the last (80).
        var text = Render(
            Snap(vramPct: 70),
            Snap(vramPct: 95),
            Snap(vramPct: 80));
        Assert.Contains("pic 95 %", text);
    }

    [Fact]
    public void AbsentVramTotal_RendersDash_WhileSystemRamStillShows()
    {
        // An unread VRAM total must render « — », never « 0 / 0 Go » — and must not suppress the real system-RAM line.
        var text = Render(Snap(vramUsed: 0, vramTotal: 0, vramPct: 0, ramUsed: 14, ramTotal: 32, ramPct: 44));
        Assert.Contains("VRAM", text);
        Assert.Contains("—", text);    // VRAM is the only unread total here, so this dash is its row
        Assert.Contains("Go", text);   // system RAM (14/32) still renders its real line
    }

    [Fact]
    public void Empty_SaysNoMeasurements_NotAFabricatedRow()
    {
        var text = MonitoringSnapshotTextReport.Render(Array.Empty<MonitoringSnapshot>(), When);
        Assert.Contains("Aucune mesure encore disponible", text);
        Assert.DoesNotContain("Charge", text);   // no fabricated metric row
    }

    [Fact]
    public void SingleSample_DoesNotCrash_AndRendersTheMetric()
    {
        var text = Render(Snap(cpuLoad: 50, cpuTemp: 70));
        Assert.Contains("Fenêtre : 1 mesure(s)", text);
        Assert.Contains("50 %  ·  moy 50 %  ·  pic 50 %", text);
        Assert.Contains("70 °C  ·  moy 70 °C  ·  pic 70 °C", text);
    }

    [Fact]
    public void Footer_KeepsHonestyContract_NoSendNoFps_AndTheInstantCaveat()
    {
        var text = Render(Snap());
        Assert.Contains("jamais envoyé", text);
        Assert.Contains("aucun gain de FPS", text);
        Assert.Contains("capteur non lu", text);
        Assert.Contains("instant", text);
    }
}
