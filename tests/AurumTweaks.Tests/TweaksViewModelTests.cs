using System.Collections.Generic;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the Tweaks page's batch-outcome honesty: applying a selection reports the real success count and,
/// when the backend fails some, admits ", N échec(s)" — the footer status must never let a partial batch
/// read like a clean run. The applied badge likewise counts only what actually landed. Driven through fakes
/// (in-memory catalog + recording service); no JSON, no registry.
/// </summary>
public class TweaksViewModelTests
{
    private static Task<TweaksViewModel> NewVm(RecordingTweakService tweaks, params Tweak[] catalog)
        => NewVm(tweaks, new FakeProfileService(), new RecordingApplyJournal(), catalog);

    private static Task<TweaksViewModel> NewVm(
        RecordingTweakService tweaks, FakeProfileService profiles, params Tweak[] catalog)
        => NewVm(tweaks, profiles, new RecordingApplyJournal(), catalog);

    private static Task<TweaksViewModel> NewVm(
        RecordingTweakService tweaks, RecordingApplyJournal journal, params Tweak[] catalog)
        => NewVm(tweaks, new FakeProfileService(), journal, catalog);

    private static async Task<TweaksViewModel> NewVm(
        RecordingTweakService tweaks, FakeProfileService profiles, RecordingApplyJournal journal,
        params Tweak[] catalog)
    {
        // Default licence: not-configured ⇒ everything unlocked, so the freemium gate is a no-op and every existing
        // status-string assertion stays exactly as before. Gating tests use NewVmLicensed to pin a configured edition.
        var vm = new TweaksViewModel(new FakeTweakRepository(catalog), tweaks, new FakeLocalizationService(),
                                     profiles, journal, new FakeLicenseService(),
                                     new PreflightBannerViewModel(new FakePreflightService()));
        await vm.Initialization;   // catalog load + on-system detection complete before the test acts
        await vm.Preflight.Initialization;   // safety-net banner settled too, so verdict-dependent assertions are deterministic
        return vm;
    }

    private static Task<TweaksViewModel> NewVmLicensed(
        RecordingTweakService tweaks, FakeLicenseService license, params Tweak[] catalog)
        => NewVmLicensed(tweaks, license, new RecordingApplyJournal(), catalog);

    private static async Task<TweaksViewModel> NewVmLicensed(
        RecordingTweakService tweaks, FakeLicenseService license, RecordingApplyJournal journal,
        params Tweak[] catalog)
    {
        var vm = new TweaksViewModel(new FakeTweakRepository(catalog), tweaks, new FakeLocalizationService(),
                                     new FakeProfileService(), journal, license,
                                     new PreflightBannerViewModel(new FakePreflightService()));
        await vm.Initialization;
        await vm.Preflight.Initialization;
        return vm;
    }

    [Fact]
    public async Task ApplySelected_WhenSomeFail_StatusLeadsWithSucceeded_AndAdmitsFailures()
    {
        var ok = new Tweak { Id = "ok", Name = new() { ["fr"] = "OK" }, IsSelected = true };
        var bad = new Tweak { Id = "bad", Name = new() { ["fr"] = "Bad" }, IsSelected = true };
        var tweaks = new RecordingTweakService();
        tweaks.FailIds.Add("bad");                       // backend reports this one as failed
        var vm = await NewVm(tweaks, ok, bad);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.StartsWith("1 tweak(s) appliqué(s)", vm.StatusMessage);   // the real success count
        Assert.Contains("1 échec(s)", vm.StatusMessage);                 // failure admitted, not dropped
        Assert.Equal(1, vm.AppliedCount);                                // badge counts only what landed
    }

    [Fact]
    public async Task ApplySelected_WhenAllSucceed_StatusHasNoFailureClause()
    {
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" }, IsSelected = true };
        var b = new Tweak { Id = "b", Name = new() { ["fr"] = "B" }, IsSelected = true };
        var tweaks = new RecordingTweakService();
        var vm = await NewVm(tweaks, a, b);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Equal("2 tweak(s) appliqué(s)", vm.StatusMessage);        // no ", échec(s)" when nothing failed
    }

    [Fact]
    public async Task ApplySelected_WhenRequiredRestorePointFails_ShowsHonestReason_AppliesNothing_AndDoesNotJournal()
    {
        // The reliability promise at the page that sells it: if the engine ABORTS because the required restore point
        // couldn't be created, the footer must speak the ONE honest reason (not a misleading "0 appliqué(s)"), nothing
        // must read as applied, and the audit journal must stay empty — nothing happened on the machine.
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" }, IsSelected = true };
        var tweaks = new RecordingTweakService { RestorePointWillFail = true };
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(tweaks, journal, a);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Equal(TweakApplyText.RestorePointFailed, vm.StatusMessage);
        Assert.False(a.IsApplied);
        Assert.Empty(journal.Entries);                                   // no audit entry for a batch that never ran
    }

    [Fact]
    public async Task ApplySelected_WhenNothingSelected_SaysSo_AndNeverTouchesBackend()
    {
        var t = new Tweak { Id = "x", Name = new() { ["fr"] = "X" } };   // present but not selected
        var tweaks = new RecordingTweakService();
        var vm = await NewVm(tweaks, t);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Empty(tweaks.Applied);                                    // no dead-button work
        Assert.Equal("Aucun tweak sélectionné.", vm.StatusMessage);      // and the footer says why
    }

    [Fact]
    public async Task RevertAll_WhenNothingApplied_SaysSo_AndNeverTouchesBackend()
    {
        var t = new Tweak { Id = "x", Name = new() { ["fr"] = "X" } };   // nothing applied to revert
        var tweaks = new RecordingTweakService();
        var vm = await NewVm(tweaks, t);

        await vm.RevertAllCommand.ExecuteAsync(null);

        Assert.Equal("Aucun tweak à restaurer.", vm.StatusMessage);
    }

    [Fact]
    public async Task RefreshState_ReprobesTheMachine_AndUpdatesBadgeAndCount()
    {
        // The manual "Rafraîchir l'état" button must genuinely re-read the system, not replay the load-time
        // snapshot: a tweak that goes active AFTER load (applied by another tool, or surviving a reboot) has
        // to light up on demand — otherwise the button is a dead control and the badge lies until relaunch.
        var t = new Tweak { Id = "later", Name = new() { ["fr"] = "Later" } };
        var tweaks = new RecordingTweakService();
        var vm = await NewVm(tweaks, t);
        Assert.False(t.IsApplied);                                  // not active at load
        Assert.Equal(0, vm.AppliedCount);

        tweaks.DetectAppliedIds.Add("later");                       // now live on the machine, outside the app

        await vm.RefreshStateCommand.ExecuteAsync(null);

        Assert.True(t.IsApplied);                                   // the re-probe reflected the new machine state
        Assert.Equal(1, vm.AppliedCount);                          // badge/count updated on demand
        Assert.Contains("1 tweak(s) actif(s)", vm.StatusMessage);   // honest count in the footer
    }

    [Fact]
    public async Task Load_DetectsAlreadyActiveTweaks_AndCountsThemAsApplied()
    {
        // The payoff of load-time detection: a tweak the user never applied *in-app*, but whose values are
        // already live on the machine, must light the badge — IsApplied is no longer a flag that resets to
        // zero every launch. A tweak the backend reports as inactive stays un-lit.
        var active = new Tweak { Id = "already", Name = new() { ["fr"] = "Déjà actif" } };
        var inactive = new Tweak { Id = "off", Name = new() { ["fr"] = "Inactif" } };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("already");          // backend reports this one as live on the system
        var vm = await NewVm(tweaks, active, inactive);

        Assert.True(active.IsApplied);                    // detection reflected the real machine state
        Assert.False(inactive.IsApplied);
        Assert.Equal(1, vm.AppliedCount);                 // the badge counts what's genuinely active
    }

    // ---- Save the current selection as a reusable profile (producer half of custom profiles) ----

    [Fact]
    public async Task SaveSelectionAsProfile_PersistsExactlyTheSelectedIds_WithTheGivenName()
    {
        var p1 = new Tweak { Id = "p1", Name = new() { ["fr"] = "P1" }, IsSelected = true };
        var p2 = new Tweak { Id = "p2", Name = new() { ["fr"] = "P2" }, IsSelected = true };
        var skipped = new Tweak { Id = "skip", Name = new() { ["fr"] = "Skip" } };   // not ticked
        var profiles = new FakeProfileService();
        var vm = await NewVm(new RecordingTweakService(), profiles, p1, p2, skipped);
        vm.NewProfileName = "Mon setup";

        await vm.SaveSelectionAsProfileCommand.ExecuteAsync(null);

        var saved = Assert.Single(profiles.Saved);
        Assert.Equal("Mon setup", saved.Name);
        Assert.False(saved.IsBuiltIn);
        Assert.Equal(new[] { "p1", "p2" }, saved.TweakIds);    // only the ticked tweaks land in the profile
        Assert.Contains("enregistré", vm.StatusMessage);
    }

    [Fact]
    public async Task SaveSelectionAsProfile_WhenNothingSelected_WritesNothing_AndSaysSo()
    {
        var t = new Tweak { Id = "x", Name = new() { ["fr"] = "X" } };   // present, unticked
        var profiles = new FakeProfileService();
        var vm = await NewVm(new RecordingTweakService(), profiles, t);

        await vm.SaveSelectionAsProfileCommand.ExecuteAsync(null);

        Assert.Empty(profiles.Saved);                                   // never persist an empty profile
        Assert.Equal("Aucun tweak sélectionné à enregistrer.", vm.StatusMessage);
    }

    [Fact]
    public async Task SaveSelectionAsProfile_WithBlankName_FallsBackToANonEmptyName()
    {
        var picked = new Tweak { Id = "p", Name = new() { ["fr"] = "P" }, IsSelected = true };
        var profiles = new FakeProfileService();
        var vm = await NewVm(new RecordingTweakService(), profiles, picked);
        // NewProfileName left blank on purpose

        await vm.SaveSelectionAsProfileCommand.ExecuteAsync(null);

        var saved = Assert.Single(profiles.Saved);
        Assert.False(string.IsNullOrWhiteSpace(saved.Name));            // a profile is never saved unnamed
    }

    // ---- Edit a saved profile in place (round-trip from the Profiles page's "Modifier") ----

    [Fact]
    public async Task BeginEditingProfile_TicksExactlyTheProfilesTweaks_AndArmsInPlaceUpdate()
    {
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var b = new Tweak { Id = "b", Name = new() { ["fr"] = "B" }, IsSelected = true };   // pre-ticked, NOT in profile
        var c = new Tweak { Id = "c", Name = new() { ["fr"] = "C" } };
        var vm = await NewVm(new RecordingTweakService(), a, b, c);
        var profile = new Profile { Name = "Setup compétitif", TweakIds = { "a", "c" } };

        vm.BeginEditingProfile(profile);

        Assert.True(a.IsSelected);                       // the profile's tweaks are ticked
        Assert.True(c.IsSelected);
        Assert.False(b.IsSelected);                      // and everything else is untouched-to-OFF, not left as-was
        Assert.True(vm.IsEditingProfile);
        Assert.Equal(profile.Id, vm.EditingProfileId);   // armed to overwrite THIS profile
        Assert.Equal("Setup compétitif", vm.NewProfileName);
        Assert.Equal(2, vm.SelectedCount);
    }

    [Fact]
    public async Task BeginEditingProfile_SkipsStaleIds_TickingOnlyWhatTheCatalogStillHas()
    {
        // A profile saved on an older build may name a tweak this build no longer ships; it simply doesn't get ticked.
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var b = new Tweak { Id = "b", Name = new() { ["fr"] = "B" } };
        var vm = await NewVm(new RecordingTweakService(), a, b);
        var profile = new Profile { Name = "Vieux profil", TweakIds = { "a", "gone" } };

        vm.BeginEditingProfile(profile);

        Assert.True(a.IsSelected);
        Assert.False(b.IsSelected);
        Assert.Equal(1, vm.SelectedCount);               // the stale "gone" id can't be resurrected
        Assert.True(vm.IsEditingProfile);
    }

    [Fact]
    public async Task SaveWhileEditing_ReusesTheProfileId_AndSaysUpdated()
    {
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var b = new Tweak { Id = "b", Name = new() { ["fr"] = "B" } };
        var profiles = new FakeProfileService();
        var vm = await NewVm(new RecordingTweakService(), profiles, a, b);
        var profile = new Profile { Name = "Setup compétitif", TweakIds = { "a" } };
        vm.BeginEditingProfile(profile);
        b.IsSelected = true;                             // user adds a tweak while editing

        await vm.SaveSelectionAsProfileCommand.ExecuteAsync(null);

        var saved = Assert.Single(profiles.Saved);
        Assert.Equal(profile.Id, saved.Id);              // same id → overwrites in place, no duplicate left behind
        Assert.Equal(new[] { "a", "b" }, saved.TweakIds);
        Assert.Contains("mis à jour", vm.StatusMessage);
        Assert.False(vm.IsEditingProfile);               // edit mode disarms after a successful save
    }

    [Fact]
    public async Task CancelEditingProfile_Disarms_SoTheNextSaveCreatesANewProfile()
    {
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var profiles = new FakeProfileService();
        var vm = await NewVm(new RecordingTweakService(), profiles, a);
        var profile = new Profile { Name = "Setup compétitif", TweakIds = { "a" } };
        vm.BeginEditingProfile(profile);

        vm.CancelEditingProfileCommand.Execute(null);
        Assert.False(vm.IsEditingProfile);               // disarmed

        await vm.SaveSelectionAsProfileCommand.ExecuteAsync(null);   // the loaded selection survives the cancel

        var saved = Assert.Single(profiles.Saved);
        Assert.NotEqual(profile.Id, saved.Id);           // a brand-new profile, not an overwrite of the edited one
        Assert.Contains("enregistré", vm.StatusMessage);
    }

    [Fact]
    public async Task SaveWhileEditing_WithEmptySelection_KeepsEditModeArmed()
    {
        // An empty selection can't update a profile — saving must no-op AND stay in edit mode so the user fixes it.
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var profiles = new FakeProfileService();
        var vm = await NewVm(new RecordingTweakService(), profiles, a);
        var profile = new Profile { Name = "Setup compétitif", TweakIds = { "a" } };
        vm.BeginEditingProfile(profile);
        a.IsSelected = false;                            // user empties the selection

        await vm.SaveSelectionAsProfileCommand.ExecuteAsync(null);

        Assert.Empty(profiles.Saved);
        Assert.True(vm.IsEditingProfile);                // still armed, not silently dropped
        Assert.Equal("Aucun tweak sélectionné à enregistrer.", vm.StatusMessage);
    }

    // ---- Bulk selection: tick the filtered set / clear everything (same predicate the list paints with) ----

    [Fact]
    public async Task SelectAllVisible_TicksOnlyTheFilteredTweaks_NotTheHiddenOnes()
    {
        var calm = new Tweak { Id = "calm", Name = new() { ["fr"] = "Calm" }, Tier = TweakTier.Tranquille };
        var wild = new Tweak { Id = "wild", Name = new() { ["fr"] = "Wild" }, Tier = TweakTier.Extreme };
        var vm = await NewVm(new RecordingTweakService(), calm, wild);
        vm.SelectedTier = TweakTier.Tranquille;          // the list now shows only the calm tier

        vm.SelectAllVisibleCommand.Execute(null);

        Assert.True(calm.IsSelected);                    // the visible one is ticked
        Assert.False(wild.IsSelected);                   // the filtered-out one is never silently ticked
        Assert.Equal(1, vm.SelectedCount);
        Assert.Equal("1 tweak(s) visible(s) sélectionné(s).", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectAllVisible_RespectsCompetitiveSafeFilter_NeverTickingAHiddenAcRiskyTweak()
    {
        // The honesty payoff: with "AC safe only" on, an anti-cheat-risky tweak is hidden — bulk-select must skip it,
        // so the user can never arm a ban-risk tweak they can't currently see.
        var safe = new Tweak { Id = "safe", Name = new() { ["fr"] = "Safe" } };
        var risky = new Tweak
        {
            Id = "risky", Name = new() { ["fr"] = "Risky" },
            AntiCheat = new AntiCheatMatrix { Vanguard = AntiCheatStatus.Risky }
        };
        var vm = await NewVm(new RecordingTweakService(), safe, risky);
        vm.CompetitiveSafeOnly = true;                   // hide everything an anti-cheat flags

        vm.SelectAllVisibleCommand.Execute(null);

        Assert.True(safe.IsSelected);
        Assert.False(risky.IsSelected);                  // hidden AC-risky tweak stays off
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task SelectAllVisible_WhenFilterShowsNothing_SaysSo_WithoutTickingAnything()
    {
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var vm = await NewVm(new RecordingTweakService(), a);
        vm.SearchText = "zzz-aucune-correspondance";      // a filter that matches no tweak

        vm.SelectAllVisibleCommand.Execute(null);

        Assert.False(a.IsSelected);
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal("Aucun tweak visible à sélectionner.", vm.StatusMessage);
    }

    [Fact]
    public async Task ClearSelection_UnticksEverything_IncludingTweaksHiddenBehindAFilter()
    {
        // Clearing must reach the hidden ticks too, or a later apply would run tweaks the filtered view can't show.
        var calm = new Tweak { Id = "calm", Name = new() { ["fr"] = "Calm" }, Tier = TweakTier.Tranquille, IsSelected = true };
        var wild = new Tweak { Id = "wild", Name = new() { ["fr"] = "Wild" }, Tier = TweakTier.Extreme, IsSelected = true };
        var vm = await NewVm(new RecordingTweakService(), calm, wild);
        vm.SelectedTier = TweakTier.Tranquille;          // wild is now hidden, but still ticked

        vm.ClearSelectionCommand.Execute(null);

        Assert.False(calm.IsSelected);
        Assert.False(wild.IsSelected);                   // the hidden tick is cleared too — no leftover armed tweak
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal("Sélection effacée.", vm.StatusMessage);
    }

    [Fact]
    public async Task ClearSelection_WhenNothingSelected_SaysSo()
    {
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var vm = await NewVm(new RecordingTweakService(), a);

        vm.ClearSelectionCommand.Execute(null);

        Assert.Equal("Aucun tweak sélectionné.", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectUnappliedVisible_TicksTheVisibleNotYetApplied_LeavingDetectedOnesAlone()
    {
        var done = new Tweak { Id = "done", Name = new() { ["fr"] = "Done" } };
        var todo = new Tweak { Id = "todo", Name = new() { ["fr"] = "Todo" } };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("done");             // the live machine reports this one already applied
        var vm = await NewVm(tweaks, done, todo);
        Assert.True(done.IsApplied);                     // load-time detection lit its badge
        Assert.False(todo.IsApplied);

        vm.SelectUnappliedVisibleCommand.Execute(null);

        Assert.True(todo.IsSelected);                    // the un-applied one is ticked for an "apply what's missing"
        Assert.False(done.IsSelected);                   // the applied one is left out — nothing to add
        Assert.Equal(1, vm.SelectedCount);
        Assert.Equal("1 tweak(s) non appliqué(s) sélectionné(s).", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectUnappliedVisible_IsAdditive_NeverUnticksAnAppliedTweakTheUserKept()
    {
        var done = new Tweak { Id = "done", Name = new() { ["fr"] = "Done" } };
        var todo = new Tweak { Id = "todo", Name = new() { ["fr"] = "Todo" } };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("done");
        var vm = await NewVm(tweaks, done, todo);
        done.IsSelected = true;                          // user deliberately keeps the applied one ticked

        vm.SelectUnappliedVisibleCommand.Execute(null);

        Assert.True(done.IsSelected);                    // additive: the kept tick survives, never cleared
        Assert.True(todo.IsSelected);
        Assert.Equal(2, vm.SelectedCount);
    }

    [Fact]
    public async Task SelectUnappliedVisible_RespectsTheFilter_IgnoringHiddenNotAppliedTweaks()
    {
        var calm = new Tweak { Id = "calm", Name = new() { ["fr"] = "Calm" }, Tier = TweakTier.Tranquille };
        var wild = new Tweak { Id = "wild", Name = new() { ["fr"] = "Wild" }, Tier = TweakTier.Extreme };
        var vm = await NewVm(new RecordingTweakService(), calm, wild);   // neither is applied
        vm.SelectedTier = TweakTier.Tranquille;          // wild is filtered out of view

        vm.SelectUnappliedVisibleCommand.Execute(null);

        Assert.True(calm.IsSelected);
        Assert.False(wild.IsSelected);                   // a hidden not-applied tweak is never ticked
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task SelectUnappliedVisible_WhenEveryVisibleTweakIsApplied_SaysSo()
    {
        var done = new Tweak { Id = "done", Name = new() { ["fr"] = "Done" } };
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("done");
        var vm = await NewVm(tweaks, done);

        vm.SelectUnappliedVisibleCommand.Execute(null);

        Assert.False(done.IsSelected);                   // nothing missing → nothing ticked
        Assert.Equal("Aucun tweak visible non appliqué à sélectionner.", vm.StatusMessage);
    }

    [Fact]
    public async Task InvertVisibleSelection_FlipsTheVisibleTweaks_LeavingHiddenTicksUntouched()
    {
        var on = new Tweak { Id = "on", Name = new() { ["fr"] = "On" }, Tier = TweakTier.Tranquille, IsSelected = true };
        var off = new Tweak { Id = "off", Name = new() { ["fr"] = "Off" }, Tier = TweakTier.Tranquille };
        var hidden = new Tweak { Id = "hidden", Name = new() { ["fr"] = "Hidden" }, Tier = TweakTier.Extreme, IsSelected = true };
        var vm = await NewVm(new RecordingTweakService(), on, off, hidden);
        vm.SelectedTier = TweakTier.Tranquille;          // hidden is filtered out

        vm.InvertVisibleSelectionCommand.Execute(null);

        Assert.False(on.IsSelected);                     // ticked → unticked
        Assert.True(off.IsSelected);                     // unticked → ticked
        Assert.True(hidden.IsSelected);                  // hidden tick untouched — invert never reaches it
        Assert.Equal("Sélection inversée sur 2 tweak(s) visible(s).", vm.StatusMessage);
    }

    [Fact]
    public async Task InvertVisibleSelection_WhenFilterShowsNothing_SaysSo()
    {
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var vm = await NewVm(new RecordingTweakService(), a);
        vm.SearchText = "zzz-aucune-correspondance";

        vm.InvertVisibleSelectionCommand.Execute(null);

        Assert.False(a.IsSelected);
        Assert.Equal("Aucun tweak visible à inverser.", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectedCount_TracksCheckboxState_Live()
    {
        // The footer's "N sélectionnés" must follow the boxes as they're ticked, not only after an apply/revert.
        var a = new Tweak { Id = "a", Name = new() { ["fr"] = "A" } };
        var b = new Tweak { Id = "b", Name = new() { ["fr"] = "B" } };
        var vm = await NewVm(new RecordingTweakService(), a, b);
        Assert.Equal(0, vm.SelectedCount);

        a.IsSelected = true;
        Assert.Equal(1, vm.SelectedCount);
        b.IsSelected = true;
        Assert.Equal(2, vm.SelectedCount);
        a.IsSelected = false;
        Assert.Equal(1, vm.SelectedCount);
    }

    // ---- Apply-plan preview: informed consent before a batch of as-admin changes ----

    private static TweakOperation RegOp(string name = "N", string apply = "1")
        => new() { Type = OperationType.Registry, Hive = "HKLM", Key = "K", Name = name, Apply = apply, Revert = "0" };

    private static Tweak Picked(string id, params TweakOperation[] ops)
    {
        var t = new Tweak { Id = id, Name = new() { ["fr"] = id }, IsSelected = true };
        foreach (var op in ops) t.Operations.Add(op);
        return t;
    }

    private static Tweak PickedTier(string id, TweakTier tier, params TweakOperation[] ops)
    {
        var t = Picked(id, ops);
        t.Tier = tier;
        return t;
    }

    [Fact]
    public async Task PreviewPlan_WithSelection_BuildsThePlan_ShowsOverlay_AndTouchesNothing()
    {
        var a = Picked("a", RegOp(), RegOp());
        var b = Picked("b", RegOp());
        var tweaks = new RecordingTweakService();
        var vm = await NewVm(tweaks, a, b);

        await vm.PreviewPlanCommand.ExecuteAsync(null);

        Assert.True(vm.IsPlanVisible);
        Assert.NotNull(vm.CurrentPlan);
        Assert.Equal(2, vm.CurrentPlan!.TweakCount);
        Assert.Equal(3, vm.CurrentPlan.TotalOperations);
        Assert.Empty(tweaks.Applied);                       // previewing must never touch the machine
    }

    [Fact]
    public async Task PreviewPlan_WithNothingSelected_IsHonestNoOp()
    {
        var t = new Tweak { Id = "x", Name = new() { ["fr"] = "X" } };   // present, not ticked
        var tweaks = new RecordingTweakService();
        var vm = await NewVm(tweaks, t);

        await vm.PreviewPlanCommand.ExecuteAsync(null);

        Assert.False(vm.IsPlanVisible);
        Assert.Null(vm.CurrentPlan);
        Assert.Equal("Aucun tweak sélectionné.", vm.StatusMessage);
    }

    [Fact]
    public async Task ConfirmPlan_RunsTheSameApply_AndClosesTheOverlay()
    {
        var a = Picked("a", RegOp());
        var tweaks = new RecordingTweakService();
        var vm = await NewVm(tweaks, a);
        await vm.PreviewPlanCommand.ExecuteAsync(null);
        Assert.True(vm.IsPlanVisible);

        await vm.ConfirmPlanCommand.ExecuteAsync(null);

        Assert.False(vm.IsPlanVisible);
        var applied = Assert.Single(tweaks.Applied);                    // confirm genuinely applied the selection
        Assert.Equal("a", applied.Id);
        Assert.Equal("1 tweak(s) appliqué(s)", vm.StatusMessage);
    }

    [Fact]
    public async Task ClosePlan_HidesTheOverlay_WithoutApplying()
    {
        var a = Picked("a", RegOp());
        var tweaks = new RecordingTweakService();
        var vm = await NewVm(tweaks, a);
        await vm.PreviewPlanCommand.ExecuteAsync(null);

        vm.ClosePlanCommand.Execute(null);

        Assert.False(vm.IsPlanVisible);
        Assert.Empty(tweaks.Applied);                                   // cancelling is a true no-op
    }

    [Fact]
    public async Task PreviewPlan_WhenSelectionConflicts_SurfacesTheConflict_AndStillTouchesNothing()
    {
        // Two ticked tweaks write the SAME registry value to DIFFERENT values: apply order would silently
        // decide the winner, so the preview must warn before the user consents — and still apply nothing.
        var a = Picked("a", RegOp(apply: "1"));
        var b = Picked("b", RegOp(apply: "0"));
        var tweaks = new RecordingTweakService();
        var vm = await NewVm(tweaks, a, b);

        await vm.PreviewPlanCommand.ExecuteAsync(null);

        Assert.True(vm.HasConflicts);
        var c = Assert.Single(vm.CurrentConflicts);
        Assert.Equal(@"HKLM\K\N", c.Target);
        Assert.Empty(tweaks.Applied);                                  // surfacing a conflict is still a no-op
    }

    [Fact]
    public async Task PreviewPlan_WhenSelectionIsClean_ReportsNoConflicts()
    {
        var a = Picked("a", RegOp(name: "N1"));
        var b = Picked("b", RegOp(name: "N2"));                        // different targets → nothing to fight over
        var tweaks = new RecordingTweakService();
        var vm = await NewVm(tweaks, a, b);

        await vm.PreviewPlanCommand.ExecuteAsync(null);

        Assert.False(vm.HasConflicts);
        Assert.Empty(vm.CurrentConflicts);
    }

    // ---- Post-apply verification: re-probe after apply and surface writes the system can't confirm ----

    [Fact]
    public async Task ApplySelected_WhenEveryWriteReadsBack_ShowsNoUnconfirmedBanner()
    {
        var a = Picked("a", RegOp());
        var b = Picked("b", RegOp());
        var tweaks = new RecordingTweakService();          // default: applied tweaks read back as live
        var vm = await NewVm(tweaks, a, b);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.NotNull(vm.LastVerification);               // verification ran
        Assert.False(vm.HasUnconfirmed);                   // …and found nothing amiss → no banner
        Assert.Empty(vm.LastVerification!.Unconfirmed);
    }

    [Fact]
    public async Task ApplySelected_WhenAWriteDoesntStick_FlagsIt_WithoutAlteringTheStatusContract()
    {
        // The engine reports BOTH applied (FailIds empty), but the live machine reads one back as not active.
        // Honesty: the status string stays the engine's truthful "2 appliqué(s)", and the divergence surfaces
        // ONLY through the separate verification banner — never as a fabricated ✓ nor folded into the status.
        var ok = Picked("ok", RegOp());
        var stuck = Picked("stuck", RegOp());
        var tweaks = new RecordingTweakService();
        tweaks.NotConfirmedIds.Add("stuck");               // applied, but doesn't read back as live
        var vm = await NewVm(tweaks, ok, stuck);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Equal("2 tweak(s) appliqué(s)", vm.StatusMessage);   // pinned status, untouched by verification
        Assert.True(vm.HasUnconfirmed);
        Assert.Equal("stuck", vm.LastVerification!.UnconfirmedLabel);
        Assert.Equal(new[] { "ok" }, vm.LastVerification.Confirmed);
    }

    [Fact]
    public async Task ApplySelected_ShellOnlyTweak_StaysUnverifiable_NeverAFalseAlarm()
    {
        // A shell-only op has no honest readback. After apply it must land in "unverifiable", NOT "didn't stick":
        // raising the alarm banner for something we simply can't read would itself be a dishonest claim.
        var shell = Picked("shell", RegOp());
        var tweaks = new RecordingTweakService();
        tweaks.IndeterminateIds.Add("shell");
        var vm = await NewVm(tweaks, shell);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.False(vm.HasUnconfirmed);                            // unverifiable ≠ failure → no banner
        Assert.Equal(new[] { "shell" }, vm.LastVerification!.Unverifiable);
    }

    [Fact]
    public async Task RevertAll_ClearsAStaleVerificationBanner()
    {
        var stuck = Picked("stuck", RegOp());
        var tweaks = new RecordingTweakService();
        tweaks.NotConfirmedIds.Add("stuck");
        var vm = await NewVm(tweaks, stuck);
        await vm.ApplySelectedCommand.ExecuteAsync(null);
        Assert.True(vm.HasUnconfirmed);                             // banner is up after the apply

        await vm.RevertAllCommand.ExecuteAsync(null);

        Assert.Null(vm.LastVerification);                          // reverting drops the now-stale verification
        Assert.False(vm.HasUnconfirmed);
    }

    [Fact]
    public async Task RefreshState_ClearsAStaleVerificationBanner()
    {
        // A wholesale manual re-probe supersedes the last apply's verification; the banner must not linger over
        // state the fresh detection has already folded into the count.
        var stuck = Picked("stuck", RegOp());
        var tweaks = new RecordingTweakService();
        tweaks.NotConfirmedIds.Add("stuck");
        var vm = await NewVm(tweaks, stuck);
        await vm.ApplySelectedCommand.ExecuteAsync(null);
        Assert.True(vm.HasUnconfirmed);

        await vm.RefreshStateCommand.ExecuteAsync(null);

        Assert.Null(vm.LastVerification);
        Assert.False(vm.HasUnconfirmed);
    }

    // ---- Post-revert verification: re-probe after "Tout restaurer" and surface tweaks still active despite it ----

    [Fact]
    public async Task RevertAll_WhenATweakStaysActive_FlagsItInTheRevertBanner()
    {
        // "t" is live at load (so there's something to revert) AND stays live after the revert (DetectAppliedIds is
        // honored independent of the cleared IsApplied — modelling a group policy / another tool re-applying it).
        // The honest result: the revert ran, but the readback proves it's STILL ACTIVE → the separate banner lights up.
        var t = Picked("t", RegOp());
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("t");
        var vm = await NewVm(tweaks, t);

        await vm.RevertAllCommand.ExecuteAsync(null);

        Assert.True(vm.HasStillActiveAfterRevert);
        Assert.Equal("t", vm.LastRevertVerification!.UnconfirmedLabel);
        Assert.Empty(vm.LastRevertVerification.Confirmed);
    }

    [Fact]
    public async Task RevertAll_WhenEverythingRevertsCleanly_ShowsNoStillActiveBanner()
    {
        // Apply then revert with the default fake (reverted tweaks read back off): verification runs and confirms the
        // revert took, so the banner stays down — an honest clean "tout restauré", not a suppressed check.
        var t = Picked("t", RegOp());
        var vm = await NewVm(new RecordingTweakService(), t);
        await vm.ApplySelectedCommand.ExecuteAsync(null);

        await vm.RevertAllCommand.ExecuteAsync(null);

        Assert.NotNull(vm.LastRevertVerification);          // verification ran
        Assert.False(vm.HasStillActiveAfterRevert);         // …and found nothing still active → no banner
        Assert.Equal(new[] { "t" }, vm.LastRevertVerification!.Confirmed);
    }

    [Fact]
    public async Task ApplySelected_ClearsAStaleRevertBanner()
    {
        // A fresh apply changes the machine, so the previous "still active after revert" warning is now stale.
        var t = Picked("t", RegOp());
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("t");
        var vm = await NewVm(tweaks, t);
        await vm.RevertAllCommand.ExecuteAsync(null);
        Assert.True(vm.HasStillActiveAfterRevert);          // banner is up after the revert

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Null(vm.LastRevertVerification);
        Assert.False(vm.HasStillActiveAfterRevert);
    }

    [Fact]
    public async Task RefreshState_ClearsAStaleRevertBanner()
    {
        var t = Picked("t", RegOp());
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("t");
        var vm = await NewVm(tweaks, t);
        await vm.RevertAllCommand.ExecuteAsync(null);
        Assert.True(vm.HasStillActiveAfterRevert);

        await vm.RefreshStateCommand.ExecuteAsync(null);

        Assert.Null(vm.LastRevertVerification);
        Assert.False(vm.HasStillActiveAfterRevert);
    }

    // ---- Change journal: every apply/revert batch lands in the persisted audit trail ----

    [Fact]
    public async Task ApplySelected_RecordsAnApplicationEntry_WithTheTweakIds()
    {
        var a = Picked("a", RegOp());
        var b = Picked("b", RegOp());
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(new RecordingTweakService(), journal, a, b);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("Application", entry.Action);
        Assert.Equal(2, entry.Succeeded);
        Assert.Equal(0, entry.Failed);
        Assert.Equal(new[] { "a", "b" }, entry.TweakIds);
    }

    [Fact]
    public async Task ApplySelected_CarriesTheUnconfirmedIdsIntoTheJournal()
    {
        // The audit trail must record not just what we ran but what the machine couldn't confirm stuck —
        // the journal's "non confirmé(s)" clause is the verification's honesty, persisted.
        var ok = Picked("ok", RegOp());
        var stuck = Picked("stuck", RegOp());
        var tweaks = new RecordingTweakService();
        tweaks.NotConfirmedIds.Add("stuck");
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(tweaks, journal, ok, stuck);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        var entry = Assert.Single(journal.Entries);
        Assert.True(entry.HasUnconfirmed);
        Assert.Equal(new[] { "stuck" }, entry.Unconfirmed);
    }

    [Fact]
    public async Task ApplySelected_WhenNothingSelected_RecordsNothing()
    {
        var t = new Tweak { Id = "x", Name = new() { ["fr"] = "X" } };   // present, not ticked
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(new RecordingTweakService(), journal, t);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Empty(journal.Entries);                                  // an honest no-op leaves no audit entry
    }

    [Fact]
    public async Task RevertAll_RecordsARestaurationEntry_CleanRevert_CarriesNoUnconfirmed()
    {
        // Apply then revert with the default fake (reverted tweaks read back off): the re-probe confirms the revert
        // took, so the durable entry carries no "still active" — an honest clean restore in the audit trail.
        var t = Picked("t", RegOp());
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(new RecordingTweakService(), journal, t);
        await vm.ApplySelectedCommand.ExecuteAsync(null);

        await vm.RevertAllCommand.ExecuteAsync(null);

        var entry = Assert.Single(journal.Entries, e => e.Action == "Restauration");
        Assert.Equal(1, entry.Succeeded);
        Assert.False(entry.HasUnconfirmed);
    }

    [Fact]
    public async Task RevertAll_RecordsStillActiveIdsAsUnconfirmed_WhenRevertDidntTake()
    {
        // The audit-trail symmetry with apply's "didn't stick": "t" stays live after the revert (DetectAppliedIds is
        // honored independent of the cleared IsApplied), so the durable entry must record it as still-active —
        // a revert that didn't take is logged exactly as an apply that didn't stick, not silently dropped.
        var t = Picked("t", RegOp());
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("t");
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(tweaks, journal, t);

        await vm.RevertAllCommand.ExecuteAsync(null);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("Restauration", entry.Action);
        Assert.True(entry.HasUnconfirmed);
        Assert.Equal(new[] { "t" }, entry.Unconfirmed);
    }

    [Fact]
    public async Task ApplyThenRevert_RecordsBothBatches_NewestFirst()
    {
        var t = Picked("t", RegOp());
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(new RecordingTweakService(), journal, t);

        await vm.ApplySelectedCommand.ExecuteAsync(null);   // t is now applied (read back as live)
        await vm.RevertAllCommand.ExecuteAsync(null);

        Assert.Equal(2, journal.Entries.Count);
        Assert.Equal("Restauration", journal.Entries[0].Action);   // newest first: the revert leads
        Assert.Equal("Application", journal.Entries[1].Action);
    }

    // ---- Freemium tier gate: a configured Free build may apply only Tranquille; Premium and "not configured"
    //      pass everything. The gate must bite at the elevated apply path (and mirror in the preview) without ever
    //      silently touching a locked tweak, and without disturbing the pinned status contract in the common case. ----

    [Fact]
    public async Task ApplySelected_ConfiguredFree_WholeSelectionPremium_AppliesNothing_AndPointsToLicence()
    {
        var ext = PickedTier("ext", TweakTier.Extreme, RegOp());
        var adv = PickedTier("adv", TweakTier.Avance, RegOp());
        var tweaks = new RecordingTweakService();
        var vm = await NewVmLicensed(tweaks, new FakeLicenseService(AppEdition.Free, configured: true), ext, adv);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Empty(tweaks.Applied);                       // the gate refused — the elevated backend is never touched
        Assert.Equal(0, vm.AppliedCount);
        Assert.Contains("réservé(s) à Premium", vm.StatusMessage);
        Assert.Contains("Licence", vm.StatusMessage);       // tells the user exactly where to unlock
    }

    [Fact]
    public async Task ApplySelected_ConfiguredFree_MixedSelection_AppliesTranquilleOnly_NotesTheLocked()
    {
        var free = PickedTier("free", TweakTier.Tranquille, RegOp());
        var ext = PickedTier("ext", TweakTier.Extreme, RegOp());
        var tweaks = new RecordingTweakService();
        var vm = await NewVmLicensed(tweaks, new FakeLicenseService(AppEdition.Free, configured: true), free, ext);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        var applied = Assert.Single(tweaks.Applied);        // only the allowed tier reached the backend
        Assert.Equal("free", applied.Id);
        Assert.StartsWith("1 tweak(s) appliqué(s)", vm.StatusMessage);
        Assert.Contains("1 réservé(s) à Premium", vm.StatusMessage);
        Assert.Equal(1, vm.AppliedCount);
    }

    [Fact]
    public async Task ApplySelected_ConfiguredFree_WholeSelectionPremium_RecordsNothingInTheJournal()
    {
        var ext = PickedTier("ext", TweakTier.Extreme, RegOp());
        var journal = new RecordingApplyJournal();
        var vm = await NewVmLicensed(new RecordingTweakService(),
                                     new FakeLicenseService(AppEdition.Free, configured: true), journal, ext);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Empty(journal.Entries);                      // a gated no-op leaves no audit entry, same as nothing-selected
    }

    [Fact]
    public async Task ApplySelected_ConfiguredFree_Mixed_JournalsOnlyTheAllowedIds()
    {
        var free = PickedTier("free", TweakTier.Tranquille, RegOp());
        var ext = PickedTier("ext", TweakTier.Extreme, RegOp());
        var journal = new RecordingApplyJournal();
        var vm = await NewVmLicensed(new RecordingTweakService(),
                                     new FakeLicenseService(AppEdition.Free, configured: true), journal, free, ext);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal(new[] { "free" }, entry.TweakIds);     // the locked premium id never enters the audit trail
    }

    [Fact]
    public async Task ApplySelected_ConfiguredPremium_AppliesEveryTier_WithNoLockedSuffix()
    {
        var free = PickedTier("free", TweakTier.Tranquille, RegOp());
        var ext = PickedTier("ext", TweakTier.Extreme, RegOp());
        var tweaks = new RecordingTweakService();
        var vm = await NewVmLicensed(tweaks, new FakeLicenseService(AppEdition.Premium, configured: true), free, ext);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Equal(2, tweaks.Applied.Count);
        Assert.Equal("2 tweak(s) appliqué(s)", vm.StatusMessage);   // nothing withheld → the pinned status is untouched
    }

    [Fact]
    public async Task ApplySelected_NotConfigured_AppliesEvenExtreme_OnAFreeEdition()
    {
        var ext = PickedTier("ext", TweakTier.Extreme, RegOp());
        var tweaks = new RecordingTweakService();
        // As-shipped state: no embedded key ⇒ licensing not configured ⇒ the gate is dormant, so even an Extreme
        // tweak applies on a Free edition. This proves the whole freemium gate is a no-op until a seller embeds a key.
        var vm = await NewVmLicensed(tweaks, new FakeLicenseService(AppEdition.Free, configured: false), ext);

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Single(tweaks.Applied);
        Assert.Equal("1 tweak(s) appliqué(s)", vm.StatusMessage);   // no suffix: nothing is reserved when unconfigured
    }

    [Fact]
    public async Task PreviewPlan_ConfiguredFree_WholeSelectionPremium_ShowsNoOverlay_AndPointsToLicence()
    {
        var ext = PickedTier("ext", TweakTier.Extreme, RegOp());
        var tweaks = new RecordingTweakService();
        var vm = await NewVmLicensed(tweaks, new FakeLicenseService(AppEdition.Free, configured: true), ext);

        await vm.PreviewPlanCommand.ExecuteAsync(null);

        Assert.False(vm.IsPlanVisible);                     // the preview refuses just as the apply does
        Assert.Null(vm.CurrentPlan);
        Assert.Empty(tweaks.Applied);
        Assert.Contains("réservé(s) à Premium", vm.StatusMessage);
    }

    [Fact]
    public async Task PreviewPlan_ConfiguredFree_Mixed_PlansOnlyTheAllowed_AndNotesTheExcluded()
    {
        var free = PickedTier("free", TweakTier.Tranquille, RegOp());
        var ext = PickedTier("ext", TweakTier.Extreme, RegOp());
        var tweaks = new RecordingTweakService();
        var vm = await NewVmLicensed(tweaks, new FakeLicenseService(AppEdition.Free, configured: true), free, ext);

        await vm.PreviewPlanCommand.ExecuteAsync(null);

        Assert.True(vm.IsPlanVisible);
        Assert.NotNull(vm.CurrentPlan);
        Assert.Equal(1, vm.CurrentPlan!.TweakCount);        // the overlay reflects only what apply would actually run
        Assert.Contains("exclu(s) de l'aperçu", vm.StatusMessage);
    }
}
