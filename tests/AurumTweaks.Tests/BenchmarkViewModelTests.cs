using System;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the benchmark page's before/after orchestration — the honest-proof workflow: pin a run as the "Avant"
/// reference, capture again after applying tweaks, and get a real comparison. The load-bearing rules: a baseline
/// on its own is NOT yet a comparison (nothing claimed prematurely), no baseline means no fabricated comparison
/// at all, clearing the baseline tears the comparison down, and a real FPS drop surfaces as a regression at the
/// VM level too. Driven by <see cref="FakeBenchmarkService"/>; the VM's ctor wiring is synchronous so state is
/// deterministic right after construction.
/// </summary>
public class BenchmarkViewModelTests
{
    private static BenchmarkResult Run(double avgFps, string process = "game", double durationSec = 20)
        => new()
        {
            TargetProcess = process,
            Stats = new FrameTimeStats { FrameCount = 1000, DurationSec = durationSec, AvgFps = avgFps }
        };

    private static BenchmarkViewModel NewVm(FakeBenchmarkService fake, FakeBenchmarkHistoryService? history = null,
                                            IEvidenceLedger? evidence = null)
        => new(fake, history ?? new FakeBenchmarkHistoryService(), evidence ?? new EvidenceLedger())
            { TargetProcess = "game" };

    private static BenchmarkHistoryEntry Entry(string path = "run.csv", string process = "game", double avgFps = 120)
        => new(path, new DateTime(2026, 6, 20, 14, 0, 0), process, 1000, avgFps, avgFps * 0.8);

    [Fact]
    public async Task SetBaseline_ThenCaptureAgain_ComparesAgainstTheBaseline()
    {
        var fake = new FakeBenchmarkService();
        var vm = NewVm(fake);

        fake.ResultToReturn = Run(avgFps: 100);
        await vm.CaptureLiveCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult);

        vm.SetAsBaselineCommand.Execute(null);
        Assert.True(vm.HasBaseline);
        Assert.False(vm.HasComparison);          // a baseline on its own claims nothing yet

        fake.ResultToReturn = Run(avgFps: 120);
        await vm.CaptureLiveCommand.ExecuteAsync(null);

        Assert.True(vm.HasComparison);
        Assert.Equal(100, vm.Comparison!.Headline.Before, 3);
        Assert.Equal(120, vm.Comparison!.Headline.After, 3);
        Assert.True(vm.Comparison!.Headline.Improved);
    }

    [Fact]
    public async Task CaptureWithoutBaseline_DoesNotFabricateAComparison()
    {
        var fake = new FakeBenchmarkService();
        var vm = NewVm(fake);

        fake.ResultToReturn = Run(avgFps: 100);
        await vm.CaptureLiveCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.False(vm.HasComparison);          // no "Avant" pinned → no A/B invented
    }

    [Fact]
    public async Task ClearBaseline_TearsDownBaselineAndComparison()
    {
        var fake = new FakeBenchmarkService();
        var vm = NewVm(fake);

        fake.ResultToReturn = Run(avgFps: 100);
        await vm.CaptureLiveCommand.ExecuteAsync(null);
        vm.SetAsBaselineCommand.Execute(null);
        fake.ResultToReturn = Run(avgFps: 120);
        await vm.CaptureLiveCommand.ExecuteAsync(null);
        Assert.True(vm.HasComparison);

        vm.ClearBaselineCommand.Execute(null);

        Assert.False(vm.HasBaseline);
        Assert.False(vm.HasComparison);
    }

    [Fact]
    public async Task Comparison_IsPublishedToTheEvidenceLedger_AndClearedWithTheBaseline()
    {
        // The page feeds the unified « preuve »: when an A/B exists it lands in the shared ledger, and when the
        // baseline is cleared the slot clears too — so the Dashboard can never paste a torn-down comparison as current.
        var fake = new FakeBenchmarkService();
        var ledger = new EvidenceLedger();
        var vm = NewVm(fake, evidence: ledger);

        fake.ResultToReturn = Run(avgFps: 100);
        await vm.CaptureLiveCommand.ExecuteAsync(null);
        vm.SetAsBaselineCommand.Execute(null);
        Assert.False(ledger.Current().HasPerformance);   // a baseline alone is not yet a comparison

        fake.ResultToReturn = Run(avgFps: 120);
        await vm.CaptureLiveCommand.ExecuteAsync(null);
        Assert.True(ledger.Current().HasPerformance);    // the A/B was published

        vm.ClearBaselineCommand.Execute(null);
        Assert.False(ledger.Current().HasPerformance);   // ...and cleared when the comparison was torn down
    }

    [Fact]
    public async Task CaptureRegression_IsSurfacedHonestly_NotHidden()
    {
        var fake = new FakeBenchmarkService();
        var vm = NewVm(fake);

        fake.ResultToReturn = Run(avgFps: 120);
        await vm.CaptureLiveCommand.ExecuteAsync(null);
        vm.SetAsBaselineCommand.Execute(null);

        fake.ResultToReturn = Run(avgFps: 90);          // a tweak made it WORSE
        await vm.CaptureLiveCommand.ExecuteAsync(null);

        Assert.True(vm.HasComparison);
        Assert.True(vm.Comparison!.Headline.Regressed);
        Assert.False(vm.Comparison!.Headline.Improved);
    }

    // ----- Persistent run history -----

    [Fact]
    public async Task LiveCapture_IsAutoArchived_SoItSurvivesARestart()
    {
        var fake = new FakeBenchmarkService();
        var history = new FakeBenchmarkHistoryService();
        var vm = NewVm(fake, history);

        fake.ResultToReturn = Run(avgFps: 120);
        await vm.CaptureLiveCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.Single(history.Saved);            // the in-memory capture was persisted, not lost on close
    }

    [Fact]
    public async Task LoadFromHistory_SetsCurrentResult_WithoutReArchiving()
    {
        var fake = new FakeBenchmarkService();
        var history = new FakeBenchmarkHistoryService { ToLoad = Run(avgFps: 144) };
        var vm = NewVm(fake, history);

        await vm.LoadFromHistoryCommand.ExecuteAsync(Entry());

        Assert.True(vm.HasResult);
        Assert.Equal(144, vm.Result!.Stats.AvgFps, 3);
        Assert.Empty(history.Saved);             // reloading a stored run must NOT create a duplicate archive
    }

    [Fact]
    public async Task PinHistoricalRunAsBaseline_WithCurrentRun_ComparesAcrossSessions()
    {
        // The cross-session A/B: a run captured today (Après) measured against one archived days ago (Avant).
        var fake = new FakeBenchmarkService();
        var history = new FakeBenchmarkHistoryService { ToLoad = Run(avgFps: 90) };   // the older « Avant »
        var vm = NewVm(fake, history);

        fake.ResultToReturn = Run(avgFps: 120);                                       // today's « Après »
        await vm.CaptureLiveCommand.ExecuteAsync(null);

        await vm.PinFromHistoryAsBaselineCommand.ExecuteAsync(Entry(avgFps: 90));

        Assert.True(vm.HasBaseline);
        Assert.True(vm.HasComparison);
        Assert.Equal(90, vm.Comparison!.Headline.Before, 3);
        Assert.Equal(120, vm.Comparison!.Headline.After, 3);
        Assert.True(vm.Comparison!.Headline.Improved);
    }

    [Fact]
    public async Task PinHistoricalRunAsBaseline_NoCurrentRun_IsBaselineOnly_NotAFabricatedComparison()
    {
        var fake = new FakeBenchmarkService();
        var history = new FakeBenchmarkHistoryService { ToLoad = Run(avgFps: 90) };
        var vm = NewVm(fake, history);

        await vm.PinFromHistoryAsBaselineCommand.ExecuteAsync(Entry(avgFps: 90));

        Assert.True(vm.HasBaseline);
        Assert.False(vm.HasComparison);          // no « Après » yet → nothing compared
    }

    [Fact]
    public async Task DeleteFromHistory_RemovesTheStoredRun()
    {
        var fake = new FakeBenchmarkService();
        var history = new FakeBenchmarkHistoryService();
        var vm = NewVm(fake, history);

        await vm.DeleteFromHistoryCommand.ExecuteAsync(Entry(path: "run-42.csv"));

        Assert.Contains("run-42.csv", history.Deleted);
    }

    // ----- Tail-low honesty on the live card (shared frame-count floor) -----

    [Fact]
    public async Task ThinRun_HedgesOnTheLiveCard_ThatTheTailLowsRestOnTooFewFrames()
    {
        // A sub-1000-frame run: its « 0,1% low » leans on ~1 frame. The on-screen card must say so — the same honesty
        // the shared paste and the A/B comparer apply, through the one FrameSampleAdequacy threshold (no third wording).
        var fake = new FakeBenchmarkService();
        var vm = NewVm(fake);

        fake.ResultToReturn = new BenchmarkResult
        {
            TargetProcess = "game",
            Stats = new FrameTimeStats { FrameCount = 600, DurationSec = 20, AvgFps = 120 }
        };
        await vm.CaptureLiveCommand.ExecuteAsync(null);

        Assert.True(vm.TailLowsThin);
        Assert.NotNull(vm.TailLowHint);
        Assert.Contains("600", vm.TailLowHint!);     // the actual frame count, surfaced honestly
        Assert.Contains("1000", vm.TailLowHint!);    // the shared floor — sourced from the const, not hardcoded in XAML
    }

    [Fact]
    public async Task AmpleRun_CarriesNoTailLowHedge_NorAPhantomCaption()
    {
        // 1000 frames clears the floor (the guard is exclusive at the floor) → no hedge, and the card binds null so the
        // caption collapses rather than leaving an empty muted line.
        var fake = new FakeBenchmarkService();
        var vm = NewVm(fake);

        fake.ResultToReturn = Run(avgFps: 120);      // Run builds FrameCount 1000
        await vm.CaptureLiveCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.False(vm.TailLowsThin);
        Assert.Null(vm.TailLowHint);
    }
}
