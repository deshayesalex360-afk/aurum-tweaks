using System;
using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>How long to torture the CPU and on how many logical cores.</summary>
public sealed record CpuTestConfig
{
    public int DurationSec { get; init; } = 30;
    public int Threads { get; init; } = Environment.ProcessorCount;   // a CPU test wants every logical core

    /// <summary>
    /// Use the 256-bit AVX2 vector kernel (heavier, closer to real SIMD game/encode load) when the CPU
    /// supports it. Falls back to the scalar kernel honestly when AVX2 is absent — see
    /// <see cref="CpuTestResult.Avx2Used"/> for what actually ran.
    /// </summary>
    public bool UseAvx2 { get; init; } = true;
}

/// <summary>Live progress pushed during a run (via <see cref="IProgress{T}"/>).</summary>
public sealed record CpuTestProgress
{
    public double Percent { get; init; }            // 0..100, time-based
    public double ElapsedSec { get; init; }
    public double TotalSec { get; init; }
    public double IterationsPerSec { get; init; }   // completed workload iterations / s (honest: not "FLOPS")
    public long Batches { get; init; }              // verification batches completed across all cores
    public int Errors { get; init; }
    public int Threads { get; init; }
}

/// <summary>One detected miscalculation: a worker's checksum didn't match the reference for its seed.</summary>
public sealed record CpuComputeError
{
    public int Thread { get; init; }
    public ulong Expected { get; init; }
    public ulong Actual { get; init; }
    public double AtSec { get; init; }
}

/// <summary>
/// Outcome of a CPU stability run. <see cref="Passed"/> means it ran the full duration with zero
/// miscalculations — honest about what that proves (a quick coherence test) and what it does not
/// (it is not Prime95 small-FFT for hours, and managed code can't drive dedicated AVX-512 torture).
/// </summary>
public sealed record CpuTestResult
{
    public bool Completed { get; init; }     // ran the full requested duration (not cancelled)
    public bool Cancelled { get; init; }
    public bool Avx2Used { get; init; }       // true ⇒ the 256-bit AVX2 kernel ran; false ⇒ scalar kernel
    public int ThreadsUsed { get; init; }
    public double DurationSec { get; init; }
    public long Batches { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<CpuComputeError> Errors { get; init; } = Array.Empty<CpuComputeError>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public double AvgIterationsPerSec { get; init; }

    public bool Passed => Completed && ErrorCount == 0;
    public bool HasRun => Completed || Cancelled || ErrorCount > 0;

    public static CpuTestResult Empty { get; } = new();
}
