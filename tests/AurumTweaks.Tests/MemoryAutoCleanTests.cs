using System;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Services;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

// Pinned behaviour for « auto-nettoyage » (the opt-in Mem Reduct-style feature). The load-bearing honesty rules live in
// the pure MemoryAutoClean core and are tested without a timer or a live machine: never fire while disabled/off, never
// act on an unreadable composition, and never hammer the kernel on a sustained high-memory state (the cooldown). The
// store's clamp-on-load and the ViewModel's fire/no-fire wiring are pinned too.

/// <summary>Builds a composition with a given in-use percentage (total = 100, available = 100 − pct) so the
/// threshold logic can be exercised with clean, readable numbers. DetailAvailable is irrelevant to the decision.</summary>
public class MemoryAutoCleanDecisionTests
{
    private static MemoryComposition AtInUsePercent(long pct) =>
        new(TotalBytes: 100, AvailableBytes: 100 - pct, StandbyBytes: 0, FreeBytes: 0, ModifiedBytes: 0, DetailAvailable: false);

    private static MemoryAutoCleanSettings Settings(
        bool enabled = true,
        MemoryAutoCleanTrigger trigger = MemoryAutoCleanTrigger.Threshold,
        int threshold = 85,
        int interval = 30,
        MemoryFlushKind kind = MemoryFlushKind.StandbyList) =>
        new(enabled, trigger, threshold, interval, kind);

    [Fact]
    public void Disabled_NeverCleans_EvenAtFullPressure()
        => Assert.False(MemoryAutoClean.ShouldClean(Settings(enabled: false), AtInUsePercent(99), minutesSinceLastClean: 10));

    [Fact]
    public void TriggerOff_NeverCleans()
        => Assert.False(MemoryAutoClean.ShouldClean(Settings(trigger: MemoryAutoCleanTrigger.Off), AtInUsePercent(99), 10));

    [Fact]
    public void NoData_NeverCleans()   // an unreadable composition must never fire a flush on a guess
        => Assert.False(MemoryAutoClean.ShouldClean(Settings(), MemoryComposition.Empty, 10));

    [Fact]
    public void Threshold_Fires_WhenPressureAtOrAboveCeiling_AndCooldownElapsed()
        => Assert.True(MemoryAutoClean.ShouldClean(Settings(threshold: 85), AtInUsePercent(90), minutesSinceLastClean: 10));

    [Fact]
    public void Threshold_AtExactCeiling_Fires()
        => Assert.True(MemoryAutoClean.ShouldClean(Settings(threshold: 85), AtInUsePercent(85), 10));

    [Fact]
    public void Threshold_DoesNotFire_BelowCeiling()
        => Assert.False(MemoryAutoClean.ShouldClean(Settings(threshold: 85), AtInUsePercent(80), 10));

    [Fact]
    public void Threshold_Respects_Cooldown()   // pressure is high, but too soon since the last clean
        => Assert.False(MemoryAutoClean.ShouldClean(Settings(threshold: 85), AtInUsePercent(95), minutesSinceLastClean: 0.5));

    [Fact]
    public void Interval_Fires_WhenElapsed_RegardlessOfPressure()
        => Assert.True(MemoryAutoClean.ShouldClean(
            Settings(trigger: MemoryAutoCleanTrigger.Interval, interval: 30), AtInUsePercent(10), minutesSinceLastClean: 30));

    [Fact]
    public void Interval_DoesNotFire_BeforeElapsed()
        => Assert.False(MemoryAutoClean.ShouldClean(
            Settings(trigger: MemoryAutoCleanTrigger.Interval, interval: 30), AtInUsePercent(99), minutesSinceLastClean: 29));

    [Fact]
    public void Interval_IgnoresPressure()   // high pressure alone must not fire an interval-only policy
        => Assert.False(MemoryAutoClean.ShouldClean(
            Settings(trigger: MemoryAutoCleanTrigger.Interval, interval: 30), AtInUsePercent(99), minutesSinceLastClean: 5));

    [Fact]
    public void Both_Fires_OnThreshold_EvenIfIntervalNotElapsed()
        => Assert.True(MemoryAutoClean.ShouldClean(
            Settings(trigger: MemoryAutoCleanTrigger.Both, threshold: 85, interval: 60), AtInUsePercent(95), minutesSinceLastClean: 5));

    [Fact]
    public void Both_Fires_OnInterval_EvenIfPressureLow()
        => Assert.True(MemoryAutoClean.ShouldClean(
            Settings(trigger: MemoryAutoCleanTrigger.Both, threshold: 85, interval: 30), AtInUsePercent(10), minutesSinceLastClean: 30));

    [Fact]
    public void Normalized_Clamps_ThresholdAndInterval()
    {
        var s = new MemoryAutoCleanSettings(true, MemoryAutoCleanTrigger.Both, ThresholdPercent: 5, IntervalMinutes: 9999, MemoryFlushKind.StandbyList).Normalized();
        Assert.Equal(MemoryAutoClean.MinThresholdPercent, s.ThresholdPercent);
        Assert.Equal(MemoryAutoClean.MaxIntervalMinutes, s.IntervalMinutes);
    }

    [Fact]
    public void Describe_Off_SaysDisabled()
        => Assert.Contains("désactivé", MemoryAutoClean.Describe(Settings(enabled: false)), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Describe_Threshold_MentionsCeiling()
        => Assert.Contains("85", MemoryAutoClean.Describe(Settings(threshold: 85)));
}

/// <summary>The JSON store: round-trips, and degrades a missing/corrupt/out-of-range file to a sane, clamped value.</summary>
public class MemoryAutoCleanStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "aurum-autoclean-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void RoundTrip_PreservesSettings()
    {
        var path = TempPath();
        try
        {
            var store = new MemoryAutoCleanStore(path);
            var settings = new MemoryAutoCleanSettings(true, MemoryAutoCleanTrigger.Interval, 70, 15, MemoryFlushKind.WorkingSets);
            store.Save(settings);
            Assert.Equal(settings, store.Load());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDisabledDefault()
    {
        var store = new MemoryAutoCleanStore(TempPath());   // path never created
        Assert.Equal(MemoryAutoCleanSettings.Default, store.Load());
        Assert.False(store.Load().Enabled);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefault()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json");
            Assert.Equal(MemoryAutoCleanSettings.Default, new MemoryAutoCleanStore(path).Load());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_Then_Load_ClampsOutOfRange()
    {
        var path = TempPath();
        try
        {
            var store = new MemoryAutoCleanStore(path);
            store.Save(new MemoryAutoCleanSettings(true, MemoryAutoCleanTrigger.Threshold, 5, 9999, MemoryFlushKind.StandbyList));
            var loaded = store.Load();
            Assert.Equal(MemoryAutoClean.MinThresholdPercent, loaded.ThresholdPercent);
            Assert.Equal(MemoryAutoClean.MaxIntervalMinutes, loaded.IntervalMinutes);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>The ViewModel wiring: EvaluateAutoCleanAsync fires the real (faked) flush only when the pure rule says so.</summary>
public class MemoryViewModelAutoCleanTests
{
    private static MemoryComposition InUse(long pct) =>
        new(TotalBytes: 100, AvailableBytes: 100 - pct, StandbyBytes: 0, FreeBytes: 0, ModifiedBytes: 0, DetailAvailable: false);

    private static MemoryViewModel Build(FakeMemoryManagementService svc) =>
        new(svc, new NullMemoryAutoCleanStore());

    [Fact]
    public async Task Evaluate_FiresFlush_WhenEnabledAndPressureHigh()
    {
        var svc = new FakeMemoryManagementService { NextComposition = InUse(90) };
        var vm = Build(svc);
        vm.AutoCleanEnabled = true;                         // default trigger = Threshold, threshold = 85
        vm.LastAutoCleanUtc = DateTime.UtcNow.AddMinutes(-10);   // past the cooldown

        await vm.EvaluateAutoCleanAsync();

        Assert.Equal(1, svc.FlushCount);
        Assert.Equal(MemoryFlushKind.StandbyList, svc.LastFlushKind);
    }

    [Fact]
    public async Task Evaluate_DoesNotFire_WhenDisabled()
    {
        var svc = new FakeMemoryManagementService { NextComposition = InUse(99) };
        var vm = Build(svc);   // auto-clean stays disabled (default)
        vm.LastAutoCleanUtc = DateTime.UtcNow.AddMinutes(-10);

        await vm.EvaluateAutoCleanAsync();

        Assert.Equal(0, svc.FlushCount);
    }

    [Fact]
    public async Task Evaluate_DoesNotFire_WhenPressureBelowCeiling()
    {
        var svc = new FakeMemoryManagementService { NextComposition = InUse(50) };
        var vm = Build(svc);
        vm.AutoCleanEnabled = true;                         // threshold = 85, pressure only 50 %
        vm.LastAutoCleanUtc = DateTime.UtcNow.AddMinutes(-10);

        await vm.EvaluateAutoCleanAsync();

        Assert.Equal(0, svc.FlushCount);
    }
}
