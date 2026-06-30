using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Behavioural tests for <see cref="TweakService"/> using the in-memory fakes. These cover the
/// safety net the whole app leans on: apply/revert write the right values, null apply/revert
/// means "delete", service ops flip the startup type, and — the one that matters most — a
/// batch apply forces a system restore point BEFORE it touches the registry, proven via the
/// shared <see cref="EventLog"/> ordering.
/// </summary>
public class TweakServiceTests
{
    private const string Hive = "HKLM";
    private const string Key = @"SOFTWARE\Aurum\Test";

    private static (TweakService svc, FakeRegistryService reg, FakeServiceManager svcMgr,
                    RecordingRestorePointService rp, EventLog log) NewService(bool createRestorePoint = true)
    {
        var log = new EventLog();
        var reg = new FakeRegistryService(log);
        var svcMgr = new FakeServiceManager(log);
        var rp = new RecordingRestorePointService(log);
        var settings = new FakeAppSettingsStore();
        settings.Current.CreateRestorePointBeforeTweaks = createRestorePoint;
        return (new TweakService(reg, svcMgr, rp, settings), reg, svcMgr, rp, log);
    }

    private static Tweak RegTweak(string id = "test-reg", string name = "Flag",
                                  string? apply = "1", string? revert = "0") => new()
    {
        Id = id,
        Operations =
        {
            new TweakOperation
            {
                Type = OperationType.Registry,
                Hive = Hive,
                Key = Key,
                Name = name,
                ValueType = RegistryValueType.DWord,
                Apply = apply,
                Revert = revert
            }
        }
    };

    private static Tweak SvcTweak(string serviceName = "DiagTrack",
                                  string apply = "Disabled", string revert = "Automatic") => new()
    {
        Id = "test-svc",
        Operations =
        {
            new TweakOperation
            {
                Type = OperationType.Service,
                ServiceName = serviceName,
                StartupApply = apply,
                StartupRevert = revert
            }
        }
    };

    // A Cmd op whose Script becomes "cmd.exe /c {script}" — used to drive the real RunShell process runner.
    private static Tweak CmdTweak(string script) => new()
    {
        Id = "test-cmd",
        Operations =
        {
            new TweakOperation { Type = OperationType.Cmd, Script = script }
        }
    };

    // Two independent registry ops, so a test can fail exactly one and observe partial-failure handling.
    private static Tweak TwoRegTweak(string id, string nameA, string nameB) => new()
    {
        Id = id,
        Operations =
        {
            new TweakOperation { Type = OperationType.Registry, Hive = Hive, Key = Key, Name = nameA,
                                 ValueType = RegistryValueType.DWord, Apply = "1", Revert = "0" },
            new TweakOperation { Type = OperationType.Registry, Hive = Hive, Key = Key, Name = nameB,
                                 ValueType = RegistryValueType.DWord, Apply = "1", Revert = "0" },
        }
    };

    // ---- Registry apply / revert -----------------------------------------

    [Fact]
    public async Task Apply_WritesApplyValue_AndMarksApplied()
    {
        var (svc, reg, _, _, _) = NewService();
        var tweak = RegTweak(apply: "1", revert: "0");

        var result = await svc.ApplyAsync(tweak);

        Assert.True(result.Success);
        Assert.True(tweak.IsApplied);
        Assert.True(reg.TryReadValue(Hive, Key, "Flag", out var v));
        Assert.Equal("1", v);
    }

    [Fact]
    public async Task Revert_WritesRevertValue_AndClearsApplied()
    {
        var (svc, reg, _, _, _) = NewService();
        var tweak = RegTweak(apply: "1", revert: "0");

        await svc.ApplyAsync(tweak);
        var result = await svc.RevertAsync(tweak);

        Assert.True(result.Success);
        Assert.False(tweak.IsApplied);
        reg.TryReadValue(Hive, Key, "Flag", out var v);
        Assert.Equal("0", v);
    }

    [Fact]
    public async Task Apply_WithNullApply_DeletesValue()
    {
        var (svc, reg, _, _, log) = NewService();
        reg.Seed(Hive, Key, "Flag", "preexisting");
        var tweak = RegTweak(apply: null, revert: "0");

        await svc.ApplyAsync(tweak);

        Assert.False(reg.TryReadValue(Hive, Key, "Flag", out _));
        Assert.Contains(log.Events, e => e.StartsWith("reg.delete:"));
    }

    [Fact]
    public async Task Revert_WithNullRevert_DeletesValue()
    {
        var (svc, reg, _, _, log) = NewService();
        var tweak = RegTweak(apply: "1", revert: null);

        await svc.ApplyAsync(tweak);
        Assert.True(reg.TryReadValue(Hive, Key, "Flag", out _));

        await svc.RevertAsync(tweak);

        Assert.False(reg.TryReadValue(Hive, Key, "Flag", out _));
        Assert.Contains(log.Events, e => e.StartsWith("reg.delete:"));
    }

    // ---- Service startup ops ---------------------------------------------

    [Fact]
    public async Task Apply_Service_SetsStartupType_AndRevertRestoresIt()
    {
        var (svc, _, svcMgr, _, _) = NewService();
        var tweak = SvcTweak("DiagTrack", apply: "Disabled", revert: "Automatic");

        await svc.ApplyAsync(tweak);
        Assert.True(svcMgr.TryGetStartupType("DiagTrack", out var applied));
        Assert.Equal("Disabled", applied);

        await svc.RevertAsync(tweak);
        svcMgr.TryGetStartupType("DiagTrack", out var reverted);
        Assert.Equal("Automatic", reverted);
    }

    // ---- IsApplied probe --------------------------------------------------

    [Fact]
    public async Task IsApplied_Applied_WhenRegistryMatchesApply()
    {
        var (svc, reg, _, _, _) = NewService();
        reg.Seed(Hive, Key, "Flag", "1");

        Assert.Equal(TweakAppliedState.Applied, await svc.IsAppliedAsync(RegTweak(apply: "1", revert: "0")));
    }

    [Fact]
    public async Task IsApplied_NotApplied_WhenRegistryDiffers()
    {
        var (svc, reg, _, _, _) = NewService();
        reg.Seed(Hive, Key, "Flag", "0");

        Assert.Equal(TweakAppliedState.NotApplied, await svc.IsAppliedAsync(RegTweak(apply: "1", revert: "0")));
    }

    [Fact]
    public async Task IsApplied_NotApplied_WhenValueMissing()
    {
        var (svc, _, _, _, _) = NewService();
        Assert.Equal(TweakAppliedState.NotApplied, await svc.IsAppliedAsync(RegTweak(apply: "1", revert: "0")));
    }

    [Fact]
    public async Task IsApplied_Service_Applied_WhenStartupTypeMatchesApply()
    {
        var (svc, _, svcMgr, _, _) = NewService();
        svcMgr.Seed("DiagTrack", "Disabled");   // already at the applied startup type

        Assert.Equal(TweakAppliedState.Applied,
            await svc.IsAppliedAsync(SvcTweak("DiagTrack", apply: "Disabled", revert: "Automatic")));
    }

    [Fact]
    public async Task IsApplied_Service_NotApplied_WhenStartupTypeDiffers()
    {
        var (svc, _, svcMgr, _, _) = NewService();
        svcMgr.Seed("DiagTrack", "Automatic");  // not yet disabled → the tweak is NOT applied

        Assert.Equal(TweakAppliedState.NotApplied,
            await svc.IsAppliedAsync(SvcTweak("DiagTrack", apply: "Disabled", revert: "Automatic")));
    }

    [Fact]
    public async Task IsApplied_Applied_WhenDwordWrittenAsHex_ReadsBackAsDecimal()
    {
        // End-to-end honesty: a tweak whose Apply is hex ("0x1") must still read back as applied when the
        // registry returns the decimal form ("1"). Proves the probe compares DWords by value, not by string —
        // otherwise a perfectly-applied hex tweak would forever show "not applied" in the UI.
        var (svc, reg, _, _, _) = NewService();
        reg.Seed(Hive, Key, "Flag", "1");        // how the real registry surfaces a DWord of 1

        Assert.Equal(TweakAppliedState.Applied, await svc.IsAppliedAsync(RegTweak(apply: "0x1", revert: "0x0")));
    }

    [Fact]
    public async Task IsApplied_MultiOp_NotApplied_WhenOnlySomeOpsMatch()
    {
        // The old heuristic stopped at the FIRST op, so a tweak with op A applied but op B still at default
        // would have lit up as "applied". Now every readable op must match — a half-applied tweak reads as
        // NotApplied, never a green check over a system that only changed halfway.
        var (svc, reg, _, _, _) = NewService();
        reg.Seed(Hive, Key, "A", "1");   // first op applied
        reg.Seed(Hive, Key, "B", "0");   // second op NOT applied

        Assert.Equal(TweakAppliedState.NotApplied, await svc.IsAppliedAsync(TwoRegTweak("multi", "A", "B")));
    }

    [Fact]
    public async Task IsApplied_MultiOp_Applied_WhenEveryOpMatches()
    {
        var (svc, reg, _, _, _) = NewService();
        reg.Seed(Hive, Key, "A", "1");
        reg.Seed(Hive, Key, "B", "1");

        Assert.Equal(TweakAppliedState.Applied, await svc.IsAppliedAsync(TwoRegTweak("multi", "A", "B")));
    }

    [Fact]
    public async Task IsApplied_Indeterminate_WhenTweakHasNoReadableOp()
    {
        // A shell-only tweak (PowerShell/Cmd/Bcdedit/AppX/ScheduledTask) has no readback. Honesty: report
        // Indeterminate rather than guessing — the UI must not paint a ✓ or ✗ we can't actually verify.
        var (svc, _, _, _, _) = NewService();

        Assert.Equal(TweakAppliedState.Indeterminate, await svc.IsAppliedAsync(CmdTweak("echo hi")));
    }

    [Fact]
    public async Task IsApplied_Applied_WhenDeleteOnApply_AndValueAbsent()
    {
        // Apply == null means "apply = delete this value". The probe must read ABSENCE as applied — the old
        // code did the opposite and would forever show a correctly-applied delete-tweak as "not applied".
        var (svc, _, _, _, _) = NewService();

        Assert.Equal(TweakAppliedState.Applied, await svc.IsAppliedAsync(RegTweak(apply: null, revert: "0")));
    }

    [Fact]
    public async Task IsApplied_Service_Indeterminate_WhenServiceMissing()
    {
        // Can't read the startup type of a service that isn't installed → we won't claim either way (the old
        // code silently fell through and ended up reporting "not applied", a quiet over-claim).
        var (svc, _, _, _, _) = NewService();

        Assert.Equal(TweakAppliedState.Indeterminate,
            await svc.IsAppliedAsync(SvcTweak("NoSuchService", apply: "Disabled", revert: "Automatic")));
    }

    [Fact]
    public async Task DetectApplied_Batch_ReturnsPerTweakFlags_InInputOrder()
    {
        // The batch primitive the Tweaks page and Dashboard share: one flag per input tweak, in input order, with
        // ONLY Applied → true. NotApplied and Indeterminate both collapse to false — we never light a ✓ we can't
        // verify. One of each here pins all three folds end-to-end through the real registry/shell probe path.
        var (svc, reg, _, _, _) = NewService();
        reg.Seed(Hive, Key, "Flag", "1");                            // first tweak: applied
        var applied = RegTweak(apply: "1", revert: "0");
        var notApplied = RegTweak(id: "off", name: "Off", apply: "1", revert: "0");  // value absent → NotApplied
        var indeterminate = CmdTweak("echo hi");                     // shell-only → Indeterminate

        var flags = await svc.DetectAppliedAsync(new[] { applied, notApplied, indeterminate });

        Assert.Equal(new[] { true, false, false }, flags);
    }

    // ---- DetectAfterRevert: the revert-side twin, with the inverted fold ----

    [Fact]
    public async Task DetectAfterRevert_Batch_FlipsTheQuestion_StillOnIsApplied_OffIsNotApplied()
    {
        // Re-probe a batch right after reverting it, answering "is each tweak OFF now?". The fold is INVERTED vs the
        // apply side: a still-on op makes the tweak Applied (= STILL ACTIVE, the alarming "revert didn't take"), a
        // confirmed-off op reads NotApplied (the clean "reverted"), shell-only stays Indeterminate. One of each here
        // pins all three folds end-to-end through the real registry/shell probe path.
        var (svc, reg, _, _, _) = NewService();
        reg.Seed(Hive, Key, "Flag", "1");                            // still at the applied value → STILL ACTIVE
        var stillActive = RegTweak(apply: "1", revert: "0");
        var reverted = RegTweak(id: "off", name: "Off", apply: "1", revert: "0");  // value absent → confirmed off
        var shellOnly = CmdTweak("echo hi");                         // no readback → Indeterminate

        var states = await svc.DetectAfterRevertAsync(new[] { stillActive, reverted, shellOnly });

        Assert.Equal(
            new[] { TweakAppliedState.Applied, TweakAppliedState.NotApplied, TweakAppliedState.Indeterminate },
            states);
    }

    [Fact]
    public async Task DetectAfterRevert_MultiOp_StillActive_WhenOneOpSurvives_EvenIfAnotherReverted()
    {
        // The honesty crux that makes AggregateAfterRevert a SEPARATE fold from Aggregate: with op A still at its
        // applied value but op B back at default, naively reusing the apply fold ("any off op → NotApplied") would
        // report the tweak fully reverted and MASK that A is still live. The dual fold must surface STILL ACTIVE.
        var (svc, reg, _, _, _) = NewService();
        reg.Seed(Hive, Key, "A", "1");   // op A survived the revert (still applied)
        reg.Seed(Hive, Key, "B", "0");   // op B genuinely reverted

        var states = await svc.DetectAfterRevertAsync(new[] { TwoRegTweak("multi", "A", "B") });

        Assert.Equal(TweakAppliedState.Applied, states[0]);
    }

    [Fact]
    public async Task DetectAfterRevert_MultiOp_NotApplied_WhenEveryOpReverted()
    {
        var (svc, reg, _, _, _) = NewService();
        reg.Seed(Hive, Key, "A", "0");
        reg.Seed(Hive, Key, "B", "0");

        var states = await svc.DetectAfterRevertAsync(new[] { TwoRegTweak("multi", "A", "B") });

        Assert.Equal(TweakAppliedState.NotApplied, states[0]);
    }

    [Fact]
    public async Task PreviewApplyPlan_ReadsRegistryAndServiceCurrentValues_WithoutWritingAnything()
    {
        var (svc, reg, svcMgr, _, log) = NewService();
        reg.Seed(Hive, Key, "Flag", "0");
        svcMgr.Seed("DiagTrack", "Automatic");
        var tweak = new Tweak
        {
            Id = "preview",
            Operations =
            {
                new TweakOperation
                {
                    Type = OperationType.Registry,
                    Hive = Hive,
                    Key = Key,
                    Name = "Flag",
                    ValueType = RegistryValueType.DWord,
                    Apply = "1",
                    Revert = "0"
                },
                new TweakOperation
                {
                    Type = OperationType.Service,
                    ServiceName = "DiagTrack",
                    StartupApply = "Disabled",
                    StartupRevert = "Automatic"
                }
            }
        };

        var plan = await svc.PreviewApplyPlanAsync(new[] { tweak });

        Assert.Equal(2, plan.TotalOperations);
        Assert.Equal("0", plan.Operations[0].Delta.Current);
        Assert.Equal("écrit 1 (DWORD)", plan.Operations[0].Delta.Apply);
        Assert.Equal("Automatic", plan.Operations[1].Delta.Current);
        Assert.Equal("démarrage → Disabled", plan.Operations[1].Delta.Apply);
        Assert.Empty(log.Events); // no restore point, no registry write, no service write
        Assert.Equal("0", reg.Store[$@"{Hive}\{Key}\Flag"]);
        Assert.Equal("Automatic", svcMgr.Startup["DiagTrack"]);
    }

    // ---- The safety net: restore point precedes writes -------------------

    [Fact]
    public async Task ApplyMany_CreatesRestorePoint_BeforeAnyRegistryWrite()
    {
        var (svc, _, _, rp, log) = NewService();
        var tweaks = new[]
        {
            RegTweak(id: "t1", name: "A"),
            RegTweak(id: "t2", name: "B")
        };

        await svc.ApplyManyAsync(tweaks);

        Assert.Single(rp.Created);

        int restoreIdx = log.Events.FindIndex(e => e.StartsWith("restore.create:"));
        int firstWriteIdx = log.Events.FindIndex(e => e.StartsWith("reg.write:"));

        Assert.True(restoreIdx >= 0, "a restore point must be created");
        Assert.True(firstWriteIdx >= 0, "at least one registry write must happen");
        Assert.True(restoreIdx < firstWriteIdx,
            $"restore point (idx {restoreIdx}) must precede the first registry write (idx {firstWriteIdx})");
    }

    [Fact]
    public async Task ApplyMany_WhenRestorePointDisabled_SkipsIt_ButStillApplies()
    {
        // Honesty regression: the Settings toggle must mean what it says. With it OFF, no restore
        // point is created — we don't quietly make one anyway — yet the tweaks still apply.
        var (svc, reg, _, rp, log) = NewService(createRestorePoint: false);
        var tweaks = new[]
        {
            RegTweak(id: "t1", name: "A"),
            RegTweak(id: "t2", name: "B")
        };

        var r = await svc.ApplyManyAsync(tweaks);

        Assert.Equal(2, r.Succeeded);                                        // tweaks still applied
        Assert.Empty(rp.Created);                                            // but NO restore point
        Assert.DoesNotContain(log.Events, e => e.StartsWith("restore.create:"));
        Assert.Contains(log.Events, e => e.StartsWith("reg.write:"));        // writes did happen
    }

    [Fact]
    public async Task ApplyMany_WhenRequiredRestorePointFails_AppliesNothing_AndFlagsIt()
    {
        // The load-bearing reliability fix ("testable sans peur"): with the toggle ON, a restore point that
        // GENUINELY fails (System Restore off/broken — not the 24h throttle, which still exits 0) must ABORT the
        // batch. Applying anyway would strand the user with un-backed changes under a UI that elsewhere claims a
        // point was made — a fabricated-safety lie. Prove zero registry writes and the honest abort flag.
        var (svc, reg, _, rp, log) = NewService();
        rp.ShouldFail = true;
        var tweaks = new[] { RegTweak(id: "t1", name: "A"), RegTweak(id: "t2", name: "B") };

        var r = await svc.ApplyManyAsync(tweaks);

        Assert.True(r.RestorePointFailed);
        Assert.Equal(0, r.Succeeded);
        Assert.Equal(0, r.Failed);
        Assert.Empty(rp.Created);                                            // nothing was actually created
        Assert.Contains(log.Events, e => e.StartsWith("restore.create.fail:"));
        Assert.DoesNotContain(log.Events, e => e.StartsWith("reg.write:"));  // not a single write slipped through
        Assert.Empty(reg.Store);                                             // the box is untouched
    }

    [Fact]
    public async Task ApplyMany_WhenToggleOff_NeverAttemptsRestore_SoAFailingServiceCantBlockApply()
    {
        // The abort is gated on the toggle, not unconditional: with "create a restore point" OFF, the engine never
        // calls the restore service at all, so even a service that WOULD fail can't block a genuine apply.
        var (svc, _, _, rp, log) = NewService(createRestorePoint: false);
        rp.ShouldFail = true;                                  // would fail IF called — but it must not be called
        var tweaks = new[] { RegTweak(id: "t1", name: "A") };

        var r = await svc.ApplyManyAsync(tweaks);

        Assert.False(r.RestorePointFailed);
        Assert.Equal(1, r.Succeeded);
        Assert.DoesNotContain(log.Events, e => e.StartsWith("restore.create"));   // neither success nor fail logged
        Assert.Contains(log.Events, e => e.StartsWith("reg.write:"));
    }

    [Fact]
    public async Task ApplyMany_ReturnsAppliedCount()
    {
        var (svc, _, _, _, _) = NewService();

        var r = await svc.ApplyManyAsync(new[]
        {
            RegTweak(id: "t1", name: "A"),
            RegTweak(id: "t2", name: "B")
        });

        Assert.Equal(2, r.Succeeded);
        Assert.Equal(0, r.Failed);
    }

    [Fact]
    public async Task RevertAll_RevertsEach_AndReturnsCount()
    {
        var (svc, reg, _, _, _) = NewService();
        var t1 = RegTweak(id: "t1", name: "A", apply: "1", revert: "0");
        var t2 = RegTweak(id: "t2", name: "B", apply: "1", revert: "0");
        await svc.ApplyManyAsync(new[] { t1, t2 });

        var r = await svc.RevertAllAsync(new[] { t1, t2 });

        Assert.Equal(2, r.Succeeded);
        reg.TryReadValue(Hive, Key, "A", out var a);
        reg.TryReadValue(Hive, Key, "B", out var b);
        Assert.Equal("0", a);
        Assert.Equal("0", b);
        Assert.False(t1.IsApplied);
        Assert.False(t2.IsApplied);
    }

    // ---- Partial-failure honesty: never claim success an op didn't deliver ----

    [Fact]
    public async Task Apply_WhenAnOperationFails_ReportsFailure_AndDoesNotMarkApplied()
    {
        // The load-bearing honesty fix: a tweak whose 2nd op fails must NOT report success or show as
        // applied — otherwise the UI paints a clean "✓" over a system that only half-changed.
        var (svc, reg, _, _, _) = NewService();
        reg.FailWritesForName.Add("B");                       // the second op's write will fail
        var tweak = TwoRegTweak("partial-apply", "A", "B");

        var result = await svc.ApplyAsync(tweak);

        Assert.False(result.Success);
        Assert.False(tweak.IsApplied);
        Assert.Contains("1/2", result.Error);                // one of two ops failed
        Assert.True(reg.TryReadValue(Hive, Key, "A", out _)); // the op that DID succeed still wrote
    }

    [Fact]
    public async Task Revert_WhenAnOperationFails_ReportsFailure_AndKeepsApplied()
    {
        // Symmetric honesty: a half-reverted tweak still has changes on the box, so it must stay "applied"
        // and report failure — not flip to a fabricated "restored to default".
        var (svc, reg, _, _, _) = NewService();
        var tweak = TwoRegTweak("partial-revert", "A", "B");

        var applied = await svc.ApplyAsync(tweak);
        Assert.True(applied.Success);
        Assert.True(tweak.IsApplied);

        reg.FailWritesForName.Add("B");                       // now the revert of op B will fail
        var result = await svc.RevertAsync(tweak);

        Assert.False(result.Success);
        Assert.True(tweak.IsApplied);                         // still applied — revert didn't fully succeed
        Assert.Contains("1/2", result.Error);
    }

    [Fact]
    public async Task ApplyMany_CountsOnlyFullyAppliedTweaks_WhenOneHasAFailingOp()
    {
        // The dashboard's "N appliquée(s)" must be the count of tweaks that ACTUALLY fully applied, not the
        // number attempted. A tweak with a failing op is not counted and is not marked applied.
        var (svc, reg, _, _, _) = NewService();
        reg.FailWritesForName.Add("Bad");
        var good = RegTweak(id: "good", name: "Good");
        var bad = RegTweak(id: "bad", name: "Bad");

        var r = await svc.ApplyManyAsync(new[] { good, bad });

        Assert.Equal(1, r.Succeeded);                         // only "good" fully applied
        Assert.Equal(1, r.Failed);                            // the failed tweak is counted, not silently dropped
        Assert.True(good.IsApplied);
        Assert.False(bad.IsApplied);
    }

    // ---- RunShell: real-process exit-code semantics (Windows only) ------------

    [Fact]
    public async Task Apply_CmdOp_ExitZero_ReportsSuccess()
    {
        // RunShell is the elevated shell-op runner behind PowerShell/Cmd/Bcdedit/AppX/ScheduledTask and was
        // untested. This spawns a real cmd.exe through the full ApplyAsync → ExecuteAsync → RunShell path to
        // pin the contract the timeout/drain hardening must preserve: exit code 0 = success. cmd.exe → Windows.
        if (!OperatingSystem.IsWindows()) return;
        var (svc, _, _, _, _) = NewService();
        var tweak = CmdTweak("exit 0");

        var result = await svc.ApplyAsync(tweak);

        Assert.True(result.Success);
        Assert.True(tweak.IsApplied);
    }

    [Fact]
    public async Task Apply_CmdOp_ExitNonZero_ReportsFailure()
    {
        // Honesty: a nonzero exit code is a genuine failure and must never be painted as success.
        if (!OperatingSystem.IsWindows()) return;
        var (svc, _, _, _, _) = NewService();
        var tweak = CmdTweak("exit 1");

        var result = await svc.ApplyAsync(tweak);

        Assert.False(result.Success);
        Assert.False(tweak.IsApplied);
        Assert.Contains("1/1", result.Error);
    }
}
