using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace AurumTweaks.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly ITweakRepository _repo;
    private readonly ITweakService _tweakService;
    private readonly IAppSettingsStore _settings;
    private readonly IApplyJournal _journal;
    private readonly ILicenseService _license;

    // The SAME shared pre-flight banner the Tweaks page and the Dashboard one-click show, from one probe. Applying a
    // profile is the heaviest apply surface — a whole bundle of tweaks through ApplyManyAsync — so it must forecast
    // the same restore-point / pending-reboot posture; leaving it unguarded would be the one big batch with no warning.
    public PreflightBannerViewModel Preflight { get; }

    public ObservableCollection<Profile> Presets { get; } = new();
    public ObservableCollection<Profile> UserProfiles { get; } = new();

    [ObservableProperty] private Profile? _selected;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string _status = string.Empty;

    // Post-apply verification of the loaded bundle. Loading a profile is the HEAVIEST apply surface (a whole batch
    // through ApplyManyAsync), so trusting the engine's success count alone here would be the biggest unverified
    // surface in the app. After applying we RE-READ the machine (DetectStatesAsync) and surface any tweak the engine
    // reported applied that does NOT read back as active — the same honest « didn't stick » check the Tweaks page and
    // the Dashboard one-click run. Null = no apply yet / cleared by a fresh apply or an explicit « Vérifier » re-probe.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnconfirmed))]
    private VerificationReport? _lastVerification;

    public bool HasUnconfirmed => LastVerification?.HasUnconfirmed == true;

    // Inline rename editor state. Renaming points at the user profile being edited (null = editor hidden) and
    // RenameText is the bound input. A single editor for the whole list, driven by this pointer, means no
    // per-item edit flag is needed on the (non-observable) Profile model.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenaming))]
    private Profile? _renaming;

    [ObservableProperty] private string _renameText = string.Empty;

    public bool IsRenaming => Renaming is not null;

    // Risky-apply confirmation gate. PendingProfile points at a profile whose to-apply set tripped
    // ProfileApplyRisk (hardware / anti-cheat / Extreme-tier); until the user confirms, nothing is executed.
    // A single VM-level pointer (like the rename editor) means one gate at a time, no per-item flag on the model.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingConfirmation))]
    private Profile? _pendingProfile;

    [ObservableProperty] private string _pendingRiskSummary = string.Empty;

    public bool HasPendingConfirmation => PendingProfile is not null;

    // Profile comparison tool. AllProfiles is the union (presets + user) the two pickers choose from; it's kept in
    // sync by subscribing to both source collections, so a profile duplicated or imported mid-session shows up in the
    // dropdowns without a reload. The result is rendered from plain strings (tweak ids, exactly how the Tweaks page
    // titles them) so the panel stays trivial XAML; HasComparison shows it only once a comparison has actually run.
    public ObservableCollection<Profile> AllProfiles { get; } = new();

    [ObservableProperty] private Profile? _compareLeft;
    [ObservableProperty] private Profile? _compareRight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison))]
    private string _compareSummary = string.Empty;

    [ObservableProperty] private string _compareOnlyLeftText = string.Empty;
    [ObservableProperty] private string _compareSharedText = string.Empty;
    [ObservableProperty] private string _compareOnlyRightText = string.Empty;

    public bool HasComparison => !string.IsNullOrEmpty(CompareSummary);

    // Captured at "Comparer" time so "Copier le comparatif" renders exactly what was compared, even if the pickers
    // are changed afterwards. Plain fields (not bound) — only the copy command reads them.
    private ProfileComparison? _lastComparison;
    private string _lastComparisonLeftName = string.Empty;
    private string _lastComparisonRightName = string.Empty;

    public ProfilesViewModel(
        IProfileService profiles,
        ITweakRepository repo,
        ITweakService tweakService,
        IAppSettingsStore settings,
        IApplyJournal journal,
        ILicenseService license,
        PreflightBannerViewModel preflight)
    {
        _profiles = profiles;
        _repo = repo;
        _tweakService = tweakService;
        _settings = settings;
        _journal = journal;
        _license = license;
        Preflight = preflight;   // shared singleton; probes the safety net in its own ctor, off this profile load
        // Mirror presets + user profiles into AllProfiles on every change, so the comparison pickers always reflect
        // the current set (including profiles added/removed this session) without a manual rebuild at each call site.
        Presets.CollectionChanged += (_, _) => RebuildAllProfiles();
        UserProfiles.CollectionChanged += (_, _) => RebuildAllProfiles();
        _ = LoadAsync();
    }

    private void RebuildAllProfiles()
    {
        AllProfiles.Clear();
        foreach (var p in Presets) AllProfiles.Add(p);
        foreach (var p in UserProfiles) AllProfiles.Add(p);
    }

    private async Task LoadAsync()
    {
        // Resolve each profile against the live catalogue once, so every card can show — before « Charger » — both
        // what it contains (count + per-tier split) and whether any of it is risky. The same Resolve the apply path
        // uses, so neither line can ever disagree with what loading actually does.
        var catalog = await _repo.LoadAllAsync();

        Presets.Clear();
        foreach (var p in await _profiles.GetBuiltInPresetsAsync())
        {
            Describe(p, catalog);
            Presets.Add(p);
        }

        UserProfiles.Clear();
        foreach (var p in await _profiles.LoadProfilesAsync())
        {
            Describe(p, catalog);
            UserProfiles.Add(p);
        }
    }

    // Hang all three pre-apply honesty lines off a SINGLE resolve of the profile's members: the composition (count +
    // per-tier split), the risk caution (« Attention : … », empty when nothing is risky), and the conflict caution
    // (« ⚠ … en conflit », empty when the set is consistent). All three describe the profile's full contents — a
    // stable property of the bundle — using the same Resolve the apply path (« Charger ») uses.
    private static void Describe(Profile p, IReadOnlyList<Tweak> catalog)
    {
        var members = ProfileComposition.Resolve(p, catalog);
        p.CompositionLabel = ProfileComposition.Summarize(members).Label;
        p.RiskHint = ProfileApplyRisk.Assess(members).ShortLabel;
        p.ConflictHint = ProfileConflicts.Summarize(TweakConflictDetector.Detect(members));
    }

    /// <summary>Reload presets + user profiles. Called when the page is shown (ProfilesView.Loaded) so a profile
    /// saved from the Tweaks page appears without an app restart — both VMs are singletons, so this list is
    /// otherwise built only once, at construction.</summary>
    public Task RefreshAsync() => LoadAsync();

    // "Charger" — genuinely apply the profile's tweak set, instead of the old dead button. A built-in preset
    // derives its membership from live catalogue metadata (ProfileComposition); a user profile resolves its
    // explicit id list. Routes through ApplyManyAsync so the restore-point safety net and the honest
    // success/failure tally are exactly the Dashboard's — counts are the backend's, never fabricated.
    [RelayCommand]
    private async Task ApplyProfileAsync(Profile? profile)
    {
        if (profile is null || IsApplying) return;

        // Every fresh « Charger » click supersedes any previous bundle's « didn't stick » banner — including the
        // early-return no-ops below (empty / all-already-applied / risk-gate). Re-raised only if the actual apply
        // re-probes and finds a contradiction. The durable record of a past miss survives in the journal regardless.
        LastVerification = null;

        var catalog = await _repo.LoadAllAsync();
        var members = ProfileComposition.Resolve(profile, catalog);

        if (members.Count == 0)
        {
            // Honest: Stock (and any empty profile) applies nothing — never fabricate a count or a restore claim.
            Status = $"« {profile.Name} » ne contient aucun tweak — rien à appliquer.";
            return;
        }

        var toApply = members.Where(t => !t.IsApplied).ToList();
        if (toApply.Count == 0)
        {
            Status = $"« {profile.Name} » : tout est déjà appliqué ✓";
            return;
        }

        // Gate the heaviest sets behind an explicit confirmation that names exactly what's risky (hardware /
        // anti-cheat / Extreme-tier) before anything is executed. This is an honest disclosure, not a fake speed
        // bump: a fully-safe set (preset-tranquille, most user profiles) returns RequiresConfirmation == false
        // and applies immediately. Risky sets arm the gate and touch the backend only after ConfirmApply.
        var risk = ProfileApplyRisk.Assess(toApply);
        if (risk.RequiresConfirmation)
        {
            PendingProfile = profile;
            PendingRiskSummary = risk.Summary;
            Status = $"Confirmation requise avant d'appliquer « {profile.Name} ».";
            return;
        }

        await ExecuteApplyAsync(profile, toApply);
    }

    // Confirm a gated risky apply. Re-resolve the to-apply set first: state may have shifted while the gate was
    // armed, and re-resolving preserves the "already applied" honesty if it emptied meanwhile (never a fake run).
    [RelayCommand]
    private async Task ConfirmApplyAsync()
    {
        var profile = PendingProfile;
        PendingProfile = null;
        PendingRiskSummary = string.Empty;
        if (profile is null || IsApplying) return;

        var catalog = await _repo.LoadAllAsync();
        var toApply = ProfileComposition.Resolve(profile, catalog).Where(t => !t.IsApplied).ToList();
        if (toApply.Count == 0)
        {
            Status = $"« {profile.Name} » : tout est déjà appliqué ✓";
            return;
        }

        await ExecuteApplyAsync(profile, toApply);
    }

    // Dismiss the gate without touching the backend. Honest about the no-op: it says the apply was cancelled.
    [RelayCommand]
    private void CancelApply()
    {
        var name = PendingProfile?.Name;
        PendingProfile = null;
        PendingRiskSummary = string.Empty;
        if (name is not null)
            Status = $"Application de « {name} » annulée.";
    }

    // The actual apply, shared by the direct (safe) path and the post-confirmation (risky) path. Routes through
    // ApplyManyAsync so the restore-point safety net and the honest success/failure tally are exactly the Dashboard's.
    private async Task ExecuteApplyAsync(Profile profile, IReadOnlyList<Tweak> toApply)
    {
        // Freemium gate at the shared apply choke point — both the direct safe path (ApplyProfileAsync) and the
        // post-confirmation risky path (ConfirmApplyAsync) funnel through here, so gating once covers both. A profile
        // can bundle Avancé/Extreme (Premium) tweaks, so a configured Free build must refuse those rather than silently
        // apply them. Same TweakGate/PremiumGateText the Tweaks and Dashboard pages use, so the lock reads identically
        // everywhere. As-shipped (no embedded key) everything is allowed — a no-op until a seller configures licensing.
        var (allowed, locked) = TweakGate.Partition(_license.IsConfigured, _license.CurrentEdition, toApply);
        if (allowed.Count == 0)
        {
            Status = PremiumGateText.AllLocked(locked.Count);
            return;
        }

        // Only claim a restore point when the toggle is on — ApplyManyAsync genuinely skips it otherwise, so
        // announcing it regardless would be a fabricated safety claim. (Same contract as the Dashboard.)
        bool withRestore = _settings.Current.CreateRestorePointBeforeTweaks;

        IsApplying = true;
        Status = withRestore
            ? $"Application de « {profile.Name} » : {allowed.Count} optimisation(s)… (point de restauration en cours)"
            : $"Application de « {profile.Name} » : {allowed.Count} optimisation(s)…";

        var result = await _tweakService.ApplyManyAsync(allowed);
        if (result.RestorePointFailed)
        {
            // The required restore point failed → nothing was applied. Surface the honest reason and reset the
            // busy flag; skip the journal AND the "last applied" stamp, because nothing happened on the machine.
            Status = TweakApplyText.RestorePointFailed;
            IsApplying = false;
            return;
        }

        // Re-read the machine through the SHARED honest verification, then record it alongside the batch. Loading a
        // profile is an apply, so the audit trail's "every application is recorded" promise must hold here too — AND
        // (parity with the Tweaks page and the Dashboard one-click, through the one ITweakService.VerifyAppliedAsync)
        // any write the engine REPORTED applied that reads back wrong is surfaced, never trusted on the success count.
        // That method re-probes ONLY the tweaks whose IsApplied the engine set, so a genuinely failed tweak is excluded
        // and can't be mislabeled "didn't stick" — a fabricated alarm. This is the heaviest apply surface, so verifying
        // it matters most; null = nothing genuinely applied → no banner, no claim. The re-probe runs off the UI thread.
        var verification = await _tweakService.VerifyAppliedAsync(allowed);
        await _journal.RecordAsync(JournalReport.ForApply(result, allowed.Select(t => t.Id), verification));
        LastVerification = verification;

        // Record a real "last applied" stamp only on a genuine apply (≥1 success) and only for USER profiles —
        // built-in presets are regenerated from code each launch and must never be written to the user store (a
        // preset file there would resurface as a phantom user profile). Skipping it on a fully-failed batch keeps
        // the stamp honest: it marks when the profile was actually applied, not merely when "Charger" was clicked.
        if (result.Succeeded > 0 && !profile.IsBuiltIn)
        {
            profile.LastAppliedUtc = DateTime.UtcNow;
            await _profiles.SaveAsync(profile);
            var idx = UserProfiles.IndexOf(profile);
            if (idx >= 0) UserProfiles[idx] = profile;   // Profile isn't observable; re-realize the card to show the stamp
        }

        // Lead with the REAL success count and always surface failures, so a partial batch can't read as a clean
        // run of a smaller set. The restore clause is keyed to the toggle, not the result: the point is created
        // before the loop, so it exists regardless of how many ops succeeded.
        string status = $"{result.Succeeded} optimisation(s) appliquée(s)";
        if (result.Failed > 0)
            status += $", {result.Failed} échec(s)";
        status += withRestore
            ? ". Un point de restauration a été créé."
            : " — sans point de restauration (option désactivée dans Paramètres).";
        if (result.Succeeded > 0)
            status += " Un redémarrage peut être requis.";
        // Disclose any Premium picks withheld, ONLY when some were — so a fully-allowed profile keeps the exact
        // "N optimisation(s) appliquée(s)…" contract the tests pin.
        if (locked.Count > 0)
            status += PremiumGateText.LockedSuffix(locked.Count);
        Status = status;
        IsApplying = false;
    }

    // "Personnaliser" — take the user to the Tweaks page, the surface where individual tweaks are picked.
    // Real navigation (mirrors the Dashboard's GoTo* commands), not the old no-op button.
    [RelayCommand]
    private void Customize()
        => App.Services.GetRequiredService<MainViewModel>().Navigate("Tweaks");

    // "Modifier" — edit a user profile's tweak set: load it into the Tweaks page selection (where it can be
    // adjusted with the full filter/preview UI) and arm an in-place update so saving there overwrites THIS profile
    // rather than creating a copy. Presets are immutable code, so they never enter edit mode — the UI offers this
    // only on user cards, and this guard is the backstop. Thin cross-VM glue (like Customize), so it's untested.
    [RelayCommand]
    private void EditProfile(Profile? profile)
    {
        if (profile is null || profile.IsBuiltIn) return;
        App.Services.GetRequiredService<TweaksViewModel>().BeginEditingProfile(profile);
        App.Services.GetRequiredService<MainViewModel>().Navigate("Tweaks");
    }

    // "Dupliquer" — fork a profile into a fresh, editable user profile (a new id, never overwriting the source).
    // A user profile is copied faithfully (its stored id list verbatim, stale ids included); a built-in preset is
    // forked by FREEZING its currently-resolved membership into a concrete snapshot — the only way an otherwise
    // immutable preset becomes editable. Empty sets (e.g. Stock) decline honestly rather than mint a do-nothing copy.
    [RelayCommand]
    private async Task DuplicateProfileAsync(Profile? profile)
    {
        if (profile is null) return;

        var catalog = await _repo.LoadAllAsync();
        // Presets carry no explicit id list, so resolve their predicate to a snapshot; user profiles are copied as-is
        // (resolving would silently drop ids the current catalogue lacks — a faithful copy must keep them).
        var memberIds = profile.IsBuiltIn
            ? ProfileComposition.Resolve(profile, catalog).Select(t => t.Id).ToList()
            : new List<string>(profile.TweakIds);

        if (memberIds.Count == 0)
        {
            Status = $"« {profile.Name} » ne contient aucun tweak à dupliquer.";
            return;
        }

        var copy = ProfileDuplicate.Clone(profile, memberIds, UserProfiles.Select(p => p.Name));
        await _profiles.SaveAsync(copy);
        Describe(copy, catalog);   // give the new card its composition + risk lines immediately, not blank until reload
        UserProfiles.Add(copy);
        Status = $"Profil dupliqué : « {copy.Name} » ({memberIds.Count} tweak(s)).";
    }

    // "Comparer" — set-diff two chosen profiles' resolved memberships (shared / left-only / right-only). Both sides
    // are resolved with the SAME ProfileComposition.Resolve the apply path uses, so a preset's predicate-derived set
    // and a user profile's id list compare on equal footing. Declines honestly until two profiles are picked.
    [RelayCommand]
    private async Task CompareProfilesAsync()
    {
        if (CompareLeft is null || CompareRight is null)
        {
            Status = "Sélectionnez deux profils à comparer.";
            return;
        }

        var catalog = await _repo.LoadAllAsync();
        var leftIds = ProfileComposition.Resolve(CompareLeft, catalog).Select(t => t.Id);
        var rightIds = ProfileComposition.Resolve(CompareRight, catalog).Select(t => t.Id);
        var diff = ProfileDiff.Compare(leftIds, rightIds);

        CompareSummary = ProfileDiff.Summarize(diff, CompareLeft.Name, CompareRight.Name);
        CompareOnlyLeftText = JoinIds(diff.OnlyInLeft);
        CompareSharedText = JoinIds(diff.Shared);
        CompareOnlyRightText = JoinIds(diff.OnlyInRight);
        // Snapshot the comparison + the names it used, so "Copier" reflects this run, not a later picker change.
        _lastComparison = diff;
        _lastComparisonLeftName = CompareLeft.Name;
        _lastComparisonRightName = CompareRight.Name;
        Status = CompareSummary;
    }

    private static string JoinIds(IReadOnlyList<string> ids) => ids.Count == 0 ? "—" : string.Join("\n", ids);

    // "Copier le comparatif" — put the last comparison on the clipboard as a shareable French text block (a paste
    // into a forum/Discord thread is how the FR scene shares setups). Read-only: it renders what was compared and
    // applies nothing. Honest no-op with a reason before any comparison has run; graceful message if the clipboard
    // is momentarily locked. Mirrors the Journal / System Report copy pair (pure Render + thin Clipboard.SetText).
    [RelayCommand]
    private void CopyComparison()
    {
        if (_lastComparison is null)
        {
            Status = "Aucun comparatif à copier : lance d'abord « Comparer ».";
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(
                ProfileDiffReport.Render(_lastComparison, _lastComparisonLeftName, _lastComparisonRightName, DateTime.UtcNow));
            Status = "Comparatif copié. Colle-le où tu veux.";
        }
        catch
        {
            Status = "Copie impossible (presse-papiers occupé).";
        }
    }

    // "Vérifier" — the read-only counterpart of "Charger": probe the LIVE machine right now and report how many of
    // this profile's resolved tweaks are actually applied, vs not, vs unverifiable. It reads fresh state every call
    // (never the cached Tweak.IsApplied, which a never-opened Tweaks page would leave at its stale default) and
    // mutates nothing — so a user can ask "is this setup currently active?" without re-applying it. The honest
    // tri-state tally lives in the pure ProfileLiveState; here we just resolve the members and feed their states.
    [RelayCommand]
    private async Task CheckProfileOnSystemAsync(Profile? profile)
    {
        if (profile is null) return;

        // An explicit on-system re-probe supersedes any apply-time « didn't stick » banner (mirrors the Dashboard,
        // where a manual refresh clears the post-apply verification): the fresh tri-state tally goes to Status instead.
        LastVerification = null;

        var catalog = await _repo.LoadAllAsync();
        var members = ProfileComposition.Resolve(profile, catalog);
        if (members.Count == 0)
        {
            Status = $"« {profile.Name} » ne contient aucun tweak à vérifier.";
            return;
        }

        var states = await _tweakService.DetectStatesAsync(members);
        Status = ProfileLiveState.Summarize(states).Label(profile.Name);
    }

    // "Fusionner" — combine the two picked profiles into a NEW user profile holding the union of their tweaks
    // ("everything from A and from B"). Reuses the comparison pickers; resolves both sides the same way the apply
    // path does. The merged profile is fresh (new id, never applied) and its anti-cheat-safe flag is DERIVED from
    // the actual union (safe only when no member trips an anti-cheat), never copied — so the badge can't overclaim.
    [RelayCommand]
    private async Task MergeProfilesAsync()
    {
        if (CompareLeft is null || CompareRight is null)
        {
            Status = "Sélectionnez deux profils à fusionner.";
            return;
        }

        var catalog = await _repo.LoadAllAsync();
        var leftIds = ProfileComposition.Resolve(CompareLeft, catalog).Select(t => t.Id);
        var rightIds = ProfileComposition.Resolve(CompareRight, catalog).Select(t => t.Id);
        var unionIds = ProfileMerge.Union(leftIds, rightIds);

        if (unionIds.Count == 0)
        {
            Status = "Ces deux profils ne contiennent aucun tweak à fusionner.";
            return;
        }

        var idSet = new HashSet<string>(unionIds, StringComparer.OrdinalIgnoreCase);
        var members = catalog.Where(t => idSet.Contains(t.Id)).ToList();
        var merged = new Profile
        {
            Name = ProfileMerge.UniqueMergedName(CompareLeft.Name, CompareRight.Name, UserProfiles.Select(p => p.Name)),
            IsBuiltIn = false,
            IsCompetitiveSafe = members.All(m => !m.AntiCheat.HasAnyConcern),
            TweakIds = unionIds.ToList()
        };
        await _profiles.SaveAsync(merged);
        Describe(merged, catalog);   // composition + risk lines on the new card immediately, not blank until reload
        UserProfiles.Add(merged);
        Status = $"Profils fusionnés : « {merged.Name} » ({unionIds.Count} tweak(s)).";
    }

    // Difference of the two picked profiles: a new user profile holding the tweaks in A that B does NOT have — the
    // comparison's "propre à A" bucket made actionable (e.g. keep a "tweaks à éviter" profile and subtract it from any
    // setup). Reuses ProfileDiff for the set math so it can never disagree with the panel, and the same honest
    // derivations as merge (fresh id, never applied, anti-cheat-safe flag computed from the actual members). Honest
    // no-op when A ⊆ B: nothing to subtract, so nothing is persisted rather than fabricating an empty profile.
    [RelayCommand]
    private async Task SubtractProfilesAsync()
    {
        if (CompareLeft is null || CompareRight is null)
        {
            Status = "Sélectionnez deux profils à soustraire.";
            return;
        }

        var catalog = await _repo.LoadAllAsync();
        var leftIds = ProfileComposition.Resolve(CompareLeft, catalog).Select(t => t.Id);
        var rightIds = ProfileComposition.Resolve(CompareRight, catalog).Select(t => t.Id);
        var onlyLeft = ProfileDiff.Compare(leftIds, rightIds).OnlyInLeft;

        if (onlyLeft.Count == 0)
        {
            Status = $"« {CompareLeft.Name} » ne contient aucun tweak absent de « {CompareRight.Name} ».";
            return;
        }

        var idSet = new HashSet<string>(onlyLeft, StringComparer.OrdinalIgnoreCase);
        var members = catalog.Where(t => idSet.Contains(t.Id)).ToList();
        var result = new Profile
        {
            Name = ProfileNaming.Disambiguate($"{CompareLeft.Name} sans {CompareRight.Name}", UserProfiles.Select(p => p.Name)),
            IsBuiltIn = false,
            IsCompetitiveSafe = members.All(m => !m.AntiCheat.HasAnyConcern),
            TweakIds = onlyLeft.ToList()
        };
        await _profiles.SaveAsync(result);
        Describe(result, catalog);
        UserProfiles.Add(result);
        Status = $"Profil « {result.Name} » créé ({onlyLeft.Count} tweak(s) propre(s) à « {CompareLeft.Name} »).";
    }

    // Open the inline rename editor for a user profile, seeded with its current name. Presets are immutable, so
    // they never enter edit mode — the UI only shows the affordance on user cards, and this guard is the backstop.
    [RelayCommand]
    private void BeginRename(Profile profile)
    {
        if (profile.IsBuiltIn) return;
        Renaming = profile;
        RenameText = profile.Name;
    }

    [RelayCommand]
    private void CancelRename()
    {
        Renaming = null;
        RenameText = string.Empty;
    }

    // Commit a rename: persist the new name under the SAME id — SaveAsync upserts by id, so the file is
    // overwritten in place (no orphaned copy, unlike a delete + re-save under a fresh id). Honest no-op on a
    // blank name: we never store an unnamed profile, and we keep the editor open so the user can correct it.
    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        var target = Renaming;
        if (target is null) return;

        var name = RenameText.Trim();
        if (name.Length == 0)
        {
            Status = "Le nom du profil ne peut pas être vide.";
            return;
        }

        target.Name = name;
        await _profiles.SaveAsync(target);

        // Profile is a plain (non-observable) model, so mutating Name won't refresh its bound card; replace the
        // item in place to re-realize the container with the new name (keeps list order, cheaper than a reload).
        var i = UserProfiles.IndexOf(target);
        if (i >= 0) UserProfiles[i] = target;

        Status = $"Profil renommé en « {name} ».";
        Renaming = null;
        RenameText = string.Empty;
    }

    [RelayCommand]
    private async Task DeleteAsync(Profile profile)
    {
        if (profile.IsBuiltIn) return;
        await _profiles.DeleteAsync(profile.Id);
        UserProfiles.Remove(profile);
    }

    // Export a user profile to a portable file so it can be shared between machines / with the community.
    // The portable payload (ProfileTransfer.Serialize) is the tested part; this is the thin save-dialog glue.
    [RelayCommand]
    private async Task ExportProfileAsync(Profile profile)
    {
        if (profile is null || profile.IsBuiltIn) return;      // presets are reproducible from the app — only user profiles export

        var dlg = new SaveFileDialog
        {
            Title = "Exporter le profil",
            FileName = SafeFileName(profile.Name) + ".aurumprofile.json",
            Filter = "Profil Aurum (*.aurumprofile.json)|*.aurumprofile.json|JSON (*.json)|*.json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await File.WriteAllTextAsync(dlg.FileName, ProfileTransfer.Serialize(profile));
            Status = $"Profil « {profile.Name} » exporté.";
        }
        catch (IOException ex) { Status = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Status = $"Export impossible : {ex.Message}"; }
    }

    // Import a profile file. The open-dialog + file read are thin glue; the honesty-bearing reconcile against the
    // live catalogue (recognized vs unknown ids, never trusting the payload's flags) lives in ImportJsonAsync.
    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Importer un profil Aurum",
            Filter = "Profil Aurum (*.aurumprofile.json)|*.aurumprofile.json|JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;

        string json;
        try { json = await File.ReadAllTextAsync(dlg.FileName); }
        catch (IOException ex) { Status = $"Lecture impossible : {ex.Message}"; return; }
        catch (UnauthorizedAccessException ex) { Status = $"Lecture impossible : {ex.Message}"; return; }

        await ImportJsonAsync(json);
    }

    /// <summary>Import a profile from its JSON content: reconcile it against the live catalogue, store it only
    /// when it yields at least one recognized tweak, and surface the honest outcome. Public (and dialog-free) so
    /// the trust/reconcile behaviour is unit-testable — the file-dialog command above is the only untested glue.</summary>
    public async Task<ProfileImport> ImportJsonAsync(string json)
    {
        var catalog = await _repo.LoadAllAsync();
        var result = ProfileTransfer.Parse(json, catalog);
        if (result.Ok && result.Profile is not null)
        {
            await _profiles.SaveAsync(result.Profile);
            Describe(result.Profile, catalog);   // so the new card shows its composition + risk at once, not blank until reload
            UserProfiles.Add(result.Profile);
        }
        Status = result.Summary;
        return result;
    }

    // Export every user profile to one portable bundle file — a backup / migration / full-setup share in a single
    // file. Presets are excluded (the app reproduces them). Honest no-op when there's nothing to export. The bundle
    // payload (ProfileBundle.Serialize) is the tested part; this is the thin save-dialog glue.
    [RelayCommand]
    private async Task ExportAllProfilesAsync()
    {
        if (UserProfiles.Count == 0)
        {
            Status = "Aucun profil personnalisé à exporter.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Exporter tous mes profils",
            FileName = "mes-profils.aurumbundle.json",
            Filter = "Lot de profils Aurum (*.aurumbundle.json)|*.aurumbundle.json|JSON (*.json)|*.json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await File.WriteAllTextAsync(dlg.FileName, ProfileBundle.Serialize(UserProfiles));
            Status = $"{UserProfiles.Count} profil(s) exporté(s) vers le lot.";
        }
        catch (IOException ex) { Status = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Status = $"Export impossible : {ex.Message}"; }
    }

    // Import a bundle of profiles. The open-dialog + file read are thin glue; the per-profile reconcile against the
    // live catalogue (and the aggregate honest tally) lives in ImportBundleJsonAsync.
    [RelayCommand]
    private async Task ImportBundleAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Importer un lot de profils",
            Filter = "Lot de profils Aurum (*.aurumbundle.json)|*.aurumbundle.json|JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;

        string json;
        try { json = await File.ReadAllTextAsync(dlg.FileName); }
        catch (IOException ex) { Status = $"Lecture impossible : {ex.Message}"; return; }
        catch (UnauthorizedAccessException ex) { Status = $"Lecture impossible : {ex.Message}"; return; }

        await ImportBundleJsonAsync(json);
    }

    /// <summary>Import a bundle from its JSON content: reconcile every profile against the live catalogue, store the
    /// ones that yield at least one recognized tweak, and surface the aggregate honest outcome. Public (dialog-free)
    /// so the trust/reconcile behaviour is unit-testable — the file-dialog command above is the only untested glue.</summary>
    public async Task<ProfileBundleImport> ImportBundleJsonAsync(string json)
    {
        var catalog = await _repo.LoadAllAsync();
        var result = ProfileBundle.Parse(json, catalog);
        foreach (var p in result.Profiles)
        {
            await _profiles.SaveAsync(p);
            Describe(p, catalog);   // each new card shows its composition + risk at once, not blank until reload
            UserProfiles.Add(p);
        }
        Status = result.Summary;
        return result;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "profil" : clean;
    }
}
