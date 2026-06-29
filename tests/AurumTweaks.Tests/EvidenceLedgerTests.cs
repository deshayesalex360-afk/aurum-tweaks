using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the in-memory rendez-vous the three before/after pages publish into and the Dashboard export reads back. The
/// honesty-bearing behaviours: each surface is held independently (one page's publish never clobbers another's), a
/// published null CLEARS a slot (so a closed comparison can't be pasted as current), and <see cref="EvidenceInputs"/>
/// always reflects the latest of each.
/// </summary>
public class EvidenceLedgerTests
{
    private static BenchmarkComparison Perf()
        => BenchmarkComparer.Compare(
            new BenchmarkResult { Stats = new FrameTimeStats { FrameCount = 1000, DurationSec = 20, AvgFps = 100 } },
            new BenchmarkResult { Stats = new FrameTimeStats { FrameCount = 1000, DurationSec = 20, AvgFps = 120 } });

    private static SnapshotComparison Settings()
        => new() { Summary = "diff" };

    private static OptimizationScorecard Score()
        => OptimizationScore.Compute(new[] { new ScoreInput(TweakCategory.Gaming, 1, TweakAppliedState.Applied) });

    [Fact]
    public void NewLedger_IsEmpty()
    {
        var inputs = new EvidenceLedger().Current();

        Assert.False(inputs.HasSettings);
        Assert.False(inputs.HasPerformance);
        Assert.False(inputs.HasScore);
        Assert.False(inputs.HasAnyEvidence);
    }

    [Fact]
    public void EachSurface_IsHeldIndependently_AndReflectedInCurrent()
    {
        var ledger = new EvidenceLedger();

        ledger.PublishPerformance(Perf());
        ledger.PublishSettings(Settings(), "Avant", "maintenant");
        ledger.PublishScore(Score(), new ScoreProgress(true, 100, 80, 20, null));

        var inputs = ledger.Current();
        Assert.True(inputs.HasPerformance);
        Assert.True(inputs.HasSettings);
        Assert.True(inputs.HasScore);
        Assert.Equal("Avant", inputs.SettingsBaselineLabel);
        Assert.Equal("maintenant", inputs.SettingsTargetLabel);
        Assert.True(inputs.ScoreTrend!.HasTrend);
    }

    [Fact]
    public void PublishingNull_ClearsThatSlot_WithoutTouchingTheOthers()
    {
        var ledger = new EvidenceLedger();
        ledger.PublishPerformance(Perf());
        ledger.PublishSettings(Settings(), "Avant", "maintenant");

        // The Snapshot page's comparison closes → it publishes null. The performance slot must survive.
        ledger.PublishSettings(null, null, null);

        var inputs = ledger.Current();
        Assert.False(inputs.HasSettings);
        Assert.True(inputs.HasPerformance);
    }

    [Fact]
    public void RepublishingASurface_ReplacesTheEarlierValue()
    {
        var ledger = new EvidenceLedger();
        ledger.PublishSettings(new SnapshotComparison { Summary = "premier" }, "A", "B");
        ledger.PublishSettings(new SnapshotComparison { Summary = "second" }, "C", "D");

        var inputs = ledger.Current();
        Assert.Equal("second", inputs.Settings!.Summary);
        Assert.Equal("C", inputs.SettingsBaselineLabel);
    }

    [Fact]
    public void Performance_IsRehydratedFromTheStore_OnConstruction()
    {
        // A proof built in a previous session is sitting in the durable store; a fresh ledger must surface it.
        var store = new FakeEvidenceStore { Stored = Perf() };

        Assert.True(new EvidenceLedger(store).Current().HasPerformance);
    }

    [Fact]
    public void PublishPerformance_WritesThroughToTheStore_AndNullClearsTheDurableCopy()
    {
        var store = new FakeEvidenceStore();
        var ledger = new EvidenceLedger(store);

        ledger.PublishPerformance(Perf());
        Assert.NotNull(store.Stored);          // persisted, so it can outlive this session

        ledger.PublishPerformance(null);
        Assert.Null(store.Stored);             // a cleared A/B clears the durable copy too — no resurrection next launch
    }

    [Fact]
    public void OnlyPerformancePersists_SettingsAndScoreAreGoneInTheNextSession()
    {
        // Everything published in one session...
        var store = new FakeEvidenceStore();
        var first = new EvidenceLedger(store);
        first.PublishPerformance(Perf());
        first.PublishSettings(Settings(), "Avant", "maintenant");
        first.PublishScore(Score(), new ScoreProgress(true, 100, 80, 20, null));

        // ...but a NEW ledger over the SAME durable store only gets the frame-time A/B back. The « maintenant »
        // settings diff and the live score are intentionally NOT persisted — reloading either could read as current
        // after a reboot. They self-heal (re-detect / one-click recompute); the durable proof is the historical A/B.
        var next = new EvidenceLedger(store).Current();
        Assert.True(next.HasPerformance);
        Assert.False(next.HasSettings);
        Assert.False(next.HasScore);
    }
}
