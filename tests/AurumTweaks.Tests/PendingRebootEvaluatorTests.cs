using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="PendingRebootEvaluator"/> — the honest verdict behind the « Redémarrage en attente » banner and
/// system-report section. Honesty contract: with no signal set it reads « aucun redémarrage en attente » (worded as a
/// reflection of standard signals, never a guarantee), each set signal yields exactly one plain-French reason naming
/// it, the summary's signal count is real, and a signal that is NOT set never fabricates a reason. The registry probe
/// that produces the booleans is thin glue; this tests the decision, not the world.
/// </summary>
public class PendingRebootEvaluatorTests
{
    private static PendingRebootSignals Signals(
        bool cbs = false, bool wu = false, bool fileRename = false, bool computerRename = false)
        => new(cbs, wu, fileRename, computerRename);

    [Fact]
    public void NoSignals_IsNotPending_WithNoReasons_AndAnHonestlyHedgedSummary()
    {
        var status = PendingRebootEvaluator.Evaluate(Signals());

        Assert.False(status.IsPending);
        Assert.Empty(status.Reasons);
        Assert.Contains("Aucun redémarrage en attente", status.Summary);
        Assert.Contains("signaux Windows standards", status.Summary);   // reflects what we read, never a guarantee
    }

    [Fact]
    public void ComponentBasedServicing_IsPending_WithACbsReason()
    {
        var status = PendingRebootEvaluator.Evaluate(Signals(cbs: true));

        Assert.True(status.IsPending);
        Assert.Single(status.Reasons);
        Assert.Contains("CBS", status.Reasons[0]);
        Assert.Contains("1 signal détecté", status.Summary);
    }

    [Fact]
    public void WindowsUpdate_IsPending_WithAWindowsUpdateReason()
    {
        var status = PendingRebootEvaluator.Evaluate(Signals(wu: true));

        Assert.True(status.IsPending);
        Assert.Contains("Windows Update", Assert.Single(status.Reasons));
    }

    [Fact]
    public void PendingFileRename_IsPending_NamingTheOperation()
    {
        var status = PendingRebootEvaluator.Evaluate(Signals(fileRename: true));

        Assert.True(status.IsPending);
        Assert.Contains("PendingFileRenameOperations", Assert.Single(status.Reasons));
    }

    [Fact]
    public void ComputerRename_IsPending_NamingTheComputerNameChange()
    {
        var status = PendingRebootEvaluator.Evaluate(Signals(computerRename: true));

        Assert.True(status.IsPending);
        Assert.Contains("nom de l'ordinateur", Assert.Single(status.Reasons));
    }

    [Fact]
    public void MultipleSignals_ListEachReason_AndPluraliseTheCount()
    {
        var status = PendingRebootEvaluator.Evaluate(Signals(cbs: true, wu: true));

        Assert.True(status.IsPending);
        Assert.Equal(2, status.Reasons.Count);
        Assert.Contains("2 signaux détectés", status.Summary);   // plural form
    }

    [Fact]
    public void AllSignals_ProduceFourReasons()
    {
        var status = PendingRebootEvaluator.Evaluate(
            Signals(cbs: true, wu: true, fileRename: true, computerRename: true));

        Assert.True(status.IsPending);
        Assert.Equal(4, status.Reasons.Count);
        Assert.Contains("4 signaux détectés", status.Summary);
    }

    [Fact]
    public void UnsetSignal_NeverFabricatesItsReason()
    {
        // Only WU is set: the CBS / file-rename / computer-rename reasons must be entirely absent — no fabricated tell.
        var status = PendingRebootEvaluator.Evaluate(Signals(wu: true));

        var only = Assert.Single(status.Reasons);
        Assert.DoesNotContain("CBS", only);
        Assert.DoesNotContain("PendingFileRenameOperations", only);
        Assert.DoesNotContain("nom de l'ordinateur", only);
    }
}
