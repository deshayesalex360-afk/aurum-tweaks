using System;
using System.Threading;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Tests the orchestration around <see cref="CpuWorkload"/>: a short real run on a healthy CI box must come
/// back STABLE having actually done work, and cancellation must surface as an honest
/// <see cref="CpuTestResult.Cancelled"/> result — never a thrown task and never a fabricated pass.
/// </summary>
public class CpuStabilityServiceTests
{
    [Fact]
    public async Task RunAsync_ShortHealthyRun_PassesDoesWork_AndReportsProgress()
    {
        int reports = 0;
        bool sawThroughput = false;
        var progress = new Progress<CpuTestProgress>(p =>
        {
            reports++;
            if (p.IterationsPerSec > 0) sawThroughput = true;
        });

        // 5s is the service's floor; keep it to 2 threads so we never starve the CI runner.
        var cfg = new CpuTestConfig { DurationSec = 5, Threads = 2 };
        var result = await new CpuStabilityService().RunAsync(cfg, progress, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.False(result.Cancelled);
        Assert.Equal(0, result.ErrorCount);
        Assert.True(result.Passed);
        Assert.Equal(2, result.ThreadsUsed);
        Assert.True(result.Batches > 0);                 // it actually ground through work
        Assert.True(result.AvgIterationsPerSec > 0);
        Assert.NotEmpty(result.Notes);

        // The "not Prime95/OCCT for hours" caveat must be present — no overclaiming.
        Assert.Contains(result.Notes, n => n.Contains("Prime95") || n.Contains("OCCT") || n.Contains("y-cruncher"));

        await Task.Delay(50);                            // let queued Progress<T> callbacks drain
        Assert.True(reports > 0);
        Assert.True(sawThroughput);
    }

    [Fact]
    public async Task RunAsync_PreCancelledToken_ReturnsCancelled_NeverThrows_NeverFabricatesPass()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cfg = new CpuTestConfig { DurationSec = 30, Threads = 2 };
        var result = await new CpuStabilityService().RunAsync(cfg, progress: null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Passed);     // a cancelled run must NEVER report success
        Assert.False(result.Completed);
        Assert.True(result.HasRun);      // it happened (cancelled), so the UI can show a verdict
    }

    [Fact]
    public async Task RunAsync_WithAvx2Requested_UsesAvx2WhenSupported_AndStaysHonest()
    {
        var cfg = new CpuTestConfig { DurationSec = 5, Threads = 2, UseAvx2 = true };
        var result = await new CpuStabilityService().RunAsync(cfg, progress: null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(0, result.ErrorCount);
        Assert.True(result.Passed);

        // Honesty: the result reflects what truly ran — AVX2 iff this machine supports it, never a false claim.
        Assert.Equal(CpuWorkload.Avx2Available, result.Avx2Used);
    }

    [Fact]
    public async Task RunAsync_WithAvx2Disabled_RunsScalarKernel()
    {
        var cfg = new CpuTestConfig { DurationSec = 5, Threads = 2, UseAvx2 = false };
        var result = await new CpuStabilityService().RunAsync(cfg, progress: null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(0, result.ErrorCount);
        Assert.True(result.Passed);
        Assert.False(result.Avx2Used);   // explicitly opted out ⇒ scalar, regardless of hardware
    }
}
