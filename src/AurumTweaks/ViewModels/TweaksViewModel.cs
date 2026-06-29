using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

public partial class TweaksViewModel : ObservableObject
{
    private readonly ITweakRepository _repo;
    private readonly ITweakService _tweakService;
    private readonly ILocalizationService _localization;
    private readonly IProfileService _profiles;
    private readonly IApplyJournal _journal;
    private readonly ILicenseService _license;

    public ObservableCollection<Tweak> Tweaks { get; } = new();
    public ICollectionView TweaksView { get; }

    /// <summary>The shared pre-flight safety banner (System Restore readable? reboot pending?), shown before the user
    /// applies anything. A child VM so the dashboard's one-click apply shows the SAME posture from one probe — owned
    /// here (not inlined) to kill the duplicate glue. Its probe is kicked in its own ctor, off the catalog load.</summary>
    public PreflightBannerViewModel Preflight { get; }

    /// <summary>Completes when the catalog is loaded AND on-system state detection has run. Lets callers
    /// (and tests) await the full initial load rather than racing the fire-and-forget in the constructor.</summary>
    public Task Initialization { get; }

    [ObservableProperty] private TweakTier? _selectedTier;
    [ObservableProperty] private TweakCategory? _selectedCategory;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _competitiveSafeOnly;
    [ObservableProperty] private int _appliedCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _newProfileName = string.Empty;

    // Edit-a-profile mode. When the user picks "Modifier" on a saved profile (Profiles page), this holds that
    // profile's id so the next save UPDATES it in place rather than minting a copy; null = the normal "create a new
    // profile" mode. The banner (and its cancel) exist for honesty: edit mode lives on this singleton VM, so without
    // a visible, dismissable notice an armed edit could silently overwrite a long-forgotten profile on a later save.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingProfile))]
    [NotifyPropertyChangedFor(nameof(EditingProfileBanner))]
    private string? _editingProfileId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditingProfileBanner))]
    private string _editingProfileName = string.Empty;

    public bool IsEditingProfile => EditingProfileId is not null;

    public string EditingProfileBanner => IsEditingProfile
        ? $"Édition de « {EditingProfileName} » — « Enregistrer le profil » mettra ce profil à jour."
        : string.Empty;

    // Apply-plan preview state: the built plan and whether the confirmation overlay is showing. Null plan +
    // hidden overlay is the resting state; PreviewPlan fills them, Confirm/Close clear the overlay.
    [ObservableProperty] private ApplyPlan? _currentPlan;
    [ObservableProperty] private bool _isPlanVisible;

    // Selection conflicts surfaced in the same preview: registry/service targets two+ selected tweaks set to
    // DIFFERENT values, where apply order alone would decide the winner. Empty (the resting state) hides the
    // warning; HasConflicts drives the section's visibility so a clean selection shows nothing.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflicts))]
    private IReadOnlyList<TweakConflict> _currentConflicts = System.Array.Empty<TweakConflict>();

    public bool HasConflicts => CurrentConflicts.Count > 0;

    // Post-apply verification: after a batch runs we re-probe the just-applied tweaks and keep any the system does
    // NOT confirm are live. Null (resting) or an all-confirmed report hides the banner; HasUnconfirmed drives its
    // visibility so only a genuine "didn't stick" is ever shown — never a fabricated success.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnconfirmed))]
    private VerificationReport? _lastVerification;

    public bool HasUnconfirmed => LastVerification?.HasUnconfirmed == true;

    // Post-revert verification (the honest mirror of the post-apply banner): after "Tout restaurer" we re-probe the
    // just-reverted tweaks and keep any the live machine STILL reads as active despite the engine reporting the
    // revert ran — a group policy re-applies it, another tool re-wrote it, a protected value. Null (resting) or an
    // all-clear report hides the banner; HasStillActiveAfterRevert drives its visibility so only a genuine "still
    // active" is shown, never a fabricated "tout restauré" over changes that are demonstrably still on the machine.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStillActiveAfterRevert))]
    private VerificationReport? _lastRevertVerification;

    public bool HasStillActiveAfterRevert => LastRevertVerification?.HasUnconfirmed == true;

    public IReadOnlyList<TweakTier> AllTiers { get; } = new[] { TweakTier.Tranquille, TweakTier.Avance, TweakTier.Extreme };
    public IReadOnlyList<TweakCategory> AllCategories { get; } = System.Enum.GetValues<TweakCategory>();

    public TweaksViewModel(ITweakRepository repo, ITweakService tweakService, ILocalizationService localization,
                           IProfileService profiles, IApplyJournal journal, ILicenseService license,
                           PreflightBannerViewModel preflight)
    {
        _repo = repo;
        _tweakService = tweakService;
        _localization = localization;
        _profiles = profiles;
        _journal = journal;
        _license = license;
        Preflight = preflight;   // the child VM probes the safety net in its own ctor, off this catalog load
        TweaksView = CollectionViewSource.GetDefaultView(Tweaks);
        TweaksView.Filter = FilterTweak;
        Initialization = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var all = await _repo.LoadAllAsync();
        foreach (var t in Tweaks) t.PropertyChanged -= OnTweakPropertyChanged;
        Tweaks.Clear();
        foreach (var t in all.OrderBy(t => t.Tier).ThenBy(t => t.Category))
        {
            t.PropertyChanged += OnTweakPropertyChanged;
            Tweaks.Add(t);
        }
        // No RefreshCounts() here: freshly loaded tweaks are all un-applied/un-selected, so it would only ever
        // compute 0. DetectAppliedStateAsync sets the real applied flags and refreshes the counts itself.
        await DetectAppliedStateAsync();
    }

    // Keep the footer's "N sélectionnés" live as the user ticks boxes: SelectedCount is otherwise only recomputed
    // on apply/revert/detect, so it lagged the real checkbox state between actions.
    private void OnTweakPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Tweak.IsSelected))
            SelectedCount = Tweaks.Count(t => t.IsSelected);
    }

    // Honesty + power: IsApplied is otherwise just an in-session flag that resets every launch, so a tweak the
    // user applied last week reads as "not applied" today. Probe the live machine once at load and light the
    // badge for what's genuinely active. Indeterminate (un-readable, e.g. shell-only) tweaks stay un-lit — we
    // never paint a check we can't verify.
    private async Task DetectAppliedStateAsync()
    {
        // The service probes registry/SCM OFF the UI thread and hands back per-row flags in input order. Writing
        // Tweak.IsApplied raises PropertyChanged (the "✓ Appliqué" badge is bound to it), so it MUST happen back
        // on the UI thread — the read (await) and the write stay on opposite sides of the boundary.
        var snapshot = Tweaks.ToList();
        var applied = await _tweakService.DetectAppliedAsync(snapshot);
        for (var i = 0; i < snapshot.Count; i++)
            snapshot[i].IsApplied = applied[i];
        RefreshCounts();
    }

    partial void OnSelectedTierChanged(TweakTier? value) => TweaksView.Refresh();
    partial void OnSelectedCategoryChanged(TweakCategory? value) => TweaksView.Refresh();
    partial void OnSearchTextChanged(string value) => TweaksView.Refresh();
    partial void OnCompetitiveSafeOnlyChanged(bool value) => TweaksView.Refresh();

    private bool FilterTweak(object item)
    {
        if (item is not Tweak t) return false;
        if (SelectedTier.HasValue && t.Tier != SelectedTier.Value) return false;
        if (SelectedCategory.HasValue && t.Category != SelectedCategory.Value) return false;
        if (CompetitiveSafeOnly && t.AntiCheat.HasAnyConcern) return false;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.ToLowerInvariant();
            var name = _localization.GetLocalizedFrom(t.Name).ToLowerInvariant();
            var desc = _localization.GetLocalizedFrom(t.Description).ToLowerInvariant();
            return name.Contains(s) || desc.Contains(s) || t.Id.ToLowerInvariant().Contains(s);
        }
        return true;
    }

    private void RefreshCounts()
    {
        AppliedCount = Tweaks.Count(t => t.IsApplied);
        SelectedCount = Tweaks.Count(t => t.IsSelected);
    }

    // Bulk-select exactly what the current filter (tier / category / search / competitive-safe) shows — reusing the
    // very predicate the list view paints with, so the two can never diverge. The honesty payoff: with the "AC safe
    // only" filter on, the anti-cheat-risky tweaks are hidden, so this can never silently tick one. No-op (with a
    // reason) when the filter shows nothing, so the button never appears to act yet do nothing.
    [RelayCommand]
    private void SelectAllVisible()
    {
        var visible = Tweaks.Where(t => FilterTweak(t)).ToList();
        if (visible.Count == 0)
        {
            StatusMessage = "Aucun tweak visible à sélectionner.";
            return;
        }
        foreach (var t in visible) t.IsSelected = true;
        RefreshCounts();
        StatusMessage = $"{visible.Count} tweak(s) visible(s) sélectionné(s).";
    }

    // Clear the ENTIRE selection, not just the visible subset — a predictable reset that never leaves hidden ticks
    // behind a filter (which would later apply tweaks the user can't currently see). No-op with a reason when empty.
    [RelayCommand]
    private void ClearSelection()
    {
        if (Tweaks.All(t => !t.IsSelected))
        {
            StatusMessage = "Aucun tweak sélectionné.";
            return;
        }
        foreach (var t in Tweaks) t.IsSelected = false;
        RefreshCounts();
        StatusMessage = "Sélection effacée.";
    }

    // "Apply what's missing": tick the visible tweaks the live machine does NOT report as applied. Additive — it
    // never un-ticks an already-applied tweak the user chose to keep selected. !IsApplied means the ✓ badge is dark,
    // which honestly covers both "verified not applied" and "unverifiable"; we offer exactly what the page shows as
    // not-active, never claiming more. No-op with a reason when every visible tweak already reads as applied.
    [RelayCommand]
    private void SelectUnappliedVisible()
    {
        var targets = Tweaks.Where(t => FilterTweak(t) && !t.IsApplied).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "Aucun tweak visible non appliqué à sélectionner.";
            return;
        }
        foreach (var t in targets) t.IsSelected = true;
        RefreshCounts();
        StatusMessage = $"{targets.Count} tweak(s) non appliqué(s) sélectionné(s).";
    }

    // Flip the tick of every VISIBLE tweak, leaving filtered-out ones untouched — so inverting under "anti-cheat
    // safe only" can never reach a hidden ban-risk tweak. No-op with a reason when the filter shows nothing.
    [RelayCommand]
    private void InvertVisibleSelection()
    {
        var visible = Tweaks.Where(t => FilterTweak(t)).ToList();
        if (visible.Count == 0)
        {
            StatusMessage = "Aucun tweak visible à inverser.";
            return;
        }
        foreach (var t in visible) t.IsSelected = !t.IsSelected;
        RefreshCounts();
        StatusMessage = $"Sélection inversée sur {visible.Count} tweak(s) visible(s).";
    }

    // Informed-consent path (opt-in): build the full list of operations the selection WOULD run and show it for
    // review before anything touches the elevated machine. Honest no-op with a reason when nothing is selected,
    // so the button never silently does nothing.
    [RelayCommand]
    private void PreviewPlan()
    {
        var selected = Tweaks.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Aucun tweak sélectionné.";
            return;
        }
        // Mirror the apply gate exactly so the preview can never show a plan that apply would then refuse: build the
        // overlay only from what this edition may run, and say plainly when premium-tier picks were left out.
        var (allowed, locked) = TweakGate.Partition(_license.IsConfigured, _license.CurrentEdition, selected);
        if (allowed.Count == 0)
        {
            StatusMessage = PremiumGateText.AllLocked(locked.Count);
            return;
        }
        CurrentPlan = TweakApplyPlan.Build(allowed);
        CurrentConflicts = TweakConflictDetector.Detect(allowed);
        IsPlanVisible = true;
        if (locked.Count > 0)
            StatusMessage = $"{locked.Count} tweak(s) Premium exclu(s) de l'aperçu.";
    }

    // Confirm from the preview overlay: close it, then run the SAME apply path as the direct button — including
    // the gated restore point — so the previewed plan and what actually executes can't diverge.
    [RelayCommand]
    private async Task ConfirmPlanAsync()
    {
        IsPlanVisible = false;
        await ApplySelectedAsync();
    }

    [RelayCommand]
    private void ClosePlan() => IsPlanVisible = false;

    [RelayCommand]
    private async Task ApplySelectedAsync()
    {
        var selected = Tweaks.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            // No silent dead-button: tell the user why nothing happened rather than leaving a stale status.
            StatusMessage = "Aucun tweak sélectionné.";
            return;
        }
        // Freemium gate at the one place it must genuinely bite: the elevated apply path. Partition the selection into
        // what this edition may run and what's reserved for Premium; apply ONLY the allowed set and never silently
        // touch a locked tweak. As-shipped (no embedded key) licensing is "not configured", so everything lands in
        // allowed and this is a no-op — the gate bites only once a seller embeds a public key.
        var (allowed, locked) = TweakGate.Partition(_license.IsConfigured, _license.CurrentEdition, selected);
        if (allowed.Count == 0)
        {
            StatusMessage = PremiumGateText.AllLocked(locked.Count);
            return;
        }
        var result = await _tweakService.ApplyManyAsync(allowed);
        if (result.RestorePointFailed)
        {
            // The required restore point couldn't be created, so the engine applied nothing. Say so honestly
            // instead of formatting a "0 appliqué(s)" that hides WHY — and skip verification + journaling, since
            // nothing was attempted on the machine (no audit entry, no counts to refresh).
            StatusMessage = TweakApplyText.RestorePointFailed;
            return;
        }
        RefreshCounts();
        // A fresh apply changed the machine — the previous "still active after revert" banner is now stale.
        LastRevertVerification = null;
        StatusMessage = FormatOutcome(result, "appliqué");
        // Append the locked note ONLY when some were withheld, so the pinned "N tweak(s) appliqué(s)" contract is
        // untouched in the common (and as-shipped) case where nothing is reserved.
        if (locked.Count > 0)
            StatusMessage += PremiumGateText.LockedSuffix(locked.Count);
        // Verify AFTER the status is set, and never fold the result into it: the apply outcome string is a pinned
        // contract. Verification speaks only through LastVerification/HasUnconfirmed (a separate banner). The shared
        // service method re-reads only the tweaks the engine reported applied (a real check, not an echo of "I ran it").
        LastVerification = await _tweakService.VerifyAppliedAsync(allowed);
        // Persist the batch to the audit journal, carrying the verification's unconfirmed list so the trail records
        // not just what we ran but what the machine couldn't confirm stuck. RecordAsync swallows its own I/O errors:
        // a journaling failure must never make a successful apply look failed.
        await _journal.RecordAsync(JournalReport.ForApply(result, allowed.Select(t => t.Id), LastVerification));
    }

    // The honest "verified" half of revert — the mirror of the shared apply verification. Re-read the live machine for the
    // tweaks we tried to revert (the pre-revert snapshot, NOT a now-cleared IsApplied filter) and remember any still
    // in their applied state. A revert the engine reported done but that didn't take surfaces as "still active"
    // rather than a clean "tout restauré"; shell-only ops (no readback) stay silent instead of being claimed either way.
    private async Task VerifyRevertedAsync(IReadOnlyList<Tweak> attempted)
    {
        if (attempted.Count == 0) { LastRevertVerification = null; return; }
        var states = await _tweakService.DetectAfterRevertAsync(attempted);
        LastRevertVerification = RevertVerifier.Build(attempted.Zip(states, (t, s) => (t.Id, s)));
    }

    // Load a saved profile into the tick-box selection for editing: tick exactly its tweaks (untick the rest),
    // prefill its name, and remember its id so the next save UPDATES it in place. Called cross-VM from the Profiles
    // page's "Modifier". Honest about a stale catalogue: an id this build no longer has simply doesn't get ticked
    // (a missing tweak can't be resurrected), and the footer count reflects what was actually selected.
    public void BeginEditingProfile(Profile profile)
    {
        var wanted = new HashSet<string>(profile.TweakIds, System.StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tweaks) t.IsSelected = wanted.Contains(t.Id);
        EditingProfileId = profile.Id;
        EditingProfileName = profile.Name;
        NewProfileName = profile.Name;
        RefreshCounts();
        StatusMessage = $"Édition de « {profile.Name} » : ajuste la sélection puis enregistre pour mettre à jour.";
    }

    // Leave edit mode without saving. Deliberately does NOT untick the loaded selection (non-destructive — the user
    // may still want to apply it); it only disarms the in-place update, so a subsequent save creates a new profile.
    [RelayCommand]
    private void CancelEditingProfile()
    {
        if (!IsEditingProfile) return;
        EditingProfileId = null;
        EditingProfileName = string.Empty;
        NewProfileName = string.Empty;
        StatusMessage = "Édition annulée — l'enregistrement créera désormais un nouveau profil.";
    }

    // Save the current tick-box selection as a reusable named profile — the producer half of custom profiles
    // (the Profiles page loads it back, resolving the stored ids against the live catalog). Honest no-op when
    // nothing is selected: we never persist an empty profile that would later "apply" nothing. In edit mode it
    // reuses the edited profile's id so SaveAsync overwrites in place instead of leaving a duplicate behind.
    [RelayCommand]
    private async Task SaveSelectionAsProfileAsync()
    {
        var ids = Tweaks.Where(t => t.IsSelected).Select(t => t.Id).ToList();
        if (ids.Count == 0)
        {
            // Keep edit mode armed: an empty selection can't update a profile, so the user fixes it or cancels.
            StatusMessage = "Aucun tweak sélectionné à enregistrer.";
            return;
        }
        var name = string.IsNullOrWhiteSpace(NewProfileName) ? "Mon profil" : NewProfileName.Trim();
        var profile = new Profile
        {
            Name = name,
            Description = $"{ids.Count} tweak(s) sélectionné(s).",
            TweakIds = ids
        };
        bool updating = EditingProfileId is not null;
        if (updating) profile.Id = EditingProfileId!;   // same id → SaveAsync overwrites the file in place
        await _profiles.SaveAsync(profile);

        EditingProfileId = null;
        EditingProfileName = string.Empty;
        NewProfileName = string.Empty;
        StatusMessage = updating
            ? $"Profil « {name} » mis à jour ({ids.Count} tweak(s))."
            : $"Profil « {name} » enregistré ({ids.Count} tweak(s)).";
    }

    // Manual re-probe: detection otherwise runs only once at load, so a tweak applied or reverted by another
    // tool — or undone by a reboot — would show a stale badge until the next launch. This re-reads the live
    // machine on demand. The generated AsyncRelayCommand disables itself while running, so probes can't overlap.
    [RelayCommand]
    private async Task RefreshStateAsync()
    {
        await DetectAppliedStateAsync();
        // A wholesale re-probe supersedes both verification banners; clearing them avoids showing an apply
        // "didn't stick" or a revert "still active" warning that the fresh detection above already reflects in the count.
        LastVerification = null;
        LastRevertVerification = null;
        StatusMessage = $"État rafraîchi : {AppliedCount} tweak(s) actif(s) détecté(s).";
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        // Snapshot before reverting: RevertAsync flips IsApplied, so a lazy filter would shift under us.
        var toRevert = Tweaks.Where(t => t.IsApplied).ToList();
        if (toRevert.Count == 0)
        {
            StatusMessage = "Aucun tweak à restaurer.";
            return;
        }
        var result = await _tweakService.RevertAllAsync(toRevert);
        RefreshCounts();
        // The post-apply verification banner described a state we just undid — drop it so it can't linger as a
        // stale "didn't stick" over tweaks that are now intentionally reverted.
        LastVerification = null;
        StatusMessage = FormatOutcome(result, "restauré");
        // Verify the revert the way we verify an apply: re-read the machine and surface any tweak still ACTIVE
        // despite the engine reporting it reverted. Speaks only through LastRevertVerification (a separate banner),
        // never folded into the pinned outcome string.
        await VerifyRevertedAsync(toRevert);
        // A revert is a change worth auditing too — record its tally AND any tweak the re-probe found still active,
        // so the durable journal reports a revert that didn't take exactly as it reports an apply that didn't stick.
        await _journal.RecordAsync(JournalReport.ForRevert(result, toRevert.Select(t => t.Id), LastRevertVerification));
    }

    // Honest one-line footer status: the real success count, plus ", N échec(s)" only when some failed —
    // so a partial batch never reads as a clean run. <paramref name="verbPast"/> is the FR past participle.
    private static string FormatOutcome(BatchTweakResult result, string verbPast)
    {
        var s = $"{result.Succeeded} tweak(s) {verbPast}(s)";
        if (result.Failed > 0)
            s += $", {result.Failed} échec(s)";
        return s;
    }
}
