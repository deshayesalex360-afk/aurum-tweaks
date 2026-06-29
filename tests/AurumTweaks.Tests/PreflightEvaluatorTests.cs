using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pre-flight safety check's pure core: probed signals → an honest, advisory posture shown BEFORE applying.
/// The honesty invariants are load-bearing — the banner must FORECAST the apply-time restore-point abort (never fake a
/// green check), must never claim a safety net the user opted out of, and must surface ONLY the two real signals
/// (elevation is omitted because the manifest forces it true; disk space is omitted because there is no honest
/// threshold to assert). No I/O — the decision is tested without spawning PowerShell or reading the registry.
/// </summary>
public class PreflightEvaluatorTests
{
    private static PreflightVerdict Eval(bool requested, bool readable, bool pending) =>
        PreflightEvaluator.Evaluate(new PreflightSignals(requested, readable, pending));

    private static PreflightCheck Restore(PreflightVerdict v) => v.Checks[0];
    private static PreflightCheck Reboot(PreflightVerdict v) => v.Checks[1];

    // --- Restore check: the three honest outcomes -------------------------------------------------

    [Fact]
    public void Restore_Requested_AndReadable_IsOk_AndHedgesThe24hThrottle()
    {
        var c = Restore(Eval(requested: true, readable: true, pending: false));
        Assert.Equal(PreflightSeverity.Ok, c.Severity);
        Assert.Equal(PreflightEvaluator.RestoreActiveTitle, c.Title);
        Assert.Contains("24 h", c.Detail); // never over-promises: Windows' throttle case is acknowledged up front
    }

    [Fact]
    public void Restore_Requested_ButUnreadable_IsCaution_AndForecastsTheApplyTimeAbort()
    {
        var c = Restore(Eval(requested: true, readable: false, pending: false));
        Assert.Equal(PreflightSeverity.Caution, c.Severity);
        Assert.Equal(PreflightEvaluator.RestoreUnavailableTitle, c.Title);
        Assert.Contains("tentera de l'activer", c.Detail);                  // the EnableSystemRestoreIfDisabled attempt
        Assert.Contains("aucune modification ne sera appliquée", c.Detail); // forecasts the real abort, same promise
    }

    [Theory]
    [InlineData(true)]   // readability is irrelevant once the user opts out
    [InlineData(false)]
    public void Restore_OptedOut_IsInfo_AndNeverClaimsASafetyNet(bool readable)
    {
        var v = Eval(requested: false, readable: readable, pending: false);
        var c = Restore(v);
        Assert.Equal(PreflightSeverity.Info, c.Severity);
        Assert.Equal(PreflightEvaluator.RestoreOptedOutTitle, c.Title);
        Assert.Contains("désactivée dans les Paramètres", c.Detail);
        // Opted out is NOT "all clear", and the summary must not imply a net that won't be created.
        Assert.False(v.AllClear);
        Assert.False(v.HasCaution);
        Assert.DoesNotContain("filet de sécurité en place", v.Summary);
        Assert.Equal("Prêt à appliquer.", v.Summary);
    }

    // --- Reboot check -----------------------------------------------------------------------------

    [Fact]
    public void Reboot_NotPending_IsOk()
    {
        var c = Reboot(Eval(requested: true, readable: true, pending: false));
        Assert.Equal(PreflightSeverity.Ok, c.Severity);
        Assert.Equal(PreflightEvaluator.RebootClearTitle, c.Title);
    }

    [Fact]
    public void Reboot_Pending_IsCaution()
    {
        var c = Reboot(Eval(requested: true, readable: true, pending: true));
        Assert.Equal(PreflightSeverity.Caution, c.Severity);
        Assert.Equal(PreflightEvaluator.RebootPendingTitle, c.Title);
        Assert.Contains("redémarrage en attente", c.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // --- Verdict aggregation ----------------------------------------------------------------------

    [Fact]
    public void AllClear_WhenRestoreReadable_AndNoReboot_PromisesTheNet()
    {
        var v = Eval(requested: true, readable: true, pending: false);
        Assert.True(v.AllClear);
        Assert.False(v.HasCaution);
        Assert.Equal(0, v.CautionCount);
        Assert.Equal("Prêt à appliquer — filet de sécurité en place.", v.Summary);
    }

    [Fact]
    public void OneCaution_IsCountedAndSummarised()
    {
        var v = Eval(requested: true, readable: true, pending: true); // only the reboot is a worry
        Assert.True(v.HasCaution);
        Assert.Equal(1, v.CautionCount);
        Assert.False(v.AllClear);
        Assert.Equal("1 point(s) d'attention avant d'appliquer.", v.Summary);
    }

    [Fact]
    public void TwoCautions_AreBothCountedAndSummarised()
    {
        var v = Eval(requested: true, readable: false, pending: true); // restore unreadable AND reboot pending
        Assert.True(v.HasCaution);
        Assert.Equal(2, v.CautionCount);
        Assert.Equal("2 point(s) d'attention avant d'appliquer.", v.Summary);
    }

    // --- Restore remediation: the ONE caution the user can fix on the spot ------------------------

    [Fact]
    public void OffersRestoreRemediation_OnlyWhenSystemRestoreIsUnreadable()
        => Assert.True(PreflightEvaluator.OffersRestoreRemediation(
            Eval(requested: true, readable: false, pending: false)));

    [Theory]
    [InlineData(true, true, false)]    // restore readable ⇒ nothing to remediate
    [InlineData(false, false, false)]  // opted out ⇒ Info, not a caution
    [InlineData(true, true, true)]     // only a pending reboot ⇒ a caution, but NOT one this action fixes
    public void OffersRestoreRemediation_IsFalse_WhenThereIsNoUnreadableRestore(bool req, bool readable, bool pending)
        => Assert.False(PreflightEvaluator.OffersRestoreRemediation(Eval(req, readable, pending)));

    [Fact]
    public void OffersRestoreRemediation_StillTrue_WhenARebootIsAlsoPending()
    {
        // Both cautions present: the remediation still applies because the unreadable-restore caution is there —
        // the reboot caution simply has no in-app fix and doesn't suppress the one that does.
        var v = Eval(requested: true, readable: false, pending: true);
        Assert.Equal(2, v.CautionCount);
        Assert.True(PreflightEvaluator.OffersRestoreRemediation(v));
    }

    // --- Honesty: only the two REAL signals are ever surfaced -------------------------------------

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void Verdict_AlwaysHasExactlyTwoChecks_NoFabricatedElevationOrDisk(bool req, bool readable, bool pending)
    {
        var v = Eval(req, readable, pending);
        Assert.Equal(2, v.Checks.Count); // Restore + Reboot only
        Assert.DoesNotContain(v.Checks, c =>
            c.Title.Contains("élévation", StringComparison.OrdinalIgnoreCase) ||
            c.Title.Contains("disque", StringComparison.OrdinalIgnoreCase) ||
            c.Title.Contains("espace", StringComparison.OrdinalIgnoreCase));
    }
}
