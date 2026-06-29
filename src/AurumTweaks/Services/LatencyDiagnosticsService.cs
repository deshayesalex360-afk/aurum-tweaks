using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  « Latence système (DPC/ISR) » — a LatencyMon-adjacent diagnostic. It measures the REAL load that hardware
//  interrupts (ISR) and Deferred Procedure Calls (DPC) put on each logical core, read straight from the kernel's
//  own per-processor time counters (NtQuerySystemInformation / SystemProcessorPerformanceInformation — the very
//  source Process Explorer uses for its "% DPC Time" / "% Interrupt Time" columns). High DPC/ISR load is a classic
//  cause of micro-stutter and audio dropouts. The load-bearing honesty line: this tells you HOW MUCH CPU time goes
//  to DPC/ISR, NOT WHICH driver is responsible — pinning the culprit needs a full ETW kernel trace (LatencyMon,
//  DPC Latency Checker), which the page says plainly rather than fabricating a guilty driver. It's read-only: a
//  diagnostic, never a "fix" button, and it promises no FPS.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Raw per-processor kernel time counters, in 100-ns units (KernelTime already includes IdleTime, DpcTime and InterruptTime).</summary>
public readonly record struct CpuTimes(long IdleTime, long KernelTime, long UserTime, long DpcTime, long InterruptTime, long InterruptCount);

/// <summary>The computed load of one logical core over a measurement window.</summary>
public sealed record ProcessorLoad(int CpuIndex, double BusyPercent, double DpcPercent, double InterruptPercent, double InterruptsPerSecond)
{
    public string CpuLabel => $"CPU {CpuIndex}";
    public string BusyDisplay => LatencyFormat.Percent(BusyPercent);
    public string DpcDisplay => LatencyFormat.Percent(DpcPercent);
    public string InterruptDisplay => LatencyFormat.Percent(InterruptPercent);
    public string InterruptRateDisplay => LatencyFormat.Rate(InterruptsPerSecond);

    /// <summary>"CPU 3 · DPC 6,2 % · ISR 1,1 % · occupation 40,0 % · 12 000 irq/s" — one honest line per core.</summary>
    public string SummaryDisplay =>
        $"{CpuLabel}  ·  DPC {DpcDisplay}  ·  ISR {InterruptDisplay}  ·  occupation {BusyDisplay}  ·  {InterruptRateDisplay}";
}

/// <summary>
/// Pure load math from two kernel snapshots — no kernel call here, so it is fully unit-testable. Every delta is
/// clamped to ≥0 (a counter that appears to go backwards across the window can't fabricate a negative or a &gt;100 %
/// reading), and the percentages are taken over the total busy+idle CPU time the same way Process Explorer does.
/// </summary>
public static class ProcessorLoadMath
{
    public static ProcessorLoad Compute(int cpuIndex, CpuTimes a, CpuTimes b, double elapsedSeconds)
    {
        long kernel = Math.Max(0, b.KernelTime - a.KernelTime);     // includes idle + dpc + interrupt
        long user   = Math.Max(0, b.UserTime - a.UserTime);
        long idle   = Math.Max(0, b.IdleTime - a.IdleTime);
        long dpc    = Math.Max(0, b.DpcTime - a.DpcTime);
        long intr   = Math.Max(0, b.InterruptTime - a.InterruptTime);
        long count  = Math.Max(0, b.InterruptCount - a.InterruptCount);

        long total = kernel + user;     // total wall time this core accounted for in the window
        double busy = Percent(total - idle, total);
        double dpcPct = Percent(dpc, total);
        double intrPct = Percent(intr, total);
        double rate = elapsedSeconds > 0 ? count / elapsedSeconds : 0;

        return new ProcessorLoad(cpuIndex, busy, dpcPct, intrPct, rate);
    }

    private static double Percent(long part, long whole) =>
        whole <= 0 ? 0 : Math.Clamp((double)part / whole * 100.0, 0, 100);
}

/// <summary>Severity of the measured DPC/ISR pressure.</summary>
public enum LatencyLevel { Unknown, Low, Moderate, High }

/// <summary>A verdict: a level plus the French explanation shown to the user.</summary>
public sealed record LatencyAssessment(LatencyLevel Level, string Message);

/// <summary>
/// Pure verdict core, errors-first (mirrors <c>StabilityVerdict</c> / <c>DriveHealthVerdict</c>): no data ⇒ Unknown
/// (never silently "Low"), then the WORST of the measured DPC% / ISR% drives the level against two thresholds. The
/// thresholds are deliberately conservative and the UI labels them « indicatif » — we'd rather under-alarm than
/// fabricate a problem, and the High message hands the user off to a real per-driver tracer instead of naming a
/// culprit we cannot actually identify from load alone.
/// </summary>
public static class LatencyVerdict
{
    public const double ModeratePercent = 5.0;
    public const double HighPercent = 15.0;

    public static LatencyAssessment Evaluate(double maxDpcPercent, double maxInterruptPercent, bool queryOk)
    {
        if (!queryOk)
            return new(LatencyLevel.Unknown,
                "Mesure indisponible — les compteurs de temps DPC/ISR du noyau n'ont pas pu être lus.");

        double worst = Math.Max(maxDpcPercent, maxInterruptPercent);

        if (worst >= HighPercent)
            return new(LatencyLevel.High,
                "Charge DPC/ISR élevée : un pilote sollicite fortement les interruptions ou les DPC. C'est une "
                + "cause classique de micro-saccades et de coupures audio. Identifie le pilote responsable avec "
                + "LatencyMon ou DPC Latency Checker, puis mets-le à jour ou remplace-le.");

        if (worst >= ModeratePercent)
            return new(LatencyLevel.Moderate,
                "Charge DPC/ISR modérée : acceptable, mais à surveiller si tu ressens des saccades ou des coupures "
                + "audio en jeu. Relance une mesure longue pour confirmer que ce n'est pas un pic passager.");

        return new(LatencyLevel.Low,
            "Charge DPC/ISR faible : sur ce système, l'ordonnancement des interruptions et des DPC n'est pas un "
            + "goulot d'étranglement.");
    }

    /// <summary>The French level label (feminine — « charge … ») — the single source shared by the VM badge and the
    /// shareable report, so the two can never drift apart.</summary>
    public static string Label(LatencyLevel level) => level switch
    {
        LatencyLevel.Low => "Faible",
        LatencyLevel.Moderate => "Modérée",
        LatencyLevel.High => "Élevée",
        _ => "Inconnue"
    };
}

/// <summary>Pure, fr-FR-fixed formatting so the numbers are correct for the French UI and deterministic in tests.</summary>
public static class LatencyFormat
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Percent(double value) => Math.Clamp(value, 0, 100).ToString("0.0", Fr) + " %";

    /// <summary>Interrupts per second as a plain rounded integer (invariant, no locale group separator) + "/s".</summary>
    public static string Rate(double perSecond) =>
        Math.Round(Math.Max(0, perSecond)).ToString("0", CultureInfo.InvariantCulture) + "/s";
}

/// <summary>The whole measurement: per-core load, the window length, and whether the kernel query succeeded.</summary>
public sealed record LatencyReport(IReadOnlyList<ProcessorLoad> PerCpu, double MeasurementSeconds, bool QueryOk)
{
    public int CpuCount => PerCpu.Count;
    public double MaxDpcPercent => PerCpu.Count == 0 ? 0 : PerCpu.Max(p => p.DpcPercent);
    public double MaxInterruptPercent => PerCpu.Count == 0 ? 0 : PerCpu.Max(p => p.InterruptPercent);
    public double AvgDpcPercent => PerCpu.Count == 0 ? 0 : PerCpu.Average(p => p.DpcPercent);
    public double AvgInterruptPercent => PerCpu.Count == 0 ? 0 : PerCpu.Average(p => p.InterruptPercent);
    public double TotalInterruptsPerSecond => PerCpu.Sum(p => p.InterruptsPerSecond);

    /// <summary>The core carrying the highest DPC load — DPCs often serialise on one core, so the worst single core matters.</summary>
    public int WorstDpcCpu => PerCpu.Count == 0 ? -1 : PerCpu.OrderByDescending(p => p.DpcPercent).First().CpuIndex;

    public LatencyAssessment Verdict => LatencyVerdict.Evaluate(MaxDpcPercent, MaxInterruptPercent, QueryOk);

    public string MaxDpcDisplay => LatencyFormat.Percent(MaxDpcPercent);
    public string MaxInterruptDisplay => LatencyFormat.Percent(MaxInterruptPercent);
    public string TotalInterruptRateDisplay => LatencyFormat.Rate(TotalInterruptsPerSecond);

    public string Headline => QueryOk
        ? $"{CpuCount} cœurs logiques mesurés sur {MeasurementSeconds.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR"))} s"
        : "Mesure impossible";

    public string WorstDpcDisplay => WorstDpcCpu < 0
        ? "—"
        : $"Pic DPC sur CPU {WorstDpcCpu} ({MaxDpcDisplay})";
}

/// <summary>
/// Reads the per-processor kernel time counters via NtQuerySystemInformation(SystemProcessorPerformanceInformation) —
/// the same documented-by-Process-Explorer source. Pure I/O glue, fully guarded: any failure returns null so the
/// service reports an honest QueryOk=false rather than a fabricated zero load. The struct is 5×LARGE_INTEGER + a
/// trailing ULONG, 8-byte aligned ⇒ a 48-byte stride; fields are read by explicit offset to dodge any marshalling
/// surprise (same approach as the CPU-topology probe).
/// </summary>
internal static class ProcessorPerfProbe
{
    private const int SystemProcessorPerformanceInformation = 8;
    private const int Stride = 48;   // sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION) with 8-byte alignment

    public static CpuTimes[]? Sample()
    {
        int cpuCount = Math.Max(1, Environment.ProcessorCount);
        int capacity = cpuCount * Stride;
        IntPtr buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            int status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, capacity, out int returned);
            if (status != 0 || returned <= 0) return null;

            int count = Math.Min(cpuCount, returned / Stride);
            if (count <= 0) return null;

            var result = new CpuTimes[count];
            for (int i = 0; i < count; i++)
            {
                IntPtr e = buffer + i * Stride;
                long idle      = Marshal.ReadInt64(e, 0);
                long kernel    = Marshal.ReadInt64(e, 8);
                long user      = Marshal.ReadInt64(e, 16);
                long dpc       = Marshal.ReadInt64(e, 24);
                long interrupt = Marshal.ReadInt64(e, 32);
                long irqCount  = (uint)Marshal.ReadInt32(e, 40);
                result[i] = new CpuTimes(idle, kernel, user, dpc, interrupt, irqCount);
            }
            return result;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation,
        int systemInformationLength, out int returnLength);
}

/// <summary>
/// The I/O service behind « Latence système ». Takes two kernel snapshots <paramref name="sampleMs"/> apart and
/// derives each core's DPC/ISR load with the pure <see cref="ProcessorLoadMath"/>. The decision logic (load math,
/// verdict, formatting) lives in the pure cores above and is what the tests pin; this only does the sampling.
/// </summary>
public sealed class LatencyDiagnosticsService : ILatencyDiagnosticsService
{
    public async Task<LatencyReport> MeasureAsync(int sampleMs = 2000)
    {
        sampleMs = Math.Clamp(sampleMs, 250, 30000);

        var first = ProcessorPerfProbe.Sample();
        if (first is null) return Failed(sampleMs);

        var sw = Stopwatch.StartNew();
        await Task.Delay(sampleMs);
        sw.Stop();

        var second = ProcessorPerfProbe.Sample();
        if (second is null) return Failed(sampleMs);

        double seconds = sw.Elapsed.TotalSeconds;
        int count = Math.Min(first.Length, second.Length);
        var loads = new List<ProcessorLoad>(count);
        for (int i = 0; i < count; i++)
            loads.Add(ProcessorLoadMath.Compute(i, first[i], second[i], seconds));

        return new LatencyReport(loads, seconds, QueryOk: loads.Count > 0);
    }

    private static LatencyReport Failed(int sampleMs) =>
        new(Array.Empty<ProcessorLoad>(), sampleMs / 1000.0, QueryOk: false);
}
