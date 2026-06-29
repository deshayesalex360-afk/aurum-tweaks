using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace AurumTweaks.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IHardwareService _hardware;
    private readonly IMonitoringService _monitoring;
    private readonly IAdaptiveRecommendationService _adaptive;
    private readonly IAppSettingsStore _settings;
    private readonly ITweakService _tweakService;
    private readonly ITweakRepository _repo;
    private readonly ILocalizationService _localization;
    private readonly IApplyJournal _journal;
    private readonly IPowerPlanService _power;
    private readonly ITimerResolutionService _timer;
    private readonly IPendingRebootService _pendingReboot;
    private readonly IDriveHealthService _driveHealth;
    private readonly IScoreHistoryStore _scoreHistory;
    private readonly ILicenseService _license;
    private readonly IEvidenceLedger _evidence;

    /// <summary>The shared pre-flight safety banner — the SAME instance the Tweaks page shows. The dashboard is the
    /// one-click front door, so its « Appliquer le set » must forecast the apply-time restore-point abort just like the
    /// Tweaks page does. Probed once in the child VM's ctor; « Revérifier » re-probes on demand.</summary>
    public PreflightBannerViewModel Preflight { get; }

    [ObservableProperty] private HardwareInfo? _hardwareInfo;
    [ObservableProperty] private MonitoringSnapshot? _liveSnapshot;
    [ObservableProperty] private bool _isLoading = true;

    // --- Adaptive plan ("adapté à ton PC") ---
    [ObservableProperty] private bool _planReady;
    [ObservableProperty] private string _profileSummary = string.Empty;
    [ObservableProperty] private int _recommendedCount;
    [ObservableProperty] private int _totalApplicable;
    [ObservableProperty] private int _potentialScore;
    [ObservableProperty] private bool _isApplyingPlan;
    [ObservableProperty] private string _planStatus = string.Empty;

    // Post-apply verification for the one-click « Appliquer le set » — the front-door twin of the Tweaks page banner.
    // After the recommended set runs we re-read the machine and keep any tweak the engine reported applied that does
    // NOT read back as live (a real "didn't stick"). Drives a danger banner only when real (HasUnconfirmed); a clean
    // apply leaves it null/empty, so the front door is verified, not trusted — never a fabricated ✓ over the status.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnconfirmed))]
    private VerificationReport? _lastVerification;

    public bool HasUnconfirmed => LastVerification?.HasUnconfirmed == true;

    // --- Persistance des optimisations (drift) : ce que vous aviez PROUVÉ actif, et que le système a depuis désactivé ---
    // Diffs the change journal's confirmed-applied ids (tweaks read back live at apply time) against the freshest
    // on-system re-probe (_lastStates). Only a once-PROVEN tweak can drift, and an unreadable one is never counted — so
    // the card is an honest « ces optimisations ont été annulées », never a guess. _driftedTweaks keeps the Tweak
    // objects behind Drift.Drifted so « Réappliquer » acts on them directly (un bouton = une action) and names localize.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrift))]
    [NotifyPropertyChangedFor(nameof(DriftSummary))]
    [NotifyPropertyChangedFor(nameof(DriftedNames))]
    private DriftReport _drift = DriftReport.None;

    private List<Tweak> _driftedTweaks = new();

    public bool HasDrift => Drift.HasDrift;

    /// <summary>Honest count line for the drift card — shown only when there really is drift (HasDrift gates the card).</summary>
    public string DriftSummary => Drift.HasDrift
        ? $"{Drift.DriftedCount} optimisation(s) que vous aviez appliquée(s) ont été désactivées depuis (mise à jour Windows, stratégie de groupe ou autre logiciel)."
        : string.Empty;

    /// <summary>The drifted tweaks' localized names for the card body (the same localization the plan uses). Empty when none.</summary>
    public string DriftedNames => string.Join(", ", _driftedTweaks.Select(t => _localization.GetLocalizedFrom(t.Name)));

    // --- Optimization scorecard (how optimized THIS machine reads RIGHT NOW) ---
    // The capstone of on-system detection: the safe recommended set's live applied-state, weighted into one
    // honest 0-100. HasScore gates the ring so a pre-detection blank never shows as "0/100". The grade band
    // drives the ring colour (ScoreGradeToBrushConverter); ScoreCategories feeds the per-domain breakdown bars.
    [ObservableProperty] private bool _hasScore;
    [ObservableProperty] private int _optimizationScoreValue;
    [ObservableProperty] private ScoreGrade _scoreGrade;
    [ObservableProperty] private string _scoreGradeLabel = string.Empty;
    [ObservableProperty] private string _scoreSummary = string.Empty;
    [ObservableProperty] private string _scoreHint = string.Empty;
    [ObservableProperty] private int _scoreIndeterminateCount;

    // --- Score progression (how the score moved since the last DIFFERENT verifiable measure) ---
    // Each refresh records the score into a bounded timeline; HasScoreTrend gates the line so a first-ever
    // measure (no prior point) shows nothing rather than a fabricated "+0". The improvement/regression flags
    // drive the arrow's colour; the text is composed in the VM (no converter — the direction is already a
    // French word). "depuis le …" dates the last change, so a long-flat score doesn't imply fresh movement.
    [ObservableProperty] private bool _hasScoreTrend;
    [ObservableProperty] private string _scoreTrendText = string.Empty;
    [ObservableProperty] private bool _scoreTrendIsImprovement;
    [ObservableProperty] private bool _scoreTrendIsRegression;

    // --- Score sparkline (the trend line's visual companion) ---
    // The whole recorded timeline projected to pixel points by the pure ScoreSparkline core (fixed 0-100 scale, even
    // chronological spacing); a thin converter repackages them into the Polyline's PointCollection. Shares the trend's
    // ≥2-samples gate (HasScoreTrend), so the line and the « +13 pts » text appear together — empty until then.
    [ObservableProperty] private IReadOnlyList<ScorePoint> _scoreSparklinePoints = Array.Empty<ScorePoint>();

    // The sparkline's fixed render box, exposed so the XAML container binds the SAME dimensions the projection used —
    // one source of truth, so the Polyline maps 1:1 into the box. Padding keeps the 2px stroke off the top/bottom edge.
    public double SparklineWidth => 240;
    public double SparklineHeight => 44;
    private const double SparklinePadding = 3;

    /// <summary>Per-category slices of the score, so the front door shows WHERE the machine is strong or weak.</summary>
    public ObservableCollection<CategoryScore> ScoreCategories { get; } = new();

    /// <summary>The safe, one-click recommended tweaks tuned for this machine.</summary>
    public ObservableCollection<TweakRecommendation> TopRecommendations { get; } = new();

    /// <summary>Hardware-derived insights (RAM speed, BIOS, VBS, vendor guidance…).</summary>
    public ObservableCollection<HardwareInsight> Insights { get; } = new();

    /// <summary>The newest change-journal entries, surfaced on the front door so the audit trail is discoverable
    /// (it's otherwise only reachable via the nav rail / Ctrl+K). A read-only mirror of the store's newest-first
    /// head — it never re-applies or re-derives anything, exactly like the journal page.</summary>
    public ObservableCollection<JournalEntry> RecentActivity { get; } = new();

    [ObservableProperty] private bool _hasRecentActivity;

    // --- Pending-reboot banner (standard Windows signals; shown only when a restart is genuinely queued) ---
    [ObservableProperty] private bool _rebootPending;
    [ObservableProperty] private string _rebootSummary = string.Empty;

    /// <summary>One plain-French line per detected reboot signal (CBS, Windows Update, file renames, computer rename).</summary>
    public ObservableCollection<string> RebootReasons { get; } = new();

    // --- Shareable system report ---
    [ObservableProperty] private string _reportStatus = string.Empty;
    [ObservableProperty] private bool _isExportingReport;

    // --- Shareable before/after « preuve » (settings diff + frame-time A/B + score, folded into one paste) ---
    [ObservableProperty] private string _evidenceStatus = string.Empty;

    private const int RecentActivityShown = 4;

    private List<Tweak> _defaultSet = new();

    // The last on-system probe's tri-state per tweak, kept so the scorecard can exclude unverifiable
    // (Indeterminate) tweaks — collapsing to the bool IsApplied flag would lose that distinction and make a
    // genuine 100 unreachable. Repopulated every DetectAppliedStateAsync; the same instances the plan references.
    private IReadOnlyDictionary<Tweak, TweakAppliedState> _lastStates =
        new Dictionary<Tweak, TweakAppliedState>();

    public DashboardViewModel(
        IHardwareService hardware,
        IMonitoringService monitoring,
        IAdaptiveRecommendationService adaptive,
        IAppSettingsStore settings,
        ITweakService tweakService,
        ITweakRepository repo,
        ILocalizationService localization,
        IApplyJournal journal,
        IPowerPlanService power,
        ITimerResolutionService timer,
        IPendingRebootService pendingReboot,
        IDriveHealthService driveHealth,
        IScoreHistoryStore scoreHistory,
        ILicenseService license,
        IEvidenceLedger evidence,
        PreflightBannerViewModel preflight)
    {
        _hardware = hardware;
        _monitoring = monitoring;
        _adaptive = adaptive;
        _settings = settings;
        _tweakService = tweakService;
        _repo = repo;
        _localization = localization;
        _journal = journal;
        _power = power;
        _timer = timer;
        _pendingReboot = pendingReboot;
        _driveHealth = driveHealth;
        _scoreHistory = scoreHistory;
        _license = license;
        _evidence = evidence;
        Preflight = preflight;
        _monitoring.SnapshotReady += (_, s) => LiveSnapshot = s;
        _ = InitialiseAsync();
    }

    private async Task InitialiseAsync()
    {
        HardwareInfo = await _hardware.DetectAsync();
        _monitoring.Start();
        IsLoading = false;
        await RefreshPlanAsync();
        await LoadRecentActivityAsync();
        await RefreshPendingRebootAsync();
    }

    // Probe the standard Windows pending-reboot signals into the dashboard banner. The service reads the registry off
    // the UI thread; we set the observable banner state here on the captured context. Honest by construction: the
    // banner shows only when IsPending is true, and lists exactly the signals that read as set — never a guess.
    private async Task RefreshPendingRebootAsync()
    {
        var status = await _pendingReboot.GetStatusAsync();
        RebootReasons.Clear();
        foreach (var reason in status.Reasons)
            RebootReasons.Add(reason);
        RebootSummary = status.Summary;
        RebootPending = status.IsPending;
    }

    // Mirror the newest few journal entries for the dashboard card. The store is already newest-first, so Take is
    // the head. Kept separate from RefreshPlanAsync so the cheap cross-page refresh on view-show doesn't rebuild
    // the whole adaptive plan.
    private async Task LoadRecentActivityAsync()
    {
        RecentActivity.Clear();
        foreach (var e in (await _journal.LoadAsync()).Take(RecentActivityShown))
            RecentActivity.Add(e);
        HasRecentActivity = RecentActivity.Count > 0;
    }

    /// <summary>Refresh the cross-page dashboard state on view-show (DashboardView.Loaded) so a batch applied from
    /// the Tweaks/Profiles page — or a reboot that became pending meanwhile — appears without an app restart (the VM
    /// is a singleton). The two probes are independent, so they run concurrently.</summary>
    public Task RefreshRecentActivityAsync()
        => Task.WhenAll(LoadRecentActivityAsync(), RefreshPendingRebootAsync());

    [RelayCommand]
    private async Task RefreshPlanAsync()
    {
        // A wholesale manual re-probe supersedes the last apply's verification banner; clear it so a "didn't stick"
        // warning can't linger over state this fresh detection has already folded into the plan. (During a one-click
        // apply this runs first, then ApplyRecommendedAsync re-raises the banner from the just-measured result.)
        LastVerification = null;
        // Probe the live machine FIRST, so the adaptive plan reads honest IsApplied flags. Otherwise, on a fresh
        // launch every tweak looks un-applied: PotentialScore counts work already done, and "Appliquer le set"
        // re-runs tweaks that are already live. The catalog is a singleton shared with the engine, so the flags
        // we set here are exactly what BuildPlanAsync reads a line later.
        await DetectAppliedStateAsync();
        var plan = await _adaptive.BuildPlanAsync(_settings.Current.StrictCompetitiveAntiCheat);

        ProfileSummary = plan.ProfileSummaryFr;
        RecommendedCount = plan.RecommendedCount;
        TotalApplicable = plan.TotalApplicable;
        PotentialScore = plan.PotentialScore;

        _defaultSet = plan.Recommendations.Where(r => r.InDefaultSet).Select(r => r.Tweak).ToList();

        TopRecommendations.Clear();
        foreach (var r in plan.Recommendations.Where(r => r.InDefaultSet).Take(8))
        {
            r.DisplayName = _localization.GetLocalizedFrom(r.Tweak.Name);
            TopRecommendations.Add(r);
        }

        Insights.Clear();
        foreach (var i in plan.Insights.Take(8))
            Insights.Add(i);

        var card = UpdateScorecard();
        var progress = await RecordScoreTrendAsync(card);

        // Publish the live score + its trend into the shared ledger so the unified « preuve avant / après » can fold
        // it in alongside the settings diff and the frame-time A/B. A NoData card flows through too — EvidenceInputs
        // reads it as « non disponible », never a fabricated « 0/100 ».
        _evidence.PublishScore(card, progress);

        // Persistance : with the live states just probed (DetectAppliedStateAsync, above) and the change journal, surface
        // any once-proven tweak the machine now reports off — reusing this refresh's detection, so no extra probe. Last,
        // so the drift card reflects the newest plan and the freshest readings.
        await RefreshDriftAsync();

        PlanReady = true;
    }

    // Turn the recommended-and-applicable set's live tri-state into the front-door optimization score. The
    // denominator is the SAFE default set only (so skipping a risky Extreme tweak never drags the score down),
    // and the pure core drops Indeterminate tweaks from both sides (so 100 stays honestly reachable). A tweak the
    // plan recommends but the last probe never saw is treated as Indeterminate — unscored, not assumed missing.
    private OptimizationScorecard UpdateScorecard()
    {
        var card = OptimizationScore.Compute(ScoreInputsFor(_defaultSet, _lastStates));

        HasScore = card.HasData;
        OptimizationScoreValue = card.Score;
        ScoreGrade = card.Grade;
        ScoreGradeLabel = card.GradeLabel;
        ScoreIndeterminateCount = card.IndeterminateCount;
        ScoreSummary = card.HasData
            ? $"{card.AppliedCount} / {card.VerifiableCount} optimisations recommandées actives"
            : "Analyse de l'état du système…";
        ScoreHint = BuildScoreHint(card);

        ScoreCategories.Clear();
        foreach (var c in card.Categories)
            ScoreCategories.Add(c);

        return card;
    }

    // Persist this refresh's score into the bounded timeline and surface the movement since the last DIFFERENT
    // measure. Skipped when there's nothing verifiable to score, so the history never gains a fabricated 0. The
    // store dedupes an unchanged score (returning the same series without a write), so "depuis le …" tracks the
    // last real movement, not every launch. A pure side-record: the store swallows its own I/O errors, so this
    // can never block or fail the plan refresh. The headline text comes from the shared ScoreProgress.TrendLine
    // (same source the system report uses), which is empty on a first-ever measure — so the line stays hidden
    // (HasScoreTrend gates it) rather than inventing a "+0".
    private async Task<ScoreProgress> RecordScoreTrendAsync(OptimizationScorecard card)
    {
        // Nothing verifiable to score → no sample recorded (the history never gains a fabricated 0) and no trend to
        // publish. ScoreProgress.None keeps the caller's PublishScore honest: a NoData card with no movement.
        if (!card.HasData) return ScoreProgress.None;
        var history = await _scoreHistory.RecordAsync(card.Score);
        var progress = ScoreHistory.Summarize(history);

        HasScoreTrend = progress.HasTrend;
        ScoreTrendIsImprovement = progress.IsImprovement;
        ScoreTrendIsRegression = progress.IsRegression;
        ScoreTrendText = progress.TrendLine;
        // Project the whole timeline for the sparkline (empty < 2 samples, so it hides in lock-step with the trend).
        ScoreSparklinePoints = ScoreSparkline.Project(history, SparklineWidth, SparklineHeight, SparklinePadding);
        return progress;
    }

    // The recommended set's per-tweak score inputs against a given state map — shared by the dashboard ring (off the
    // last load-time probe, _lastStates) and the shareable report (off its own fresh probe), so the two surfaces can
    // never disagree on how a tweak is scored. A tweak the map never saw is Indeterminate — unscored, never assumed
    // missing. The map is keyed by the same Tweak instances the catalog singleton holds, so lookups hit.
    private static IEnumerable<ScoreInput> ScoreInputsFor(
        IEnumerable<Tweak> recommendedSet,
        IReadOnlyDictionary<Tweak, TweakAppliedState> states)
        => recommendedSet.Select(t => new ScoreInput(
            t.Category, t.Priority,
            states.TryGetValue(t, out var s) ? s : TweakAppliedState.Indeterminate));

    // One honest, actionable French line under the ring — never a fabricated promise. At 100 we congratulate and
    // disclose any unverifiable tweaks (so "Optimisé" doesn't over-claim); otherwise we name the real remaining
    // count, pointing the user at the one-click apply that exists right below.
    private static string BuildScoreHint(OptimizationScorecard card)
    {
        if (!card.HasData)
            return "Détection en cours — le score apparaît dès que l'état du système est lu.";
        int remaining = card.VerifiableCount - card.AppliedCount;
        if (remaining <= 0)
        {
            return card.IndeterminateCount > 0
                ? $"Tout le set recommandé vérifiable est actif. {card.IndeterminateCount} tweak(s) non vérifiable(s) (hors score)."
                : "Tout le set recommandé est actif. Ton PC est au taquet ✓";
        }
        string line = $"{remaining} optimisation(s) recommandée(s) encore à appliquer pour grimper.";
        if (card.IndeterminateCount > 0)
            line += $" ({card.IndeterminateCount} non vérifiable(s), hors score.)";
        return line;
    }

    // Reads the catalog's real applied-state into the shared Tweak instances before the plan is built. Mirrors
    // the Tweaks page: the service probes off the UI thread, we write the observable IsApplied back here on the
    // UI thread (the catalog is the same singleton the adaptive engine loads, so these writes are what it sees).
    // Probes the full TRI-STATE (not the bool DetectAppliedAsync): IsApplied takes the Applied half for the
    // badges, and the kept map lets the scorecard tell "not applied" from "unverifiable" — one probe, both needs.
    private async Task DetectAppliedStateAsync()
    {
        var all = await _repo.LoadAllAsync();
        var states = await _tweakService.DetectStatesAsync(all);
        var map = new Dictionary<Tweak, TweakAppliedState>(all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            all[i].IsApplied = states[i] == TweakAppliedState.Applied;
            map[all[i]] = states[i];
        }
        _lastStates = map;
    }

    // Recompute drift from the freshest on-system states (_lastStates, just set by DetectAppliedStateAsync) and the
    // change journal: JournalApplyIntent.Resolve picks the tweaks whose most-recent apply PROVED them live, and
    // DriftAnalysis.Detect keeps only those the machine now reports off. No extra detection I/O — only a small journal
    // read. _driftedTweaks carries the Tweak objects so « Réappliquer » and the localized names have what they need.
    private async Task RefreshDriftAsync()
    {
        var byId = new Dictionary<string, TweakAppliedState>(StringComparer.OrdinalIgnoreCase);
        var tweakById = new Dictionary<string, Tweak>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _lastStates)
        {
            byId[kv.Key.Id] = kv.Value;
            tweakById[kv.Key.Id] = kv.Key;
        }

        var intended = JournalApplyIntent.Resolve(await _journal.LoadAsync());
        var report = DriftAnalysis.Detect(intended, byId);

        _driftedTweaks = report.Drifted
            .Where(tweakById.ContainsKey)
            .Select(id => tweakById[id])
            .ToList();
        Drift = report;
    }

    [RelayCommand]
    private async Task ApplyRecommendedAsync()
    {
        var toApply = _defaultSet.Where(t => !t.IsApplied).ToList();
        await ApplySetAsync(toApply, "Tout le set recommandé est déjà appliqué ✓");
    }

    [RelayCommand]
    private async Task ReapplyDriftedAsync()
    {
        // « Réappliquer » re-runs exactly the tweaks this refresh proved drifted off — a real action on the machine, not
        // a redirect (un bouton = une action). The list was frozen by the last RefreshDriftAsync; ApplySetAsync re-probes
        // at the end, so a successful re-apply clears the drift card on the spot (the tweaks read back Applied again).
        var toApply = _driftedTweaks.Where(t => !t.IsApplied).ToList();
        await ApplySetAsync(toApply, "Les optimisations annulées sont déjà ré-appliquées ✓");
    }

    // The shared apply pipeline behind both the one-click recommended set and the drift « Réappliquer ». Identical
    // honesty contract either way: freemium gate → optional restore point (only when the toggle is on) → ApplyManyAsync
    // → the SHARED VerifyAppliedAsync re-probe → journal → an honest status that leads with the real success count and
    // always surfaces failures → refresh (which recomputes the score AND drift) → raise the verification banner last.
    private async Task ApplySetAsync(IReadOnlyList<Tweak> toApply, string alreadyDoneMessage)
    {
        if (toApply.Count == 0)
        {
            PlanStatus = alreadyDoneMessage;
            return;
        }

        // Freemium gate at the same place it bites on the Tweaks page: the set can include Avancé (Premium) tweaks, so a
        // configured Free build must refuse those rather than silently apply them. Routed through the shared TweakGate/
        // PremiumGateText so every apply surface words the lock identically. As-shipped everything lands in allowed.
        var (allowed, locked) = TweakGate.Partition(_license.IsConfigured, _license.CurrentEdition, toApply);
        if (allowed.Count == 0)
        {
            PlanStatus = PremiumGateText.AllLocked(locked.Count);
            return;
        }

        // Honest: only claim a restore point when one is actually created. The backend genuinely skips it
        // when this toggle is off, so announcing it regardless would be a fabricated safety claim.
        bool withRestore = _settings.Current.CreateRestorePointBeforeTweaks;

        LastVerification = null;   // a fresh apply supersedes any prior banner (and clears it if this run aborts)
        IsApplyingPlan = true;
        PlanStatus = withRestore
            ? $"Application de {allowed.Count} optimisations… (point de restauration en cours)"
            : $"Application de {allowed.Count} optimisations…";

        var result = await _tweakService.ApplyManyAsync(allowed);
        if (result.RestorePointFailed)
        {
            // The required restore point failed → nothing was applied. Honest reason, reset the busy flag, and skip
            // the journal + refreshes: the plan and the activity card are unchanged because the machine wasn't touched.
            PlanStatus = TweakApplyText.RestorePointFailed;
            IsApplyingPlan = false;
            return;
        }

        // Re-read the machine through the SHARED honest verification (the one ITweakService.VerifyAppliedAsync the
        // Tweaks and Profiles pages use): any write the engine REPORTED applied that doesn't read back surfaces honestly
        // — in the banner (LastVerification, set below) and in the durable journal — never a fabricated ✓. It re-probes
        // ONLY the tweaks whose IsApplied the engine set, so a genuinely failed tweak can't be mislabeled "didn't stick";
        // null = nothing genuinely applied → no banner, no claim. Its Confirmed ids seed future drift detection.
        var verification = await _tweakService.VerifyAppliedAsync(allowed);
        await _journal.RecordAsync(JournalReport.ForApply(result, allowed.Select(t => t.Id), verification));

        // Honest: lead with the real success count and always surface failures, so a partial batch can't read as a
        // clean run of a smaller set. The restore-point clause is keyed to the toggle, not the result.
        string status = $"{result.Succeeded} optimisation(s) appliquée(s)";
        if (result.Failed > 0)
            status += $", {result.Failed} échec(s)";
        status += withRestore
            ? ". Un point de restauration a été créé."
            : " — sans point de restauration (option désactivée dans Paramètres).";
        if (result.Succeeded > 0)
            status += " Un redémarrage peut être requis.";
        // Disclose any Premium picks withheld, ONLY when some were — so the common (and as-shipped) case keeps the
        // exact "N optimisation(s) appliquée(s)…" contract the tests pin.
        if (locked.Count > 0)
            status += PremiumGateText.LockedSuffix(locked.Count);
        PlanStatus = status;
        IsApplyingPlan = false;

        await RefreshPlanAsync();
        await LoadRecentActivityAsync();   // the apply we just journaled should appear in the card immediately
        // Raise the verification banner LAST: RefreshPlanAsync (above) clears it for the manual-refresh case, so setting
        // it here guarantees the internal refresh that's part of this apply can't wipe what we just measured.
        LastVerification = verification;
    }

    /// <summary>Compose and save the shareable « rapport système » — real detected hardware + the tweaks currently
    /// detected as applied + the recent journal + the safety toggles. Honest no-op (with a reason) until hardware
    /// detection has finished, so the report can never go out as a sheet of blanks. The tested part is the pure
    /// <see cref="SystemReport.Render"/>; this is the thin probe + save-dialog glue.</summary>
    [RelayCommand]
    private async Task ExportSystemReportAsync()
    {
        if (IsExportingReport) return;
        IsExportingReport = true;
        ReportStatus = "Analyse du système en cours…";
        try
        {
            var built = await BuildSystemReportAsync();
            if (built is not { } report)
            {
                ReportStatus = "Patiente : la détection matérielle n'est pas encore terminée.";
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Exporter le rapport système",
                FileName = $"aurum-rapport-systeme-{DateTime.Now:yyyyMMdd-HHmm}.txt",
                Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) { ReportStatus = string.Empty; return; }

            await File.WriteAllTextAsync(dlg.FileName, report.Text);
            ReportStatus = $"Rapport exporté — {report.AppliedCount} tweak(s) appliqué(s) recensé(s).";
        }
        catch (IOException ex) { ReportStatus = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { ReportStatus = $"Export impossible : {ex.Message}"; }
        finally { IsExportingReport = false; }
    }

    /// <summary>Copy the same « rapport système » straight to the clipboard — the way a config is actually shared on
    /// a forum or Discord is a paste, not a file attachment. Reuses the exact probe + render the file export uses.</summary>
    [RelayCommand]
    private async Task CopySystemReportAsync()
    {
        if (IsExportingReport) return;
        IsExportingReport = true;
        ReportStatus = "Analyse du système en cours…";
        try
        {
            var built = await BuildSystemReportAsync();
            if (built is not { } report)
            {
                ReportStatus = "Patiente : la détection matérielle n'est pas encore terminée.";
                return;
            }
            try
            {
                Clipboard.SetText(report.Text);
                ReportStatus = $"Rapport copié — {report.AppliedCount} tweak(s) appliqué(s). Colle-le où tu veux.";
            }
            catch
            {
                // Clipboard can be momentarily locked by another process — non-fatal; point at the file route.
                ReportStatus = "Copie impossible (presse-papiers occupé). Utilise « Exporter » à la place.";
            }
        }
        finally { IsExportingReport = false; }
    }

    // Probe the live machine and render the shareable report — the shared core of the file export and the clipboard
    // copy. Returns null only before hardware detection has finished, so each caller can state the honest reason.
    private async Task<(string Text, int AppliedCount)?> BuildSystemReportAsync()
    {
        if (HardwareInfo is null) return null;

        // Read the live applied-state (full tri-state) so the report lists what's REALLY on the machine AND scores it
        // from the SAME probe — the applied list and the optimization headline can never disagree. One probe feeds
        // both: the applied names (state == Applied) and the per-tweak map the scorecard needs. The catalog is the
        // singleton the plan's _defaultSet references, so the map's keys line up with the recommended set.
        var all = await _repo.LoadAllAsync();
        var states = await _tweakService.DetectStatesAsync(all);
        var appliedNames = new List<string>();
        var stateMap = new Dictionary<Tweak, TweakAppliedState>(all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            stateMap[all[i]] = states[i];
            if (states[i] == TweakAppliedState.Applied)
                appliedNames.Add(_localization.GetLocalizedFrom(all[i].Name));
        }
        var scorecard = OptimizationScore.Compute(ScoreInputsFor(_defaultSet, stateMap));

        // The score's movement over time for the report's OPTIMISATION section — READ-only here (exporting a report
        // must never mutate the timeline), so we summarise the persisted history without recording a new sample. The
        // trend therefore matches exactly what the dashboard last recorded, and a relative "depuis le …" line can't
        // contradict the fresh headline score.
        var scoreProgress = ScoreHistory.Summarize(await _scoreHistory.LoadAsync());

        // Hand the FULL trail to the renderer — it leads the journal section with a whole-trail synthesis and caps
        // the per-entry detail itself (that windowing policy lives in the tested pure core now, not here).
        var journal = await _journal.LoadAsync();

        // Live power/timer readouts for the report's machine-state sections. The two powercfg probes are independent,
        // so start both before awaiting; the timer read is an instantaneous ntdll query. Only an explicit user action
        // hits this path, never a hot loop.
        var powerReportTask = _power.GetReportAsync();
        var processorDetailTask = _power.GetProcessorDetailAsync();
        var pendingRebootTask = _pendingReboot.GetStatusAsync();
        // Drive health joins the optional probes so the report surfaces a dying disk, not just its capacity. Same
        // PowerShell-backed read the Santé des disques page uses; one explicit user action, never a hot loop.
        var driveHealthTask = _driveHealth.GetReportAsync();
        var timer = _timer.Read();
        var powerReport = await powerReportTask;
        var processorDetail = await processorDetailTask;
        var pendingReboot = await pendingRebootTask;
        var driveHealth = await driveHealthTask;

        var text = SystemReport.Render(
            HardwareInfo, appliedNames, journal,
            _settings.Current.CreateRestorePointBeforeTweaks,
            _settings.Current.StrictCompetitiveAntiCheat,
            DateTime.UtcNow,
            powerReport.ActiveName, processorDetail, timer, pendingReboot, driveHealth,
            scorecard, scoreProgress);
        return (text, appliedNames.Count);
    }

    // Said when the user asks for the proof before measuring anything — an honest no-op, never an empty document. Kept
    // in one place so the export and the copy can't word the "nothing yet" case differently.
    private const string NoEvidenceGuidance =
        "Rien à prouver pour l'instant — compare un instantané (Instantanés), lance un A/B (Benchmark), " +
        "ou laisse le score se calculer ici, puis reviens.";

    /// <summary>Export the unified « preuve avant / après » — the settings diff, the frame-time A/B, and the
    /// optimization score, each published by its own page into the shared ledger and folded into one shareable text
    /// block. An honest no-op (with guidance, before any dialog) when nothing has been measured yet, so the proof is
    /// never a sheet of « non disponible ». The tested shape is the pure <see cref="EvidenceReport.Render"/>; this is
    /// the save-dialog glue.</summary>
    [RelayCommand]
    private async Task ExportEvidenceReportAsync()
    {
        var inputs = _evidence.Current();
        if (!inputs.HasAnyEvidence) { EvidenceStatus = NoEvidenceGuidance; return; }

        var dlg = new SaveFileDialog
        {
            Title = "Exporter la preuve avant/après",
            FileName = $"aurum-preuve-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) { EvidenceStatus = string.Empty; return; }
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, EvidenceReport.Render(inputs, DateTime.UtcNow, HardwareInfo));
            EvidenceStatus = "Preuve exportée.";
        }
        catch (IOException ex) { EvidenceStatus = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { EvidenceStatus = $"Export impossible : {ex.Message}"; }
    }

    /// <summary>Copy the same « preuve » straight to the clipboard — the way an A/B is actually shared on a forum or
    /// Discord is a paste. Reads the same ledger snapshot the export does; the same honest no-op when nothing's measured.</summary>
    [RelayCommand]
    private void CopyEvidenceReport()
    {
        var inputs = _evidence.Current();
        if (!inputs.HasAnyEvidence) { EvidenceStatus = NoEvidenceGuidance; return; }
        try
        {
            Clipboard.SetText(EvidenceReport.Render(inputs, DateTime.UtcNow, HardwareInfo));
            EvidenceStatus = "Preuve copiée. Colle-la où tu veux (forum, Discord, thread OC).";
        }
        catch { EvidenceStatus = "Copie impossible (presse-papiers occupé). Utilise « Exporter » à la place."; }
    }

    [RelayCommand]
    private void RunInsightAction(HardwareInsight? insight)
    {
        if (insight?.ActionPage is { Length: > 0 } page)
            App.Services.GetRequiredService<MainViewModel>().Navigate(page);
    }

    [RelayCommand]
    private void GoToTweaks() => App.Services.GetRequiredService<MainViewModel>().Navigate("Tweaks");

    [RelayCommand]
    private void GoToBios() => App.Services.GetRequiredService<MainViewModel>().Navigate("Bios");

    [RelayCommand]
    private void GoToGaming() => App.Services.GetRequiredService<MainViewModel>().Navigate("Gaming");

    [RelayCommand]
    private void GoToJournal() => App.Services.GetRequiredService<MainViewModel>().Navigate("Journal");
}
