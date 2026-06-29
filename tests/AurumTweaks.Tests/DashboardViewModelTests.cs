using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the dashboard's "apply the recommended set" honesty contract — the UI-layer counterpart to the
/// restore-point safety net that <c>TweakServiceTests</c> already pins at the backend. The dashboard is the
/// one-click front door, so its status line is the message most users actually read. It must never:
///   • claim "Un point de restauration a été créé" when the Settings toggle is OFF (the backend genuinely
///     skips it — so saying it happened would be a fabricated safety claim);
///   • fabricate an "appliquée(s)" count — the number must equal what the backend actually applied;
///   • pretend it did work when the recommended set was already fully applied (no-op honesty).
/// Driven entirely through fakes (no WMI / registry / restore API): the VM's ctor InitialiseAsync completes
/// synchronously because every fake returns a completed Task, so state is deterministic right after <c>new</c>.
/// </summary>
public class DashboardViewModelTests
{
    private static DashboardViewModel NewVm(
        FakeAppSettingsStore settings,
        RecordingTweakService tweaks,
        FakeAdaptiveRecommendationService? adaptive = null,
        FakeTweakRepository? repo = null,
        RecordingApplyJournal? journal = null,
        FakeScoreHistoryStore? scoreHistory = null,
        FakeLicenseService? license = null,
        IEvidenceLedger? evidence = null)
        => new(
            new FakeHardwareService(new HardwareInfo()),
            new FakeMonitoringService(),
            adaptive ?? new FakeAdaptiveRecommendationService(),
            settings,
            tweaks,
            repo ?? new FakeTweakRepository(System.Array.Empty<Tweak>()),
            new FakeLocalizationService(),
            journal ?? new RecordingApplyJournal(),
            new FakePowerPlanService(),
            new FakeTimerResolutionService(),
            new FakePendingRebootService(),
            new FakeDriveHealthService(),
            scoreHistory ?? new FakeScoreHistoryStore(),
            license ?? new FakeLicenseService(),
            evidence ?? new EvidenceLedger(),
            new PreflightBannerViewModel(new FakePreflightService()));

    // ---- The load-bearing claim: a restore point is only announced when it truly happened ----

    [Fact]
    public async Task ApplyRecommended_WhenRestorePointToggleOn_ClaimsTheRestorePointWasCreated()
    {
        var settings = new FakeAppSettingsStore();          // restore point ON by default
        var tweaks = new RecordingTweakService();
        var vm = NewVm(settings, tweaks);

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Single(tweaks.Applied);                      // the button really applied via the backend
        Assert.Contains("Un point de restauration a été créé", vm.PlanStatus);
    }

    [Fact]
    public async Task ApplyRecommended_WhenRestorePointToggleOff_AdmitsNoRestorePointWasCreated()
    {
        var settings = new FakeAppSettingsStore();
        settings.Current.CreateRestorePointBeforeTweaks = false;  // user disabled the safety net
        var tweaks = new RecordingTweakService();
        var vm = NewVm(settings, tweaks);

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Single(tweaks.Applied);                      // tweaks still applied...
        // ...but the UI must NOT claim a restore point was made...
        Assert.DoesNotContain("Un point de restauration a été créé", vm.PlanStatus);
        // ...and must state plainly why there isn't one.
        Assert.Contains("sans point de restauration (option désactivée dans Paramètres)", vm.PlanStatus);
    }

    [Fact]
    public async Task ApplyRecommended_WhenRequiredRestorePointFails_ShowsHonestReason_AppliesNothing_ResetsBusy_AndDoesNotJournal()
    {
        // The one-click front door must abort on a failed required restore point too — never silently apply unprotected.
        // Honest reason, nothing applied, the busy flag reset, and no audit entry (nothing happened on the machine).
        var settings = new FakeAppSettingsStore();          // restore point ON by default
        var tweaks = new RecordingTweakService { RestorePointWillFail = true };
        var journal = new RecordingApplyJournal();
        var vm = NewVm(settings, tweaks, journal: journal);

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Equal(TweakApplyText.RestorePointFailed, vm.PlanStatus);
        Assert.Empty(tweaks.Applied);                       // nothing applied
        Assert.False(vm.IsApplyingPlan);                    // not stuck busy
        Assert.Empty(journal.Entries);                      // no audit entry
    }

    // ---- The count is real, not invented ----

    [Fact]
    public async Task ApplyRecommended_ReportsTheRealAppliedCountFromTheBackend()
    {
        // Two recommended, unapplied tweaks → the status count must equal what the backend applied (2).
        var t1 = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var t2 = new Tweak { Id = "b", Name = new() { ["fr"] = "B" } };
        var plan = new AdaptivePlan
        {
            Recommendations = new[]
            {
                new TweakRecommendation { Tweak = t1, InDefaultSet = true },
                new TweakRecommendation { Tweak = t2, InDefaultSet = true }
            },
            RecommendedCount = 2,
            TotalApplicable = 2
        };
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new FakeAppSettingsStore(), tweaks, new FakeAdaptiveRecommendationService(plan));

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Equal(2, tweaks.Applied.Count);
        Assert.StartsWith("2 optimisation(s) appliquée(s)", vm.PlanStatus);
    }

    // ---- Partial failure is admitted, never hidden behind a smaller success count ----

    [Fact]
    public async Task ApplyRecommended_WhenSomeFail_LeadsWithAppliedCount_AndAdmitsTheFailures()
    {
        // Two recommended tweaks, one backend failure → the status must lead with the real applied count (1)
        // AND state the failure count, so a half-failed batch never reads like a clean run of a smaller set.
        var ok = new Tweak { Id = "ok", Name = new() { ["fr"] = "OK" } };
        var bad = new Tweak { Id = "bad", Name = new() { ["fr"] = "Bad" } };
        var plan = new AdaptivePlan
        {
            Recommendations = new[]
            {
                new TweakRecommendation { Tweak = ok, InDefaultSet = true },
                new TweakRecommendation { Tweak = bad, InDefaultSet = true }
            },
            RecommendedCount = 2,
            TotalApplicable = 2
        };
        var tweaks = new RecordingTweakService();
        tweaks.FailIds.Add("bad");                          // the backend reports this one as failed
        var vm = NewVm(new FakeAppSettingsStore(), tweaks, new FakeAdaptiveRecommendationService(plan));

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.StartsWith("1 optimisation(s) appliquée(s)", vm.PlanStatus);   // the real success count
        Assert.Contains("1 échec(s)", vm.PlanStatus);                         // and the failure is admitted
    }

    // ---- No-op honesty: nothing left to apply → no "applied"/restore claim at all ----

    [Fact]
    public async Task ApplyRecommended_WhenNothingLeftToApply_DoesNotFabricateAnAppliedOrRestoreClaim()
    {
        var alreadyApplied = new Tweak { Id = "already", Name = new() { ["fr"] = "Déjà" }, IsApplied = true };
        var plan = new AdaptivePlan
        {
            Recommendations = new[] { new TweakRecommendation { Tweak = alreadyApplied, InDefaultSet = true } },
            RecommendedCount = 1,
            TotalApplicable = 1
        };
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new FakeAppSettingsStore(), tweaks, new FakeAdaptiveRecommendationService(plan));

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Empty(tweaks.Applied);                                  // backend never touched
        Assert.DoesNotContain("point de restauration", vm.PlanStatus); // no safety-net claim when idle
        Assert.Contains("déjà appliqué", vm.PlanStatus);
    }

    // ---- Detection honesty: a tweak already live on the machine is seen as done, not re-applied ----

    [Fact]
    public async Task RefreshPlan_DetectsAlreadyLiveTweak_SoApplyRecommendedTreatsItAsDone()
    {
        // The gap this closes: BuildPlanAsync reads Tweak.IsApplied (for PotentialScore, ranking, and the apply
        // set), but the dashboard never probed the machine — so a tweak already live (applied last week, survived
        // a reboot) looked un-applied on every launch, PotentialScore counted work already done, and "Appliquer le
        // set" would re-run it. The dashboard now detects FIRST. The fake repo and the canned plan share the SAME
        // Tweak instance, exactly as the singleton TweakRepository wires production — so the IsApplied flags
        // detection writes are precisely what the plan reads.
        var live = new Tweak { Id = "already-live", Name = new() { ["fr"] = "Déjà actif" } };  // IsApplied=false in-memory
        var plan = new AdaptivePlan
        {
            Recommendations = new[] { new TweakRecommendation { Tweak = live, InDefaultSet = true } },
            RecommendedCount = 1,
            TotalApplicable = 1
        };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("already-live");                 // the backend reports it as live on the system
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(plan),
                       new FakeTweakRepository(new[] { live }));     // the very instance the plan recommends

        Assert.True(live.IsApplied);                                 // load-time detection lit it, not the in-memory flag

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Empty(tweaks.Applied);                                // nothing re-applied — it was already done
        Assert.Contains("déjà appliqué", vm.PlanStatus);
    }

    // ---- Change journal: the one-click "Appliquer le set" IS an apply, so the audit trail must record it (the
    //      journal page promises "every application is recorded" — silence here would make that claim false) ----

    [Fact]
    public async Task ApplyRecommended_RecordsAnApplicationEntry_WithTheAppliedIds()
    {
        var t1 = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var t2 = new Tweak { Id = "b", Name = new() { ["fr"] = "B" } };
        var plan = new AdaptivePlan
        {
            Recommendations = new[]
            {
                new TweakRecommendation { Tweak = t1, InDefaultSet = true },
                new TweakRecommendation { Tweak = t2, InDefaultSet = true }
            },
            RecommendedCount = 2,
            TotalApplicable = 2
        };
        var journal = new RecordingApplyJournal();
        var vm = NewVm(new FakeAppSettingsStore(), new RecordingTweakService(),
                       new FakeAdaptiveRecommendationService(plan), journal: journal);

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("Application", entry.Action);
        Assert.Equal(2, entry.Succeeded);
        Assert.Equal(new[] { "a", "b" }, entry.TweakIds);   // the applied set, in apply order
        Assert.Empty(entry.Unconfirmed);                    // every write read back live → verification ran, found all clean
        Assert.NotNull(vm.LastVerification);                // the Dashboard now verifies its front-door apply too
        Assert.False(vm.HasUnconfirmed);                    // …and a clean apply raises no banner
    }

    [Fact]
    public async Task ApplyRecommended_WhenAWriteDoesntStick_FlagsItInBannerAndJournal()
    {
        // The front-door mirror of the Tweaks page check: the engine reports both applied, but the live machine reads
        // "stuck" back as not active. Honesty: the status keeps the truthful applied count, while the divergence
        // surfaces through the verification banner (HasUnconfirmed) AND the durable journal — never a fabricated ✓.
        var ok = new Tweak { Id = "ok", Name = new() { ["fr"] = "OK" } };
        var stuck = new Tweak { Id = "stuck", Name = new() { ["fr"] = "Stuck" } };
        var plan = new AdaptivePlan
        {
            Recommendations = new[]
            {
                new TweakRecommendation { Tweak = ok, InDefaultSet = true },
                new TweakRecommendation { Tweak = stuck, InDefaultSet = true }
            },
            RecommendedCount = 2,
            TotalApplicable = 2
        };
        var tweaks = new RecordingTweakService();
        tweaks.NotConfirmedIds.Add("stuck");                // applied, but doesn't read back as live
        var journal = new RecordingApplyJournal();
        var vm = NewVm(new FakeAppSettingsStore(), tweaks, new FakeAdaptiveRecommendationService(plan), journal: journal);

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.True(vm.HasUnconfirmed);
        Assert.Equal("stuck", vm.LastVerification!.UnconfirmedLabel);
        var entry = Assert.Single(journal.Entries);
        Assert.Equal(new[] { "stuck" }, entry.Unconfirmed);   // the durable trail records the "didn't stick" too
    }

    [Fact]
    public async Task ApplyRecommended_WhenAWriteFails_IsNotMislabeledAsUnconfirmed()
    {
        // The honesty fix the shared ITweakService.VerifyAppliedAsync brought to the front door. This surface SHIPPED
        // verifying ALL of `allowed`, so a tweak the engine genuinely FAILED (IsApplied never set) read back as not
        // active and was wrongly flagged « didn't stick » — a fabricated alarm over a real, already-admitted failure.
        // The shared method re-probes ONLY what actually applied, so the failure is excluded; only a write that DID
        // land then reads back wrong earns the banner (the test above). The failure still shows in the count/journal.
        var ok = new Tweak { Id = "ok", Name = new() { ["fr"] = "OK" } };
        var bad = new Tweak { Id = "bad", Name = new() { ["fr"] = "Bad" } };
        var plan = new AdaptivePlan
        {
            Recommendations = new[]
            {
                new TweakRecommendation { Tweak = ok, InDefaultSet = true },
                new TweakRecommendation { Tweak = bad, InDefaultSet = true }
            },
            RecommendedCount = 2,
            TotalApplicable = 2
        };
        var tweaks = new RecordingTweakService();
        tweaks.FailIds.Add("bad");                          // a genuine apply failure → IsApplied stays false
        var journal = new RecordingApplyJournal();
        var vm = NewVm(new FakeAppSettingsStore(), tweaks, new FakeAdaptiveRecommendationService(plan), journal: journal);

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.False(vm.HasUnconfirmed);                                 // the failure is NOT a fabricated « didn't stick »
        Assert.NotNull(vm.LastVerification);                             // the write that DID land was still verified…
        Assert.Equal(new[] { "ok" }, vm.LastVerification!.Confirmed);    // …and only the genuine success is confirmed
        var entry = Assert.Single(journal.Entries);
        Assert.Empty(entry.Unconfirmed);                                 // the durable trail carries no unconfirmed write
        Assert.Equal(1, entry.Failed);                                   // while the failure is still admitted, honestly
    }

    [Fact]
    public async Task ApplyRecommended_WhenNothingToApply_RecordsNothing()
    {
        // Honest no-op end to end: an already-applied set touches the backend in no way AND journals nothing —
        // never a fabricated "Application" entry for a click that changed the system in no way.
        var alreadyApplied = new Tweak { Id = "already", Name = new() { ["fr"] = "Déjà" }, IsApplied = true };
        var plan = new AdaptivePlan
        {
            Recommendations = new[] { new TweakRecommendation { Tweak = alreadyApplied, InDefaultSet = true } },
            RecommendedCount = 1,
            TotalApplicable = 1
        };
        var journal = new RecordingApplyJournal();
        var vm = NewVm(new FakeAppSettingsStore(), new RecordingTweakService(),
                       new FakeAdaptiveRecommendationService(plan), journal: journal);

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Empty(journal.Entries);
    }

    // ---- Discoverability: the dashboard surfaces the newest journal entries, so the audit trail is reachable
    //      from the front door (it's otherwise only on the nav rail / Ctrl+K) ----

    private static JournalEntry JEntry(string action, params string[] ids)
        => new(System.DateTime.UtcNow, action, ids.Length, 0, ids, System.Array.Empty<string>());

    [Fact]
    public async Task Dashboard_SurfacesRecentJournalActivity_NewestFirst()
    {
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(JEntry("Application", "a"));
        await journal.RecordAsync(JEntry("Restauration", "a"));   // newest
        var vm = NewVm(new FakeAppSettingsStore(), new RecordingTweakService(), journal: journal);

        Assert.True(vm.HasRecentActivity);
        Assert.Equal(2, vm.RecentActivity.Count);
        Assert.Equal("Restauration", vm.RecentActivity[0].Action);   // newest-first, mirroring the store
        Assert.Equal("Application", vm.RecentActivity[1].Action);
    }

    [Fact]
    public void Dashboard_WithEmptyJournal_HasNoRecentActivity()
    {
        var vm = NewVm(new FakeAppSettingsStore(), new RecordingTweakService());

        Assert.False(vm.HasRecentActivity);   // the card stays hidden rather than showing an empty shell
        Assert.Empty(vm.RecentActivity);
    }

    [Fact]
    public async Task Dashboard_RecentActivity_AppearsAfterApplyingTheRecommendedSet()
    {
        var t1 = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var plan = new AdaptivePlan
        {
            Recommendations = new[] { new TweakRecommendation { Tweak = t1, InDefaultSet = true } },
            RecommendedCount = 1,
            TotalApplicable = 1
        };
        var journal = new RecordingApplyJournal();
        var vm = NewVm(new FakeAppSettingsStore(), new RecordingTweakService(),
                       new FakeAdaptiveRecommendationService(plan), journal: journal);
        Assert.False(vm.HasRecentActivity);   // nothing applied yet

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.True(vm.HasRecentActivity);
        var entry = Assert.Single(vm.RecentActivity);
        Assert.Equal("Application", entry.Action);   // the apply we just journaled shows up immediately
    }

    [Fact]
    public async Task Dashboard_RecentActivity_IsCappedToTheNewestFew()
    {
        var journal = new RecordingApplyJournal();
        for (var i = 0; i < 7; i++)
            await journal.RecordAsync(JEntry("Application", $"t{i}"));
        var vm = NewVm(new FakeAppSettingsStore(), new RecordingTweakService(), journal: journal);

        Assert.Equal(4, vm.RecentActivity.Count);             // a bounded head, not the whole trail
        Assert.Equal("t6", vm.RecentActivity[0].TweakIds[0]); // and it's the newest few
    }

    // ---- Optimization score: the front-door "how optimized AM I right now" number must be built from the REAL
    //      detected state of the safe recommended set, and must honour the two load-bearing honesty rules the pure
    //      core pins (OptimizationScoreTests) — proven here end-to-end through the VM, with the detection probe, the
    //      shared-instance map, and the plan wired exactly as production wires them. The score reads synchronously
    //      after `new` because every fake completes inline (InitialiseAsync → RefreshPlanAsync → UpdateScorecard). ----

    private static AdaptivePlan PlanOfDefaultSet(params Tweak[] recommended) => new()
    {
        Recommendations = recommended
            .Select(t => new TweakRecommendation { Tweak = t, InDefaultSet = true })
            .ToArray(),
        RecommendedCount = recommended.Length,
        TotalApplicable = recommended.Length
    };

    [Fact]
    public void Score_IsComputedFromTheDetectedRecommendedSet()
    {
        // Two equal-priority recommended tweaks, only one live on the machine → a weighted 1-of-2 = 50. The repo and
        // the plan share the SAME instances (as the singleton TweakRepository does), so the state the probe writes
        // is exactly what the scorecard reads.
        var live = new Tweak { Id = "live", Name = new() { ["fr"] = "Actif" } };
        var off  = new Tweak { Id = "off",  Name = new() { ["fr"] = "Inactif" } };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("live");                  // only one of the two reads back as applied
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(PlanOfDefaultSet(live, off)),
                       new FakeTweakRepository(new[] { live, off }));

        Assert.True(vm.HasScore);
        Assert.Equal(50, vm.OptimizationScoreValue);          // 1 of 2 equal-weight tweaks live → 50
        Assert.Equal(ScoreGrade.Partial, vm.ScoreGrade);
        Assert.NotEmpty(vm.ScoreCategories);                  // the per-domain breakdown is populated through the VM
    }

    [Fact]
    public void Score_IsPublishedToTheEvidenceLedger_ForTheUnifiedProof()
    {
        // The dashboard feeds the unified « preuve »: the same score it shows on the ring is published into the shared
        // ledger, so the Dashboard export folds it in alongside the settings diff and the frame-time A/B — one number,
        // never two contradicting surfaces.
        var live = new Tweak { Id = "live", Name = new() { ["fr"] = "Actif" } };
        var off  = new Tweak { Id = "off",  Name = new() { ["fr"] = "Inactif" } };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("live");
        var ledger = new EvidenceLedger();
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(PlanOfDefaultSet(live, off)),
                       new FakeTweakRepository(new[] { live, off }),
                       evidence: ledger);

        var published = ledger.Current();
        Assert.True(published.HasScore);
        Assert.Equal(vm.OptimizationScoreValue, published.Score!.Score);   // the ledger holds exactly what the ring shows
    }

    [Fact]
    public void Score_ExcludesUnverifiableTweaks_So100StaysReachable()
    {
        // One applied + one unverifiable (shell-only, no readback). The load-bearing honesty rule: the unverifiable
        // tweak is dropped from BOTH sides, so the score is an honest 100 (not 50), and the unread count is disclosed
        // separately — never folded into the number as a silent penalty for a value Windows won't let us read.
        var applied = new Tweak { Id = "applied", Name = new() { ["fr"] = "Fait" } };
        var blind   = new Tweak { Id = "blind",   Name = new() { ["fr"] = "Aveugle" } };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("applied");
        tweaks.IndeterminateIds.Add("blind");                 // unreadable → must not drag the score
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(PlanOfDefaultSet(applied, blind)),
                       new FakeTweakRepository(new[] { applied, blind }));

        Assert.True(vm.HasScore);
        Assert.Equal(100, vm.OptimizationScoreValue);         // 100 stays reachable despite the blind tweak
        Assert.Equal(ScoreGrade.Excellent, vm.ScoreGrade);
        Assert.Equal(1, vm.ScoreIndeterminateCount);          // ...and the unverifiable one is disclosed, not hidden
    }

    [Fact]
    public void Score_IgnoresTweaksOutsideTheRecommendedSet()
    {
        // The denominator is the SAFE recommended set only. A risky tweak we never advised (InDefaultSet=false) sitting
        // un-applied must not pull the score below 100 — declining what we didn't recommend isn't "unoptimized".
        var recommended = new Tweak { Id = "rec",   Name = new() { ["fr"] = "Recommandé" } };
        var risky       = new Tweak { Id = "risky", Name = new() { ["fr"] = "Risqué" } };  // off-set, left un-applied
        var plan = new AdaptivePlan
        {
            Recommendations = new[]
            {
                new TweakRecommendation { Tweak = recommended, InDefaultSet = true },
                new TweakRecommendation { Tweak = risky, InDefaultSet = false }
            },
            RecommendedCount = 1,
            TotalApplicable = 2
        };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("rec");                   // the recommended one is live; the risky one stays off
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(plan),
                       new FakeTweakRepository(new[] { recommended, risky }));

        Assert.True(vm.HasScore);
        Assert.Equal(100, vm.OptimizationScoreValue);         // the off-set risky tweak is excluded → still 100
        var only = Assert.Single(vm.ScoreCategories);
        Assert.Equal(1, only.VerifiableCount);                // exactly one tweak scored — the recommended one
    }

    // ---- Score progression: the front door tracks the score over time, so it can say « +50 depuis la dernière
    //      mesure » instead of a context-free number — but it must never fabricate a trend on a first-ever measure.
    //      Wired end-to-end through the VM: the same detection probe feeds the score, which is recorded into the
    //      timeline and summarised against the seeded prior sample, all synchronously after `new`. ----

    [Fact]
    public void Score_RecordsTrend_AndReportsImprovementSinceThePreviousMeasure()
    {
        // Prior measure was 50; the machine now reads a full 100 (the one recommended tweak is live) → the dashboard
        // surfaces a +50 climb, coloured as an improvement, anchored to the previous reading.
        var live = new Tweak { Id = "live", Name = new() { ["fr"] = "Actif" } };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("live");
        var history = new FakeScoreHistoryStore().Seed(new ScoreSnapshot(System.DateTime.UtcNow.AddDays(-3), 50));
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(PlanOfDefaultSet(live)),
                       new FakeTweakRepository(new[] { live }),
                       scoreHistory: history);

        Assert.Equal(100, vm.OptimizationScoreValue);
        Assert.True(vm.HasScoreTrend);
        Assert.True(vm.ScoreTrendIsImprovement);
        Assert.False(vm.ScoreTrendIsRegression);
        Assert.Contains("En hausse", vm.ScoreTrendText);
        Assert.Contains("+50", vm.ScoreTrendText);
    }

    [Fact]
    public void Score_FirstEverMeasure_ShowsNoFabricatedTrend()
    {
        // Empty timeline → the sample is recorded, but one point is a value, not a trend: the line stays hidden
        // (HasScoreTrend gates the View's TextBlock) and the text is empty rather than an invented "+0".
        var live = new Tweak { Id = "live", Name = new() { ["fr"] = "Actif" } };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("live");
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(PlanOfDefaultSet(live)),
                       new FakeTweakRepository(new[] { live }),
                       scoreHistory: new FakeScoreHistoryStore());

        Assert.True(vm.HasScore);
        Assert.False(vm.HasScoreTrend);
        Assert.Empty(vm.ScoreTrendText);
    }

    // ---- Freemium gate: the adaptive default set can include Avancé (Premium) tweaks (low-risk Avancé land there),
    //      so the one-click front door must obey the SAME gate the Tweaks page does — refuse Premium picks on a
    //      configured Free build, apply them on Premium, and stay fully unlocked in the as-shipped (not-configured)
    //      build. Routed through the shared TweakGate/PremiumGateText so the four apply surfaces can't word it apart. ----

    [Fact]
    public async Task ApplyRecommended_ConfiguredFree_RefusesPremiumPicks_AndPointsToLicence()
    {
        // The whole recommended set is Avancé (Premium). A configured Free build must apply NOTHING and say why.
        var prem = new Tweak { Id = "adv", Name = new() { ["fr"] = "Avancé" }, Tier = TweakTier.Avance };
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(PlanOfDefaultSet(prem)),
                       license: new FakeLicenseService(AppEdition.Free, configured: true));

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Empty(tweaks.Applied);                          // the gate refused — the backend was never touched
        Assert.Contains("réservé(s) à Premium", vm.PlanStatus);
        Assert.Contains("Licence", vm.PlanStatus);
    }

    [Fact]
    public async Task ApplyRecommended_ConfiguredFree_AppliesTheFreeTier_AndDisclosesTheLockedPremium()
    {
        // Mixed set: one Tranquille (free) + one Avancé (Premium). A configured Free build applies ONLY the free
        // tweak and discloses the one it withheld — never a silent drop of a paid pick.
        var free = new Tweak { Id = "free", Name = new() { ["fr"] = "Libre" }, Tier = TweakTier.Tranquille };
        var prem = new Tweak { Id = "adv",  Name = new() { ["fr"] = "Avancé" }, Tier = TweakTier.Avance };
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(PlanOfDefaultSet(free, prem)),
                       license: new FakeLicenseService(AppEdition.Free, configured: true));

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "free" }, tweaks.Applied.Select(t => t.Id).ToArray());   // only the free tier ran
        Assert.StartsWith("1 optimisation(s) appliquée(s)", vm.PlanStatus);
        Assert.Contains("1 réservé(s) à Premium", vm.PlanStatus);                     // the withheld Premium disclosed
    }

    [Fact]
    public async Task ApplyRecommended_ConfiguredPremium_AppliesEverything()
    {
        var free = new Tweak { Id = "free", Name = new() { ["fr"] = "Libre" }, Tier = TweakTier.Tranquille };
        var prem = new Tweak { Id = "adv",  Name = new() { ["fr"] = "Avancé" }, Tier = TweakTier.Avance };
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(PlanOfDefaultSet(free, prem)),
                       license: new FakeLicenseService(AppEdition.Premium, configured: true));

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Equal(2, tweaks.Applied.Count);                                        // Premium passes the gate
        Assert.StartsWith("2 optimisation(s) appliquée(s)", vm.PlanStatus);
        Assert.DoesNotContain("réservé(s) à Premium", vm.PlanStatus);
    }

    [Fact]
    public async Task ApplyRecommended_NotConfigured_AppliesEverything_EvenAvance()
    {
        // As-shipped: no embedded key ⇒ the gate is dormant, so even an Avancé tweak applies on a Free edition.
        var prem = new Tweak { Id = "adv", Name = new() { ["fr"] = "Avancé" }, Tier = TweakTier.Avance };
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       new FakeAdaptiveRecommendationService(PlanOfDefaultSet(prem)),
                       license: new FakeLicenseService(AppEdition.Free, configured: false));

        await vm.ApplyRecommendedCommand.ExecuteAsync(null);

        Assert.Single(tweaks.Applied);                                                // dormant gate → applied
        Assert.DoesNotContain("réservé(s) à Premium", vm.PlanStatus);
    }

    // ---- Persistance des optimisations (drift): a tweak the user once PROVED live that the system has SINCE turned
    //      off (Windows Update / Group Policy / anti-cheat / another tool). The load-bearing honesty rule, pinned end
    //      to end through the VM: ONLY a once-confirmed apply can drift — a failed/never-confirmed apply that reads off
    //      has no proven-on baseline, so flagging it would be a fabricated regression. The card is computed on load
    //      (no user action) by diffing the journal's Confirmed ids against the fresh on-system re-probe (_lastStates).
    //      Mirrors the pure-core DriftDetectionTests, but here proves the Confirmed-list plumbing carries through. ----

    private static JournalEntry ConfirmedApply(params string[] ids)
        => new(System.DateTime.UtcNow, "Application", ids.Length, 0, ids, System.Array.Empty<string>())
        { Confirmed = ids };

    [Fact]
    public async Task Drift_OnceConfirmedTweakNowOff_IsSurfacedAndNamed()
    {
        // "a" was confirmed live at apply time, but the machine now reads it off → real drift, named for the card.
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "Optim A" } };
        var tweaks = new RecordingTweakService();                      // "a" probes NotApplied (off) by default
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(ConfirmedApply("a"));

        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       repo: new FakeTweakRepository(new[] { a }), journal: journal);

        Assert.True(vm.HasDrift);                                      // detected on load, no user action needed
        Assert.Equal(new[] { "a" }, vm.Drift.Drifted);
        Assert.Contains("Optim A", vm.DriftedNames);                  // localized like the plan, never a raw id
    }

    [Fact]
    public async Task Drift_OnceConfirmedTweakStillLive_ShowsNoDrift()
    {
        // Same confirmed-apply history, but the tweak is still live → persisted, not drifted: no false alarm.
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "Optim A" } };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("a");                             // the machine still reports it applied
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(ConfirmedApply("a"));

        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       repo: new FakeTweakRepository(new[] { a }), journal: journal);

        Assert.False(vm.HasDrift);
    }

    [Fact]
    public async Task Drift_TweakThatOnlyEverFailedToApply_IsNotFabricatedAsDrift()
    {
        // The honesty crux at the VM seam: "a" appears in an apply batch's TweakIds but was NEVER confirmed (it failed /
        // didn't read back) and reads off now. With no proven-on baseline, its being off is NOT drift — flagging it
        // would invent a regression we never witnessed. If the Confirmed plumbing leaked here, this would go red.
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "Optim A" } };
        var tweaks = new RecordingTweakService();                     // "a" probes off
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(new JournalEntry(System.DateTime.UtcNow, "Application", 0, 1,
            new[] { "a" }, new[] { "a" }) { Confirmed = System.Array.Empty<string>() });   // attempted, never confirmed

        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       repo: new FakeTweakRepository(new[] { a }), journal: journal);

        Assert.False(vm.HasDrift);                                    // no proven-on baseline → no alarm
    }

    [Fact]
    public async Task ReapplyDrifted_RerunsTheDriftedTweaks_AndClearsTheCard()
    {
        // « Réappliquer » is a real action on the machine (un bouton = une action), not a redirect: it re-runs exactly
        // the drifted tweaks, and because ApplySetAsync re-probes at the end, a successful re-apply clears the card.
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "Optim A" } };
        var tweaks = new RecordingTweakService();                     // "a" probes off → drift shows on load
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(ConfirmedApply("a"));

        var vm = NewVm(new FakeAppSettingsStore(), tweaks,
                       repo: new FakeTweakRepository(new[] { a }), journal: journal);
        Assert.True(vm.HasDrift);                                     // precondition: the card is showing

        await vm.ReapplyDriftedCommand.ExecuteAsync(null);

        Assert.Contains(tweaks.Applied, t => t.Id == "a");           // it really re-applied the drifted tweak
        Assert.False(vm.HasDrift);                                   // …and the card cleared on the spot
    }
}
