using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the « Instantanés » page's honesty across the capture → compare → reapply loop. The load-bearing case is
/// <see cref="ReapplyRegressions_WhenItDoesntStick_StillShowsTheRegression"/>: the one-click fix routes through the
/// shared apply path, audits the batch, then RE-PROBES and re-compares — so a reapply that failed still shows the
/// regression, never a fabricated success. Driven through fakes (in-memory store that mirrors the real live probe);
/// no disk, no registry.
/// </summary>
public class SnapshotViewModelTests
{
    private static async Task<SnapshotViewModel> NewVm(
        FakeSnapshotService snapshots, FakeTweakRepository repo, RecordingTweakService tweaks,
        RecordingApplyJournal journal, FakeLicenseService? license = null, IEvidenceLedger? evidence = null,
        PreflightBannerViewModel? preflight = null)
    {
        var vm = new SnapshotViewModel(snapshots, repo, tweaks, journal,
            license ?? new FakeLicenseService(), evidence ?? new EvidenceLedger(),
            preflight ?? new PreflightBannerViewModel(new FakePreflightService()));
        await vm.Initialization;   // initial load from the store settles before the test acts
        return vm;
    }

    private static Tweak Tw(string id, TweakTier tier = TweakTier.Tranquille)
        => new() { Id = id, Name = new Dictionary<string, string> { ["fr"] = id }, Tier = tier };

    private static SnapshotEntry Entry(string id, TweakAppliedState state) => new(id, id, state);

    // ---- Pre-flight safety banner ----

    [Fact]
    public async Task Preflight_IsSurfacedOnTheSnapshotPage_FromTheSharedProbe()
    {
        // « Ré-appliquer les régressions » and « aligner » both mutate the live machine through ApplyManyAsync, so this
        // page must forecast the SAME restore-point / pending-reboot posture as the Tweaks and Profiles pages — bound to
        // the one shared banner VM, not a private one. A genuine caution must reach it before any reapply.
        var preflight = new PreflightBannerViewModel(new FakePreflightService
        {
            Verdict = PreflightEvaluator.Evaluate(new PreflightSignals(
                RestorePointRequested: true, SystemRestoreReadable: false, RebootPending: false))
        });
        var repo = new FakeTweakRepository(new[] { Tw("t") });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal(), preflight: preflight);
        await vm.Preflight.Initialization;

        Assert.Same(preflight, vm.Preflight);     // the page exposes the injected shared banner, not a fresh private one
        Assert.True(vm.Preflight.HasCaution);      // and the genuine caution is forecast, not swallowed
    }

    // ---- Capture ----

    [Fact]
    public async Task Capture_AddsSnapshot_ClearsLabel_AndReportsTheRealDetectedState()
    {
        var t = Tw("t");
        var repo = new FakeTweakRepository(new[] { t });
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("t");                       // live on the machine at capture time
        var snaps = new FakeSnapshotService(repo, tweaks);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());
        Assert.True(vm.IsEmpty);
        vm.NewSnapshotLabel = "avant MAJ Windows";

        await vm.CaptureCommand.ExecuteAsync(null);

        Assert.Single(vm.Snapshots);
        Assert.True(vm.HasSnapshots);
        Assert.False(vm.IsEmpty);
        Assert.Equal("avant MAJ Windows", vm.Snapshots[0].Label);
        Assert.Equal(1, vm.Snapshots[0].AppliedCount);          // the capture recorded the genuine probe, not a guess
        Assert.Equal(string.Empty, vm.NewSnapshotLabel);         // input cleared after capture
        Assert.Contains("Instantané capturé", vm.Status);
    }

    // ---- Compare to now ----

    [Fact]
    public async Task CompareToNow_FlagsATweakThatWentInactive_AsARegression()
    {
        var t = Tw("t");
        var repo = new FakeTweakRepository(new[] { t });
        var tweaks = new RecordingTweakService();               // t reads back NotApplied now (default)
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot { Label = "ref", Entries = { Entry("t", TweakAppliedState.Applied) } };
        snaps.Stored.Add(baseline);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());

        await vm.CompareToNowCommand.ExecuteAsync(baseline);

        Assert.True(vm.HasComparison);
        Assert.True(vm.Comparison!.HasRegressions);
        Assert.True(vm.CanReapplyRegressions);                  // the reapply button is live precisely here
        Assert.Equal("t", Assert.Single(vm.Comparison.Regressions).TweakId);
        Assert.Equal("ref", vm.ComparisonBaselineLabel);
    }

    [Fact]
    public async Task CompareToNow_WithNullBaseline_IsAnHonestNoOp()
    {
        var repo = new FakeTweakRepository(new[] { Tw("t") });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());

        await vm.CompareToNowCommand.ExecuteAsync(null);

        Assert.False(vm.HasComparison);
    }

    [Fact]
    public async Task CompareToNow_PublishesTheDiffWithItsLabels_AndCloseClearsIt()
    {
        // The page feeds the unified « preuve »: showing a comparison publishes it (with its baseline/target labels)
        // into the shared ledger, and closing it clears the slot so a stale diff can't be pasted as current.
        var t = Tw("t");
        var repo = new FakeTweakRepository(new[] { t });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot { Label = "ref", Entries = { Entry("t", TweakAppliedState.Applied) } };
        snaps.Stored.Add(baseline);
        var ledger = new EvidenceLedger();
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal(), evidence: ledger);

        await vm.CompareToNowCommand.ExecuteAsync(baseline);

        var published = ledger.Current();
        Assert.True(published.HasSettings);
        Assert.Equal("ref", published.SettingsBaselineLabel);
        Assert.Equal("maintenant", published.SettingsTargetLabel);

        vm.CloseComparisonCommand.Execute(null);
        Assert.False(ledger.Current().HasSettings);
    }

    // ---- Compare two SAVED snapshots (historical A → B drift) ----

    [Fact]
    public async Task CompareTwoSavedSnapshots_DiffsThemHistorically_ButHidesReapply()
    {
        // The pure diff is general — any two snapshots — so a saved A→B compare surfaces a real regression. But
        // neither side is the live machine, so « ré-appliquer » must stay hidden even WITH a regression present:
        // reapplying acts on the current PC, which this historical comparison doesn't describe.
        var t = Tw("t");
        var repo = new FakeTweakRepository(new[] { t });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var before = new SystemSnapshot { Label = "avant MAJ", Entries = { Entry("t", TweakAppliedState.Applied) } };
        var after = new SystemSnapshot { Label = "après MAJ", Entries = { Entry("t", TweakAppliedState.NotApplied) } };
        snaps.Stored.Add(before);
        snaps.Stored.Add(after);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());
        vm.BaselineA = before;
        vm.BaselineB = after;

        vm.CompareSavedCommand.Execute(null);

        Assert.True(vm.HasComparison);
        Assert.True(vm.Comparison!.HasRegressions);          // t went Applied → NotApplied between the two captures
        Assert.False(vm.CanReapplyRegressions);              // …but reapply stays hidden: historical, not « à maintenant »
        Assert.Equal("avant MAJ", vm.ComparisonBaselineLabel);
        Assert.Equal("après MAJ", vm.ComparisonTargetLabel); // header names B, never pretends it's "now"
        Assert.Equal(0, snaps.CaptureLiveCount);             // a historical diff never probes the live machine
    }

    [Fact]
    public async Task CompareSaved_WithOnlyOneSelection_IsAnHonestNoOp()
    {
        var repo = new FakeTweakRepository(new[] { Tw("t") });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var a = new SystemSnapshot { Label = "A", Entries = { Entry("t", TweakAppliedState.Applied) } };
        snaps.Stored.Add(a);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());
        vm.BaselineA = a;                                    // B left unselected

        vm.CompareSavedCommand.Execute(null);

        Assert.False(vm.HasComparison);
        Assert.Equal("Choisis deux instantanés à comparer.", vm.Status);
    }

    [Fact]
    public async Task Delete_OfTheHistoricalCompareTarget_DropsTheStaleComparison()
    {
        // The delete-staleness guard must cover BOTH sides of a historical A→B compare, not only the baseline.
        var t = Tw("t");
        var repo = new FakeTweakRepository(new[] { t });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var a = new SystemSnapshot { Label = "A", Entries = { Entry("t", TweakAppliedState.Applied) } };
        var b = new SystemSnapshot { Label = "B", Entries = { Entry("t", TweakAppliedState.NotApplied) } };
        snaps.Stored.Add(a);
        snaps.Stored.Add(b);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());
        vm.BaselineA = a;
        vm.BaselineB = b;
        vm.CompareSavedCommand.Execute(null);
        Assert.True(vm.HasComparison);

        await vm.DeleteCommand.ExecuteAsync(b);              // delete the TARGET (B) side

        Assert.False(vm.HasComparison);                      // a panel describing a deleted snapshot would be a lie
    }

    // ---- Reapply regressions: the detect → fix → re-verify loop ----

    [Fact]
    public async Task ReapplyRegressions_ReappliesExactlyTheRegressedIds_AuditsThem_AndClearsTheRegressionWhenItSticks()
    {
        var t = Tw("t");
        var other = Tw("other");                                // NotApplied in both snapshots — must not be touched
        var repo = new FakeTweakRepository(new[] { t, other });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot
        {
            Label = "ref",
            Entries = { Entry("t", TweakAppliedState.Applied), Entry("other", TweakAppliedState.NotApplied) }
        };
        snaps.Stored.Add(baseline);
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(snaps, repo, tweaks, journal);
        await vm.CompareToNowCommand.ExecuteAsync(baseline);
        Assert.True(vm.Comparison!.HasRegressions);

        await vm.ReapplyRegressionsCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "t" }, tweaks.Applied.Select(x => x.Id).ToArray());   // ONLY the regressed tweak
        var entry = Assert.Single(journal.Entries);                                 // audited like any apply batch
        Assert.Equal("Application", entry.Action);
        Assert.Equal(new[] { "t" }, entry.TweakIds);
        Assert.Equal(1, entry.Succeeded);
        Assert.False(vm.Comparison!.HasRegressions);            // re-probe shows t live again → loop closed honestly
        Assert.Contains("ré-appliquée", vm.Status);
    }

    [Fact]
    public async Task ReapplyRegressions_WhenItDoesntStick_StillShowsTheRegression()
    {
        // The killer honesty test: the apply path reports failure (and does NOT flip the state), so the mandatory
        // re-probe must still surface the regression. We never paint a green "fixed" over a fix that didn't land.
        var t = Tw("t");
        var repo = new FakeTweakRepository(new[] { t });
        var tweaks = new RecordingTweakService();
        tweaks.FailIds.Add("t");                                // reapply fails; t stays NotApplied
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot { Label = "ref", Entries = { Entry("t", TweakAppliedState.Applied) } };
        snaps.Stored.Add(baseline);
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(snaps, repo, tweaks, journal);
        await vm.CompareToNowCommand.ExecuteAsync(baseline);
        Assert.True(vm.Comparison!.HasRegressions);

        await vm.ReapplyRegressionsCommand.ExecuteAsync(null);

        Assert.True(vm.Comparison!.HasRegressions);             // still there — honest about the failed reapply
        Assert.Contains("échec", vm.Status);
        var entry = Assert.Single(journal.Entries);             // and the failed attempt is still audited
        Assert.Equal(0, entry.Succeeded);
        Assert.Equal(1, entry.Failed);
    }

    [Fact]
    public async Task ReapplyRegressions_WhenRequiredRestorePointFails_ShowsHonestReason_AppliesNothing_KeepsTheRegression_AndDoesNotJournal()
    {
        // Re-applying is an apply direction, so a failed required restore point aborts it like every other surface:
        // honest reason, nothing applied, no audit entry, and — critically — the regression stays on screen (the
        // machine is untouched, so we never paint a "fixed" we didn't earn).
        var t = Tw("t");
        var repo = new FakeTweakRepository(new[] { t });
        var tweaks = new RecordingTweakService { RestorePointWillFail = true };
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot { Label = "ref", Entries = { Entry("t", TweakAppliedState.Applied) } };
        snaps.Stored.Add(baseline);
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(snaps, repo, tweaks, journal);
        await vm.CompareToNowCommand.ExecuteAsync(baseline);
        Assert.True(vm.Comparison!.HasRegressions);

        await vm.ReapplyRegressionsCommand.ExecuteAsync(null);

        Assert.Equal(TweakApplyText.RestorePointFailed, vm.Status);
        Assert.Empty(tweaks.Applied);                       // nothing applied
        Assert.True(vm.Comparison!.HasRegressions);         // regression still shown — no fabricated fix
        Assert.Empty(journal.Entries);                      // no audit entry (the re-compare is skipped too)
    }

    [Fact]
    public async Task ReapplyRegressions_WithNoActiveComparison_IsAnHonestNoOp()
    {
        var repo = new FakeTweakRepository(new[] { Tw("t") });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());

        await vm.ReapplyRegressionsCommand.ExecuteAsync(null);

        Assert.Empty(tweaks.Applied);                           // nothing to reapply → no backend work
        Assert.Equal("Aucune régression à ré-appliquer.", vm.Status);
    }

    // ---- Align to snapshot: the full two-direction match ----

    [Fact]
    public async Task Align_ReappliesRegressions_AndRevertsImprovements_ToMatchTheSnapshot_ThenReprobes()
    {
        // The capstone: a live diff with BOTH a regression (tA off now, on in the snapshot) and an improvement
        // (tB on now, off in the snapshot). Aligning must drive BOTH directions — re-apply tA, revert tB — then
        // re-probe so the panel reflects reality. After a clean align the machine matches the snapshot exactly.
        var tA = Tw("tA");
        var tB = Tw("tB");
        var repo = new FakeTweakRepository(new[] { tA, tB });
        var tweaks = new RecordingTweakService();
        tB.IsApplied = true;                                    // tB is live now but was OFF in the baseline → improvement
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot
        {
            Label = "ref",
            Entries = { Entry("tA", TweakAppliedState.Applied), Entry("tB", TweakAppliedState.NotApplied) }
        };
        snaps.Stored.Add(baseline);
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(snaps, repo, tweaks, journal);
        await vm.CompareToNowCommand.ExecuteAsync(baseline);
        Assert.True(vm.Comparison!.HasRegressions);             // tA: Applied → NotApplied
        Assert.True(vm.Comparison!.HasImprovements);            // tB: NotApplied → Applied
        Assert.True(vm.CanAlignToSnapshot);                     // both directions present → align is offered

        await vm.AlignToSnapshotCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "tA" }, tweaks.Applied.Select(x => x.Id).ToArray());   // re-applied the regression
        Assert.Equal(new[] { "tB" }, tweaks.Reverted.Select(x => x.Id).ToArray());  // undid the improvement
        // Re-probe: tA on again, tB off again → the machine now matches the snapshot, no drift left.
        Assert.False(vm.Comparison!.HasAnyChange);
        Assert.Equal(2, vm.Comparison!.UnchangedCount);
        // BOTH batches audited, each carrying exactly its own ids under its own action.
        Assert.Equal(2, journal.Entries.Count);
        Assert.Equal(new[] { "tA" }, journal.Entries.Single(e => e.Action == "Application").TweakIds);
        Assert.Equal(new[] { "tB" }, journal.Entries.Single(e => e.Action == "Restauration").TweakIds);
        Assert.Contains("Alignement", vm.Status);
    }

    [Fact]
    public async Task Align_WhenRequiredRestorePointFails_AbortsBothDirections_AppliesAndRevertsNothing_AndDoesNotJournal()
    {
        // The align creates ONE restore point (via the apply direction) that also covers the subsequent revert. If it
        // can't be created, we abort the WHOLE align before the revert too — no half-aligned machine, no mutation
        // without the safety net. Honest reason, neither direction ran, no audit, and the drift stays on screen.
        var tA = Tw("tA");
        var tB = Tw("tB");
        var repo = new FakeTweakRepository(new[] { tA, tB });
        var tweaks = new RecordingTweakService { RestorePointWillFail = true };
        tB.IsApplied = true;                                // improvement: on now, off in the baseline
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot
        {
            Label = "ref",
            Entries = { Entry("tA", TweakAppliedState.Applied), Entry("tB", TweakAppliedState.NotApplied) }
        };
        snaps.Stored.Add(baseline);
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(snaps, repo, tweaks, journal);
        await vm.CompareToNowCommand.ExecuteAsync(baseline);
        Assert.True(vm.CanAlignToSnapshot);                 // both directions present

        await vm.AlignToSnapshotCommand.ExecuteAsync(null);

        Assert.Equal(TweakApplyText.RestorePointFailed, vm.Status);
        Assert.Empty(tweaks.Applied);                       // apply direction touched nothing
        Assert.Empty(tweaks.Reverted);                      // and the revert never ran either
        Assert.True(vm.Comparison!.HasRegressions);         // both directions still pending → drift preserved
        Assert.True(vm.Comparison!.HasImprovements);
        Assert.Empty(journal.Entries);                      // nothing audited
    }

    [Fact]
    public async Task Align_IsHiddenWhenThereAreOnlyRegressions_SoItNeverDuplicatesReapply()
    {
        // With ONLY a regression (nothing switched on since the capture), « ré-appliquer » already does the whole job.
        // Offering « aligner » too would be a redundant button → it must stay hidden, even though reapply is live.
        var t = Tw("t");
        var repo = new FakeTweakRepository(new[] { t });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot { Label = "ref", Entries = { Entry("t", TweakAppliedState.Applied) } };
        snaps.Stored.Add(baseline);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());

        await vm.CompareToNowCommand.ExecuteAsync(baseline);

        Assert.True(vm.CanReapplyRegressions);                  // there IS a regression to re-apply
        Assert.False(vm.CanAlignToSnapshot);                    // …but no improvement to undo → align would be redundant
    }

    [Fact]
    public async Task Align_OnAHistoricalComparison_IsHiddenAndANoOp()
    {
        // A saved A→B diff doesn't describe the live machine, so aligning onto it would act blindly. The button is
        // hidden AND the command refuses, touching no backend — the same « à maintenant » gate as « ré-appliquer ».
        var tA = Tw("tA");
        var tB = Tw("tB");
        var repo = new FakeTweakRepository(new[] { tA, tB });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var before = new SystemSnapshot
        {
            Label = "avant",
            Entries = { Entry("tA", TweakAppliedState.Applied), Entry("tB", TweakAppliedState.NotApplied) }
        };
        var after = new SystemSnapshot
        {
            Label = "après",
            Entries = { Entry("tA", TweakAppliedState.NotApplied), Entry("tB", TweakAppliedState.Applied) }
        };
        snaps.Stored.Add(before);
        snaps.Stored.Add(after);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());
        vm.BaselineA = before;
        vm.BaselineB = after;
        vm.CompareSavedCommand.Execute(null);
        Assert.True(vm.Comparison!.HasRegressions);             // there IS drift (tA) …
        Assert.True(vm.Comparison!.HasImprovements);            // … and an improvement (tB) …
        Assert.False(vm.CanAlignToSnapshot);                    // … but it's historical, so align stays hidden

        await vm.AlignToSnapshotCommand.ExecuteAsync(null);

        Assert.Empty(tweaks.Applied);                           // the gate held: no apply …
        Assert.Empty(tweaks.Reverted);                          // … and no revert
        Assert.Equal("Aligner n'est possible que pour une comparaison « à maintenant ».", vm.Status);
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_OfTheComparedBaseline_DropsTheNowStaleComparison()
    {
        var t = Tw("t");
        var repo = new FakeTweakRepository(new[] { t });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot { Label = "ref", Entries = { Entry("t", TweakAppliedState.Applied) } };
        snaps.Stored.Add(baseline);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());
        await vm.CompareToNowCommand.ExecuteAsync(baseline);
        Assert.True(vm.HasComparison);

        await vm.DeleteCommand.ExecuteAsync(baseline);

        Assert.Empty(vm.Snapshots);
        Assert.False(vm.HasComparison);                         // a panel describing a deleted baseline would be a lie
    }

    [Fact]
    public async Task Delete_OfADifferentSnapshot_LeavesTheActiveComparisonStanding()
    {
        // The "drop stale comparison" guard must be precise: deleting an unrelated snapshot must NOT tear down a
        // comparison the user is still reading.
        var t = Tw("t");
        var repo = new FakeTweakRepository(new[] { t });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot { Label = "A", Entries = { Entry("t", TweakAppliedState.Applied) } };
        var other = new SystemSnapshot { Label = "B", Entries = { Entry("t", TweakAppliedState.Applied) } };
        snaps.Stored.Add(baseline);
        snaps.Stored.Add(other);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());
        await vm.CompareToNowCommand.ExecuteAsync(baseline);

        await vm.DeleteCommand.ExecuteAsync(other);

        Assert.True(vm.HasComparison);                          // baseline's comparison survives the unrelated delete
        Assert.Single(vm.Snapshots);
    }

    // ---- Reload ----

    [Fact]
    public async Task Reload_PicksUpASnapshotCapturedOutsideThisViewModel()
    {
        // The VM is a singleton; reloading when the page is shown must reflect a capture made on an earlier visit
        // (or a deletion), otherwise the list silently lies until relaunch.
        var repo = new FakeTweakRepository(new[] { Tw("t") });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());
        Assert.True(vm.IsEmpty);

        snaps.Stored.Add(new SystemSnapshot { Label = "ailleurs" });   // captured elsewhere, not via this VM
        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.Single(vm.Snapshots);
        Assert.False(vm.IsEmpty);
    }

    // ---- Import a portable snapshot file (the parse/validate honesty is pinned on SnapshotPortability) ----

    [Fact]
    public async Task Import_FromAFile_AddsItToTheListAndReportsIt()
    {
        var repo = new FakeTweakRepository(new[] { Tw("t") });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        snaps.ImportToReturn = new SystemSnapshot
        {
            Label = "baseline partagé",
            Entries = { Entry("t", TweakAppliedState.Applied) }
        };
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());
        Assert.True(vm.IsEmpty);

        await vm.ImportFromPathAsync("anywhere.json");

        Assert.Single(vm.Snapshots);
        Assert.False(vm.IsEmpty);
        Assert.Equal("baseline partagé", vm.Snapshots[0].Label);   // the imported snapshot joins the list
        Assert.Contains("importé", vm.Status);
    }

    [Fact]
    public async Task Import_OfABadFile_ShowsTheReason_AndLeavesTheListUntouched()
    {
        // A rejected file must surface the service's French reason and NEVER half-fill the list with an
        // uncomparable record — the honesty rule the whole import path exists to keep.
        var repo = new FakeTweakRepository(new[] { Tw("t") });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        snaps.ImportErrorMessage = "Fichier d'instantané illisible : JSON invalide.";
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal());

        await vm.ImportFromPathAsync("broken.json");

        Assert.True(vm.IsEmpty);
        Assert.Equal("Fichier d'instantané illisible : JSON invalide.", vm.Status);
    }

    // ---- Freemium gate: « ré-appliquer » and « aligner » turn tweaks back ON, so they obey the same tier lock as
    //      every other apply surface. A regressed/aligned tweak can be Avancé/Extreme (Premium), so a configured Free
    //      build refuses those while an unconfigured build (the as-shipped default, used by every test above through the
    //      bare NewVm) keeps doing everything. The load-bearing case is the align ASYMMETRY: reverting an improvement
    //      turns a tweak OFF and is NEVER gated, so a Free build still undoes Premium improvements while declining to
    //      re-apply Premium regressions. ----

    [Fact]
    public async Task ReapplyRegressions_ConfiguredFree_RefusesAPremiumRegression_AndPointsToLicence()
    {
        var premium = Tw("p", TweakTier.Avance);                // Premium-only; reads back NotApplied now → regression
        var repo = new FakeTweakRepository(new[] { premium });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot { Label = "ref", Entries = { Entry("p", TweakAppliedState.Applied) } };
        snaps.Stored.Add(baseline);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal(),
                             new FakeLicenseService(AppEdition.Free, configured: true));
        await vm.CompareToNowCommand.ExecuteAsync(baseline);
        Assert.True(vm.Comparison!.HasRegressions);

        await vm.ReapplyRegressionsCommand.ExecuteAsync(null);

        Assert.Empty(tweaks.Applied);                           // the gate refused — nothing re-applied
        Assert.True(vm.Comparison!.HasRegressions);             // and it stays regressed, honestly (no re-probe either)
        Assert.Contains("réservé(s) à Premium", vm.Status);
        Assert.Contains("Licence", vm.Status);
    }

    [Fact]
    public async Task ReapplyRegressions_ConfiguredFree_ReappliesTheFreeOne_AndDisclosesTheLockedPremium()
    {
        var free = Tw("free");                                  // Tranquille
        var premium = Tw("premium", TweakTier.Avance);
        var repo = new FakeTweakRepository(new[] { free, premium });
        var tweaks = new RecordingTweakService();               // both read back NotApplied now → both regressed
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot
        {
            Label = "ref",
            Entries = { Entry("free", TweakAppliedState.Applied), Entry("premium", TweakAppliedState.Applied) }
        };
        snaps.Stored.Add(baseline);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal(),
                             new FakeLicenseService(AppEdition.Free, configured: true));
        await vm.CompareToNowCommand.ExecuteAsync(baseline);

        await vm.ReapplyRegressionsCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "free" }, tweaks.Applied.Select(x => x.Id).ToArray());   // only the free regression landed
        Assert.StartsWith("1 régression(s) ré-appliquée(s)", vm.Status);
        Assert.Contains("1 réservé(s) à Premium", vm.Status);
    }

    [Fact]
    public async Task ReapplyRegressions_ConfiguredPremium_ReappliesThePremiumRegression()
    {
        var premium = Tw("p", TweakTier.Avance);
        var repo = new FakeTweakRepository(new[] { premium });
        var tweaks = new RecordingTweakService();
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot { Label = "ref", Entries = { Entry("p", TweakAppliedState.Applied) } };
        snaps.Stored.Add(baseline);
        var vm = await NewVm(snaps, repo, tweaks, new RecordingApplyJournal(),
                             new FakeLicenseService(AppEdition.Premium, configured: true));
        await vm.CompareToNowCommand.ExecuteAsync(baseline);

        await vm.ReapplyRegressionsCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "p" }, tweaks.Applied.Select(x => x.Id).ToArray());   // a paid build unlocks the reapply
        Assert.DoesNotContain("réservé(s) à Premium", vm.Status);
    }

    [Fact]
    public async Task Align_ConfiguredFree_StillRevertsAPremiumImprovement_ButDoesNotReapplyThePremiumRegression()
    {
        // The asymmetry, end to end: a live diff with a Premium regression (pReg, on in the snapshot, off now) AND a
        // Premium improvement (pImp, off in the snapshot, on now). On a configured Free build, aligning must REFUSE the
        // apply direction (re-applying pReg) but still RUN the revert direction (undoing pImp) — because backing a tweak
        // OUT is never gated. The withheld apply is disclosed; only the revert batch is audited.
        var pReg = Tw("pReg", TweakTier.Avance);
        var pImp = Tw("pImp", TweakTier.Avance);
        var repo = new FakeTweakRepository(new[] { pReg, pImp });
        var tweaks = new RecordingTweakService();
        pImp.IsApplied = true;                                  // live now, off in baseline → improvement
        var snaps = new FakeSnapshotService(repo, tweaks);
        var baseline = new SystemSnapshot
        {
            Label = "ref",
            Entries = { Entry("pReg", TweakAppliedState.Applied), Entry("pImp", TweakAppliedState.NotApplied) }
        };
        snaps.Stored.Add(baseline);
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(snaps, repo, tweaks, journal,
                             new FakeLicenseService(AppEdition.Free, configured: true));
        await vm.CompareToNowCommand.ExecuteAsync(baseline);
        Assert.True(vm.Comparison!.HasRegressions);
        Assert.True(vm.Comparison!.HasImprovements);

        await vm.AlignToSnapshotCommand.ExecuteAsync(null);

        Assert.Empty(tweaks.Applied);                                                  // Premium regression NOT re-applied
        Assert.Equal(new[] { "pImp" }, tweaks.Reverted.Select(x => x.Id).ToArray());   // but Premium improvement undone
        var entry = Assert.Single(journal.Entries);                                    // only the revert batch ran/audited
        Assert.Equal("Restauration", entry.Action);
        Assert.Equal(new[] { "pImp" }, entry.TweakIds);
        Assert.Contains("réservé(s) à Premium", vm.Status);                           // the withheld apply, disclosed
    }
}
