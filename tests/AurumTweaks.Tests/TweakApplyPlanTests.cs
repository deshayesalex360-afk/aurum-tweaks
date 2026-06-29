using System.Linq;
using AurumTweaks.Models;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the apply-plan aggregator (<see cref="TweakApplyPlan"/>) behind the Tweaks page's "Plan de modifications"
/// preview. The honesty stake: before a batch of as-admin changes runs, the plan must enumerate EVERY operation
/// (tagged with its tweak), tally the kinds truthfully, and own up to a required reboot or to operations that
/// can't be auto-reverted — never present a batch as smaller or cleaner than it is.
/// </summary>
public class TweakApplyPlanTests
{
    private static TweakOperation Reg(string name, string? revert = "0")
        => new() { Type = OperationType.Registry, Hive = "HKLM", Key = "K", Name = name, Apply = "1", Revert = revert };

    private static TweakOperation Svc(string name)
        => new() { Type = OperationType.Service, ServiceName = name, StartupApply = "Disabled", StartupRevert = "Manual" };

    private static TweakOperation Pwsh(string? revert)
        => new() { Type = OperationType.PowerShell, Script = "do", RevertScript = revert };

    private static Tweak T(string id, bool reboot, params TweakOperation[] ops)
    {
        var t = new Tweak { Id = id, RequiresReboot = reboot };
        foreach (var op in ops) t.Operations.Add(op);
        return t;
    }

    [Fact]
    public void Build_FlattensEveryOperation_TaggedByTweakId_InOrder()
    {
        var plan = TweakApplyPlan.Build(new[]
        {
            T("alpha", false, Reg("A"), Reg("B")),
            T("beta", false, Svc("S")),
        });

        Assert.Equal(3, plan.TotalOperations);
        Assert.Equal(new[] { "alpha", "alpha", "beta" }, plan.Operations.Select(o => o.TweakId));
        Assert.Equal(new[] { "Registre", "Registre", "Service" }, plan.Operations.Select(o => o.Operation.Kind));
    }

    [Fact]
    public void Build_CountsByKind_TalliesAndOrdersByCountThenName()
    {
        var plan = TweakApplyPlan.Build(new[]
        {
            T("t", false, Reg("A"), Reg("B"), Reg("C"), Svc("S"), Pwsh("undo"), Pwsh("undo")),
        });

        Assert.Equal(
            new[] { ("Registre", 3), ("PowerShell", 2), ("Service", 1) },
            plan.CountsByKind.Select(k => (k.Kind, k.Count)));
    }

    [Fact]
    public void Build_TweakCount_IsTheNumberOfTweaks_NotOperations()
    {
        var plan = TweakApplyPlan.Build(new[] { T("a", false, Reg("A"), Reg("B")), T("b", false, Svc("S")) });
        Assert.Equal(2, plan.TweakCount);
    }

    [Fact]
    public void Build_RequiresReboot_WhenAnyTweakDoes()
    {
        Assert.True(TweakApplyPlan.Build(new[] { T("a", false, Reg("A")), T("b", true, Reg("B")) }).RequiresReboot);
        Assert.False(TweakApplyPlan.Build(new[] { T("a", false, Reg("A")) }).RequiresReboot);
    }

    [Fact]
    public void Build_HasIrreversible_WhenAnyOperationCannotBeAutoReverted()
    {
        // A PowerShell op with no revert script surfaces "aucun rétablissement automatique" → the plan must admit it.
        Assert.True(TweakApplyPlan.Build(new[] { T("a", false, Reg("A"), Pwsh(revert: null)) }).HasIrreversible);
    }

    [Fact]
    public void Build_NoIrreversible_WhenEveryOperationDeclaresARevert()
    {
        Assert.False(TweakApplyPlan.Build(new[] { T("a", false, Reg("A"), Svc("S"), Pwsh("undo")) }).HasIrreversible);
    }

    [Fact]
    public void Build_IrreversibleCount_IsTheExactNumberOfNoRevertOps_NotMerelyYesNo()
    {
        // Two no-undo shell ops among reversible registry/service/undo-script ops ⇒ the plan owns the number « 2 », so
        // the consent banner can say HOW MANY (1-of-12 vs 8-of-12 are very different decisions), not just « some exist ».
        var plan = TweakApplyPlan.Build(new[]
        {
            T("a", false, Reg("A"), Pwsh(revert: null)),
            T("b", false, Svc("S"), Pwsh(revert: null), Pwsh("undo")),
        });

        Assert.Equal(2, plan.IrreversibleCount);
        Assert.True(plan.HasIrreversible);                              // the gate is derived from the count, never separate
        Assert.Equal(2, plan.Operations.Count(o => o.IsIrreversible));  // and it equals the flagged rows the UI marks red
    }

    [Fact]
    public void PlannedOperation_IsIrreversible_TracksTheNoRevertSentinel_PerRow()
    {
        var plan = TweakApplyPlan.Build(new[] { T("a", false, Reg("A"), Pwsh(revert: null)) });

        Assert.False(plan.Operations.Single(o => o.Operation.Kind == "Registre").IsIrreversible);   // restores its old value
        Assert.True(plan.Operations.Single(o => o.Operation.Kind == "PowerShell").IsIrreversible);  // a no-undo script
    }

    [Fact]
    public void Build_EmptySelection_IsAnEmptyPlan()
    {
        var plan = TweakApplyPlan.Build(System.Array.Empty<Tweak>());
        Assert.Equal(0, plan.TotalOperations);
        Assert.Equal(0, plan.TweakCount);
        Assert.Empty(plan.CountsByKind);
        Assert.False(plan.RequiresReboot);
        Assert.False(plan.HasIrreversible);
        Assert.Equal(0, plan.IrreversibleCount);
    }
}
