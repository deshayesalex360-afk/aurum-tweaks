using System;
using System.Collections.Generic;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="CpuStabilityVerdict.Describe"/> — the single verdict SENTENCE read by both the page status and the
/// shareable report. Honesty contract (on top of the shared <see cref="StabilityVerdict.Classify"/> ordering this
/// builds on): a completed clean run reads « STABLE sur ce test » (never an unqualified « STABLE »); a run with
/// errors reads « INSTABLE » and names the count even when it was also cancelled (errors-first); a clean cancel keeps
/// « ne prouve rien »; and the kernel that actually ran (AVX2 vs scalaire) is named truthfully, never assumed.
/// </summary>
public class CpuStabilityVerdictTests
{
    private static CpuTestResult Run(
        bool completed = true, bool cancelled = false, bool avx2 = true, int threads = 16,
        double durationSec = 30, int errorCount = 0, IReadOnlyList<string>? notes = null) => new()
    {
        Completed = completed,
        Cancelled = cancelled,
        Avx2Used = avx2,
        ThreadsUsed = threads,
        DurationSec = durationSec,
        ErrorCount = errorCount,
        Notes = notes ?? Array.Empty<string>(),
    };

    [Fact]
    public void CompletedClean_ReadsStable_WithTheSurCeTestQualifier()
    {
        var text = CpuStabilityVerdict.Describe(Run(completed: true, errorCount: 0));
        Assert.Contains("STABLE sur ce test", text);
        Assert.Contains("0 erreur", text);
    }

    [Fact]
    public void WithErrors_ReadsUnstable_NamingTheCount()
    {
        var text = CpuStabilityVerdict.Describe(Run(completed: true, errorCount: 2));
        Assert.Contains("INSTABLE", text);
        Assert.Contains("2 erreur", text);
    }

    [Fact]
    public void ErrorsOnACancelledRun_StillReadUnstable_NeverInterrompu()
    {
        var text = CpuStabilityVerdict.Describe(Run(completed: false, cancelled: true, errorCount: 1));
        Assert.Contains("INSTABLE", text);
        Assert.DoesNotContain("Interrompu", text);
    }

    [Fact]
    public void CleanCancel_ReadsInterrompu_AndProvesNothing()
    {
        var text = CpuStabilityVerdict.Describe(Run(completed: false, cancelled: true, errorCount: 0));
        Assert.Contains("Interrompu", text);
        Assert.Contains("Ne prouve rien", text);
        Assert.DoesNotContain("STABLE", text);
    }

    [Fact]
    public void DidNotRun_SurfacesTheServiceNote_OrAnHonestFallback()
    {
        Assert.Contains("mémoire insuffisante",
            CpuStabilityVerdict.Describe(Run(completed: false, cancelled: false, notes: new[] { "mémoire insuffisante" })));
        Assert.Contains("n'a pas pu s'exécuter",
            CpuStabilityVerdict.Describe(Run(completed: false, cancelled: false)));
    }

    [Theory]
    [InlineData(true, "AVX2")]
    [InlineData(false, "scalaire")]
    public void Names_TheKernelThatActuallyRan(bool avx2, string expected)
        => Assert.Contains(expected, CpuStabilityVerdict.Describe(Run(completed: true, avx2: avx2)));
}
