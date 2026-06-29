using System;
using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>What to test: how much memory, how many passes, and how many worker threads.</summary>
public sealed record MemoryTestConfig
{
    public int SizeMb { get; init; } = 1024;
    public int Passes { get; init; } = 2;
    public int Threads { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
}

/// <summary>A live progress snapshot pushed during a run (via <see cref="IProgress{T}"/>).</summary>
public sealed record MemoryTestProgress
{
    public int Pass { get; init; }
    public int TotalPasses { get; init; }
    public string Phase { get; init; } = string.Empty;   // e.g. "0xAA · inversion ↑"
    public double Percent { get; init; }                 // 0..100 overall
    public long BytesTested { get; init; }               // cumulative sweep coverage
    public double ThroughputMbps { get; init; }          // sweep throughput, MB/s
    public int Errors { get; init; }
}

/// <summary>One detected mismatch. Offset is logical (virtual) — without a kernel driver we cannot map it to a DIMM.</summary>
public sealed record MemoryError
{
    public long ByteOffset { get; init; }
    public ulong Expected { get; init; }
    public ulong Actual { get; init; }
    public string Pattern { get; init; } = string.Empty;
}

/// <summary>
/// Outcome of a memory stability run. <see cref="Passed"/> means it ran to completion with zero errors —
/// honest about what that does and does NOT prove (it is a quick coverage test, not an overnight TM5/Karhu run).
/// </summary>
public sealed record MemoryTestResult
{
    public bool Completed { get; init; }     // ran every planned pass (not cancelled, not an allocation failure)
    public bool Cancelled { get; init; }
    public int SizeMbTested { get; init; }
    public int PassesCompleted { get; init; }
    public double DurationSec { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<MemoryError> Errors { get; init; } = Array.Empty<MemoryError>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public double AvgThroughputMbps { get; init; }

    public bool Passed => Completed && ErrorCount == 0;
    public bool HasRun => Completed || Cancelled || ErrorCount > 0;

    public static MemoryTestResult Empty { get; } = new();
}
