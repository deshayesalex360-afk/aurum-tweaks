using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the Profiles page's "Charger" honesty contract — the click that turns a preset into real registry/SCM
/// writes (it used to be a dead button). It is the Dashboard's apply contract applied to presets, so it obeys
/// the same rules: report the REAL backend count (never a fabricated one), only claim a restore point when the
/// toggle is on, never re-apply what is already live, admit partial failures, and — the load-bearing one —
/// never apply an anti-cheat-risky tweak from the competitive-safe preset. Driven entirely through fakes; the
/// VM's ctor LoadAsync settles inline because every fake returns a completed Task.
/// </summary>
public class ProfilesViewModelTests
{
    private static Tweak Tw(string id, TweakTier tier = TweakTier.Tranquille, RiskLevel risk = RiskLevel.None,
                            TweakCategory cat = TweakCategory.PerformanceMultimedia, bool acConcern = false,
                            bool applied = false)
        => new()
        {
            Id = id,
            Name = new() { ["fr"] = id },
            Tier = tier,
            Risk = risk,
            Category = cat,
            IsApplied = applied,
            AntiCheat = acConcern ? new AntiCheatMatrix { Vanguard = AntiCheatStatus.Risky } : new AntiCheatMatrix()
        };

    // A tweak carrying ONE registry operation, so a pair of these can genuinely conflict (same target, different
    // applied value) and drive the real TweakConflictDetector through the profile-card Describe path.
    private static Tweak TwReg(string id, string regName, string apply)
        => new()
        {
            Id = id,
            Name = new() { ["fr"] = id },
            Operations =
            {
                new TweakOperation
                {
                    Type = OperationType.Registry, Hive = "HKLM", Key = "K",
                    Name = regName, Apply = apply, Revert = "0", ValueType = RegistryValueType.DWord
                }
            }
        };

    private static ProfilesViewModel NewVm(
        IEnumerable<Tweak> catalog,
        RecordingTweakService tweaks,
        FakeAppSettingsStore? settings = null,
        RecordingApplyJournal? journal = null,
        FakeLicenseService? license = null,
        PreflightBannerViewModel? preflight = null)
        => new(
            new FakeProfileService(),                      // serves the real six built-in presets
            new FakeTweakRepository(catalog),
            tweaks,
            settings ?? new FakeAppSettingsStore(),
            journal ?? new RecordingApplyJournal(),
            license ?? new FakeLicenseService(),
            preflight ?? new PreflightBannerViewModel(new FakePreflightService()));

    // Overload that injects the profile store, so a test can seed user profiles and/or assert what was persisted.
    private static ProfilesViewModel NewVm(
        IEnumerable<Tweak> catalog,
        RecordingTweakService tweaks,
        FakeProfileService profiles)
        => new(profiles, new FakeTweakRepository(catalog), tweaks,
               new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(),
               new PreflightBannerViewModel(new FakePreflightService()));

    private static Profile Preset(ProfilesViewModel vm, string id) => vm.Presets.Single(p => p.Id == id);

    [Fact]
    public async Task Preflight_IsSurfacedOnTheProfilesPage_FromTheSharedProbe()
    {
        // Applying a profile is the heaviest apply surface (a whole bundle through ApplyManyAsync), so it must forecast
        // the SAME restore-point / pending-reboot posture as the Tweaks page and the dashboard one-click — bound to the
        // same shared banner VM, not a private one. A genuine caution must reach it before any « Charger ».
        var preflight = new PreflightBannerViewModel(new FakePreflightService
        {
            Verdict = PreflightEvaluator.Evaluate(new PreflightSignals(
                RestorePointRequested: true, SystemRestoreReadable: false, RebootPending: false))
        });
        var vm = NewVm(new[] { Tw("t1") }, new RecordingTweakService(), preflight: preflight);
        await vm.Preflight.Initialization;

        Assert.Same(preflight, vm.Preflight);     // the page exposes the injected shared banner, not a fresh private one
        Assert.True(vm.Preflight.HasCaution);      // and the genuine caution is forecast, not swallowed
    }

    // ---- The count is the backend's, and the composed set is what actually gets applied ----

    [Fact]
    public async Task ApplyProfile_BuiltInPreset_AppliesTheComposedSet_AndReportsTheRealCount()
    {
        // Tranquille composes to exactly the Tranquille-tier tweaks; the status count must equal what landed.
        var t1 = Tw("t1", TweakTier.Tranquille);
        var t2 = Tw("t2", TweakTier.Tranquille);
        var avance = Tw("a1", TweakTier.Avance);          // not part of the Tranquille preset
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new[] { t1, t2, avance }, tweaks);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));

        Assert.Equal(2, tweaks.Applied.Count);
        Assert.Contains(t1, tweaks.Applied);
        Assert.Contains(t2, tweaks.Applied);
        Assert.DoesNotContain(avance, tweaks.Applied);
        Assert.StartsWith("2 optimisation(s) appliquée(s)", vm.Status);
        Assert.Contains("Un point de restauration a été créé", vm.Status);   // toggle on by default
    }

    [Fact]
    public async Task ApplyProfile_WhenRequiredRestorePointFails_ShowsHonestReason_AppliesNothing_ResetsBusy_AndDoesNotJournal()
    {
        // Profiles is an apply surface too: a failed required restore point must abort here exactly as on the Tweaks
        // page — honest reason, nothing applied, no audit entry, and the busy flag reset so the page isn't stuck.
        // (This also makes the page's "Un point de restauration a été créé." claim honest: the abort fires before it.)
        var t1 = Tw("t1", TweakTier.Tranquille);
        var tweaks = new RecordingTweakService { RestorePointWillFail = true };
        var journal = new RecordingApplyJournal();
        var vm = NewVm(new[] { t1 }, tweaks, journal: journal);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));

        Assert.Equal(TweakApplyText.RestorePointFailed, vm.Status);
        Assert.Empty(tweaks.Applied);                       // nothing was applied
        Assert.False(vm.IsApplying);                        // not stuck busy
        Assert.Empty(journal.Entries);                      // no audit entry for a batch that never ran
    }

    // ---- Each card discloses its composition before « Charger » (informed consent, resolved from the live catalogue) ----

    [Fact]
    public async Task Load_LabelsEachPresetCard_WithItsHonestComposition()
    {
        var vm = NewVm(new[]
        {
            Tw("t1", TweakTier.Tranquille),
            Tw("t2", TweakTier.Tranquille),
            Tw("a1", TweakTier.Avance)
        }, new RecordingTweakService());
        await vm.RefreshAsync();   // ensure the catalogue-resolved labels are in place

        // Tranquille composes to exactly the two tranquille tweaks — said honestly, no invented higher-tier clause.
        Assert.Equal("2 tweak(s) · 2 tranquille", Preset(vm, "preset-tranquille").CompositionLabel);
        // Stock contains nothing — the card admits it rather than implying a phantom batch.
        Assert.Equal("Aucun tweak", Preset(vm, "preset-stock").CompositionLabel);
    }

    [Fact]
    public async Task Load_FlagsRiskOnCardsThatCarryIt_AndStaysSilentOnSafeOnes()
    {
        // preset-extreme resolves to everything (so it carries the anti-cheat-risky tweak); preset-gaming-safe
        // excludes every anti-cheat concern by construction. The card caution must mirror exactly that: present on
        // the risky preset, empty on the competitive-safe one — the same risk the « Charger » gate names, earlier.
        var vm = NewVm(new[]
        {
            Tw("clean", TweakTier.Tranquille),
            Tw("risky", TweakTier.Avance, cat: TweakCategory.Gaming, acConcern: true)
        }, new RecordingTweakService());
        await vm.RefreshAsync();

        Assert.Equal("Attention : 1 à risque anti-cheat", Preset(vm, "preset-extreme").RiskHint);
        Assert.Equal(string.Empty, Preset(vm, "preset-gaming-safe").RiskHint);
    }

    // ---- Dupliquer: fork a profile into a fresh, editable user profile (the copy is persisted, never a ghost) ----

    [Fact]
    public async Task DuplicateProfile_OnAUserProfile_AddsAPersistedCopy_WithFreshIdAndCopieName_PreservingIds()
    {
        var profiles = new FakeProfileService();
        var vm = NewVm(new[] { Tw("a"), Tw("b") }, new RecordingTweakService(), profiles);
        var source = new Profile { Name = "Mon setup", IsBuiltIn = false, TweakIds = { "a", "b" } };

        await vm.DuplicateProfileCommand.ExecuteAsync(source);

        var copy = Assert.Single(vm.UserProfiles);
        Assert.Equal("Mon setup (copie)", copy.Name);
        Assert.NotEqual(source.Id, copy.Id);                 // a fresh profile, never an overwrite of the source
        Assert.False(copy.IsBuiltIn);
        Assert.Equal(new[] { "a", "b" }, copy.TweakIds);
        Assert.Contains(copy, profiles.Saved);               // genuinely persisted, not just added to the in-memory list
        Assert.Contains("dupliqué", vm.Status);
    }

    [Fact]
    public async Task DuplicateProfile_Twice_YieldsDistinctOrderedNames()
    {
        var vm = NewVm(new[] { Tw("a") }, new RecordingTweakService());
        var source = new Profile { Name = "Setup", IsBuiltIn = false, TweakIds = { "a" } };

        await vm.DuplicateProfileCommand.ExecuteAsync(source);
        await vm.DuplicateProfileCommand.ExecuteAsync(source);

        Assert.Equal(new[] { "Setup (copie)", "Setup (copie 2)" },
                     vm.UserProfiles.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task DuplicateProfile_OnAPreset_FreezesItsResolvedMembership_IntoAnEditableUserProfile()
    {
        // preset-extreme resolves to every catalogue tweak; the fork must capture that snapshot as explicit ids.
        var vm = NewVm(new[] { Tw("a"), Tw("b", TweakTier.Extreme) }, new RecordingTweakService());

        await vm.DuplicateProfileCommand.ExecuteAsync(Preset(vm, "preset-extreme"));

        var copy = Assert.Single(vm.UserProfiles);
        Assert.False(copy.IsBuiltIn);                        // a forked preset is an editable user profile
        Assert.Equal(new[] { "a", "b" }, copy.TweakIds);     // the preset's dynamic membership, frozen to a snapshot
    }

    [Fact]
    public async Task DuplicateProfile_OnAnEmptyPreset_DeclinesHonestly_WithoutMintingADoNothingCopy()
    {
        // Stock resolves to nothing — duplicating it must create no profile and say so, not a phantom empty card.
        var vm = NewVm(new[] { Tw("a") }, new RecordingTweakService());

        await vm.DuplicateProfileCommand.ExecuteAsync(Preset(vm, "preset-stock"));

        Assert.Empty(vm.UserProfiles);
        Assert.Contains("aucun tweak", vm.Status);
    }

    // ---- Bundle: back up / migrate every user profile in one file (import reconciles each against the catalogue) ----

    [Fact]
    public async Task ImportBundle_StoresEveryRecognizedProfile_AndPersistsThem()
    {
        var profiles = new FakeProfileService();
        var vm = NewVm(new[] { Tw("a"), Tw("b") }, new RecordingTweakService(), profiles);
        var json = ProfileBundle.Serialize(new[]
        {
            new Profile { Name = "Setup A", TweakIds = { "a" } },
            new Profile { Name = "Setup B", TweakIds = { "b" } }
        });

        await vm.ImportBundleJsonAsync(json);

        Assert.Equal(new[] { "Setup A", "Setup B" }, vm.UserProfiles.Select(p => p.Name).ToArray());
        Assert.Equal(2, profiles.Saved.Count);             // both genuinely persisted, not just shown in the list
        Assert.Contains("Lot importé", vm.Status);
    }

    [Fact]
    public async Task ImportBundle_SkipsUnimportableProfiles_KeepingTheRest()
    {
        var vm = NewVm(new[] { Tw("a") }, new RecordingTweakService());
        var json = ProfileBundle.Serialize(new[]
        {
            new Profile { Name = "Good", TweakIds = { "a" } },
            new Profile { Name = "Bad", TweakIds = { "ghost" } }    // no recognized tweak → dropped
        });

        await vm.ImportBundleJsonAsync(json);

        var kept = Assert.Single(vm.UserProfiles);
        Assert.Equal("Good", kept.Name);
        Assert.Contains("1 ignoré", vm.Status);
    }

    [Fact]
    public async Task ExportAllProfiles_WithNoUserProfiles_DeclinesHonestly_WithoutOpeningAFileDialog()
    {
        // The empty-guard returns before any SaveFileDialog, so this is safe to exercise headless.
        var vm = NewVm(new[] { Tw("a") }, new RecordingTweakService());

        await vm.ExportAllProfilesCommand.ExecuteAsync(null);

        Assert.Equal("Aucun profil personnalisé à exporter.", vm.Status);
    }

    // ---- Comparer deux profils: set-diff two resolved memberships (shared / propre à A / propre à B) ----

    [Fact]
    public async Task CompareProfiles_TwoUserProfiles_FillsTheThreeBuckets_AndSummarizes()
    {
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Setup A", TweakIds = { "a", "b" } },
            new Profile { Name = "Setup B", TweakIds = { "b", "c" } }
        });
        var vm = NewVm(new[] { Tw("a"), Tw("b"), Tw("c") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        vm.CompareLeft = vm.UserProfiles.Single(p => p.Name == "Setup A");
        vm.CompareRight = vm.UserProfiles.Single(p => p.Name == "Setup B");
        await vm.CompareProfilesCommand.ExecuteAsync(null);

        Assert.True(vm.HasComparison);
        Assert.Equal("a", vm.CompareOnlyLeftText);     // ids rendered exactly how the Tweaks page titles them
        Assert.Equal("b", vm.CompareSharedText);
        Assert.Equal("c", vm.CompareOnlyRightText);
        Assert.Contains("1 en commun", vm.CompareSummary);
    }

    [Fact]
    public async Task CompareProfiles_PresetVsUserProfile_ResolvesBothOnEqualFooting()
    {
        // preset-tranquille resolves to BOTH tranquille tweaks; comparing it must diff that resolved membership,
        // not the preset's (empty) stored id list — otherwise a preset would look like it contains nothing.
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Mine", TweakIds = { "calm1" } }
        });
        var vm = NewVm(new[]
        {
            Tw("calm1", TweakTier.Tranquille),
            Tw("calm2", TweakTier.Tranquille)
        }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        vm.CompareLeft = Preset(vm, "preset-tranquille");
        vm.CompareRight = vm.UserProfiles.Single();
        await vm.CompareProfilesCommand.ExecuteAsync(null);

        Assert.Equal("calm2", vm.CompareOnlyLeftText);   // the preset carries calm2, which the user profile lacks
        Assert.Equal("calm1", vm.CompareSharedText);
        Assert.Equal("—", vm.CompareOnlyRightText);      // the user profile adds nothing beyond the preset
    }

    [Fact]
    public async Task CompareProfiles_WithoutTwoSelections_DeclinesHonestly()
    {
        var vm = NewVm(new[] { Tw("a") }, new RecordingTweakService());
        vm.CompareLeft = Preset(vm, "preset-stock");   // only one side picked

        await vm.CompareProfilesCommand.ExecuteAsync(null);

        Assert.False(vm.HasComparison);
        Assert.Equal("Sélectionnez deux profils à comparer.", vm.Status);
    }

    [Fact]
    public async Task AllProfiles_IncludesPresetsAndUserProfiles_AndPicksUpOnesAddedThisSession()
    {
        var vm = NewVm(new[] { Tw("a") }, new RecordingTweakService());
        await vm.RefreshAsync();
        var baseline = vm.AllProfiles.Count;
        Assert.Equal(vm.Presets.Count + vm.UserProfiles.Count, baseline);   // the union of both source lists

        await vm.DuplicateProfileCommand.ExecuteAsync(
            new Profile { Name = "Setup", IsBuiltIn = false, TweakIds = { "a" } });

        Assert.Equal(baseline + 1, vm.AllProfiles.Count);   // a profile added this session shows up without a reload
        Assert.Contains(vm.AllProfiles, p => p.Name == "Setup (copie)");
    }

    // ---- Fusionner: union two resolved memberships into a fresh, persisted user profile ----

    [Fact]
    public async Task MergeProfiles_TwoUserProfiles_PersistsTheUnion_NamedAPlusB()
    {
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Setup A", TweakIds = { "a", "b" } },
            new Profile { Name = "Setup B", TweakIds = { "b", "c" } }
        });
        var vm = NewVm(new[] { Tw("a"), Tw("b"), Tw("c") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        vm.CompareLeft = vm.UserProfiles.Single(p => p.Name == "Setup A");
        vm.CompareRight = vm.UserProfiles.Single(p => p.Name == "Setup B");
        await vm.MergeProfilesCommand.ExecuteAsync(null);

        var merged = vm.UserProfiles.Single(p => p.Name == "Setup A + Setup B");
        Assert.Equal(new[] { "a", "b", "c" }, merged.TweakIds);   // A's ids, then only what B adds — the shared "b" once
        Assert.Contains(profiles.Saved, p => p.Name == "Setup A + Setup B");   // persisted, not merely added in-memory
        Assert.Contains("3 tweak(s)", vm.Status);
    }

    [Fact]
    public async Task MergeProfiles_DerivesCompetitiveSafe_TrueWhenNoMemberTripsAnAntiCheat()
    {
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Safe A", TweakIds = { "a" } },
            new Profile { Name = "Safe B", TweakIds = { "b" } }
        });
        var vm = NewVm(new[] { Tw("a"), Tw("b") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        vm.CompareLeft = vm.UserProfiles.Single(p => p.Name == "Safe A");
        vm.CompareRight = vm.UserProfiles.Single(p => p.Name == "Safe B");
        await vm.MergeProfilesCommand.ExecuteAsync(null);

        Assert.True(vm.UserProfiles.Single(p => p.Name == "Safe A + Safe B").IsCompetitiveSafe);
    }

    [Fact]
    public async Task MergeProfiles_DerivesCompetitiveSafe_FalseWhenAMemberIsAntiCheatRisky()
    {
        // The badge is DERIVED from the actual union, never copied: one anti-cheat-risky member sinks it even though
        // both source profiles falsely advertise themselves safe. The merge re-checks reality rather than trusting flags.
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Safe",  IsCompetitiveSafe = true, TweakIds = { "a" } },
            new Profile { Name = "Risky", IsCompetitiveSafe = true, TweakIds = { "x" } }
        });
        var vm = NewVm(new[] { Tw("a"), Tw("x", acConcern: true) }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        vm.CompareLeft = vm.UserProfiles.Single(p => p.Name == "Safe");
        vm.CompareRight = vm.UserProfiles.Single(p => p.Name == "Risky");
        await vm.MergeProfilesCommand.ExecuteAsync(null);

        Assert.False(vm.UserProfiles.Single(p => p.Name == "Safe + Risky").IsCompetitiveSafe);
    }

    [Fact]
    public async Task MergeProfiles_WithoutTwoSelections_DeclinesHonestly()
    {
        var profiles = new FakeProfileService();
        var vm = NewVm(new[] { Tw("a") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();
        vm.CompareLeft = Preset(vm, "preset-tranquille");   // only one side picked

        await vm.MergeProfilesCommand.ExecuteAsync(null);

        Assert.Equal("Sélectionnez deux profils à fusionner.", vm.Status);
        Assert.Empty(profiles.Saved);   // nothing persisted from a refused merge
    }

    [Fact]
    public async Task MergeProfiles_SamePairTwice_ProducesTwoDistinctlyNamedCards()
    {
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "A", TweakIds = { "a" } },
            new Profile { Name = "B", TweakIds = { "b" } }
        });
        var vm = NewVm(new[] { Tw("a"), Tw("b") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();
        vm.CompareLeft = vm.UserProfiles.Single(p => p.Name == "A");
        vm.CompareRight = vm.UserProfiles.Single(p => p.Name == "B");

        await vm.MergeProfilesCommand.ExecuteAsync(null);
        await vm.MergeProfilesCommand.ExecuteAsync(null);   // same pickers, second time disambiguates the name

        Assert.Contains(vm.UserProfiles, p => p.Name == "A + B");
        Assert.Contains(vm.UserProfiles, p => p.Name == "A + B (2)");
    }

    // ---- Soustraire: A minus B — a profile of the tweaks in A that B does not have (the "propre à A" bucket) ----

    [Fact]
    public async Task SubtractProfiles_CreatesProfileOfTheTweaksOnlyInA_NamedASansB()
    {
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "A", TweakIds = { "a", "b", "c" } },
            new Profile { Name = "B", TweakIds = { "b" } }
        });
        var vm = NewVm(new[] { Tw("a"), Tw("b"), Tw("c") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        vm.CompareLeft = vm.UserProfiles.Single(p => p.Name == "A");
        vm.CompareRight = vm.UserProfiles.Single(p => p.Name == "B");
        await vm.SubtractProfilesCommand.ExecuteAsync(null);

        var result = vm.UserProfiles.Single(p => p.Name == "A sans B");
        Assert.Equal(new[] { "a", "c" }, result.TweakIds);   // "b" is in B, so it's dropped; a and c survive in A's order
        Assert.Contains(profiles.Saved, p => p.Name == "A sans B");   // persisted, not merely added in-memory
        Assert.Contains("2 tweak(s)", vm.Status);
    }

    [Fact]
    public async Task SubtractProfiles_WhenAllOfAIsAlsoInB_DeclinesHonestly_PersistingNothing()
    {
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "A", TweakIds = { "a", "b" } },
            new Profile { Name = "B", TweakIds = { "a", "b", "c" } }
        });
        var vm = NewVm(new[] { Tw("a"), Tw("b"), Tw("c") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        vm.CompareLeft = vm.UserProfiles.Single(p => p.Name == "A");
        vm.CompareRight = vm.UserProfiles.Single(p => p.Name == "B");
        await vm.SubtractProfilesCommand.ExecuteAsync(null);

        Assert.DoesNotContain(vm.UserProfiles, p => p.Name == "A sans B");
        Assert.Empty(profiles.Saved);
        Assert.Contains("ne contient aucun tweak absent de", vm.Status);
    }

    [Fact]
    public async Task SubtractProfiles_DerivesCompetitiveSafe_TrueWhenTheRiskyTweakWasSubtractedAway()
    {
        // The only anti-cheat-risky tweak ("x") lives in B, so subtracting B leaves a result that is genuinely safe —
        // the "maintain a 'tweaks à éviter' profile and subtract it" workflow, with the badge earned by the real members.
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "A", TweakIds = { "a", "x" } },
            new Profile { Name = "B", TweakIds = { "x" } }
        });
        var vm = NewVm(new[] { Tw("a"), Tw("x", acConcern: true) }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        vm.CompareLeft = vm.UserProfiles.Single(p => p.Name == "A");
        vm.CompareRight = vm.UserProfiles.Single(p => p.Name == "B");
        await vm.SubtractProfilesCommand.ExecuteAsync(null);

        Assert.True(vm.UserProfiles.Single(p => p.Name == "A sans B").IsCompetitiveSafe);
    }

    [Fact]
    public async Task SubtractProfiles_DerivesCompetitiveSafe_FalseWhenARiskyTweakSurvives()
    {
        // The risky "x" is unique to A, so it survives the subtraction → the result cannot be competitive-safe,
        // regardless of A's (here false) self-advertised flag. Derived from reality, never copied.
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "A", IsCompetitiveSafe = true, TweakIds = { "a", "x" } },
            new Profile { Name = "B", TweakIds = { "a" } }
        });
        var vm = NewVm(new[] { Tw("a"), Tw("x", acConcern: true) }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        vm.CompareLeft = vm.UserProfiles.Single(p => p.Name == "A");
        vm.CompareRight = vm.UserProfiles.Single(p => p.Name == "B");
        await vm.SubtractProfilesCommand.ExecuteAsync(null);

        Assert.False(vm.UserProfiles.Single(p => p.Name == "A sans B").IsCompetitiveSafe);
    }

    [Fact]
    public async Task SubtractProfiles_WithoutTwoSelections_DeclinesHonestly()
    {
        var profiles = new FakeProfileService();
        var vm = NewVm(new[] { Tw("a") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();
        vm.CompareLeft = Preset(vm, "preset-tranquille");   // only one side picked

        await vm.SubtractProfilesCommand.ExecuteAsync(null);

        Assert.Equal("Sélectionnez deux profils à soustraire.", vm.Status);
        Assert.Empty(profiles.Saved);   // nothing persisted from a refused subtraction
    }

    // ---- Vérifier: read the LIVE system and report how much of a profile is actually applied (read-only) ----

    [Fact]
    public async Task CheckProfileOnSystem_AllMembersApplied_ReportsFullRatioWithCheck()
    {
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Mon setup", TweakIds = { "a", "b" } }
        });
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("a");
        tweaks.DetectAppliedIds.Add("b");          // the live machine reports both as applied
        var vm = NewVm(new[] { Tw("a"), Tw("b") }, tweaks, profiles);
        await vm.RefreshAsync();

        await vm.CheckProfileOnSystemCommand.ExecuteAsync(vm.UserProfiles.Single(p => p.Name == "Mon setup"));

        Assert.Equal("« Mon setup » : 2/2 tweak(s) appliqué(s) ✓", vm.Status);
    }

    [Fact]
    public async Task CheckProfileOnSystem_MixedState_ReportsHonestRatio_NoCheck()
    {
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Setup", TweakIds = { "a", "b", "c" } }
        });
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("a");          // only "a" is live; b and c read back NotApplied
        var vm = NewVm(new[] { Tw("a"), Tw("b"), Tw("c") }, tweaks, profiles);
        await vm.RefreshAsync();

        await vm.CheckProfileOnSystemCommand.ExecuteAsync(vm.UserProfiles.Single(p => p.Name == "Setup"));

        Assert.Equal("« Setup » : 1/3 tweak(s) appliqué(s), 2 non appliqué(s).", vm.Status);
        Assert.DoesNotContain("✓", vm.Status);
    }

    [Fact]
    public async Task CheckProfileOnSystem_IndeterminateMember_IsDisclosedSeparately_NeverAsApplied()
    {
        // "b" is shell-only — the engine can't read it back. It must be reported as indéterminé, NOT counted as
        // applied, and the profile must not earn the ✓ on its account.
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Setup", TweakIds = { "a", "b" } }
        });
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("a");
        tweaks.IndeterminateIds.Add("b");
        var vm = NewVm(new[] { Tw("a"), Tw("b") }, tweaks, profiles);
        await vm.RefreshAsync();

        await vm.CheckProfileOnSystemCommand.ExecuteAsync(vm.UserProfiles.Single(p => p.Name == "Setup"));

        Assert.Equal("« Setup » : 1/2 tweak(s) appliqué(s), 1 indéterminé(s).", vm.Status);
    }

    [Fact]
    public async Task CheckProfileOnSystem_IsReadOnly_AppliesAndRevertsNothing()
    {
        // The load-bearing promise: « Vérifier » only READS — it must never apply or revert a single tweak, so a
        // user can ask "is this active?" without changing their machine.
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Setup", TweakIds = { "a", "b" } }
        });
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new[] { Tw("a"), Tw("b") }, tweaks, profiles);
        await vm.RefreshAsync();

        await vm.CheckProfileOnSystemCommand.ExecuteAsync(vm.UserProfiles.Single(p => p.Name == "Setup"));

        Assert.Empty(tweaks.Applied);
        Assert.Empty(tweaks.Reverted);
        Assert.Empty(profiles.Saved);
    }

    [Fact]
    public async Task CheckProfileOnSystem_BuiltInPreset_ResolvesItsMembership_AndReportsAgainstTheLiveCatalogue()
    {
        // Proves « Vérifier » works on presets too: it resolves the preset's composition (Tranquille-tier here) from
        // the live catalogue, then probes exactly those — not the whole catalogue.
        var tweaks = new RecordingTweakService();
        tweaks.DetectAppliedIds.Add("t1");
        tweaks.DetectAppliedIds.Add("t2");         // both Tranquille members are live; the Avance tweak is irrelevant
        var vm = NewVm(new[]
        {
            Tw("t1", TweakTier.Tranquille),
            Tw("t2", TweakTier.Tranquille),
            Tw("a1", TweakTier.Avance)
        }, tweaks);
        await vm.RefreshAsync();

        await vm.CheckProfileOnSystemCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));

        Assert.Contains("2/2 tweak(s) appliqué(s) ✓", vm.Status);
    }

    [Fact]
    public async Task CheckProfileOnSystem_EmptyComposition_SaysNothingToVerify()
    {
        // preset-stock resolves to no tweaks → an honest "nothing to verify", never a fabricated 0/0 ✓.
        var vm = NewVm(new[] { Tw("a") }, new RecordingTweakService());
        await vm.RefreshAsync();

        await vm.CheckProfileOnSystemCommand.ExecuteAsync(Preset(vm, "preset-stock"));

        Assert.Contains("ne contient aucun tweak à vérifier", vm.Status);
    }

    [Fact]
    public async Task CheckProfileOnSystem_NullProfile_IsAQuietNoOp()
    {
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new[] { Tw("a") }, tweaks);
        await vm.RefreshAsync();
        var statusBefore = vm.Status;

        await vm.CheckProfileOnSystemCommand.ExecuteAsync(null);

        Assert.Equal(statusBefore, vm.Status);     // no crash, no status churn
        Assert.Empty(tweaks.Applied);
    }

    // ---- Conflict awareness: a card honestly warns when its resolved set is internally contradictory ----

    [Fact]
    public async Task Describe_ConsistentProfile_LeavesTheConflictHintEmpty()
    {
        // Two tweaks touching DIFFERENT registry values agree by construction → no conflict, no warning line.
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Net", TweakIds = { "a", "b" } }
        });
        var vm = NewVm(new[] { TwReg("a", "Foo", "1"), TwReg("b", "Bar", "1") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        Assert.True(string.IsNullOrEmpty(vm.UserProfiles.Single(p => p.Name == "Net").ConflictHint));
    }

    [Fact]
    public async Task Describe_ContradictoryProfile_SurfacesTheConflictHint()
    {
        // a and b both write HKLM\K\Foo but to different values → a real cross-tweak conflict the card must disclose.
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "Bagarre", TweakIds = { "a", "b" } }
        });
        var vm = NewVm(new[] { TwReg("a", "Foo", "1"), TwReg("b", "Foo", "0") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        Assert.Contains("conflit", vm.UserProfiles.Single(p => p.Name == "Bagarre").ConflictHint);
    }

    [Fact]
    public async Task MergeProfiles_WhenTheUnionIsContradictory_TheMergedCardWarnsImmediately()
    {
        // The headline scenario: A and B each look fine alone, but their union pulls one registry value two ways.
        // The merge must not bury that — the new card carries the conflict warning the moment it's created.
        var profiles = new FakeProfileService(userProfiles: new[]
        {
            new Profile { Name = "A", TweakIds = { "a" } },
            new Profile { Name = "B", TweakIds = { "b" } }
        });
        var vm = NewVm(new[] { TwReg("a", "Foo", "1"), TwReg("b", "Foo", "0") }, new RecordingTweakService(), profiles);
        await vm.RefreshAsync();

        vm.CompareLeft = vm.UserProfiles.Single(p => p.Name == "A");
        vm.CompareRight = vm.UserProfiles.Single(p => p.Name == "B");
        await vm.MergeProfilesCommand.ExecuteAsync(null);

        Assert.Contains("conflit", vm.UserProfiles.Single(p => p.Name == "A + B").ConflictHint);
    }

    // ---- No-op honesty: an empty composition fabricates neither a count nor a restore claim ----

    [Fact]
    public async Task ApplyProfile_Stock_AppliesNothing_AndMakesNoCountOrRestoreClaim()
    {
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new[] { Tw("t1", TweakTier.Tranquille) }, tweaks);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-stock"));

        Assert.Empty(tweaks.Applied);
        Assert.DoesNotContain("appliquée(s)", vm.Status);
        Assert.DoesNotContain("point de restauration", vm.Status);
        Assert.Contains("aucun tweak", vm.Status);
    }

    // ---- The restore-point claim is keyed to the toggle, not invented ----

    [Fact]
    public async Task ApplyProfile_WhenRestoreToggleOff_AdmitsNoRestorePointWasCreated()
    {
        var settings = new FakeAppSettingsStore();
        settings.Current.CreateRestorePointBeforeTweaks = false;
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new[] { Tw("t1", TweakTier.Tranquille) }, tweaks, settings);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));

        Assert.Single(tweaks.Applied);
        Assert.DoesNotContain("Un point de restauration a été créé", vm.Status);
        Assert.Contains("sans point de restauration (option désactivée dans Paramètres)", vm.Status);
    }

    // ---- Detection honesty: nothing left to apply → no re-apply, no restore claim ----

    [Fact]
    public async Task ApplyProfile_WhenAllAlreadyApplied_SaysSo_AndDoesNotReapply()
    {
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new[] { Tw("t1", TweakTier.Tranquille, applied: true) }, tweaks);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));

        Assert.Empty(tweaks.Applied);
        Assert.Contains("déjà appliqué", vm.Status);
        Assert.DoesNotContain("point de restauration", vm.Status);
    }

    // ---- The ban-risk promise, enforced at the click ----

    [Fact]
    public async Task ApplyProfile_CompetitiveSafePreset_NeverAppliesAnAntiCheatRiskyTweak()
    {
        var safe = Tw("safe", TweakTier.Tranquille);
        var risky = Tw("risky", TweakTier.Tranquille, acConcern: true);   // present in the catalog, but ban-risky
        var tweaks = new RecordingTweakService();
        var vm = NewVm(new[] { safe, risky }, tweaks);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-gaming-safe"));

        Assert.Contains(safe, tweaks.Applied);
        Assert.DoesNotContain(risky, tweaks.Applied);
    }

    // ---- Partial failure is admitted, never hidden behind a smaller success count ----

    [Fact]
    public async Task ApplyProfile_PartialBackendFailure_LeadsWithAppliedCount_AndAdmitsFailures()
    {
        var ok = Tw("ok", TweakTier.Tranquille);
        var bad = Tw("bad", TweakTier.Tranquille);
        var tweaks = new RecordingTweakService();
        tweaks.FailIds.Add("bad");                          // the backend reports this one as failed
        var vm = NewVm(new[] { ok, bad }, tweaks);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));

        Assert.StartsWith("1 optimisation(s) appliquée(s)", vm.Status);   // the real success count
        Assert.Contains("1 échec(s)", vm.Status);                         // and the failure is admitted
    }

    // ---- User profiles are now genuinely loadable: "Charger" applies their explicit, stored tweak set ----

    [Fact]
    public async Task ApplyProfile_UserProfile_AppliesItsExplicitTweakIds_AndSkipsOthers()
    {
        var a = Tw("a", TweakTier.Tranquille);
        var b = Tw("b", TweakTier.Tranquille);
        var c = Tw("c", TweakTier.Tranquille);
        var user = new Profile { Name = "Mon profil", TweakIds = { "a", "c" } };   // b deliberately excluded
        var tweaks = new RecordingTweakService();
        var vm = new ProfilesViewModel(
            new FakeProfileService(userProfiles: new[] { user }),
            new FakeTweakRepository(new[] { a, b, c }),
            tweaks,
            new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());

        Assert.Equal(2, tweaks.Applied.Count);
        Assert.Contains(a, tweaks.Applied);
        Assert.Contains(c, tweaks.Applied);
        Assert.DoesNotContain(b, tweaks.Applied);     // only the ids the profile actually stored
    }

    // ---- Cross-page consistency: a profile saved after construction shows up on refresh (no app restart) ----

    [Fact]
    public async Task RefreshAsync_SurfacesAProfileSavedAfterConstruction()
    {
        var store = new FakeProfileService();             // no user profiles at first
        var vm = new ProfilesViewModel(store, new FakeTweakRepository(System.Array.Empty<Tweak>()),
                                       new RecordingTweakService(), new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));
        Assert.Empty(vm.UserProfiles);

        // Mirror a save coming from the Tweaks page (shared store): the list is otherwise built once, at ctor.
        await store.SaveAsync(new Profile { Name = "Nouveau", TweakIds = { "x" } });
        await vm.RefreshAsync();

        var shown = Assert.Single(vm.UserProfiles);
        Assert.Equal("Nouveau", shown.Name);
    }

    // ---- Rename a user profile in place: same id (file overwritten, no orphan), card reflects it ----

    [Fact]
    public async Task RenameProfile_PersistsTheNewName_UnderTheSameId_AndUpdatesTheCard()
    {
        var user = new Profile { Name = "Ancien", TweakIds = { "x" } };
        var originalId = user.Id;
        var profiles = new FakeProfileService(userProfiles: new[] { user });
        var vm = new ProfilesViewModel(profiles, new FakeTweakRepository(System.Array.Empty<Tweak>()),
                                       new RecordingTweakService(), new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        vm.BeginRenameCommand.Execute(vm.UserProfiles.Single());
        vm.RenameText = "Nouveau nom";
        await vm.CommitRenameCommand.ExecuteAsync(null);

        var saved = Assert.Single(profiles.Saved);
        Assert.Equal("Nouveau nom", saved.Name);
        Assert.Equal(originalId, saved.Id);                           // same id → file overwritten, no orphan copy
        Assert.Equal("Nouveau nom", vm.UserProfiles.Single().Name);   // the visible card reflects the new name
        Assert.Null(vm.Renaming);                                     // editor closed after a successful commit
    }

    // ---- Honest no-op: a blank name is never stored, and the editor stays open so the user can fix it ----

    [Fact]
    public async Task RenameProfile_BlankName_IsRejected_AndWritesNothing()
    {
        var user = new Profile { Name = "Garde-moi", TweakIds = { "x" } };
        var profiles = new FakeProfileService(userProfiles: new[] { user });
        var vm = new ProfilesViewModel(profiles, new FakeTweakRepository(System.Array.Empty<Tweak>()),
                                       new RecordingTweakService(), new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        vm.BeginRenameCommand.Execute(vm.UserProfiles.Single());
        vm.RenameText = "   ";
        await vm.CommitRenameCommand.ExecuteAsync(null);

        Assert.Empty(profiles.Saved);                                 // never persist an unnamed profile
        Assert.Equal("Garde-moi", vm.UserProfiles.Single().Name);     // original name untouched
        Assert.NotNull(vm.Renaming);                                  // editor stays open to correct the name
    }

    // ---- Presets are immutable: a built-in never enters rename mode ----

    [Fact]
    public void BeginRename_BuiltInPreset_IsRefused()
    {
        var vm = NewVm(System.Array.Empty<Tweak>(), new RecordingTweakService());

        vm.BeginRenameCommand.Execute(Preset(vm, "preset-tranquille"));

        Assert.Null(vm.Renaming);
    }

    // ---- Cancel discards the edit without touching the store ----

    [Fact]
    public void CancelRename_ClosesTheEditor_WithoutWriting()
    {
        var user = new Profile { Name = "Inchangé", TweakIds = { "x" } };
        var profiles = new FakeProfileService(userProfiles: new[] { user });
        var vm = new ProfilesViewModel(profiles, new FakeTweakRepository(System.Array.Empty<Tweak>()),
                                       new RecordingTweakService(), new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        vm.BeginRenameCommand.Execute(vm.UserProfiles.Single());
        vm.RenameText = "Pas enregistré";
        vm.CancelRenameCommand.Execute(null);

        Assert.Null(vm.Renaming);
        Assert.Empty(profiles.Saved);
        Assert.Equal("Inchangé", vm.UserProfiles.Single().Name);
    }

    // ---- IsRenaming drives the editor's visibility binding; it must track the pointer ----

    [Fact]
    public void IsRenaming_TracksTheEditorPointer()
    {
        var user = new Profile { Name = "P", TweakIds = { "x" } };
        var profiles = new FakeProfileService(userProfiles: new[] { user });
        var vm = new ProfilesViewModel(profiles, new FakeTweakRepository(System.Array.Empty<Tweak>()),
                                       new RecordingTweakService(), new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));
        Assert.False(vm.IsRenaming);

        vm.BeginRenameCommand.Execute(vm.UserProfiles.Single());
        Assert.True(vm.IsRenaming);

        vm.CancelRenameCommand.Execute(null);
        Assert.False(vm.IsRenaming);
    }

    // ---- Import: an (untrusted) profile file becomes a real profile only after reconciling with the live catalog ----

    [Fact]
    public async Task ImportJson_RecognizedProfile_IsSaved_AndAppearsInTheList()
    {
        var profiles = new FakeProfileService();
        var vm = new ProfilesViewModel(profiles, new FakeTweakRepository(new[] { Tw("a"), Tw("b") }),
                                       new RecordingTweakService(), new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));
        var json = ProfileTransfer.Serialize(new Profile { Name = "Importé", TweakIds = { "a", "b" } });

        var result = await vm.ImportJsonAsync(json);

        Assert.True(result.Ok);
        var saved = Assert.Single(profiles.Saved);
        Assert.Equal("Importé", saved.Name);
        Assert.Contains(vm.UserProfiles, p => p.Name == "Importé");    // shows on the page without a restart
        Assert.Contains("importé", vm.Status);
    }

    [Fact]
    public async Task ImportJson_AllUnknown_SavesNothing_AndSaysSo()
    {
        var profiles = new FakeProfileService();
        var vm = new ProfilesViewModel(profiles, new FakeTweakRepository(new[] { Tw("a") }),
                                       new RecordingTweakService(), new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));
        var json = ProfileTransfer.Serialize(new Profile { Name = "Étranger", TweakIds = { "x", "y" } });

        var result = await vm.ImportJsonAsync(json);

        Assert.False(result.Ok);
        Assert.Empty(profiles.Saved);                                  // nothing applicable here → nothing stored
        Assert.Empty(vm.UserProfiles);
        Assert.Contains("rien à importer", vm.Status);
    }

    [Fact]
    public async Task ImportJson_Malformed_SavesNothing_AndReportsInvalid()
    {
        var profiles = new FakeProfileService();
        var vm = new ProfilesViewModel(profiles, new FakeTweakRepository(System.Array.Empty<Tweak>()),
                                       new RecordingTweakService(), new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        var result = await vm.ImportJsonAsync("{ broken json");

        Assert.False(result.Ok);
        Assert.Empty(profiles.Saved);
        Assert.Contains("invalide", vm.Status);
    }

    // ---- Apply-history: LastAppliedUtc is a real, persisted stamp — set only on a genuine apply, never for presets ----

    [Fact]
    public async Task ApplyProfile_UserProfileSucceeds_StampsLastApplied_AndPersistsIt()
    {
        var user = new Profile { Name = "P", TweakIds = { "a" } };
        var profiles = new FakeProfileService(userProfiles: new[] { user });
        var vm = new ProfilesViewModel(profiles, new FakeTweakRepository(new[] { Tw("a") }),
                                       new RecordingTweakService(), new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());

        Assert.NotNull(vm.UserProfiles.Single().LastAppliedUtc);                              // stamped (card reflects it)
        Assert.Contains(profiles.Saved, p => p.Id == user.Id && p.LastAppliedUtc != null);   // and persisted to disk
    }

    [Fact]
    public async Task ApplyProfile_BuiltInPreset_NeverWritesToTheUserStore()
    {
        // A preset is regenerated from code each launch; persisting it (to carry a stamp) would resurface it as a
        // phantom user profile. So a preset apply touches tweaks but must never write to the profile store.
        var profiles = new FakeProfileService();
        var vm = new ProfilesViewModel(profiles, new FakeTweakRepository(new[] { Tw("a", TweakTier.Tranquille) }),
                                       new RecordingTweakService(), new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.Presets.Single(p => p.Id == "preset-tranquille"));

        Assert.Empty(profiles.Saved);
    }

    [Fact]
    public async Task ApplyProfile_WhenEveryOpFails_DoesNotStampLastApplied()
    {
        var user = new Profile { Name = "P", TweakIds = { "a" } };
        var profiles = new FakeProfileService(userProfiles: new[] { user });
        var tweaks = new RecordingTweakService();
        tweaks.FailIds.Add("a");                                          // the only op fails → zero real successes
        var vm = new ProfilesViewModel(profiles, new FakeTweakRepository(new[] { Tw("a") }),
                                       tweaks, new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());

        Assert.Null(vm.UserProfiles.Single().LastAppliedUtc);            // a fully-failed apply never claims "applied"
        Assert.Empty(profiles.Saved);
    }

    // ---- Risky-apply confirmation gate: a heavy set (here Extreme-tier) is disclosed before any backend op runs ----

    [Fact]
    public async Task ApplyProfile_RiskyProfile_RequiresConfirmation_BeforeTouchingTheBackend()
    {
        var risky = Tw("x", TweakTier.Extreme);
        var user = new Profile { Name = "Risqué", TweakIds = { "x" } };
        var tweaks = new RecordingTweakService();
        var vm = new ProfilesViewModel(new FakeProfileService(userProfiles: new[] { user }),
                                       new FakeTweakRepository(new[] { risky }), tweaks, new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());

        Assert.True(vm.HasPendingConfirmation);                          // gate armed…
        Assert.Empty(tweaks.Applied);                                   // …and nothing executed yet
        Assert.False(string.IsNullOrEmpty(vm.PendingRiskSummary));      // the disclosure names what's risky
        Assert.Contains("Confirmation requise", vm.Status);
    }

    [Fact]
    public async Task ConfirmApply_AfterTheRiskyGate_AppliesTheProfile()
    {
        var risky = Tw("x", TweakTier.Extreme);
        var user = new Profile { Name = "Risqué", TweakIds = { "x" } };
        var tweaks = new RecordingTweakService();
        var vm = new ProfilesViewModel(new FakeProfileService(userProfiles: new[] { user }),
                                       new FakeTweakRepository(new[] { risky }), tweaks, new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());   // arm the gate
        await vm.ConfirmApplyCommand.ExecuteAsync(null);                       // user confirms

        Assert.False(vm.HasPendingConfirmation);
        Assert.Contains(risky, tweaks.Applied);                               // now genuinely applied
        Assert.StartsWith("1 optimisation(s) appliquée(s)", vm.Status);
    }

    [Fact]
    public async Task CancelApply_ClearsTheGate_WithoutApplying()
    {
        var risky = Tw("x", TweakTier.Extreme);
        var user = new Profile { Name = "Risqué", TweakIds = { "x" } };
        var tweaks = new RecordingTweakService();
        var vm = new ProfilesViewModel(new FakeProfileService(userProfiles: new[] { user }),
                                       new FakeTweakRepository(new[] { risky }), tweaks, new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());   // arm the gate
        vm.CancelApplyCommand.Execute(null);

        Assert.False(vm.HasPendingConfirmation);
        Assert.Empty(tweaks.Applied);                                         // dismissing touches nothing
        Assert.Contains("annulée", vm.Status);
    }

    [Fact]
    public async Task ApplyProfile_SafeProfile_AppliesImmediately_NoConfirmation()
    {
        var safe = Tw("a", TweakTier.Tranquille);
        var user = new Profile { Name = "Sûr", TweakIds = { "a" } };
        var tweaks = new RecordingTweakService();
        var vm = new ProfilesViewModel(new FakeProfileService(userProfiles: new[] { user }),
                                       new FakeTweakRepository(new[] { safe }), tweaks, new FakeAppSettingsStore(), new RecordingApplyJournal(), new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());

        Assert.False(vm.HasPendingConfirmation);                              // a safe set never arms the gate
        Assert.Contains(safe, tweaks.Applied);                               // it applies directly
    }

    // ---- Change journal: loading a profile IS an apply, so the audit trail must record it (the journal page
    //      promises "every application is recorded" — that claim is false if this path stays silent) ----

    [Fact]
    public async Task ApplyProfile_RecordsAnApplicationEntry_WithTheAppliedIds()
    {
        var journal = new RecordingApplyJournal();
        var vm = NewVm(new[] { Tw("t1", TweakTier.Tranquille), Tw("t2", TweakTier.Tranquille) },
                       new RecordingTweakService(), journal: journal);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("Application", entry.Action);                           // a profile load is an Application, not a Restauration
        Assert.Equal(2, entry.Succeeded);
        Assert.Equal(new[] { "t1", "t2" }, entry.TweakIds.OrderBy(id => id));
        Assert.Empty(entry.Unconfirmed);                                     // clean apply: the re-probe confirmed both, so nothing is flagged
        Assert.NotNull(vm.LastVerification);                                 // verification DID run (parity with Dashboard/Tweaks)…
        Assert.False(vm.HasUnconfirmed);                                     // …and found no contradiction → no banner
    }

    [Fact]
    public async Task ApplyProfile_WhenAWriteDoesntStick_FlagsItInBannerAndJournal()
    {
        // The heaviest apply surface now self-verifies like the Tweaks page and the Dashboard one-click: after loading
        // a bundle we RE-READ the machine, so a write the engine reported applied that does NOT read back is surfaced
        // (banner via HasUnconfirmed) AND audited durably (journal entry.Unconfirmed) — never trusted on the count alone.
        var t1 = Tw("t1", TweakTier.Tranquille);
        var stuck = Tw("stuck", TweakTier.Tranquille);
        var tweaks = new RecordingTweakService();
        tweaks.NotConfirmedIds.Add("stuck");          // engine reports it applied, but the readback says NOT applied
        var journal = new RecordingApplyJournal();
        var vm = NewVm(new[] { t1, stuck }, tweaks, journal: journal);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));

        Assert.True(vm.HasUnconfirmed);
        Assert.Equal("stuck", vm.LastVerification!.UnconfirmedLabel);
        var entry = Assert.Single(journal.Entries);
        Assert.Equal(new[] { "stuck" }, entry.Unconfirmed);
    }

    [Fact]
    public async Task ApplyProfile_WhenAWriteFails_IsNotMislabeledAsUnconfirmed()
    {
        // The other half of the honesty line the shared ITweakService.VerifyAppliedAsync draws — pinned at the heaviest
        // apply surface. A tweak the engine genuinely FAILED to apply (IsApplied never set) is EXCLUDED from
        // verification, because its absence on the machine is consistent with the failure: flagging it as « didn't
        // stick » would be a fabricated alarm. Contrast the test above (a write that DID land then reads back wrong → a
        // real warning). Here the failure is admitted in the count and the journal, but never doubles as an unconfirmed
        // write. This is exactly what the inline « verify all of `allowed` » code got wrong before the shared method.
        var ok = Tw("ok", TweakTier.Tranquille);
        var bad = Tw("bad", TweakTier.Tranquille);
        var tweaks = new RecordingTweakService();
        tweaks.FailIds.Add("bad");                    // a genuine apply failure → IsApplied stays false, not a readback miss
        var journal = new RecordingApplyJournal();
        var vm = NewVm(new[] { ok, bad }, tweaks, journal: journal);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));

        Assert.False(vm.HasUnconfirmed);                                     // the failure is NOT a fabricated « didn't stick »
        Assert.NotNull(vm.LastVerification);                                 // the write that DID land was still verified…
        Assert.Equal(new[] { "ok" }, vm.LastVerification!.Confirmed);        // …and only the genuine success is confirmed
        var entry = Assert.Single(journal.Entries);
        Assert.Empty(entry.Unconfirmed);                                     // the journal carries no unconfirmed write
        Assert.Equal(1, entry.Failed);                                       // while the failure is still admitted, honestly
    }

    [Fact]
    public async Task CheckProfileOnSystem_SupersedesAStaleVerificationBanner()
    {
        // « Vérifier » is an explicit on-system re-probe, so it must drop a prior apply's « didn't stick » banner
        // (its fresh tri-state tally goes to Status instead) — the Profiles analog of the Dashboard's manual refresh
        // clearing the post-apply verification. The durable record of the past miss stays in the journal regardless.
        var stuck = Tw("stuck", TweakTier.Tranquille);
        var tweaks = new RecordingTweakService();
        tweaks.NotConfirmedIds.Add("stuck");
        var vm = NewVm(new[] { stuck }, tweaks);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));
        Assert.True(vm.HasUnconfirmed);                                      // banner is up after the apply that didn't stick

        await vm.CheckProfileOnSystemCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));
        Assert.False(vm.HasUnconfirmed);                                     // the explicit re-probe cleared it
        Assert.Null(vm.LastVerification);
    }

    [Fact]
    public async Task ApplyProfile_WhenNothingToApply_RecordsNothing()
    {
        // Honest no-op all the way down: an already-applied profile applies nothing AND journals nothing — never a
        // fabricated "Application" entry for a click that changed the system in no way.
        var journal = new RecordingApplyJournal();
        var vm = NewVm(new[] { Tw("t1", TweakTier.Tranquille, applied: true) },
                       new RecordingTweakService(), journal: journal);

        await vm.ApplyProfileCommand.ExecuteAsync(Preset(vm, "preset-tranquille"));

        Assert.Empty(journal.Entries);
    }

    // ---- Freemium gate: loading a profile is an apply, so it obeys the same tier lock as the Tweaks/Dashboard
    //      pages — a configured Free build refuses the Avancé/Extreme (Premium) members instead of silently
    //      applying them, while an unconfigured build (the as-shipped default) keeps applying everything. The lock
    //      is worded by the shared PremiumGateText so all four apply surfaces read identically. Avancé tweaks are
    //      used because they carry no Extreme/hardware/anti-cheat concern, so the apply flows straight through the
    //      gate without arming the risk-confirmation detour — isolating exactly the freemium behaviour. ----

    [Fact]
    public async Task ApplyProfile_ConfiguredFree_RefusesAnAllPremiumProfile_AndPointsToLicence()
    {
        var premium = Tw("x", TweakTier.Avance);                        // a Premium-only member
        var user = new Profile { Name = "Premium", TweakIds = { "x" } };
        var tweaks = new RecordingTweakService();
        var vm = new ProfilesViewModel(
            new FakeProfileService(userProfiles: new[] { user }),
            new FakeTweakRepository(new[] { premium }),
            tweaks,
            new FakeAppSettingsStore(), new RecordingApplyJournal(),
            new FakeLicenseService(AppEdition.Free, configured: true), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());

        Assert.Empty(tweaks.Applied);                                   // the gate refused — nothing touched the backend
        Assert.Contains("réservé(s) à Premium", vm.Status);
        Assert.Contains("Licence", vm.Status);                         // and points the user to where to unlock it
    }

    [Fact]
    public async Task ApplyProfile_ConfiguredFree_AppliesOnlyTheFreeTier_AndDisclosesTheLockedPremium()
    {
        var free = Tw("free", TweakTier.Tranquille);
        var premium = Tw("premium", TweakTier.Avance);
        var user = new Profile { Name = "Mixte", TweakIds = { "free", "premium" } };
        var tweaks = new RecordingTweakService();
        var vm = new ProfilesViewModel(
            new FakeProfileService(userProfiles: new[] { user }),
            new FakeTweakRepository(new[] { free, premium }),
            tweaks,
            new FakeAppSettingsStore(), new RecordingApplyJournal(),
            new FakeLicenseService(AppEdition.Free, configured: true), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());

        Assert.Equal(new[] { "free" }, tweaks.Applied.Select(t => t.Id).ToArray());   // only the free tier landed
        Assert.StartsWith("1 optimisation(s) appliquée(s)", vm.Status);               // the real backend count
        Assert.Contains("1 réservé(s) à Premium", vm.Status);                         // the withheld member, disclosed
    }

    [Fact]
    public async Task ApplyProfile_ConfiguredPremium_AppliesEverything()
    {
        var free = Tw("free", TweakTier.Tranquille);
        var premium = Tw("premium", TweakTier.Avance);
        var user = new Profile { Name = "Mixte", TweakIds = { "free", "premium" } };
        var tweaks = new RecordingTweakService();
        var vm = new ProfilesViewModel(
            new FakeProfileService(userProfiles: new[] { user }),
            new FakeTweakRepository(new[] { free, premium }),
            tweaks,
            new FakeAppSettingsStore(), new RecordingApplyJournal(),
            new FakeLicenseService(AppEdition.Premium, configured: true), new PreflightBannerViewModel(new FakePreflightService()));

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());

        Assert.Equal(2, tweaks.Applied.Count);                          // a paid build unlocks the whole profile
        Assert.DoesNotContain("réservé(s) à Premium", vm.Status);
    }

    [Fact]
    public async Task ApplyProfile_NotConfigured_AppliesEverything_EvenAvance()
    {
        // The as-shipped default: no embedded key → licensing dormant → the gate is a no-op and every member applies,
        // so an un-licensed build is never silently crippled.
        var free = Tw("free", TweakTier.Tranquille);
        var premium = Tw("premium", TweakTier.Avance);
        var user = new Profile { Name = "Mixte", TweakIds = { "free", "premium" } };
        var tweaks = new RecordingTweakService();
        var vm = new ProfilesViewModel(
            new FakeProfileService(userProfiles: new[] { user }),
            new FakeTweakRepository(new[] { free, premium }),
            tweaks,
            new FakeAppSettingsStore(), new RecordingApplyJournal(),
            new FakeLicenseService(), new PreflightBannerViewModel(new FakePreflightService()));                                  // not configured (the default)

        await vm.ApplyProfileCommand.ExecuteAsync(vm.UserProfiles.Single());

        Assert.Equal(2, tweaks.Applied.Count);
        Assert.DoesNotContain("réservé(s) à Premium", vm.Status);
    }
}
