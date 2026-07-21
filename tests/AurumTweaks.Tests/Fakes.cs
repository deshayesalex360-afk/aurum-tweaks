using System.Collections.Generic;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;

namespace AurumTweaks.Tests;

/// <summary>
/// Shared ordered event log so tests can assert *sequencing* across the fakes
/// (e.g. that a restore point is created BEFORE any registry write in a batch).
/// </summary>
public sealed class EventLog
{
    public List<string> Events { get; } = new();
    public void Add(string e) => Events.Add(e);
}

/// <summary>In-memory registry. No real registry is touched.</summary>
public sealed class FakeRegistryService : IRegistryService
{
    private readonly EventLog _log;
    private readonly Dictionary<string, string> _store = new();

    public FakeRegistryService(EventLog log) => _log = log;

    // Seed a value as if it already existed on the machine.
    public void Seed(string hive, string key, string name, string value)
        => _store[Path(hive, key, name)] = value;

    public IReadOnlyDictionary<string, string> Store => _store;

    // Value names whose WriteValue should fail (return false) — lets a test simulate a genuine backend
    // failure (locked/denied key) and assert TweakService's partial-failure honesty.
    public HashSet<string> FailWritesForName { get; } = new();

    public bool TryReadValue(string hive, string key, string name, out string? current)
        => _store.TryGetValue(Path(hive, key, name), out current);

    public bool WriteValue(string hive, string key, string name, string value, RegistryValueType type)
    {
        if (FailWritesForName.Contains(name))
        {
            _log.Add($"reg.write.fail:{Path(hive, key, name)}");
            return false;
        }
        _store[Path(hive, key, name)] = value;
        _log.Add($"reg.write:{Path(hive, key, name)}={value}");
        return true;
    }

    public bool DeleteValue(string hive, string key, string name)
    {
        _store.Remove(Path(hive, key, name));
        _log.Add($"reg.delete:{Path(hive, key, name)}");
        return true;
    }

    private static string Path(string hive, string key, string name) => $"{hive}\\{key}\\{name}";
}

/// <summary>In-memory service-control manager.</summary>
public sealed class FakeServiceManager : IServiceManagerService
{
    private readonly EventLog _log;
    private readonly Dictionary<string, string> _startup = new();

    public FakeServiceManager(EventLog log) => _log = log;

    public void Seed(string serviceName, string startupType) => _startup[serviceName] = startupType;

    public IReadOnlyDictionary<string, string> Startup => _startup;

    public bool TryGetStartupType(string serviceName, out string? startupType)
        => _startup.TryGetValue(serviceName, out startupType);

    public bool SetStartupType(string serviceName, string startupType)
    {
        _startup[serviceName] = startupType;
        _log.Add($"svc.startup:{serviceName}={startupType}");
        return true;
    }

    public bool StopService(string serviceName)
    {
        _log.Add($"svc.stop:{serviceName}");
        return true;
    }
}

/// <summary>Records restore-point requests so tests can assert the safety net fired.</summary>
public sealed class RecordingRestorePointService : IRestorePointService
{
    private readonly EventLog _log;
    public RecordingRestorePointService(EventLog log) => _log = log;

    public List<string> Created { get; } = new();

    // When true, CreateAsync reports genuine failure (false) — System Restore off/broken — so a test can prove the
    // engine ABORTS the batch instead of applying un-backed changes. The attempt is logged as "restore.create.fail"
    // (distinct from the success "restore.create"), and nothing lands in Created, since nothing was created.
    public bool ShouldFail { get; set; }

    public Task<bool> CreateAsync(string description)
    {
        if (ShouldFail)
        {
            _log.Add($"restore.create.fail:{description}");
            return Task.FromResult(false);
        }
        Created.Add(description);
        _log.Add($"restore.create:{description}");
        return Task.FromResult(true);
    }

    public int EnableCalls { get; private set; }

    // Optional hook so a test can simulate the live-state change a successful enable produces (e.g. flip the restore
    // manager's readability to true). The re-probe then reads THAT, proving the pre-flight verdict tracks reality, not
    // the enable command's optimistic return. Left null ⇒ « it didn't take » (policy-blocked), so the caution persists.
    public System.Action? OnEnable { get; set; }

    public Task<bool> EnableSystemRestoreIfDisabledAsync()
    {
        EnableCalls++;
        _log.Add("restore.enable");
        OnEnable?.Invoke();
        return Task.FromResult(true);
    }
}

/// <summary>In-memory settings store. Defaults mirror <see cref="AppSettings"/> (restore point ON).</summary>
public sealed class FakeAppSettingsStore : IAppSettingsStore
{
    public AppSettings Current { get; } = new();
    public Task LoadAsync() => Task.CompletedTask;
    public Task SaveAsync() => Task.CompletedTask;
}

/// <summary>
/// Returns a pre-baked <see cref="HardwareInfo"/> so the adaptive engine can be tested
/// deterministically — no real WMI / sensor / registry probing.
/// </summary>
public sealed class FakeHardwareService : IHardwareService
{
    private readonly HardwareInfo _hw;
    public FakeHardwareService(HardwareInfo hw) => _hw = hw;
    public Task<HardwareInfo> DetectAsync() => Task.FromResult(_hw);
}

/// <summary>
/// In-memory GPU OC backend. Records what was applied/reset and never touches NVAPI — a real native
/// call with an in-range profile would actually overclock the test machine's GPU. Lets the
/// OverclockingViewModel be driven deterministically (default: backend unavailable, no current offsets).
/// </summary>
public sealed class FakeGpuOcService : IGpuOcService
{
    private readonly GpuOcBackendStatus _status;
    private readonly GpuOcProfile? _current;
    private readonly bool _succeed;   // false → apply/reset return a failure, so the VM's "Erreur :" path is testable

    public List<GpuOcProfile> Applied { get; } = new();
    public int ResetCount { get; private set; }

    public FakeGpuOcService(GpuOcBackendStatus? status = null, GpuOcProfile? current = null, bool succeed = true)
    {
        _status = status ?? new GpuOcBackendStatus(GpuVendor.Nvidia, "Test GPU", BackendAvailable: false);
        _current = current;
        _succeed = succeed;
    }

    public Task<GpuOcBackendStatus> GetStatusAsync() => Task.FromResult(_status);

    public Task<GpuOcApplyResult> ApplyAsync(GpuOcProfile profile)
    {
        Applied.Add(profile);
        return Task.FromResult(_succeed
            ? new GpuOcApplyResult(true, Applied: $"core +{profile.CoreOffsetMhz} MHz")
            : new GpuOcApplyResult(false, Error: "échec natif simulé"));
    }

    public Task<GpuOcProfile?> ReadCurrentAsync() => Task.FromResult(_current);

    public Task<GpuOcApplyResult> ResetAsync()
    {
        ResetCount++;
        return Task.FromResult(_succeed
            ? new GpuOcApplyResult(true, Applied: "offsets remis à 0")
            : new GpuOcApplyResult(false, Error: "échec natif simulé"));
    }
}

/// <summary>
/// No-op live monitor. Records that <see cref="Start"/> fired and lets a test push a synthetic
/// snapshot through <see cref="SnapshotReady"/> — never reads a real sensor.
/// </summary>
public sealed class FakeMonitoringService : IMonitoringService
{
    private MonitoringSnapshot _last = new();
    public bool Started { get; private set; }

    public void Start() => Started = true;
    public void Stop() { }
    public MonitoringSnapshot GetSnapshot() => _last;

    /// <summary>Feed a snapshot to subscribers (also keeps <see cref="SnapshotReady"/> genuinely used).</summary>
    public void Push(MonitoringSnapshot snapshot)
    {
        _last = snapshot;
        SnapshotReady?.Invoke(this, snapshot);
    }

    public event System.EventHandler<MonitoringSnapshot>? SnapshotReady;
}

/// <summary>
/// Serves a fixed, pre-built <see cref="AdaptivePlan"/> so the DashboardViewModel can be driven
/// deterministically — no hardware probing, no scoring. Defaults to a one-tweak, unapplied
/// recommended set so "apply recommended" has exactly one thing to do.
/// </summary>
public sealed class FakeAdaptiveRecommendationService : IAdaptiveRecommendationService
{
    private readonly AdaptivePlan _plan;
    public bool? LastStrictCompetitive { get; private set; }

    public FakeAdaptiveRecommendationService(AdaptivePlan? plan = null) => _plan = plan ?? DefaultPlan();

    public Task<AdaptivePlan> BuildPlanAsync(bool strictCompetitive)
    {
        LastStrictCompetitive = strictCompetitive;
        return Task.FromResult(_plan);
    }

    public bool IsApplicable(Tweak tweak, HardwareInfo hw) => true;

    private static AdaptivePlan DefaultPlan() => new()
    {
        Recommendations = new List<TweakRecommendation>
        {
            new()
            {
                Tweak = new Tweak
                {
                    Id = "fake-recommended-1",
                    Name = new Dictionary<string, string> { ["fr"] = "Tweak recommandé", ["en"] = "Recommended tweak" },
                    IsApplied = false
                },
                InDefaultSet = true
            }
        },
        ProfileSummaryFr = "PC de test",
        RecommendedCount = 1,
        TotalApplicable = 1,
        PotentialScore = 42
    };
}

/// <summary>
/// Records every tweak handed to it and flips its <c>IsApplied</c> flag, returning the real count —
/// so tests can prove the dashboard's "N appliquée(s)" message reflects the backend, not a fabricated
/// number, and that an OFF restore-point toggle still routes through a genuine apply.
/// </summary>
public sealed class RecordingTweakService : ITweakService
{
    public List<Tweak> Applied { get; } = new();
    public List<Tweak> Reverted { get; } = new();   // mirror of Applied for the revert path (align-to-snapshot)

    // Tweak Ids whose apply/revert should report failure (and NOT flip IsApplied) — lets a test drive the
    // batch partial-failure path without a real backend. Empty by default → every operation succeeds.
    public HashSet<string> FailIds { get; } = new();

    // When true, ApplyManyAsync mirrors the real engine's safety-abort: a required restore point that couldn't be
    // created → apply NOTHING and return (0, 0, RestorePointFailed: true). Lets a VM test prove each apply surface
    // surfaces the honest reason and skips its journal/verification. Off by default. (Revert never makes a restore
    // point, so RevertAllAsync ignores this — exactly like production.)
    public bool RestorePointWillFail { get; set; }

    // Tweak Ids the backend should report as already live on the system — independent of the in-memory
    // IsApplied flag, so a test can drive the Tweaks page's load-time detection. Empty by default.
    public HashSet<string> DetectAppliedIds { get; } = new();

    // Tweak Ids the backend should report as NOT confirmed even though apply flipped IsApplied — simulates a
    // write the engine ran but that doesn't read back (a "didn't stick"). Lets a test drive post-apply
    // verification's alarming path. Wins over IsApplied/DetectAppliedIds. Empty by default.
    public HashSet<string> NotConfirmedIds { get; } = new();

    // Tweak Ids the backend can't read back at all (shell-only ops) → Indeterminate, the honest "unverifiable".
    // Wins over everything so verification can distinguish "unverifiable" from "didn't stick". Empty by default.
    public HashSet<string> IndeterminateIds { get; } = new();

    // One probe path shared by every detection entry point, so the boolean flag, the tri-state, and the batch
    // detector can never disagree. Precedence mirrors the real engine's honesty rule: unreadable dominates a
    // forced "didn't stick", which in turn overrides the in-memory/applied signal.
    private TweakAppliedState ProbeState(Tweak tweak)
    {
        if (IndeterminateIds.Contains(tweak.Id)) return TweakAppliedState.Indeterminate;
        if (NotConfirmedIds.Contains(tweak.Id)) return TweakAppliedState.NotApplied;
        return tweak.IsApplied || DetectAppliedIds.Contains(tweak.Id)
            ? TweakAppliedState.Applied : TweakAppliedState.NotApplied;
    }

    public Task<TweakApplyResult> ApplyAsync(Tweak tweak)
    {
        Applied.Add(tweak);
        if (FailIds.Contains(tweak.Id)) return Task.FromResult(new TweakApplyResult(false, "forced failure (test)"));
        tweak.IsApplied = true;
        return Task.FromResult(new TweakApplyResult(true));
    }

    public Task<TweakApplyResult> RevertAsync(Tweak tweak)
    {
        Reverted.Add(tweak);
        if (FailIds.Contains(tweak.Id)) return Task.FromResult(new TweakApplyResult(false, "forced failure (test)"));
        tweak.IsApplied = false;
        return Task.FromResult(new TweakApplyResult(true));
    }

    public Task<TweakAppliedState> IsAppliedAsync(Tweak tweak) => Task.FromResult(ProbeState(tweak));

    public Task<IReadOnlyList<TweakAppliedState>> DetectStatesAsync(IReadOnlyList<Tweak> tweaks)
    {
        var result = new TweakAppliedState[tweaks.Count];
        for (var i = 0; i < tweaks.Count; i++)
            result[i] = ProbeState(tweaks[i]);
        return Task.FromResult((IReadOnlyList<TweakAppliedState>)result);
    }

    // Revert-side probe. The fake doesn't model per-op aggregation (that masking subtlety is a real-engine concern),
    // so it reuses ProbeState: a tweak reads "still active" (Applied) after revert exactly when DetectAppliedIds
    // forces it on — letting a VM test drive the "revert reported done but it's still here" warning path.
    public Task<IReadOnlyList<TweakAppliedState>> DetectAfterRevertAsync(IReadOnlyList<Tweak> tweaks)
    {
        var result = new TweakAppliedState[tweaks.Count];
        for (var i = 0; i < tweaks.Count; i++)
            result[i] = ProbeState(tweaks[i]);
        return Task.FromResult((IReadOnlyList<TweakAppliedState>)result);
    }

    public Task<IReadOnlyList<bool>> DetectAppliedAsync(IReadOnlyList<Tweak> tweaks)
    {
        // Synchronous on purpose (Task.FromResult, never Task.Run): the DashboardViewModel's fire-and-forget
        // InitialiseAsync must complete inline during construction so tests see deterministic state right after
        // `new`. Shares ProbeState with the tri-state detector so the ✓ flag never disagrees with it.
        var result = new bool[tweaks.Count];
        for (var i = 0; i < tweaks.Count; i++)
            result[i] = ProbeState(tweaks[i]) == TweakAppliedState.Applied;
        return Task.FromResult((IReadOnlyList<bool>)result);
    }

    public Task<VerificationReport?> VerifyAppliedAsync(IReadOnlyList<Tweak> attempted)
    {
        // Mirrors prod's honest filter: verify ONLY the tweaks the engine reported applied (IsApplied), so a forced
        // failure (FailIds → IsApplied stays false) is excluded and can't be miscounted as "didn't stick". Shares
        // ProbeState with every other detector so verification and detection can never disagree in a test.
        var applied = new List<Tweak>();
        foreach (var t in attempted)
            if (t.IsApplied) applied.Add(t);
        if (applied.Count == 0) return Task.FromResult<VerificationReport?>(null);
        var probed = new List<(string, TweakAppliedState)>(applied.Count);
        foreach (var t in applied)
            probed.Add((t.Id, ProbeState(t)));
        return Task.FromResult<VerificationReport?>(TweakVerifier.Build(probed));
    }

    public Task<ApplyPlan> PreviewApplyPlanAsync(IReadOnlyList<Tweak> tweaks)
        => Task.FromResult(TweakApplyPlan.Build(tweaks));

    public Task<BatchTweakResult> ApplyManyAsync(IEnumerable<Tweak> tweaks)
    {
        // Safety-abort mirror: a failed REQUIRED restore point means the engine touches nothing — so the fake must
        // NOT record the tweaks or flip IsApplied either, or a VM test would wrongly see "applied" state.
        if (RestorePointWillFail) return Task.FromResult(new BatchTweakResult(0, 0, RestorePointFailed: true));
        int ok = 0, failed = 0;
        foreach (var t in tweaks)
        {
            Applied.Add(t);
            if (FailIds.Contains(t.Id)) failed++;
            else { t.IsApplied = true; ok++; }
        }
        return Task.FromResult(new BatchTweakResult(ok, failed));
    }

    public Task<BatchTweakResult> RevertAllAsync(IEnumerable<Tweak> tweaks)
    {
        int ok = 0, failed = 0;
        foreach (var t in tweaks)
        {
            Reverted.Add(t);
            if (FailIds.Contains(t.Id)) failed++;
            else { t.IsApplied = false; ok++; }
        }
        return Task.FromResult(new BatchTweakResult(ok, failed));
    }
}

/// <summary>
/// In-memory change journal that faithfully mirrors the real store's newest-first, bounded semantics via the
/// shared <see cref="JournalLog.Prepend"/> — so a VM test can assert exactly what an apply/revert recorded
/// (action, tally, ids, unconfirmed) without touching disk. <see cref="Entries"/> is the live newest-first view.
/// </summary>
public sealed class RecordingApplyJournal : IApplyJournal
{
    private IReadOnlyList<JournalEntry> _entries = System.Array.Empty<JournalEntry>();

    public IReadOnlyList<JournalEntry> Entries => _entries;

    public Task<IReadOnlyList<JournalEntry>> LoadAsync() => Task.FromResult(_entries);

    public Task RecordAsync(JournalEntry entry)
    {
        _entries = JournalLog.Prepend(_entries, entry);
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        _entries = System.Array.Empty<JournalEntry>();
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory optimization-score timeline. Routes through the REAL <see cref="ScoreHistory.Record"/> (dedupe + cap),
/// not a pinned series, so a VM test exercises the same append rules production does. Every call returns a completed
/// Task, so the dashboard VM's ctor InitialiseAsync settles synchronously and the trend is set right after <c>new</c>.
/// <see cref="Seed"/> pre-loads prior samples so a test can assert the "since last measure" delta without simulating
/// two separate launches.
/// </summary>
public sealed class FakeScoreHistoryStore : IScoreHistoryStore
{
    private IReadOnlyList<ScoreSnapshot> _samples = System.Array.Empty<ScoreSnapshot>();

    public IReadOnlyList<ScoreSnapshot> Samples => _samples;

    public FakeScoreHistoryStore Seed(params ScoreSnapshot[] samples)
    {
        _samples = samples;
        return this;
    }

    public Task<IReadOnlyList<ScoreSnapshot>> LoadAsync() => Task.FromResult(_samples);

    public Task<IReadOnlyList<ScoreSnapshot>> RecordAsync(int score)
    {
        _samples = ScoreHistory.Record(_samples, new ScoreSnapshot(System.DateTime.UtcNow, score));
        return Task.FromResult(_samples);
    }
}

/// <summary>
/// Returns the French string from a localized map (falls back to the first value). No resource files.
/// </summary>
public sealed class FakeLocalizationService : ILocalizationService
{
    public string CurrentLanguageCode => "fr";
    public string Get(string key) => key;

    public string GetLocalizedFrom(IDictionary<string, string> map)
        => map.TryGetValue("fr", out var fr) ? fr : map.Values.FirstOrDefault() ?? string.Empty;

    public void SetLanguage(string langCode) => LanguageChanged?.Invoke(this, System.EventArgs.Empty);
    public event System.EventHandler? LanguageChanged;
}

/// <summary>
/// Serves a fixed, in-memory tweak list (no JSON, no disk) so engine tests control every
/// applicability/scoring axis precisely. The shipped catalog is exercised separately by
/// <c>TweakCatalogIntegrityTests</c> through the real <see cref="TweakRepository"/>.
/// </summary>
public sealed class FakeTweakRepository : ITweakRepository
{
    private readonly IReadOnlyList<Tweak> _tweaks;

    // Defaults to a clean catalog; a test can inject rejected entries to exercise the tamper-disclosure path.
    public FakeTweakRepository(IEnumerable<Tweak> tweaks, IEnumerable<string>? rejectedFiles = null)
    {
        _tweaks = tweaks.ToList();
        RejectedFiles = (rejectedFiles ?? Enumerable.Empty<string>()).ToList();
    }

    public Task<IReadOnlyList<Tweak>> LoadAllAsync() => Task.FromResult(_tweaks);

    public Tweak? GetById(string id) =>
        _tweaks.FirstOrDefault(t => string.Equals(t.Id, id, System.StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> RejectedFiles { get; }
}

/// <summary>
/// In-memory profile store. Serves the given presets + user profiles (defaults to the real built-in presets,
/// so the ProfilesViewModel sees the same six cards production does), records saves/deletes, AND round-trips
/// them: SaveAsync upserts into the user list (by id) and DeleteAsync removes it, so a later LoadProfilesAsync
/// reflects the change — exactly like the real file-per-id store. Returns completed Tasks — never Task.Run —
/// so the VM's fire-and-forget LoadAsync settles inline during construction and tests see deterministic state
/// right after <c>new</c>.
/// </summary>
public sealed class FakeProfileService : IProfileService
{
    private readonly List<Profile> _presets;
    private readonly List<Profile> _userProfiles;

    public List<Profile> Saved { get; } = new();
    public List<string> Deleted { get; } = new();

    public FakeProfileService(IEnumerable<Profile>? presets = null, IEnumerable<Profile>? userProfiles = null)
    {
        _presets = (presets ?? ProfilePresets.BuiltIn()).ToList();
        _userProfiles = (userProfiles ?? Enumerable.Empty<Profile>()).ToList();
    }

    public Task<IReadOnlyList<Profile>> GetBuiltInPresetsAsync() => Task.FromResult((IReadOnlyList<Profile>)_presets);
    public Task<IReadOnlyList<Profile>> LoadProfilesAsync() => Task.FromResult((IReadOnlyList<Profile>)_userProfiles);
    public Task SaveAsync(Profile profile)
    {
        Saved.Add(profile);
        _userProfiles.RemoveAll(p => p.Id == profile.Id);   // upsert by id, mirroring the real file-per-id store
        _userProfiles.Add(profile);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string profileId)
    {
        Deleted.Add(profileId);
        _userProfiles.RemoveAll(p => p.Id == profileId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory <see cref="IBenchmarkService"/>: hands back whatever <see cref="ResultToReturn"/> is set to (every
/// call returns a completed Task, so the VM's synchronous ctor wiring stays deterministic). Lets a VM test drive
/// the before/after orchestration — pin a baseline, "capture" again, assert the comparison — without ETW or a CSV.
/// </summary>
public sealed class FakeBenchmarkService : IBenchmarkService
{
    public bool LiveAvailable { get; set; } = true;
    public BenchmarkResult ResultToReturn { get; set; } = new();
    public List<string> Candidates { get; } = new();

    public BenchmarkBackendStatus GetStatus()
        => new() { IsElevated = LiveAvailable, LiveCaptureAvailable = LiveAvailable, Message = "fake" };

    public BenchmarkResult AnalyzeCsv(string filePath) => ResultToReturn;

    public Task<BenchmarkResult> CaptureLiveAsync(string? targetProcess, System.TimeSpan duration,
        System.IProgress<int>? progress, System.Threading.CancellationToken ct)
        => Task.FromResult(ResultToReturn);

    public IReadOnlyList<string> GetCandidateProcesses() => Candidates;
}

/// <summary>
/// In-memory <see cref="IBenchmarkHistoryService"/>: records what the VM archives/deletes and hands back a scripted
/// list/load, so a VM test can prove a live capture is auto-archived, a stored run reloads (without re-archiving),
/// and pinning a stored run as « Avant » enables a cross-session A/B — all without touching the disk.
/// </summary>
public sealed class FakeBenchmarkHistoryService : IBenchmarkHistoryService
{
    public List<BenchmarkResult> Saved { get; } = new();
    public List<string> Deleted { get; } = new();
    public List<BenchmarkHistoryEntry> Entries { get; } = new();
    public BenchmarkResult? ToLoad { get; set; }

    public Task<bool> SaveAsync(BenchmarkResult result)
    {
        if (result is null || !result.HasData) return Task.FromResult(false);
        Saved.Add(result);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<BenchmarkHistoryEntry>> ListAsync()
        => Task.FromResult<IReadOnlyList<BenchmarkHistoryEntry>>(Entries.ToList());

    public Task<BenchmarkResult?> LoadAsync(string filePath) => Task.FromResult(ToLoad);

    public Task DeleteAsync(string filePath)
    {
        Deleted.Add(filePath);
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory <see cref="ISnapshotService"/> that mirrors the real <c>SnapshotService.BuildLiveAsync</c>: a "capture
/// now" probes the SAME repo + tweak service the VM mutates, so after a reapply the re-probe genuinely reflects
/// whether the fix stuck — never a fabricated success. No disk. Saved captures live newest-first in
/// <see cref="Stored"/>; <see cref="CaptureLiveCount"/> lets a test assert the machine was actually re-probed.
/// </summary>
public sealed class FakeSnapshotService : ISnapshotService
{
    private readonly ITweakRepository _repo;
    private readonly ITweakService _tweaks;
    private readonly ILocalizationService _localization;

    public List<SystemSnapshot> Stored { get; } = new();   // newest first, like the real file store
    public int CaptureLiveCount { get; private set; }

    public FakeSnapshotService(ITweakRepository repo, ITweakService tweaks, ILocalizationService? localization = null)
    {
        _repo = repo;
        _tweaks = tweaks;
        _localization = localization ?? new FakeLocalizationService();
    }

    public async Task<SystemSnapshot> CaptureAsync(string? label)
    {
        var snapshot = await BuildLiveAsync(label);
        Stored.Insert(0, snapshot);
        return snapshot;
    }

    public Task<SystemSnapshot> CaptureLiveAsync(string? label) => BuildLiveAsync(label);

    public Task<IReadOnlyList<SystemSnapshot>> LoadAllAsync()
        => Task.FromResult((IReadOnlyList<SystemSnapshot>)Stored.ToList());   // already newest-first

    public Task DeleteAsync(string id)
    {
        Stored.RemoveAll(s => s.Id == id);
        return Task.CompletedTask;
    }

    // Export/import knobs for the VM wiring tests (the real parse/validate is pinned directly on SnapshotPortability).
    public List<(SystemSnapshot Snapshot, string Path)> Exported { get; } = new();
    public SystemSnapshot? ImportToReturn { get; set; }
    public string? ImportErrorMessage { get; set; }

    public Task ExportAsync(SystemSnapshot snapshot, string destinationPath)
    {
        Exported.Add((snapshot, destinationPath));
        return Task.CompletedTask;
    }

    public Task<SystemSnapshot> ImportAsync(string sourcePath)
    {
        // A set error message drives the validation-failure path; otherwise persist the import into Stored, exactly as
        // the real service does, so the VM test sees the same "joins the saved list" behaviour.
        if (ImportErrorMessage is not null) throw new SnapshotImportException(ImportErrorMessage);
        var snap = ImportToReturn ?? new SystemSnapshot
        {
            Label = "importé",
            Entries = { new SnapshotEntry("t", "t", TweakAppliedState.Applied) }
        };
        Stored.Insert(0, snap);
        return Task.FromResult(snap);
    }

    // Same shape as the production BuildLiveAsync so the diff is exercised over real probes, not a canned result.
    private async Task<SystemSnapshot> BuildLiveAsync(string? label)
    {
        CaptureLiveCount++;
        var catalog = await _repo.LoadAllAsync();
        var states = await _tweaks.DetectStatesAsync(catalog);
        var entries = new List<SnapshotEntry>(catalog.Count);
        for (var i = 0; i < catalog.Count; i++)
        {
            var name = _localization.GetLocalizedFrom(catalog[i].Name);
            if (string.IsNullOrWhiteSpace(name)) name = catalog[i].Id;
            entries.Add(new SnapshotEntry(catalog[i].Id, name, states[i]));
        }
        return new SystemSnapshot { Label = label?.Trim() ?? string.Empty, Entries = entries };
    }
}

/// <summary>
/// In-memory power-plan service — returns a configurable report/detail so the dashboard's system-report builder
/// has real readings to render without spawning powercfg. The QueryOk-false detail lets a test drive the honest
/// « indéterminé » path.
/// </summary>
public sealed class FakePowerPlanService : IPowerPlanService
{
    public PowerPlanReport Report { get; set; } =
        new(System.Array.Empty<PowerScheme>(), "Utilisation normale", UltimatePresent: false);

    public ProcessorPowerDetail Detail { get; set; } =
        new(MinThrottlePercent: 5, MaxThrottlePercent: 100, MinUnparkedCoresPercent: 100, QueryOk: true);

    public Task<PowerPlanReport> GetReportAsync() => Task.FromResult(Report);
    public Task<ProcessorPowerDetail> GetProcessorDetailAsync() => Task.FromResult(Detail);
    public Task<bool> ActivateAsync(System.Guid scheme) => Task.FromResult(true);
    public Task<bool> EnableUltimateAsync() => Task.FromResult(true);
    public Task<bool> SetProcessorTuningAsync(ProcessorPowerTuning tuning)
    {
        Detail = new ProcessorPowerDetail(
            tuning.MinThrottlePercent,
            tuning.MaxThrottlePercent,
            tuning.MinUnparkedCoresPercent,
            QueryOk: true);
        return Task.FromResult(true);
    }
}

/// <summary>In-memory timer-resolution probe — returns a configurable reading; no ntdll call is made.</summary>
public sealed class FakeTimerResolutionService : ITimerResolutionService
{
    public TimerResolutionReading Reading { get; set; } =
        new(QueryOk: true, CurrentHundredNs: 5000, MinHundredNs: 156250, MaxHundredNs: 5000);

    public TimerResolutionReading Read() => Reading;
}

/// <summary>In-memory pending-reboot probe — returns a configurable verdict; no registry is read. Defaults to the
/// honest "not pending" status the real evaluator produces for all-clear signals, so the dashboard banner stays hidden.</summary>
public sealed class FakePendingRebootService : IPendingRebootService
{
    public PendingRebootStatus Status { get; set; } =
        PendingRebootEvaluator.Evaluate(new PendingRebootSignals(false, false, false, false));

    public Task<PendingRebootStatus> GetStatusAsync() => Task.FromResult(Status);
}

/// <summary>In-memory pre-flight probe — returns a configurable verdict; no PowerShell/registry is touched. Defaults to
/// the honest all-clear posture (restore requested + readable, no reboot) so the banner stays quiet in tests that don't
/// exercise it. The <see cref="Calls"/> counter lets a test prove the VM re-probes on « Revérifier ».</summary>
public sealed class FakePreflightService : IPreflightService
{
    public PreflightVerdict Verdict { get; set; } =
        PreflightEvaluator.Evaluate(new PreflightSignals(
            RestorePointRequested: true, SystemRestoreReadable: true, RebootPending: false));

    public int Calls { get; private set; }

    public Task<PreflightVerdict> EvaluateAsync()
    {
        Calls++;
        return Task.FromResult(Verdict);
    }

    // Records the one-click « activer la Restauration système » action and lets a test simulate the live state it
    // produces: set VerdictAfterEnable to the posture the re-probe should read once enabled (e.g. all-clear), or leave
    // it null to model « it didn't take » (policy-blocked) so the caution honestly persists.
    public int EnableCalls { get; private set; }
    public PreflightVerdict? VerdictAfterEnable { get; set; }

    public Task<PreflightVerdict> EnableRestoreAndReprobeAsync()
    {
        EnableCalls++;
        if (VerdictAfterEnable is not null) Verdict = VerdictAfterEnable;
        return EvaluateAsync();
    }
}

/// <summary>In-memory System Restore front-end — returns a configurable overview and records whether it was probed, so a
/// PreflightService test can prove the (PowerShell-spawning) probe is SKIPPED when the restore-point option is off.
/// Checkpoint/throttle writes are inert: the pre-flight only ever reads the overview.</summary>
public sealed class FakeRestoreManagerService : IRestoreManagerService
{
    public bool OverviewQueryOk { get; set; } = true;
    public int OverviewCalls { get; private set; }

    public Task<RestoreOverview> GetOverviewAsync()
    {
        OverviewCalls++;
        var freq = new RestoreFrequencyState(null, false);
        return Task.FromResult(OverviewQueryOk
            ? new RestoreOverview(true, System.Array.Empty<RestorePoint>(), freq)
            : RestoreOverview.Failed(freq));
    }

    public Task<CheckpointOutcome> CreateCheckpointAsync(string description) => Task.FromResult(CheckpointOutcome.Failed);
    public Task<bool> SetUnthrottledAsync(bool unthrottle) => Task.FromResult(true);

    public int EnableProtectionCalls { get; private set; }

    // Lets a test simulate the live state a successful enable produces (e.g. flip OverviewQueryOk to true), so the VM's
    // re-read reflects reality — not a fabricated success. Null ⇒ « it didn't take », OverviewQueryOk stays as-is.
    public System.Action? OnEnableProtection { get; set; }

    public Task EnableProtectionAsync()
    {
        EnableProtectionCalls++;
        OnEnableProtection?.Invoke();
        return Task.CompletedTask;
    }
}

/// <summary>In-memory drive-health probe — returns a configurable report; no PowerShell/WMI is run. Defaults to an
/// honest "queried OK, no drives listed" report so the dashboard's system-report section stays benign in tests that
/// don't exercise drive health.</summary>
public sealed class FakeDriveHealthService : IDriveHealthService
{
    public DriveHealthReport Report { get; set; } =
        new(System.Array.Empty<DriveHealthInfo>(), QueryOk: true);

    public Task<DriveHealthReport> GetReportAsync() => Task.FromResult(Report);
}

/// <summary>
/// In-memory licence token store — round-trips Save/Load/Clear so a service test can prove a valid token is persisted,
/// a rejected one writes nothing, and a deactivate clears it, all without touching disk. The counters let a test
/// assert "persisted exactly once" / "cleared once" rather than just the final state.
/// </summary>
public sealed class FakeLicenseStore : ILicenseStore
{
    public string? Token { get; private set; }
    public int SaveCount { get; private set; }
    public int ClearCount { get; private set; }

    public FakeLicenseStore(string? seed = null) => Token = seed;

    public Task<string?> LoadAsync() => Task.FromResult(Token);
    public Task SaveAsync(string token) { Token = token; SaveCount++; return Task.CompletedTask; }
    public Task ClearAsync() { Token = null; ClearCount++; return Task.CompletedTask; }
}

/// <summary>
/// A licence key ring serving a fixed public key (base64 SubjectPublicKeyInfo), or empty/garbage to drive the
/// "not configured" / "bad-key" paths. Tests build the valid case from an ephemeral keypair so the whole verify chain
/// (ImportSubjectPublicKeyInfo → LicenseVerifier) runs for real — nothing is stubbed past the trust boundary.
/// </summary>
public sealed class FakeLicenseKeyRing : ILicenseKeyRing
{
    public FakeLicenseKeyRing(string? publicKeyBase64) => PublicKeyBase64 = publicKeyBase64;
    public string? PublicKeyBase64 { get; }
}

/// <summary>
/// A licence service stand-in for ViewModel tests that need a settable edition without driving the real verify chain.
/// Defaults to Free + not-configured (the as-shipped state), so a VM built with the bare fake sees EVERYTHING unlocked
/// and the freemium gate is a no-op — which is exactly what the pinned apply/preview status-string tests depend on.
/// Activate/Deactivate flip the edition and raise <see cref="EditionChanged"/> so a subscribing VM re-gates live,
/// mirroring the real service's transition contract. The edition and configured flags are also directly settable so a
/// gating test can pin "configured + Free" or "configured + Premium" without forging a token.
/// </summary>
public sealed class FakeLicenseService : ILicenseService
{
    public FakeLicenseService(AppEdition edition = AppEdition.Free, bool configured = false)
    {
        CurrentEdition = edition;
        IsConfigured = configured;
    }

    public AppEdition CurrentEdition { get; set; }
    public bool IsConfigured { get; set; }
    public string StatusReason { get; set; } = "fake";
    public LicensePayload? CurrentPayload { get; set; }
    public event System.EventHandler? EditionChanged;

    public Task InitialiseAsync() => Task.CompletedTask;

    public Task<LicenseValidation> ActivateAsync(string token)
    {
        var payload = new LicensePayload(AppEdition.Premium, "test@example.com", System.DateTime.UtcNow, null);
        CurrentEdition = AppEdition.Premium;
        StatusReason = "ok";
        CurrentPayload = payload;
        EditionChanged?.Invoke(this, System.EventArgs.Empty);
        return Task.FromResult(LicenseValidation.Valid(payload));
    }

    public Task DeactivateAsync()
    {
        CurrentEdition = AppEdition.Free;
        StatusReason = "deactivated";
        CurrentPayload = null;
        EditionChanged?.Invoke(this, System.EventArgs.Empty);
        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="IEvidenceStore"/> for ledger-wiring tests: records what was published (a null
/// clears it, like the real file delete) and counts saves, with no disk I/O — so the ledger's rehydrate/write-through
/// behaviour is verified deterministically.</summary>
public sealed class FakeEvidenceStore : IEvidenceStore
{
    public BenchmarkComparison? Stored;
    public int SaveCount;

    public BenchmarkComparison? LoadPerformance() => Stored;
    public void SavePerformance(BenchmarkComparison? comparison) { Stored = comparison; SaveCount++; }
}
