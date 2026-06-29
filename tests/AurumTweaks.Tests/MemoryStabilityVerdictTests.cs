using System;
using System.Collections.Generic;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="MemoryStabilityVerdict.Describe"/> — the single verdict SENTENCE read by both the page status and
/// the shareable report. Honesty contract (on top of the shared <see cref="StabilityVerdict.Classify"/> ordering this
/// builds on): a completed clean run reads « STABLE sur ce test » (never an unqualified « STABLE »); a run with errors
/// reads « INSTABLE » and names the count even when it was also cancelled (errors-first); a clean cancel keeps « ne
/// prouve rien »; and a run that never executed surfaces the service note (e.g. an allocation failure) or an honest
/// fallback. Mirrors <see cref="CpuStabilityVerdict"/> on the CPU side.
/// </summary>
public class MemoryStabilityVerdictTests
{
    private static MemoryTestResult Run(
        bool completed = true, bool cancelled = false, int sizeMb = 2048, int passes = 2,
        double durationSec = 30, int errorCount = 0, double avgMbps = 48000,
        IReadOnlyList<string>? notes = null) => new()
    {
        Completed = completed,
        Cancelled = cancelled,
        SizeMbTested = sizeMb,
        PassesCompleted = passes,
        DurationSec = durationSec,
        ErrorCount = errorCount,
        AvgThroughputMbps = avgMbps,
        Notes = notes ?? Array.Empty<string>(),
    };

    [Fact]
    public void CompletedClean_ReadsStable_WithTheSurCeTestQualifier()
    {
        var text = MemoryStabilityVerdict.Describe(Run(completed: true, errorCount: 0));
        Assert.Contains("STABLE sur ce test", text);
        Assert.Contains("0 erreur", text);
        Assert.Contains("Mo", text);   // names what was actually covered
    }

    [Fact]
    public void WithErrors_ReadsUnstable_NamingTheCount()
    {
        var text = MemoryStabilityVerdict.Describe(Run(completed: true, errorCount: 2));
        Assert.Contains("INSTABLE", text);
        Assert.Contains("2 erreur", text);
    }

    [Fact]
    public void ErrorsOnACancelledRun_StillReadUnstable_NeverInterrompu()
    {
        var text = MemoryStabilityVerdict.Describe(Run(completed: false, cancelled: true, errorCount: 1));
        Assert.Contains("INSTABLE", text);
        Assert.DoesNotContain("Interrompu", text);
    }

    [Fact]
    public void CleanCancel_ReadsInterrompu_AndProvesNothing()
    {
        var text = MemoryStabilityVerdict.Describe(Run(completed: false, cancelled: true, errorCount: 0));
        Assert.Contains("Interrompu", text);
        Assert.Contains("Ne prouve rien", text);
        Assert.DoesNotContain("STABLE", text);
    }

    [Fact]
    public void DidNotRun_SurfacesTheServiceNote_OrAnHonestFallback()
    {
        Assert.Contains("allocation refusée",
            MemoryStabilityVerdict.Describe(Run(completed: false, cancelled: false, notes: new[] { "allocation refusée" })));
        Assert.Contains("n'a pas pu s'exécuter",
            MemoryStabilityVerdict.Describe(Run(completed: false, cancelled: false)));
    }
}
