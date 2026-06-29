using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

/// <summary>How strongly a pre-flight check should be surfaced. <see cref="Caution"/> is the strongest level on
/// purpose: the pre-flight is ADVISORY — it shows the safety-net posture before the user clicks, it never blocks the
/// apply (the real block is the apply-time restore-point abort). So there is no "Error/Block" level it doesn't own.</summary>
public enum PreflightSeverity
{
    Ok,
    Info,
    Caution
}

/// <summary>One pre-flight line: a severity, a short FR title, and one plain-French detail. Every field is derived
/// from a REAL probed signal — never a fabricated green check (elevation is excluded precisely because the manifest
/// forces it true, which would be a fake "verified").</summary>
public sealed record PreflightCheck(PreflightSeverity Severity, string Title, string Detail);

/// <summary>
/// The raw signals a pre-flight verdict is built from, as plain booleans so the honesty-bearing decision
/// (<see cref="PreflightEvaluator"/>) is unit-tested without spawning PowerShell or reading the registry — the
/// project's « test the decision, not the world » pattern. Deliberately small: elevation is omitted (manifest-forced
/// ⇒ always true ⇒ a fake check) and disk space is omitted (no honest threshold to assert, so none is invented).
/// </summary>
public sealed record PreflightSignals(
    bool RestorePointRequested,
    bool SystemRestoreReadable,
    bool RebootPending);

/// <summary>
/// The whole pre-flight posture: one check per probed signal. <see cref="HasCaution"/> drives the banner's
/// prominence; <see cref="Summary"/> is the honest one-liner. A purely informational result (e.g. the user turned the
/// restore-point option off) is NOT "all clear", so the summary never claims a safety net the user opted out of.
/// </summary>
public sealed record PreflightVerdict(IReadOnlyList<PreflightCheck> Checks)
{
    public bool HasCaution => Checks.Any(c => c.Severity == PreflightSeverity.Caution);
    public int CautionCount => Checks.Count(c => c.Severity == PreflightSeverity.Caution);
    public bool AllClear => Checks.All(c => c.Severity == PreflightSeverity.Ok);

    public string Summary =>
        HasCaution ? $"{CautionCount} point(s) d'attention avant d'appliquer."
        : AllClear ? "Prêt à appliquer — filet de sécurité en place."
        : "Prêt à appliquer.";
}

/// <summary>
/// Pure decision core for the pre-flight safety check: probed signals → an honest posture shown BEFORE the user
/// clicks « Appliquer », so the safety net is visible up front, not only at apply time. Mirrors
/// <see cref="PendingRebootEvaluator"/> (the renderer/evaluator is tested; the probe is thin glue). Two checks only,
/// each pinned to a REAL signal:
/// <list type="bullet">
///   <item>Restauration système — forecasts the apply-time restore-point abort: if a point is required but System
///   Restore can't be read, the app will try to enable it and, failing that, apply NOTHING. The message says exactly
///   that, so the banner and the abort can never disagree.</item>
///   <item>Redémarrage en attente — a queued Windows restart means changes may not settle cleanly.</item>
/// </list>
/// It never fabricates a worry: only a signal that genuinely read as off/pending yields a Caution.
/// </summary>
public static class PreflightEvaluator
{
    // Stable FR titles — referenced by tests, so kept as consts to pin the contract against accidental reword drift.
    public const string RestoreActiveTitle = "Restauration système active";
    public const string RestoreUnavailableTitle = "Restauration système indisponible";
    public const string RestoreOptedOutTitle = "Point de restauration désactivé";
    public const string RebootClearTitle = "Aucun redémarrage en attente";
    public const string RebootPendingTitle = "Redémarrage en attente";

    public static PreflightVerdict Evaluate(PreflightSignals signals) =>
        new(new[] { RestoreCheck(signals), RebootCheck(signals) });

    /// <summary>
    /// True when the verdict carries the « Restauration système indisponible » caution — the ONE pre-flight problem
    /// the user can fix on the spot, by re-enabling System Restore in the Windows « Protection du système » dialog.
    /// Lets the banner offer a remediation action ONLY for that caution (never for a pending reboot, which has no
    /// in-app fix), keyed to the stable title so the offer can't drift from the caution it actually remedies. Turning
    /// the forecast-only warning into an actionable one is the « testable sans peur » payoff: don't just tell the user
    /// the safety net is off — point them straight at where to switch it back on.
    /// </summary>
    public static bool OffersRestoreRemediation(PreflightVerdict verdict) =>
        verdict.Checks.Any(c => c.Severity == PreflightSeverity.Caution && c.Title == RestoreUnavailableTitle);

    private static PreflightCheck RestoreCheck(PreflightSignals s)
    {
        // User opted out: not a problem, but be honest that no net will be created — never imply one exists.
        if (!s.RestorePointRequested)
            return new PreflightCheck(PreflightSeverity.Info, RestoreOptedOutTitle,
                "L'option « créer un point de restauration » est désactivée dans les Paramètres : " +
                "aucun point ne sera créé avant d'appliquer.");

        // Readable now → forecast creation, hedged honestly for Windows' 24h throttle (which CreateAsync treats as success).
        if (s.SystemRestoreReadable)
            return new PreflightCheck(PreflightSeverity.Ok, RestoreActiveTitle,
                "Un point de restauration sera créé avant d'appliquer " +
                "(sauf si Windows en a déjà créé un de moins de 24 h).");

        // Requested but unreadable → forecast the real abort: the app tries to enable System Restore first, and if the
        // point still can't be created it applies nothing. Wording matches TweakApplyText.RestorePointFailed's promise.
        return new PreflightCheck(PreflightSeverity.Caution, RestoreUnavailableTitle,
            "La Restauration système semble désactivée ou injoignable. L'app tentera de l'activer ; " +
            "si la création du point échoue, aucune modification ne sera appliquée.");
    }

    private static PreflightCheck RebootCheck(PreflightSignals s) =>
        s.RebootPending
            ? new PreflightCheck(PreflightSeverity.Caution, RebootPendingTitle,
                "Windows a déjà un redémarrage en attente. Redémarre d'abord pour que les modifications " +
                "s'appliquent proprement.")
            : new PreflightCheck(PreflightSeverity.Ok, RebootClearTitle,
                "Aucun signal de redémarrage Windows en attente.");
}

/// <summary>
/// Thin I/O glue for the pre-flight check: gathers the real signals and hands them to the pure
/// <see cref="PreflightEvaluator"/>. It REUSES the existing safety-net services — <see cref="IRestoreManagerService"/>
/// for the genuine <c>Get-ComputerRestorePoint</c> readability probe and <see cref="IPendingRebootService"/> for the
/// standard registry reboot signals — rather than re-reading the world. It honours the restore-point toggle exactly:
/// when the user opted out, it does NOT spawn the System Restore probe (irrelevant work, and the evaluator ignores the
/// value in that branch).
/// </summary>
public sealed class PreflightService : IPreflightService
{
    private readonly IRestoreManagerService _restore;
    private readonly IPendingRebootService _reboot;
    private readonly IAppSettingsStore _settings;
    private readonly IRestorePointService _restorePoint;

    public PreflightService(IRestoreManagerService restore, IPendingRebootService reboot, IAppSettingsStore settings,
        IRestorePointService restorePoint)
    {
        _restore = restore;
        _reboot = reboot;
        _settings = settings;
        _restorePoint = restorePoint;
    }

    public async Task<PreflightVerdict> EvaluateAsync()
    {
        var restoreRequested = _settings.Current.CreateRestorePointBeforeTweaks;

        // Probe System Restore only when a point is actually requested: if opted out its readability is irrelevant and
        // querying would be wasted PowerShell. The evaluator's opted-out branch ignores this value, so `true` is safe.
        var systemRestoreReadable = true;
        if (restoreRequested)
            systemRestoreReadable = (await _restore.GetOverviewAsync()).QueryOk;

        var rebootPending = (await _reboot.GetStatusAsync()).IsPending;

        return PreflightEvaluator.Evaluate(
            new PreflightSignals(restoreRequested, systemRestoreReadable, rebootPending));
    }

    public async Task<PreflightVerdict> EnableRestoreAndReprobeAsync()
    {
        // Do the action the user asked for — actually turn System Restore on (elevated Enable-ComputerRestore), not a
        // hand-off to a Windows dialog. EnableSystemRestoreIfDisabledAsync's bool only means « the command ran », so we
        // ignore it and let EvaluateAsync re-read the live state: the caution clears ONLY if Windows now reads the net
        // back. That re-probe — never the command's optimistic return — is the honest source of truth.
        await _restorePoint.EnableSystemRestoreIfDisabledAsync();
        return await EvaluateAsync();
    }
}
