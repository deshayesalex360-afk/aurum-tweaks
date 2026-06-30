using System.Collections.Generic;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

public interface ITweakRepository
{
    /// <summary>Load all tweaks from JSON files in the Tweaks folder (Tranquille/Avance/Extreme).</summary>
    Task<IReadOnlyList<Tweak>> LoadAllAsync();
    Tweak? GetById(string id);

    /// <summary>Tweak files the integrity gate refused (hash did not match the baked-in manifest), each as
    /// <c>"relative/path.json [Verdict]"</c>. Empty in the normal case. Exposed on the contract so the transparency
    /// disclosure can report the gate's verdict honestly — a non-empty list is a tampered/dropped file that was NOT
    /// loaded and whose elevated operations never ran.</summary>
    IReadOnlyList<string> RejectedFiles { get; }
}

public interface ITweakService
{
    Task<TweakApplyResult> ApplyAsync(Tweak tweak);
    Task<TweakApplyResult> RevertAsync(Tweak tweak);

    /// <summary>
    /// Probe the live system to decide whether this tweak is currently in its applied state — honestly.
    /// Returns <see cref="TweakAppliedState.Applied"/> only when EVERY readable op matches its applied value,
    /// <see cref="TweakAppliedState.NotApplied"/> when at least one readable op differs, and
    /// <see cref="TweakAppliedState.Indeterminate"/> when nothing about the tweak can be read back (shell-only
    /// ops, a missing service) — never a guess.
    /// </summary>
    Task<TweakAppliedState> IsAppliedAsync(Tweak tweak);

    /// <summary>
    /// Probe a whole batch of tweaks off the UI thread in one shot, returning per-tweak "is it lit?" flags in
    /// the SAME order as the input. A flag is <c>true</c> only for <see cref="TweakAppliedState.Applied"/>;
    /// both <see cref="TweakAppliedState.NotApplied"/> and <see cref="TweakAppliedState.Indeterminate"/> collapse
    /// to <c>false</c> — the honesty rule "never paint a ✓ we can't verify". Deliberately returns the flags
    /// instead of mutating <c>Tweak.IsApplied</c>: that property is observable, so the caller must write it back
    /// on the UI thread. Shared by the Tweaks page (load-time badge) and the Dashboard (so the adaptive plan's
    /// PotentialScore and one-click apply read the real machine, not a flag that resets every launch).
    /// </summary>
    Task<IReadOnlyList<bool>> DetectAppliedAsync(IReadOnlyList<Tweak> tweaks);

    /// <summary>
    /// Probe a batch off the UI thread and return each tweak's full <see cref="TweakAppliedState"/> in input
    /// order — the tri-state the boolean <see cref="DetectAppliedAsync"/> collapses. Lets the Tweaks page VERIFY
    /// a batch right after applying: a write the engine reported as succeeded but that doesn't read back (group
    /// policy reverts it, another tool overwrites it, a protected value) surfaces as
    /// <see cref="TweakAppliedState.NotApplied"/> — an honest "didn't stick", never a fake green check — while
    /// shell-only ops stay <see cref="TweakAppliedState.Indeterminate"/> (we ran them, we can't confirm them).
    /// </summary>
    Task<IReadOnlyList<TweakAppliedState>> DetectStatesAsync(IReadOnlyList<Tweak> tweaks);

    /// <summary>
    /// The revert-side twin of <see cref="DetectStatesAsync"/>: re-probe a batch right after reverting it and return
    /// each tweak's state in input order, folded for the question "is it now OFF?". A tweak the engine reported
    /// reverted but that still reads back in its applied state surfaces as <see cref="TweakAppliedState.Applied"/>
    /// (STILL ACTIVE — a group policy re-applies it, another tool re-wrote it, a protected value), so the Tweaks page
    /// can show an honest "still active" warning instead of taking the revert on faith. The fold differs from
    /// <see cref="DetectStatesAsync"/>'s on purpose: there a single off op makes a tweak NotApplied, which here would
    /// MASK a sibling op still active — so a still-on op dominates, and only an all-off readback confirms the revert.
    /// </summary>
    Task<IReadOnlyList<TweakAppliedState>> DetectAfterRevertAsync(IReadOnlyList<Tweak> tweaks);

    /// <summary>
    /// The one honest post-apply verification every apply surface (Tweaks page, Dashboard one-click, profile load)
    /// shares, so the rule can't drift between them. Re-probes ONLY the tweaks the engine actually reported applied
    /// (<see cref="Tweak.IsApplied"/>) and folds their readback via <c>TweakVerifier.Build</c>. A tweak the engine
    /// FAILED is deliberately excluded: its absence on the machine is consistent with the failure, not a "didn't
    /// stick", so it must never be flagged as unconfirmed (that would be a fabricated alarm). Returns null when
    /// nothing was applied (every op failed, or an empty batch), so callers raise no banner and log no claim.
    /// </summary>
    Task<VerificationReport?> VerifyAppliedAsync(IReadOnlyList<Tweak> attempted);

    /// <summary>
    /// Build the read-only apply preview for a selected batch. This may read registry/service current values, but it
    /// must not write anything; the returned plan is built from the same dispatch the apply path executes.
    /// </summary>
    Task<ApplyPlan> PreviewApplyPlanAsync(IReadOnlyList<Tweak> tweaks);

    /// <summary>Apply every tweak; returns the honest tally so callers never present the success count as the whole story.</summary>
    Task<BatchTweakResult> ApplyManyAsync(IEnumerable<Tweak> tweaks);

    /// <summary>Revert every tweak; returns the honest tally (a half-reverted tweak counts as failed, not succeeded).</summary>
    Task<BatchTweakResult> RevertAllAsync(IEnumerable<Tweak> tweaks);
}

public sealed record TweakApplyResult(bool Success, string? Error = null, bool RequiresReboot = false);

/// <summary>
/// Outcome of a batch apply/revert. <see cref="Succeeded"/> counts tweaks that landed in full (every op),
/// <see cref="Failed"/> the rest. Kept as an honest pair — not a bare success count — so the UI can say
/// "3 appliquée(s), 2 échec(s)" instead of silently dropping the failures from a smaller-looking number.
/// <see cref="RestorePointFailed"/> is the safety-abort flag: a REQUIRED restore point that couldn't be created
/// makes the engine apply nothing and return <c>(0, 0, true)</c>, so a caller can speak the honest reason
/// (<see cref="TweakApplyText.RestorePointFailed"/>) instead of formatting a misleading "0 appliquée(s)".
/// </summary>
public sealed record BatchTweakResult(int Succeeded, int Failed, bool RestorePointFailed = false);

/// <summary>
/// The shared runtime copy the apply surfaces speak when a batch is refused for a SAFETY reason (not a per-op
/// failure). Pinned in one place — the <see cref="PremiumGateText"/> anti-drift discipline applied to apply-time
/// safety — so the Tweaks/Dashboard/Profiles/Snapshot pages can't word the same refusal differently. Today the one
/// message is <see cref="RestorePointFailed"/>: said when the user kept "create a restore point before tweaks" ON
/// but Windows couldn't make one, so the engine applied nothing rather than strand the user with un-backed changes.
/// </summary>
public static class TweakApplyText
{
    public const string RestorePointFailed =
        "Aucune modification appliquée : le point de restauration n'a pas pu être créé. " +
        "Activez la Restauration système Windows (ou désactivez l'option dans Paramètres) puis réessayez.";
}

/// <summary>
/// The honest result of probing whether a tweak is live on the system. Deliberately three-valued: we refuse to
/// collapse "we checked and it isn't applied" into the same answer as "we can't tell" — a shell-only tweak we
/// can't read back must not masquerade as either applied or reverted. Drives the Tweaks page's load-time
/// detection (the "appliqués" badge reflects the real machine, not a flag that resets every launch).
/// </summary>
public enum TweakAppliedState
{
    /// <summary>Every readable operation is in its applied state — and there is at least one readable op.</summary>
    Applied,

    /// <summary>At least one readable operation is NOT in its applied state (so the tweak isn't fully applied).</summary>
    NotApplied,

    /// <summary>Nothing about this tweak can be read back from Windows (e.g. shell-only ops, a missing service).</summary>
    Indeterminate
}

public interface IRestorePointService
{
    /// <summary>Create a Windows system restore point with the given description.</summary>
    Task<bool> CreateAsync(string description);
    Task<bool> EnableSystemRestoreIfDisabledAsync();
}

public interface IRegistryService
{
    bool TryReadValue(string hive, string key, string name, out string? current);
    bool WriteValue(string hive, string key, string name, string value, Models.RegistryValueType type);
    bool DeleteValue(string hive, string key, string name);
}

public interface IServiceManagerService
{
    bool TryGetStartupType(string serviceName, out string? startupType);
    bool SetStartupType(string serviceName, string startupType);
    bool StopService(string serviceName);
}

public interface IHardwareService
{
    Task<HardwareInfo> DetectAsync();
}

public interface IMonitoringService
{
    void Start();
    void Stop();
    MonitoringSnapshot GetSnapshot();
    event System.EventHandler<MonitoringSnapshot>? SnapshotReady;
}

public sealed class MonitoringSnapshot
{
    public float CpuUsagePercent { get; init; }
    public float CpuTempC { get; init; }
    public float CpuClockMhz { get; init; }     // peak active-core clock; 0 = sensor not read
    public float GpuUsagePercent { get; init; }
    public float GpuTempC { get; init; }
    public float GpuClockMhz { get; init; }     // GPU core clock; 0 = sensor not read
    public float GpuVramUsedGb { get; init; }
    public float GpuVramTotalGb { get; init; }  // dedicated VRAM; 0 = sensor not read
    public float GpuVramUsagePercent { get; init; }
    public float RamUsagePercent { get; init; }
    public float RamUsedGb { get; init; }
    public float RamTotalGb { get; init; }
    public System.DateTime CapturedAtUtc { get; init; }
}

public interface IProfileService
{
    Task<IReadOnlyList<Profile>> LoadProfilesAsync();
    Task SaveAsync(Profile profile);
    Task DeleteAsync(string profileId);
    Task<IReadOnlyList<Profile>> GetBuiltInPresetsAsync();
}

/// <summary>
/// The persisted change journal — an honest audit trail of every apply/revert batch the app ran (what, when, the
/// real success/failure tally, and which writes verification couldn't confirm). Newest first. Bounded so it can
/// never grow without limit. <see cref="RecordAsync"/> must not throw into the apply path: journaling is a
/// side-record, never a reason an apply appears to fail.
/// </summary>
public interface IApplyJournal
{
    Task<IReadOnlyList<JournalEntry>> LoadAsync();
    Task RecordAsync(JournalEntry entry);
    Task ClearAsync();
}

/// <summary>
/// The persisted optimization-score timeline — a bounded, oldest-first series of <see cref="ScoreSnapshot"/> so the
/// dashboard can show progression (« +13 depuis la dernière mesure »), not just a context-free number.
/// <see cref="RecordAsync"/> appends the new score (skipping the write when it's unchanged, via
/// <see cref="ScoreHistory.Record"/>) and returns the updated series, so the caller gets the trend without a second
/// read. Like the journal, it must never throw into the detection path: the timeline is a side-record, never a
/// reason a score refresh appears to fail.
/// </summary>
public interface IScoreHistoryStore
{
    Task<IReadOnlyList<ScoreSnapshot>> LoadAsync();
    Task<IReadOnlyList<ScoreSnapshot>> RecordAsync(int score);
}

/// <summary>
/// Persists the pasted licence token verbatim under <c>%LOCALAPPDATA%\AurumTweaks\License\license.key</c>. Stores the
/// opaque token string only — never a decoded edition — so the running app always re-verifies through the embedded
/// public key on load and can never trust a hand-edited "edition=Premium" file. Like every side-store, all three
/// operations swallow and log I/O errors: a licence that can't be read or written must fail safe to Free, never crash
/// the app. There is nothing secret here — the token is worthless without the seller's private key.
/// </summary>
public interface ILicenseStore
{
    /// <summary>The stored token, or null when none is saved (the normal unlicensed state — not an error).</summary>
    Task<string?> LoadAsync();
    Task SaveAsync(string token);
    Task ClearAsync();
}

/// <summary>
/// Supplies the seller's ECDSA P-256 <b>public</b> key (base64 SubjectPublicKeyInfo) that the app verifies licences
/// against — the only half of the keypair that ever ships. A null/whitespace value means « not configured »: the app
/// honestly grants nothing (everyone Free) and activation reports it, rather than faking an unlock. Behind an interface
/// so tests inject an ephemeral key and exercise the real verify chain; production binds the embedded constant.
/// </summary>
public interface ILicenseKeyRing
{
    string? PublicKeyBase64 { get; }
}

/// <summary>
/// The running app's single source of truth for « what edition am I licensed for ». Defaults to
/// <see cref="AppEdition.Free"/> and only ever reads Premium from a token the embedded public key
/// <see cref="ILicenseKeyRing"/> genuinely verifies (real ECDSA, via <see cref="LicenseVerifier"/>) — the honesty
/// mandate's hardest edge: the app must never hand out paid surfaces it can't prove were paid for. A singleton so every
/// gated ViewModel reads one shared verdict; <see cref="EditionChanged"/> lets them re-gate live the moment a licence
/// is activated or removed, without a relaunch.
/// </summary>
public interface ILicenseService
{
    /// <summary>The verified edition right now — <see cref="AppEdition.Free"/> until a token proves otherwise.</summary>
    AppEdition CurrentEdition { get; }

    /// <summary>True once a public key is embedded. False = as-shipped placeholder: activation is honestly unavailable
    /// (no fake paywall), everyone is Free. Distinct from « a key is present but the token is bad ».</summary>
    bool IsConfigured { get; }

    /// <summary>The last verdict's stable reason code ("ok", "not-configured", "empty", "signature", "expired"…) — the
    /// UI maps it to French. Never leaks key material.</summary>
    string StatusReason { get; }

    /// <summary>The verified payload (licensed-to, expiry) when Premium, else null — for an honest « licence valide
    /// jusqu'au… » line, never a fabricated owner.</summary>
    LicensePayload? CurrentPayload { get; }

    /// <summary>Load and verify any stored token at startup, settling <see cref="CurrentEdition"/> before the UI shows.</summary>
    Task InitialiseAsync();

    /// <summary>Verify a pasted token; on success persist it and unlock, otherwise stay Free and persist nothing
    /// (a rejected token never overwrites a working licence). Returns the full verdict so the UI can explain a refusal.</summary>
    Task<LicenseValidation> ActivateAsync(string token);

    /// <summary>Remove the stored licence and revert to Free immediately.</summary>
    Task DeactivateAsync();

    /// <summary>Raised only when <see cref="CurrentEdition"/> actually transitions, so gated pages re-evaluate live.</summary>
    event System.EventHandler? EditionChanged;
}

public interface ILocalizationService
{
    string Get(string key);
    string GetLocalizedFrom(System.Collections.Generic.IDictionary<string, string> map);
    string CurrentLanguageCode { get; }
    void SetLanguage(string langCode);
    event System.EventHandler? LanguageChanged;
}

public interface INavigationService
{
    /// <summary>The currently active page key (Dashboard, Tweaks, Bios...).</summary>
    string CurrentPage { get; }
    void NavigateTo(string pageKey);
    event System.EventHandler<string>? Navigated;
}

/// <summary>
/// Session-scoped, newest-first record of the pages the user has visited — the data behind the command palette's
/// "recent first" ordering on an empty query. Deliberately not persisted: it reflects where you've actually been
/// since launch, never claims to remember across sessions. A singleton so every navigator (sidebar, palette,
/// deep-links) feeds one shared trail.
/// </summary>
public interface INavigationHistory
{
    /// <summary>Most-recently-visited first, de-duplicated, bounded to the most recent few. Read by the palette.</summary>
    System.Collections.Generic.IReadOnlyList<string> Recent { get; }

    /// <summary>Record a visit to <paramref name="pageKey"/>: promote it to the front (re-visiting moves it up
    /// rather than duplicating), dropping the oldest once the small cap is exceeded.</summary>
    void Record(string pageKey);
}

public interface IGameDetectionService
{
    Task<IReadOnlyList<DetectedGame>> ScanAsync();
}

public sealed class DetectedGame
{
    public string Name { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;          // Steam, Epic, Riot, Battle.net, Standalone
    public string ExecutablePath { get; set; } = string.Empty;
    public string InstallDirectory { get; set; } = string.Empty;
    public bool HasAntiCheat { get; set; }
    public string AntiCheatName { get; set; } = string.Empty;
}

public interface INetworkOptiService
{
    Task<NetworkRouteSnapshot> MeasureAsync(string host);

    /// <summary>
    /// Trace the route to <paramref name="host"/> with real increasing-TTL ICMP probes (the <c>tracert</c>
    /// technique). Honest by construction: a non-responding hop stays a "*" with no address and no latency —
    /// the returned <see cref="TracerouteReport"/> never contains an invented router.
    /// </summary>
    Task<TracerouteReport> TraceRouteAsync(string host);

    /// <summary>
    /// Benchmark a curated set of public DNS resolvers (Cloudflare, Google, Quad9, OpenDNS, AdGuard) by timing
    /// real DNS A-record queries against each, ranked fastest-first. Honest by construction: a resolver that
    /// doesn't answer is shown as "—" (never a fabricated latency), and the recommendation is just the lowest
    /// measured median — switching DNS stays a manual step the user performs.
    /// </summary>
    Task<DnsBenchmarkReport> BenchmarkDnsAsync();
}

public sealed record NetworkRouteSnapshot(float PingMs, float JitterMs, float PacketLossPct, int HopCount);

/// <summary>
/// Analyses the detected hardware against the full tweak catalogue and produces a
/// personalized optimization plan (ranked recommendations + hardware insights).
/// This is the "adapté à chaque PC" engine.
/// </summary>
public interface IAdaptiveRecommendationService
{
    /// <summary>Build the full personalized plan. <paramref name="strictCompetitive"/> hides anti-cheat-risky tweaks.</summary>
    Task<AdaptivePlan> BuildPlanAsync(bool strictCompetitive);

    /// <summary>True if a tweak is relevant to the given machine (hardware + OS gating).</summary>
    bool IsApplicable(Tweak tweak, HardwareInfo hw);
}

/// <summary>
/// The personalized BIOS advisor. Takes the detected hardware + the chosen tier and
/// produces a ranked report of BIOS settings: which apply to THIS CPU/board, what state
/// we could read back from Windows, and the exact vendor menu path to change each one.
/// </summary>
public interface IBiosAdvisorService
{
    /// <summary>Build the per-PC BIOS report (filtered by platform, state-detected, ranked).</summary>
    BiosAdvisorReport BuildReport(HardwareInfo hw, TweakTier tier);
}

/// <summary>
/// "Modifier le BIOS depuis Windows par n'importe quel moyen" — but honestly.
/// Universal &amp; safe: reboot straight into the UEFI setup. OEM machines (Dell/HP/Lenovo):
/// read/write select settings via vendor WMI. DIY boards: no safe live API (bricking risk),
/// so we guide the manual change instead.
/// </summary>
public interface IBiosApplyService
{
    /// <summary>Probe what's possible on this machine (UEFI? vendor WMI present?).</summary>
    Task<BiosApplyCapabilities> DetectCapabilitiesAsync(HardwareInfo hw);

    /// <summary>Reboot the PC directly into the UEFI/BIOS setup screen (with a short countdown).</summary>
    Task<TweakApplyResult> RebootToFirmwareAsync();

    /// <summary>Abort a pending reboot-to-firmware countdown.</summary>
    Task<TweakApplyResult> CancelRebootAsync();
}

/// <summary>
/// Snappy-Driver-Installer-style scan: enumerate installed drivers + problem devices,
/// flag the ones worth updating (problem devices, old non-Microsoft drivers), and point
/// the user at the safe update paths (Windows Update optional updates, vendor pages).
/// </summary>
public interface IDriverScanService
{
    Task<DriverScanReport> ScanAsync();
}

/// <summary>
/// HIDUSBF-style input report — honestly. Enumerates connected mice/keyboards/controllers
/// and their bus, detects running vendor configuration apps (G HUB, Synapse, iCUE…), reads
/// the Windows pointer-acceleration state we CAN act on, and gives plain-language guidance
/// on polling rate / input latency (true rate needs a kernel filter driver we don't ship).
/// </summary>
public interface IInputDeviceService
{
    Task<InputTuningReport> ScanAsync();
}

/// <summary>
/// The "gestionnaire de démarrage" — enumerates the programs Windows launches at logon and toggles them
/// reversibly. Honest by construction: it reads the four real sources (HKCU/HKLM <c>Run</c> + the per-user and
/// all-users Startup folders) and never invents an entry; disabling <b>moves</b> the entry to Aurum's backup
/// (registry value with its exact kind preserved, or the shortcut file) rather than deleting it, so re-enabling
/// is a genuine inverse.
/// </summary>
public interface IStartupManagerService
{
    /// <summary>List every startup program, enabled and Aurum-disabled alike, fastest-to-slowest-to-act-on order.</summary>
    Task<IReadOnlyList<StartupEntry>> ScanAsync();

    /// <summary>Move a startup entry between its live location and Aurum's reversible backup. Idempotent; returns false on failure (the caller re-scans the real state).</summary>
    Task<bool> SetEnabledAsync(StartupEntry entry, bool enable);
}

/// <summary>
/// The "plan d'alimentation" manager — reads the real Windows power schemes (via <c>powercfg</c>) and switches
/// the active one. Honest by construction: it reflects the live active scheme powercfg reports, every switch is a
/// genuine <c>powercfg /setactive</c> or processor PPM write the caller re-reads afterwards, and « Performances
/// ultimes » is surfaced by the sanctioned duplicate-scheme step (it is hidden on many editions) — never a fabricated
/// perf number and never MSR/driver/firmware writes.
/// </summary>
public interface IPowerPlanService
{
    /// <summary>List the installed power schemes, marking the active one (known schemes get a stable French label).</summary>
    Task<PowerPlanReport> GetReportAsync();

    /// <summary>Make the given scheme active. Returns false if powercfg refuses (the caller re-reads the real state).</summary>
    Task<bool> ActivateAsync(System.Guid scheme);

    /// <summary>Ensure « Performances ultimes » exists (duplicating the base scheme if this edition hides it) and activate it.</summary>
    Task<bool> EnableUltimateAsync();

    /// <summary>Read the active plan's processor knobs « sur secteur » (min/max state, core-parking floor). Read-only; « — » when unreadable.</summary>
    Task<ProcessorPowerDetail> GetProcessorDetailAsync();

    /// <summary>Set the active AC plan's processor PPM knobs via powercfg. Caller re-reads; invalid tuning returns false.</summary>
    Task<bool> SetProcessorTuningAsync(ProcessorPowerTuning tuning);
}

/// <summary>
/// The « tâches planifiées » manager — surfaces a curated allow-list of well-known telemetry/privacy scheduled
/// tasks with their live state, and toggles them reversibly. Honest by construction: it reads the real
/// <c>Get-ScheduledTask</c> state (a task that isn't installed is shown as absent, never invented), and every
/// toggle is a genuine <c>schtasks /Change … /Disable|/Enable</c> — exact inverses — that the caller re-reads.
/// </summary>
public interface IScheduledTaskService
{
    /// <summary>Read the live state of every curated task (present/enabled/disabled), actionable rows first.</summary>
    Task<ScheduledTaskReport> GetReportAsync();

    /// <summary>Enable or disable a task by its full Task Scheduler path. Returns false on failure (caller re-reads the real state).</summary>
    Task<bool> SetEnabledAsync(string fullPath, bool enable);
}

/// <summary>
/// The « applications préinstallées » manager — surfaces a curated allow-list of well-known consumer bloat (promo
/// games, Bing news/weather, Office/Skype promos, redundant media players…) with their live state, and uninstalls
/// them. Honest by construction: it reads the real <c>Get-AppxPackage</c> set (an app not installed is shown absent,
/// never invented), and every removal is a genuine <c>Remove-AppxPackage</c> the caller re-reads. Unlike the
/// reversible registry tweaks, removal is per-user and reversed only by a Microsoft Store reinstall — the page says so.
/// </summary>
public interface IAppxDebloatService
{
    /// <summary>Read the live state of every curated app (installed/absent, removable/protected), actionable bloat first.</summary>
    Task<AppxReport> GetReportAsync();

    /// <summary>Uninstall a package by its versioned full name. Returns false on failure (caller re-reads the real state).</summary>
    Task<bool> RemoveAsync(string packageFullName);
}

/// <summary>
/// The « nettoyage disque » manager — measures a curated set of known-safe temp/cache folders and clears them in
/// place. Honest by construction: every size is the real sum of file lengths read from disk (never an estimate), and
/// a clean deletes best-effort then re-measures so the reclaimed figure is the space that genuinely disappeared. It
/// only touches folders Windows recreates on demand; riskier reclaimable space (WinSxS, Windows.old, Recycle Bin,
/// restore points) is left to Windows' own cleanmgr — file deletion is irreversible, so the page automates only the
/// safe, self-healing locations and links out for the rest.
/// </summary>
public interface IDiskCleanupService
{
    /// <summary>Measure the real on-disk size of every curated cleanup location.</summary>
    Task<CleanupReport> ScanAsync();

    /// <summary>Clear one location best-effort and report the bytes that actually disappeared (before − after).</summary>
    Task<CleanupOutcome> CleanAsync(CleanupCategory category);

    /// <summary>Clear every curated location in one pass and report the aggregate bytes actually freed.</summary>
    Task<CleanupOutcome> CleanAllAsync();
}

/// <summary>
/// The « santé des disques » service — reports each physical drive's real state as Windows holds it (capacity, health)
/// joined to Windows' own interpretation of the drive's SMART data via Get-StorageReliabilityCounter (temperature,
/// wear, power-on hours, uncorrected errors). Read-only and honest by construction: it never decodes raw vendor SMART
/// bytes itself, never invents a metric the drive doesn't expose (absent counters stay « unknown »), and offers no
/// fake « repair » action — riskier or hands-on maintenance is handed to Windows' own drive-optimisation / storage
/// tools, which the page links to.
/// </summary>
public interface IDriveHealthService
{
    /// <summary>Read every physical disk's health and reliability counters into a verdict-ranked report.</summary>
    Task<DriveHealthReport> GetReportAsync();
}

/// <summary>
/// The « priorité &amp; affinité CPU » manager — a Process-Lasso-class surface. Honest by construction: it reads each
/// running process's real priority class and CPU-affinity mask from Windows, applies a change with the managed Process
/// API, and returns whether Windows accepted it so the caller re-reads the true state (a refusal can never read as a
/// fake « done »). It promises scheduler consistency, never FPS. Persistent rules are opt-in, stored locally, and
/// applied through a visible scheduled task — never a ring-0 driver or hidden service — and it warns before touching an
/// anti-cheat-protected game.
/// </summary>
public interface IProcessControlService
{
    /// <summary>Enumerate the running processes worth tuning (detected games first, then windowed apps) with live state.</summary>
    Task<ProcessControlReport> GetReportAsync();

    /// <summary>Set a process's priority class (Realtime/Idle are refused). Returns false on refusal; caller re-reads.</summary>
    Task<bool> SetPriorityAsync(int pid, ProcessPriorityLevel level);

    /// <summary>Set a process's CPU affinity to a preset (all cores, or performance cores on a hybrid CPU). Caller re-reads.</summary>
    Task<bool> SetAffinityAsync(int pid, AffinityStrategy strategy);

    /// <summary>Read the saved opt-in persistence rules and whether Aurum's visible scheduled task exists.</summary>
    Task<ProcessPersistenceReport> GetPersistenceReportAsync();

    /// <summary>Add/update a persistence rule for the selected process. Optional power plan uses High Performance while running.</summary>
    Task<bool> AddPersistentRuleAsync(RunningProcessInfo process, bool includeHighPerformancePowerPlan);

    /// <summary>Remove a saved persistence rule by process name.</summary>
    Task<bool> RemovePersistentRuleAsync(string processName);

    /// <summary>Create or delete the visible scheduled task that applies saved rules.</summary>
    Task<bool> SetPersistenceTaskEnabledAsync(bool enabled);
}

/// <summary>
/// The « latence système (DPC/ISR) » diagnostic — a LatencyMon-adjacent surface. Honest by construction: it reads the
/// kernel's OWN per-processor time counters (NtQuerySystemInformation, the same source Process Explorer uses) and
/// reports how much CPU time goes to Deferred Procedure Calls and hardware interrupts — the classic cause of
/// micro-stutter and audio dropouts. The load-bearing limit, stated plainly in the UI: it measures HOW MUCH DPC/ISR
/// load there is, NOT WHICH driver is responsible (that needs a full ETW kernel trace — LatencyMon, DPC Latency
/// Checker). It's read-only — a measurement, never a « fix » button — and it promises no FPS.
/// </summary>
public interface ILatencyDiagnosticsService
{
    /// <summary>
    /// Sample the per-core kernel counters twice <paramref name="sampleMs"/> apart and derive each logical core's
    /// DPC/ISR load. Returns an honest <see cref="LatencyReport"/> with <c>QueryOk=false</c> (never a fabricated
    /// zero) if the kernel query can't be read. <paramref name="sampleMs"/> is clamped to 250–30000 ms.
    /// </summary>
    Task<LatencyReport> MeasureAsync(int sampleMs = 2000);
}

/// <summary>
/// Reads Windows' current system timer resolution — the scheduling-tick granularity the FR overclocking scene chases
/// for smoother frame pacing and audio. Honest by construction: it reports the live value from <c>NtQueryTimerResolution</c>
/// (a user-mode ntdll query — no driver, no NVRAM), READ-ONLY (Aurum never sets the resolution), and an unreadable query
/// yields <c>QueryOk=false</c> rather than a fabricated « 0 ms ». The page discloses the Windows 10 2004+ per-process
/// caveat so the global figure is never overclaimed.
/// </summary>
public interface ITimerResolutionService
{
    /// <summary>Read the current/min/max timer resolution. Returns <c>QueryOk=false</c> if ntdll refuses — never a fake reading.</summary>
    TimerResolutionReading Read();
}

/// <summary>
/// « Redémarrage en attente » — reads the standard Windows signals that a restart is queued (Component Based Servicing,
/// Windows Update, PendingFileRenameOperations, a pending computer rename) and returns an honest verdict. Genuinely
/// useful for a tweak applier: many tweaks only take full effect after a reboot, and Windows itself queues restarts
/// silently. Read-only and honest by construction — a "not pending" result reflects the well-known signals we can read,
/// never a guarantee that nothing else wants a reboot, and an unreadable key counts as « signal absent », never a
/// fabricated pending state.
/// </summary>
public interface IPendingRebootService
{
    /// <summary>Probe the standard Windows pending-reboot signals (off the UI thread) into an honest verdict.</summary>
    Task<PendingRebootStatus> GetStatusAsync();
}

/// <summary>
/// The pre-flight safety check: shows the safety-net posture BEFORE the user clicks « Appliquer », so the app is
/// "testable without fear". Honest by construction — it reuses the REAL System Restore readability probe
/// (<see cref="IRestoreManagerService"/>) and the standard registry reboot signals (<see cref="IPendingRebootService"/>),
/// forecasts (never fakes) the apply-time restore-point abort, and deliberately omits an elevation check (manifest-forced
/// ⇒ always true ⇒ a fake green) and any invented disk-space threshold.
/// </summary>
public interface IPreflightService
{
    /// <summary>Gather the live signals (off the UI thread) into an honest <see cref="PreflightVerdict"/> for the banner.</summary>
    Task<PreflightVerdict> EvaluateAsync();

    /// <summary>
    /// Actually enable Windows System Restore (the user's one-click fix for the « Restauration système indisponible »
    /// caution), then RE-PROBE and return the fresh verdict. We never trust the enable command's optimistic return —
    /// the re-probe is the honest confirmation: the caution clears ONLY if Windows now reads System Restore back.
    /// </summary>
    Task<PreflightVerdict> EnableRestoreAndReprobeAsync();
}

/// <summary>
/// The « Services Windows » manager — a curated, reversible front-end built ON TOP of the low-level
/// <see cref="IServiceManagerService"/> it reuses for every read/write. Honest by construction: it reads each
/// curated service's REAL startup type (registry Start DWORD, locale-invariant) and running state (a single
/// ServiceController pass — no localized text parsed), every change is a genuine SetStartupType the caller
/// re-reads (a refusal surfaces the unchanged truth, never a fake « done »), the set is a short hand-picked
/// allow-list (never a dump-everything footgun) with gaming/perf services flagged « à conserver », and it
/// promises confidentialité/légèreté, never FPS. It prefers « Manuel (déclenché) » over « Désactivé » wherever a
/// feature is only occasionally useful, and disabling also stops the running service so the change is true now.
/// </summary>
public interface IServiceControlService
{
    /// <summary>Read every curated service's live startup type + running state. <c>QueryOk=false</c> (never a fake zero) if the system couldn't be read.</summary>
    Task<ServiceControlReport> GetReportAsync();

    /// <summary>Set a service's startup type to a canonical value (a non-canonical target is refused). Disabling also stops it. Caller re-reads.</summary>
    Task<bool> SetStartupAsync(string serviceName, string canonicalStartupType);
}

/// <summary>
/// « Effets visuels » — the interactive equivalent of Windows' Performance Options dialog. Reads each curated UI
/// effect's live HKCU value (absent = Windows default = appearance, never a fabricated state) and applies real,
/// reversible registry writes the caller re-reads. Presets honour the per-effect « à conserver » flag (ClearType
/// stays on under Performances). Per-user UI snappiness/latency, not in-game FPS.
/// </summary>
public interface IVisualEffectsService
{
    /// <summary>Read the dialog mode + every curated effect's live state. <c>ModeKnown=false</c> (never a fake mode) if VisualFXSetting is unreadable.</summary>
    Task<VisualEffectsReport> GetReportAsync();

    /// <summary>Set one effect to its appearance (on) or performance (off) value, and flip the dialog radio to « Personnalisé ». Caller re-reads.</summary>
    Task<bool> SetEffectAsync(string effectId, bool appearance);

    /// <summary>Apply the Performances (true) or Apparence (false) preset across all effects, then record the matching dialog mode. Caller re-reads.</summary>
    Task<bool> ApplyPresetAsync(bool performance);
}

/// <summary>
/// « Mémoire vive » — a live RAM-composition view plus the honest equivalent of an ISLC/RAMMap cache flush. Reads the
/// kernel page lists (exact, language-immune) and fires a real NtSetSystemInformation flush whose effect is
/// re-measured, so the figure shown is the standby that genuinely disappeared. The page is explicit that emptying the
/// standby cache does not raise "available" memory and is not an FPS boost — Windows keeps that cache on purpose.
/// </summary>
public interface IMemoryManagementService
{
    /// <summary>Read total/available + the standby/free/modified split. <c>DetailAvailable=false</c> (never a fake 0) if the page-list query can't be read.</summary>
    Task<MemoryComposition> GetCompositionAsync();

    /// <summary>Fire a real memory-list flush, re-measuring composition before/after so the reported delta is genuine. <c>Invoked=false</c> if the kernel call failed.</summary>
    Task<MemoryFlushOutcome> FlushAsync(MemoryFlushKind kind);
}

/// <summary>
/// « Confidentialité » — a curated front-end over Windows' consent/telemetry registry switches. Reads each setting's
/// live value (absent = the setting's default/not-configured state, never a fabricated « protégé ») and applies real,
/// reversible writes/deletes the caller re-reads. Reduces collection; it does not make Windows private, and the
/// consumer-SKU telemetry floor / AI-policy caveats are disclosed per-setting. Optional telemetry blocking uses named,
/// removable Windows Firewall rules only; no hosts/DNS hijack.
/// </summary>
public interface IPrivacyService
{
    /// <summary>Read every curated privacy setting's live state. An unreadable/absent key reads as the Windows default, never a fake « protégé ».</summary>
    Task<PrivacyReport> GetReportAsync();

    /// <summary>Read Aurum's exact named telemetry firewall rules. Missing rules are reported as missing, never invented.</summary>
    Task<PrivacyFirewallReport> GetTelemetryFirewallReportAsync();

    /// <summary>Set one setting to its hardened (privacy) or default (Windows) value. Unknown id → false. Caller re-reads.</summary>
    Task<bool> SetHardenedAsync(string settingId, bool harden);

    /// <summary>Apply the « tout renforcer » (true) or « tout rétablir » (false) plan across every setting. Caller re-reads.</summary>
    Task<bool> ApplyAllAsync(bool harden);

    /// <summary>Create or remove the exact Aurum-named Windows Firewall telemetry rules. Caller re-reads.</summary>
    Task<bool> SetTelemetryFirewallBlockedAsync(bool block);
}

/// <summary>
/// « Optimisations jeu » — a curated front-end over documented gaming/responsiveness registry tweaks (multimedia
/// network throttle, background CPU reservation, Game DVR background recording). Reads each tweak's live value (absent
/// = Windows default, never a fabricated « optimisé ») and applies real, reversible writes the caller re-reads. The
/// gain is variable and configuration-dependent — no FPS figure is promised, and a reboot/relog may be needed for some
/// to take full effect. The feature-eligibility matrix is read-only: it reports HAGS/Game Mode/Auto HDR/VRR/
/// DirectStorage/APO/Reflex/Smooth Motion/AFMF from local facts, and never claims Aurum enables driver-owned features.
/// </summary>
public interface IGameOptiService
{
    /// <summary>Read every curated tweak's live state plus the read-only feature eligibility matrix. An unreadable/absent key reads honestly, never as a fake « optimisé » or fake support.</summary>
    Task<GameTweakReport> GetReportAsync();

    /// <summary>Set one tweak to its optimised (performance) or default (Windows) value. Unknown id → false. Caller re-reads.</summary>
    Task<bool> SetOptimizedAsync(string tweakId, bool optimize);

    /// <summary>Apply the « tout optimiser » (true) or « tout rétablir » (false) plan across every tweak. Caller re-reads.</summary>
    Task<bool> ApplyAllAsync(bool optimize);
}

/// <summary>
/// « Points de restauration » — a user-facing front-end for Windows System Restore, the safety net that pairs with the
/// app's existing "create a restore point before applying tweaks" promise. Honest by construction: it lists the REAL
/// points Windows reports (<c>Get-ComputerRestorePoint</c>, never invented), creates a checkpoint whose success is
/// MEASURED by the before/after point count (so Windows silently skipping under its 24h throttle reports « non créé »,
/// never a fake « créé »), and exposes the throttle lever (<c>SystemRestorePointCreationFrequency</c>) as a real
/// reversible registry write. The actual roll-back is handed off to Windows' own <c>rstrui.exe</c> wizard — never faked.
/// </summary>
public interface IRestoreManagerService
{
    /// <summary>Read the live point list + the throttle setting. <c>QueryOk=false</c> (never a fake empty list) if System Restore couldn't be read.</summary>
    Task<RestoreOverview> GetOverviewAsync();

    /// <summary>Create a checkpoint and verify it by the before/after point count, so the throttle case is reported honestly.</summary>
    Task<CheckpointOutcome> CreateCheckpointAsync(string description);

    /// <summary>Lift (true) or restore (false) Windows' 24h restore-point throttle. Returns false on failure; caller re-reads.</summary>
    Task<bool> SetUnthrottledAsync(bool unthrottle);

    /// <summary>Actually enable System Restore protection (elevated <c>Enable-ComputerRestore</c>) — the « activer la
    /// protection » one-click fix when the read came back « impossible ». Returns nothing trustworthy on purpose: the
    /// command's optimistic result means only « it ran », so the caller RE-READS the overview and <c>QueryOk</c> is the
    /// honest confirmation that protection now reads back (false again ⇒ policy-blocked, shown honestly, never faked).</summary>
    Task EnableProtectionAsync();
}

/// <summary>
/// « Veille &amp; hibernation » — manages Windows hibernation and the Fast Startup hybrid-shutdown. Honest by
/// construction: both states are read code-page-immune from the registry (never the localized <c>powercfg /a</c> text)
/// and re-read after every change; hibernation on/off goes through the OS's own <c>powercfg /hibernate</c> command so
/// <c>hiberfil.sys</c> is genuinely created/removed; the freed disk space shown is the real measured file size; Fast
/// Startup is honestly reported as unavailable when hibernation is off. No FPS gain is promised — the payoff is disk
/// space and a true cold shutdown (dual-boot, clean driver reload).
/// </summary>
public interface ISleepHibernationService
{
    /// <summary>Read both live states + the current hiberfil.sys size (« — » when unreadable, never a fake 0).</summary>
    Task<SleepHibernationReport> GetReportAsync();

    /// <summary>Enable/disable hibernation via real <c>powercfg /hibernate on|off</c>. Returns false on failure; caller re-reads.</summary>
    Task<bool> SetHibernationAsync(bool enable);

    /// <summary>Enable/disable Fast Startup via the <c>HiberbootEnabled</c> registry value. Returns false on failure; caller re-reads.</summary>
    Task<bool> SetFastStartupAsync(bool enable);
}

public interface IMemoryModulesService
{
    /// <summary>Read the installed physical RAM modules (live, via Win32_PhysicalMemory) into a display-ready report.</summary>
    Task<MemoryModulesReport> GetReportAsync();
}

public interface INetworkAdaptersService
{
    /// <summary>Enumerate the live network adapters (System.Net.NetworkInformation) into a display-ready report.</summary>
    Task<NetworkAdaptersReport> GetReportAsync();

    /// <summary>Flush the DNS resolver cache (ipconfig /flushdns). Success is read from the exit code, not localized text.</summary>
    Task<NetworkActionOutcome> FlushDnsAsync();

    /// <summary>Renew the DHCP lease (ipconfig /renew); may briefly interrupt connectivity. Exit-code verdict.</summary>
    Task<NetworkActionOutcome> RenewDhcpAsync();
}

public interface IPagefileService
{
    /// <summary>Read the live pagefile configuration (WMI) into a display-ready report; QueryOk false when unreadable.</summary>
    Task<PagefileReport> GetReportAsync();

    /// <summary>Re-enable Windows' automatic pagefile management (the safe default). The result is VERIFIED by a WMI
    /// re-read — a refused write reports failure, never a fake success; the effective resize applies at the next reboot.</summary>
    Task<PagefileActionOutcome> RestoreAutomaticAsync();
}

public interface IAudioService
{
    /// <summary>Read the Communications ducking preference (+ active sound scheme + audio devices) into a report.</summary>
    Task<AudioReport> GetReportAsync();

    /// <summary>Set the Communications ducking preference (REG_DWORD). The result is VERIFIED by a re-read — a refused
    /// write reports failure, never a fake success. Reversible: pass ReduceOther80 to restore the Windows default.</summary>
    Task<AudioActionOutcome> SetDuckingAsync(AudioDucking desired);
}

public interface IWindowsUpdateService
{
    /// <summary>Read every curated Windows Update toggle's live state. An unreadable/absent key reads as the Windows default, never a fake « appliqué ».</summary>
    Task<WindowsUpdateReport> GetReportAsync();

    /// <summary>Set one toggle to its optimised (gaming-friendly) or default (Windows) value. Unknown id → false. Caller re-reads.</summary>
    Task<bool> SetOptimizedAsync(string tweakId, bool optimize);

    /// <summary>Apply (or restore) every toggle, returning how many of the HKLM policy writes the system actually accepted —
    /// a refused write is reported honestly, never counted as success. Caller re-reads to reflect the real registry.</summary>
    Task<WindowsUpdateApplyOutcome> ApplyAllAsync(bool optimize);
}

public interface IDisplayService
{
    /// <summary>Read every attached monitor's TRUE current mode and advertised mode list (Win32 EnumDisplaySettings) into a report.
    /// An unreadable current mode or an un-enumerable mode list is reported honestly, never as a fake « à la fréquence max ».</summary>
    Task<DisplayReport> GetReportAsync();

    /// <summary>Switch one monitor's refresh rate at the given (current) resolution. The mode is validated with CDS_TEST first, then
    /// the outcome is VERIFIED by re-reading the live mode — a refused or ignored change reports the measured rate, never a fake success.</summary>
    Task<DisplayApplyOutcome> SetRefreshRateAsync(string deviceName, int width, int height, int hz);
}

/// <summary>
/// « Instantanés » — captures a point-in-time picture of the WHOLE tweak catalogue's live applied/not/indeterminate
/// state and persists it, so a later capture can be diffed against it to reveal drift: a tweak that WAS applied and
/// silently isn't any more because a Windows update, another tool, or a reboot reverted it. Honest by construction:
/// every entry is a real per-tweak probe via <see cref="ITweakService.DetectStatesAsync"/> (Indeterminate stays
/// Indeterminate, never a guessed ✓), and the diff (<see cref="SnapshotDiff"/>) only claims a regression when BOTH
/// ends were readable.
/// </summary>
public interface ISnapshotService
{
    /// <summary>Probe the live machine, persist the capture under %LOCALAPPDATA%, and return it.</summary>
    Task<SystemSnapshot> CaptureAsync(string? label);

    /// <summary>Probe the live machine into a transient snapshot WITHOUT persisting it — the right-hand "now" of a
    /// comparison. Kept off the saved list so comparing against the live system doesn't litter it with throwaways.</summary>
    Task<SystemSnapshot> CaptureLiveAsync(string? label);

    /// <summary>Every saved snapshot, newest first.</summary>
    Task<IReadOnlyList<SystemSnapshot>> LoadAllAsync();

    /// <summary>Delete one saved snapshot by id (a no-op if the file is already gone).</summary>
    Task DeleteAsync(string id);

    /// <summary>Write one snapshot to a portable file so a baseline survives a reinstall or moves between machines.</summary>
    Task ExportAsync(SystemSnapshot snapshot, string destinationPath);

    /// <summary>Read a portable snapshot file into the store under a FRESH id (never overwrites a stored one). Throws
    /// <see cref="SnapshotImportException"/> with a French message when the file is unreadable or holds no usable snapshot.</summary>
    Task<SystemSnapshot> ImportAsync(string sourcePath);
}

/// <summary>
/// The shared rendez-vous for the three before/after surfaces (settings diff, frame-time A/B, optimization score),
/// which otherwise live as mutable view-state in three separate singleton page VMs and never co-locate. Each owning
/// page PUBLISHES its latest comparison as it's produced; the Dashboard export READS them back as one
/// <see cref="EvidenceInputs"/> to render the unified « preuve avant / après » (<see cref="EvidenceReport"/>).
/// Decouples the surfaces — no VM reaches into another's state — and stays honest: a published null clears a slot, so a
/// stale before/after can't linger and be pasted as current. Implemented by the in-memory singleton <see cref="EvidenceLedger"/>.
/// </summary>
public interface IEvidenceLedger
{
    /// <summary>The Snapshot page's latest settings diff (with its baseline/target labels); null when its comparison is closed.</summary>
    void PublishSettings(SnapshotComparison? comparison, string? baselineLabel, string? targetLabel);

    /// <summary>The Benchmark page's latest A/B frame-time comparison; null when none is pinned.</summary>
    void PublishPerformance(BenchmarkComparison? comparison);

    /// <summary>The Dashboard's latest optimization score and its trend; a <see cref="ScoreGrade.NoData"/> card reads as « non disponible ».</summary>
    void PublishScore(OptimizationScorecard? scorecard, ScoreProgress? trend);

    /// <summary>An immutable snapshot of whatever has been published so far — the single value the unified report renders from.</summary>
    EvidenceInputs Current();
}

/// <summary>
/// Durable backing for the EvidenceLedger's frame-time A/B slot — the ONE before/after surface that stays honest
/// across a restart, because it is a comparison of two IMMUTABLE, dated captures: a reboot can't change what two
/// past runs measured, so reloading it and pasting it is pasting a true historical measurement. The settings diff
/// (whose « maintenant » side is a live probe of the machine) and the live optimization score are deliberately NOT
/// persisted here — reloading either could present a pre-reboot reading as « current », the exact staleness the
/// ledger exists to prevent. Both self-heal anyway: the Dashboard re-detects the score on load (and the score
/// timeline already persists), and a settings diff is one click to recompute against the live machine.
/// </summary>
public interface IEvidenceStore
{
    /// <summary>The persisted A/B, or null when none was saved or the file is unreadable/hollow. Never throws.</summary>
    BenchmarkComparison? LoadPerformance();

    /// <summary>Write-through the latest A/B; a null (a cleared/closed comparison) deletes the file so it can't
    /// resurrect on the next launch — the restart-time equivalent of the ledger's « a null clears a slot ». Never throws.</summary>
    void SavePerformance(BenchmarkComparison? comparison);
}

public interface IDnsService
{
    /// <summary>Read every IP-enabled adapter's effective DNS servers + authoritative static config into a report.
    /// Static-vs-automatic is read from the real NameServer entry, never guessed — an unreadable adapter is « Indéterminé », not faked.</summary>
    Task<DnsReport> GetReportAsync();

    /// <summary>Pin a curated resolver onto one adapter (WMI SetDNSServerSearchOrder). The outcome is VERIFIED by re-reading the static
    /// config — a refused or ignored change reports the measured servers, never a fake success.</summary>
    Task<DnsApplyOutcome> ApplyPresetAsync(string settingId, string adapterName, DnsPreset preset);

    /// <summary>Revert one adapter to DHCP-provided DNS (SetDNSServerSearchOrder with no servers). Verified by re-reading that the
    /// static NameServer entry is now empty — a revert that didn't take is reported honestly, never as a fake success.</summary>
    Task<DnsApplyOutcome> RevertToAutomaticAsync(string settingId, string adapterName);
}
