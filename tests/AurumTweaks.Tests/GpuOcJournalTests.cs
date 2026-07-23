using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure GPU-OC → JournalEntry mapping. The point: an overclock lands in the same "what did Aurum
/// change" record as every other mutation, and — because a GPU write is either read-back-confirmed or a
/// failure — a success is fully Confirmed and a failure fully Failed, with nothing Unconfirmed.
/// </summary>
public class GpuOcJournalTests
{
    private static readonly DateTime Ts = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ForApply_Success_IsAConfirmedApplication()
    {
        var e = GpuOcJournal.ForApply(new GpuOcApplyResult(true, Applied: "core +150 MHz · power limit 110 %"), Ts);

        Assert.Equal("Application", e.Action);
        Assert.Equal(1, e.Succeeded);
        Assert.Equal(0, e.Failed);
        Assert.Equal(Ts, e.TimestampUtc);
        Assert.Contains("GPU OC — core +150 MHz", e.TweakIds[0]);
        Assert.Single(e.Confirmed);          // a successful GPU write is already read-back-confirmed
        Assert.Empty(e.Unconfirmed);         // there is no "applied but unconfirmed" for a GPU write
    }

    [Fact]
    public void ForApply_Failure_IsAFailedApplication_WithNothingConfirmed()
    {
        var e = GpuOcJournal.ForApply(new GpuOcApplyResult(false, Error: "driver refusé"), Ts);

        Assert.Equal("Application", e.Action);
        Assert.Equal(0, e.Succeeded);
        Assert.Equal(1, e.Failed);
        Assert.Contains("échec", e.TweakIds[0]);
        Assert.Empty(e.Confirmed);
    }

    [Fact]
    public void ForReset_Success_IsARestauration()
    {
        var e = GpuOcJournal.ForReset(new GpuOcApplyResult(true, Applied: "offsets remis à 0"), Ts);

        Assert.Equal("Restauration", e.Action);
        Assert.Equal(1, e.Succeeded);
        Assert.Contains("GPU OC — offsets remis à 0", e.TweakIds[0]);
    }
}
