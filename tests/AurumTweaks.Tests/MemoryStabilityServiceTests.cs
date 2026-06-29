using System.Threading;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Tests for the orchestration around <see cref="MemoryPatterns"/>: a small, deterministic real run
/// must come back STABLE on a healthy CI box, and cancellation must surface as an honest
/// <see cref="MemoryTestResult.Cancelled"/> result rather than a thrown task or a fabricated pass.
/// We keep the footprint tiny (16 Mo, 1 pass, 1 thread) so it is fast and never starves the runner.
/// </summary>
public class MemoryStabilityServiceTests
{
    private static MemoryTestConfig Small => new() { SizeMb = 16, Passes = 1, Threads = 1 };

    [Fact]
    public async Task RunAsync_SmallHealthyRun_PassesWithNoErrors()
    {
        var result = await new MemoryStabilityService()
            .RunAsync(Small, progress: null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.False(result.Cancelled);
        Assert.Equal(0, result.ErrorCount);
        Assert.True(result.Passed);               // Completed && no errors
        Assert.Equal(1, result.PassesCompleted);
        Assert.True(result.SizeMbTested >= 16);   // allocated at least what we asked for
        Assert.NotEmpty(result.Notes);            // always explains what it does / doesn't prove
    }

    [Fact]
    public async Task RunAsync_AlwaysAttachesHonestyDisclaimer()
    {
        var result = await new MemoryStabilityService()
            .RunAsync(Small, progress: null, CancellationToken.None);

        // The "this is not TM5/Karhu/HCI" caveat must be present on a clean pass — no overclaiming.
        Assert.Contains(result.Notes, n => n.Contains("TM5") || n.Contains("Karhu") || n.Contains("HCI"));
    }

    [Fact]
    public async Task RunAsync_ReportsProgress_WithThroughputAndPhases()
    {
        int reports = 0;
        bool sawPhase = false;
        bool sawThroughput = false;
        var progress = new Progress<MemoryTestProgress>(p =>
        {
            reports++;
            if (!string.IsNullOrWhiteSpace(p.Phase)) sawPhase = true;
            if (p.ThroughputMbps > 0) sawThroughput = true;
        });

        var result = await new MemoryStabilityService()
            .RunAsync(Small, progress, CancellationToken.None);

        Assert.True(result.Passed);
        // Progress<T> marshals callbacks; give them a beat to drain before asserting.
        await Task.Delay(50);
        Assert.True(reports > 0);
        Assert.True(sawPhase);
        Assert.True(sawThroughput);
    }

    [Fact]
    public async Task RunAsync_PreCancelledToken_ReturnsCancelled_NeverThrows_NeverFabricatesPass()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new MemoryStabilityService()
            .RunAsync(Small, progress: null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Passed);          // a cancelled run must NEVER report success
        Assert.False(result.Completed);
        Assert.True(result.HasRun);           // it did happen (cancelled), so the UI can show a verdict
    }
}
