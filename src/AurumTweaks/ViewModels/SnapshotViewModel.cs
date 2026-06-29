using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AurumTweaks.ViewModels;

/// <summary>
/// The « Instantanés » page: capture a point-in-time picture of the whole catalogue's live state, then later
/// compare a saved baseline against a fresh probe of the machine to see what drifted — above all the
/// <see cref="SnapshotComparison.Regressions"/> (a tweak that was applied and silently isn't any more). Honest by
/// construction: the comparison is the pure <see cref="SnapshotDiff.Compare"/> over real probes, so it never claims
/// a regression it can't back with two readable states. The optional one-click "ré-appliquer les régressions"
/// closes the loop through the SAME apply path as the Tweaks page (gated restore point, audited in the journal),
/// then RE-PROBES and re-compares so a fix that didn't stick still shows the regression — never a fabricated success.
/// </summary>
public partial class SnapshotViewModel : ObservableObject
{
    private readonly ISnapshotService _snapshots;
    private readonly ITweakRepository _repo;
    private readonly ITweakService _tweaks;
    private readonly IApplyJournal _journal;
    private readonly ILicenseService _license;
    private readonly IEvidenceLedger _evidence;

    // The baseline backing the live comparison — a plain field (not observable): the View reads
    // ComparisonBaselineLabel for display, while this keeps the reference needed to re-compare after a reapply.
    private SystemSnapshot? _comparisonBaseline;

    // The target (B) side when comparing two SAVED snapshots; null means the target is the live "maintenant" probe.
    // Kept so a delete of EITHER side drops the now-stale panel.
    private SystemSnapshot? _comparisonTarget;

    // Whether the active comparison's "current" side is the live machine. Only then is « ré-appliquer » honest: a
    // reapply acts on the live machine, so it must never be offered for a purely historical A→B diff (the panel
    // would silently switch meaning after the fix). Set BEFORE Comparison so its change-notification reads it fresh.
    private bool _comparisonAllowsReapply;

    public ObservableCollection<SystemSnapshot> Snapshots { get; } = new();

    /// <summary>Completes when the first load from the store has finished — lets the page (and tests) await the
    /// initial fill rather than racing the fire-and-forget load in the constructor.</summary>
    public Task Initialization { get; }

    // The same shared safety-net banner as the Tweaks/Profiles/Dashboard apply surfaces: « ré-appliquer » and
    // « aligner » both mutate the live machine through the restore-point-gated path, so this page must forecast the
    // same pre-flight posture (Restauration système unreadable, reboot pending) before the user commits.
    public PreflightBannerViewModel Preflight { get; }

    // SnapshotCount is the single source of truth for the empty/non-empty split; the flags all derive from it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshots))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasMultipleSnapshots))]
    private int _snapshotCount;

    [ObservableProperty] private string _newSnapshotLabel = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    // The two saved snapshots picked for a historical A→B comparison (the two dropdowns). Plain selections — the
    // CompareSaved command reads them on demand.
    [ObservableProperty] private SystemSnapshot? _baselineA;
    [ObservableProperty] private SystemSnapshot? _baselineB;

    // The active comparison (a saved baseline vs a freshly captured "now"). Null = none shown.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison))]
    [NotifyPropertyChangedFor(nameof(CanReapplyRegressions))]
    [NotifyPropertyChangedFor(nameof(CanAlignToSnapshot))]
    private SnapshotComparison? _comparison;

    [ObservableProperty] private string _comparisonBaselineLabel = string.Empty;

    // The "current" side's label in the header: "maintenant" for a live compare, or B's name for a historical A→B.
    [ObservableProperty] private string _comparisonTargetLabel = "maintenant";

    public bool HasSnapshots => SnapshotCount > 0;
    public bool IsEmpty => SnapshotCount == 0;
    // The historical-compare card needs at least two saved snapshots to be meaningful.
    public bool HasMultipleSnapshots => SnapshotCount > 1;
    public bool HasComparison => Comparison is not null;
    // « Ré-appliquer » is offered ONLY for a live comparison: reapplying touches the live machine, so a historical
    // A→B diff (which doesn't describe the machine now) must never show the button — even when it has regressions.
    public bool CanReapplyRegressions => _comparisonAllowsReapply && Comparison?.HasRegressions == true;
    // « Aligner » adds the REVERT direction (undo tweaks switched on since the snapshot). Shown only for a live
    // comparison that HAS improvements to undo — when there are none, « ré-appliquer » already does everything, so
    // offering align too would be a redundant/confusing duplicate.
    public bool CanAlignToSnapshot => _comparisonAllowsReapply && Comparison?.HasImprovements == true;

    public SnapshotViewModel(ISnapshotService snapshots, ITweakRepository repo, ITweakService tweaks,
                             IApplyJournal journal, ILicenseService license, IEvidenceLedger evidence,
                             PreflightBannerViewModel preflight)
    {
        _snapshots = snapshots;
        _repo = repo;
        _tweaks = tweaks;
        _journal = journal;
        _license = license;
        _evidence = evidence;
        Preflight = preflight;   // shared singleton; probes the safety net in its own ctor, off this snapshot load
        Initialization = LoadAsync();
    }

    // Publish the on-screen settings diff to the shared ledger so the Dashboard's unified « preuve » can fold in what
    // the tweaks changed. The labels are always assigned BEFORE Comparison in the compare commands, so they're fresh
    // here; a null (CloseComparison / a deleted side) clears the slot so a stale diff can't be pasted as current.
    partial void OnComparisonChanged(SnapshotComparison? value)
        => _evidence.PublishSettings(value, ComparisonBaselineLabel, ComparisonTargetLabel);

    /// <summary>Reload from the store. Called when the page is shown so a capture made elsewhere appears without a
    /// restart — the VM is a singleton, otherwise filled once.</summary>
    public Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        Snapshots.Clear();
        foreach (var s in await _snapshots.LoadAllAsync()) Snapshots.Add(s);
        SnapshotCount = Snapshots.Count;
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    // Probe the live machine and persist the capture. Always meaningful (it records whatever the catalogue detects),
    // so there's no empty-state no-op here.
    [RelayCommand]
    private async Task CaptureAsync()
    {
        var snapshot = await _snapshots.CaptureAsync(NewSnapshotLabel);
        Snapshots.Insert(0, snapshot);   // newest first, matching the store's order
        SnapshotCount = Snapshots.Count;
        NewSnapshotLabel = string.Empty;
        Status = $"Instantané capturé : {snapshot.Entries.Count} tweak(s) — {snapshot.StateSummaryLabel}.";
    }

    // Bring a snapshot in from a portable file (a baseline carried across a reinstall, or shared by someone else).
    // Dialog glue only; the testable work is ImportFromPathAsync.
    [RelayCommand]
    private async Task ImportSnapshotAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Importer un instantané",
            Filter = "Instantané Aurum (*.json)|*.json|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        await ImportFromPathAsync(dlg.FileName);
    }

    // The seam under the dialog: persist + list-insert + honest status. Internal so a test can drive both the success
    // and the validation-failure path without a file picker. A rejected file (unreadable / entry-less) shows the
    // service's French reason and leaves the list untouched — never a half-imported, uncomparable record.
    internal async Task ImportFromPathAsync(string path)
    {
        try
        {
            var imported = await _snapshots.ImportAsync(path);
            Snapshots.Insert(0, imported);   // surface it at the top immediately; a Reload re-sorts by capture time
            SnapshotCount = Snapshots.Count;
            Status = $"Instantané importé : {imported.DisplayLabel} — {imported.StateSummaryLabel}.";
        }
        catch (SnapshotImportException ex)
        {
            Status = ex.Message;
        }
    }

    // The flagship flow: probe the machine NOW and diff it against the chosen saved baseline. The per-row button
    // hands the baseline in as the command parameter.
    [RelayCommand]
    private async Task CompareToNowAsync(SystemSnapshot? baseline)
    {
        if (baseline is null) return;
        var live = await _snapshots.CaptureLiveAsync(null);
        _comparisonBaseline = baseline;
        _comparisonTarget = null;               // target is the live probe, not a saved snapshot
        _comparisonAllowsReapply = true;        // live side → « ré-appliquer » is honest here (set before Comparison)
        ComparisonTargetLabel = "maintenant";
        ComparisonBaselineLabel = baseline.DisplayLabel;
        Comparison = SnapshotDiff.Compare(baseline, live);
        Status = $"Comparaison « {baseline.DisplayLabel} » → maintenant : {Comparison.Summary}";
    }

    // Compare two SAVED snapshots (historical drift A → B), e.g. « avant MAJ » → « après MAJ ». Reuses the SAME pure
    // diff as the live compare, but neither side is probed now, so this view is purely informational: the re-apply
    // action stays hidden (re-applying would act on the LIVE machine, which this comparison doesn't describe).
    [RelayCommand]
    private void CompareSaved()
    {
        var a = BaselineA;
        var b = BaselineB;
        if (a is null || b is null)
        {
            Status = "Choisis deux instantanés à comparer.";
            return;
        }
        _comparisonBaseline = a;
        _comparisonTarget = b;
        _comparisonAllowsReapply = false;       // historical diff → no live machine to re-apply onto (set before Comparison)
        ComparisonBaselineLabel = a.DisplayLabel;
        ComparisonTargetLabel = b.DisplayLabel;
        Comparison = SnapshotDiff.Compare(a, b);
        Status = $"Comparaison « {a.DisplayLabel} » → « {b.DisplayLabel} » : {Comparison.Summary}";
    }

    [RelayCommand]
    private void CloseComparison()
    {
        Comparison = null;
        _comparisonBaseline = null;
        _comparisonTarget = null;
        _comparisonAllowsReapply = false;
        ComparisonBaselineLabel = string.Empty;
        ComparisonTargetLabel = "maintenant";
    }

    // Save the on-screen comparison as a plain-text drift report (forum, support thread). The SHAPE is the pure
    // SnapshotReport.Render; this is only the file-dialog/write glue, with an honest no-op when nothing is shown.
    [RelayCommand]
    private async Task ExportComparisonAsync()
    {
        var comparison = Comparison;
        if (comparison is null) { Status = "Aucune comparaison à exporter."; return; }
        var dlg = new SaveFileDialog
        {
            Title = "Exporter la comparaison",
            FileName = $"aurum-comparaison-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, SnapshotReport.Render(comparison, ComparisonBaselineLabel, ComparisonTargetLabel, DateTime.UtcNow));
            Status = "Comparaison exportée.";
        }
        catch (IOException ex) { Status = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Status = $"Export impossible : {ex.Message}"; }
    }

    // Same report, straight to the clipboard. Catches the locked-clipboard case and points to Export rather than
    // failing silently — the JournalViewModel copy pattern.
    [RelayCommand]
    private void CopyComparison()
    {
        var comparison = Comparison;
        if (comparison is null) { Status = "Aucune comparaison à copier."; return; }
        try
        {
            Clipboard.SetText(SnapshotReport.Render(comparison, ComparisonBaselineLabel, ComparisonTargetLabel, DateTime.UtcNow));
            Status = "Comparaison copiée. Colle-la où tu veux.";
        }
        catch { Status = "Copie impossible (presse-papiers occupé). Utilise « Exporter… » à la place."; }
    }

    // Close the detect→fix loop: re-apply EXACTLY the tweaks the comparison flagged as regressed. Routes through the
    // shared ApplyManyAsync (gated restore point) and audits the batch in the journal, then re-probes and re-compares
    // against the same baseline so the outcome is honest — a reapply that didn't stick still shows the regression.
    [RelayCommand]
    private async Task ReapplyRegressionsAsync()
    {
        var comparison = Comparison;
        if (comparison is null || !comparison.HasRegressions)
        {
            Status = "Aucune régression à ré-appliquer.";
            return;
        }

        // Resolve the regressed ids against the LIVE catalogue, skipping any it no longer knows (a snapshot can
        // outlive a tweak) — the same defensive resolution as ProfileComposition, so a stale id can't crash or be
        // silently counted as re-applied.
        var toReapply = comparison.Regressions
            .Select(r => _repo.GetById(r.TweakId))
            .OfType<Tweak>()
            .ToList();
        if (toReapply.Count == 0)
        {
            Status = "Les tweaks régressés ne sont plus dans le catalogue — rien à ré-appliquer.";
            return;
        }

        // Freemium gate: ré-appliquer turns tweaks back ON, so it's an APPLY direction and must obey the gate. A
        // regressed tweak can be Avancé/Extreme (Premium), so a configured Free build refuses those rather than
        // silently re-writing them. Same TweakGate/PremiumGateText as every other apply surface. As-shipped (no
        // embedded key) everything is allowed — a no-op until a seller configures licensing.
        var (allowed, locked) = TweakGate.Partition(_license.IsConfigured, _license.CurrentEdition, toReapply);
        if (allowed.Count == 0)
        {
            Status = PremiumGateText.AllLocked(locked.Count);
            return;
        }

        var result = await _tweaks.ApplyManyAsync(allowed);
        if (result.RestorePointFailed)
        {
            // The required restore point failed → nothing was re-applied. Honest reason; skip the journal and the
            // re-compare, because the machine is unchanged and the existing comparison still reflects reality.
            Status = TweakApplyText.RestorePointFailed;
            return;
        }
        // Audit it like every other apply batch (Tweaks page, dashboard, profile). RecordAsync swallows its own I/O
        // errors, so journaling can never make a successful reapply look failed.
        await _journal.RecordAsync(JournalReport.ForApply(result, allowed.Select(t => t.Id), null));

        // Re-probe + re-compare so the buckets reflect reality, not optimism.
        if (_comparisonBaseline is not null)
        {
            var live = await _snapshots.CaptureLiveAsync(null);
            Comparison = SnapshotDiff.Compare(_comparisonBaseline, live);
        }

        var s = $"{result.Succeeded} régression(s) ré-appliquée(s)";
        if (result.Failed > 0) s += $", {result.Failed} échec(s)";
        if (Comparison is not null) s += $". {Comparison.Summary}";
        // Disclose any Premium regressions withheld, ONLY when some were — so a fully-allowed reapply keeps its
        // pinned "N régression(s) ré-appliquée(s)…" contract.
        if (locked.Count > 0) s += PremiumGateText.LockedSuffix(locked.Count);
        Status = s;
    }

    // The capstone of the loop: make the live machine MATCH the saved baseline exactly — re-apply its regressions
    // (off now, on in the snapshot) AND revert its improvements (on now, off in the snapshot, i.e. tweaks switched on
    // SINCE the capture). Both directions act only on definite states (SnapshotDiff already excludes Indeterminate
    // from the two buckets, so we never touch something we couldn't read). Routes through the shared apply/revert
    // paths — restore point gated on apply, exactly like the Tweaks page — audits BOTH batches, then re-probes and
    // re-compares so a partial align still shows the remaining drift rather than a fabricated "matched".
    [RelayCommand]
    private async Task AlignToSnapshotAsync()
    {
        var comparison = Comparison;
        if (comparison is null || !_comparisonAllowsReapply)
        {
            Status = "Aligner n'est possible que pour une comparaison « à maintenant ».";
            return;
        }

        // Resolve both buckets against the LIVE catalogue, skipping ids it no longer knows (a snapshot can outlive a
        // tweak) — the same defensive resolution as « ré-appliquer ».
        var toApply = comparison.Regressions.Select(r => _repo.GetById(r.TweakId)).OfType<Tweak>().ToList();
        var toRevert = comparison.Improvements.Select(r => _repo.GetById(r.TweakId)).OfType<Tweak>().ToList();
        if (toApply.Count == 0 && toRevert.Count == 0)
        {
            Status = "La machine correspond déjà à l'instantané — rien à aligner.";
            return;
        }

        // Freemium gate, applied ONLY to the apply direction. Re-applying regressions turns tweaks ON, so it obeys the
        // gate; reverting improvements turns tweaks OFF, which is NEVER gated (backing a tweak out must always be
        // allowed — mirrors RevertAllAsync and the GPU-OC reset). So a configured Free build still undoes Premium
        // improvements while declining to re-apply Premium regressions, and discloses the count it withheld. As-shipped
        // (no embedded key) everything is allowed — a no-op until a seller configures licensing.
        var (allowedApply, lockedApply) = TweakGate.Partition(_license.IsConfigured, _license.CurrentEdition, toApply);

        BatchTweakResult? applyResult = null;
        BatchTweakResult? revertResult = null;
        if (allowedApply.Count > 0)
        {
            applyResult = await _tweaks.ApplyManyAsync(allowedApply);
            if (applyResult.RestorePointFailed)
            {
                // The required restore point failed, so the apply direction touched nothing. Abort the WHOLE align
                // before the revert too: without the safety net (the one ApplyManyAsync would have created, and which
                // would also have covered the revert) we don't mutate the machine in either direction. Honest reason,
                // no journal — nothing happened.
                Status = TweakApplyText.RestorePointFailed;
                return;
            }
            await _journal.RecordAsync(JournalReport.ForApply(applyResult, allowedApply.Select(t => t.Id), null));
        }
        if (toRevert.Count > 0)
        {
            revertResult = await _tweaks.RevertAllAsync(toRevert);
            // null: the snapshot path runs no post-revert re-probe (it re-compares the whole set just below instead),
            // so — like its ForApply sibling above — it makes no "still active" claim rather than a fabricated one.
            await _journal.RecordAsync(JournalReport.ForRevert(revertResult, toRevert.Select(t => t.Id), verification: null));
        }

        // Re-probe + re-compare so the buckets reflect reality, not optimism.
        if (_comparisonBaseline is not null)
        {
            var live = await _snapshots.CaptureLiveAsync(null);
            Comparison = SnapshotDiff.Compare(_comparisonBaseline, live);
        }

        var applied = applyResult?.Succeeded ?? 0;
        var reverted = revertResult?.Succeeded ?? 0;
        var failed = (applyResult?.Failed ?? 0) + (revertResult?.Failed ?? 0);
        var s = $"Alignement : {applied} ré-appliqué(s), {reverted} annulé(s)";
        if (failed > 0) s += $", {failed} échec(s)";
        if (Comparison is not null) s += $". {Comparison.Summary}";
        // Disclose any Premium regressions NOT re-applied, ONLY when some were withheld — the revert side still ran.
        if (lockedApply.Count > 0) s += PremiumGateText.LockedSuffix(lockedApply.Count);
        Status = s;
    }

    // Per-row: write one saved snapshot to a portable file. Dialog/write glue; the SHAPE is pure (SnapshotPortability).
    [RelayCommand]
    private async Task ExportSnapshotAsync(SystemSnapshot? snapshot)
    {
        if (snapshot is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exporter l'instantané",
            // Keep the user label OUT of the file name — a label can carry characters illegal in a path; the
            // timestamp is always safe and still unique enough to tell exports apart.
            FileName = $"aurum-instantane-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "Instantané Aurum (*.json)|*.json|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await _snapshots.ExportAsync(snapshot, dlg.FileName);
            Status = "Instantané exporté.";
        }
        catch (IOException ex) { Status = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Status = $"Export impossible : {ex.Message}"; }
    }

    // Per-row: copy ONE saved snapshot's state as a human-readable text report (forum, support thread) — the readable
    // counterpart to the JSON export above. Clipboard glue only; the SHAPE is the pure SnapshotStateReport. Catches the
    // locked-clipboard case and points at « Exporter l'état… » rather than failing silently (the CopyComparison pattern).
    [RelayCommand]
    private void CopySnapshotState(SystemSnapshot? snapshot)
    {
        if (snapshot is null) return;
        try
        {
            Clipboard.SetText(SnapshotStateReport.Render(snapshot, DateTime.UtcNow));
            Status = "État de l'instantané copié — colle-le où tu veux (forum, Discord, thread OC).";
        }
        catch { Status = "Copie impossible (presse-papiers occupé). Utilise « Exporter l'état… » à la place."; }
    }

    // Per-row: save the SAME readable state report as a .txt (distinct from « Exporter… », which writes the re-importable
    // .json). Dialog/write glue; the SHAPE is the pure SnapshotStateReport, and a write failure is reported honestly.
    [RelayCommand]
    private async Task ExportSnapshotStateAsync(SystemSnapshot? snapshot)
    {
        if (snapshot is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exporter l'état de l'instantané",
            FileName = $"aurum-instantane-etat-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, SnapshotStateReport.Render(snapshot, DateTime.UtcNow));
            Status = "État de l'instantané exporté.";
        }
        catch (IOException ex) { Status = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Status = $"Export impossible : {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DeleteAsync(SystemSnapshot? snapshot)
    {
        if (snapshot is null) return;
        await _snapshots.DeleteAsync(snapshot.Id);
        Snapshots.Remove(snapshot);
        SnapshotCount = Snapshots.Count;
        // If the deleted snapshot was EITHER side on screen (the baseline, or the B of a historical A→B compare), the
        // comparison now describes a snapshot that no longer exists — drop it rather than leave a stale panel.
        if ((_comparisonBaseline is not null && _comparisonBaseline.Id == snapshot.Id) ||
            (_comparisonTarget is not null && _comparisonTarget.Id == snapshot.Id)) CloseComparison();
        Status = "Instantané supprimé.";
    }
}
