using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the selection-conflict detector behind the apply-plan preview's "Conflits de sélection" warning. The
/// honesty stake: when two SELECTED tweaks set the same registry value (or service startup) to different values,
/// the batch applies them in order and the last write silently wins — the user can't see that order. The detector
/// must surface exactly those real, cross-tweak divergences and nothing else: agreeing tweaks (including two
/// encodings of the same number) raise no warning, shell ops we can't read are never flagged, and a tweak that
/// only contradicts itself isn't dressed up as a multi-tweak conflict. Pure, table-pinned; no registry, no JSON.
/// </summary>
public class TweakConflictDetectorTests
{
    private static TweakOperation Reg(string name, string? apply, RegistryValueType type = RegistryValueType.DWord,
                                      string hive = "HKLM", string key = "K")
        => new() { Type = OperationType.Registry, Hive = hive, Key = key, Name = name, Apply = apply, Revert = "0", ValueType = type };

    private static TweakOperation Svc(string serviceName, string startupApply)
        => new() { Type = OperationType.Service, ServiceName = serviceName, StartupApply = startupApply, StartupRevert = "Manual" };

    private static TweakOperation Pwsh(string script)
        => new() { Type = OperationType.PowerShell, Script = script, RevertScript = "undo" };

    private static Tweak T(string id, params TweakOperation[] ops)
    {
        var t = new Tweak { Id = id };
        foreach (var op in ops) t.Operations.Add(op);
        return t;
    }

    [Fact]
    public void Detect_SameRegistryValue_DifferentApply_IsOneConflict_AttributedToEachTweak()
    {
        var conflicts = TweakConflictDetector.Detect(new[]
        {
            T("a", Reg("Throttle", "1")),
            T("b", Reg("Throttle", "0")),
        });

        var c = Assert.Single(conflicts);
        Assert.Equal("Registre", c.Kind);
        Assert.Equal(@"HKLM\K\Throttle", c.Target);
        Assert.Equal(new[] { "0", "1" }, c.Values.Select(v => v.Value));   // ordered by value, deterministic
        Assert.Equal(new[] { "b" }, c.Values[0].TweakIds);                 // 0 ← b
        Assert.Equal(new[] { "a" }, c.Values[1].TweakIds);                 // 1 ← a
    }

    [Fact]
    public void Detect_SameRegistryValue_SameApply_IsNotAConflict()
        => Assert.Empty(TweakConflictDetector.Detect(new[]
        {
            T("a", Reg("Throttle", "1")),
            T("b", Reg("Throttle", "1")),   // redundant, not contradictory
        }));

    [Fact]
    public void Detect_TwoEncodingsOfTheSameNumber_AreNotAConflict()
        => Assert.Empty(TweakConflictDetector.Detect(new[]
        {
            T("a", Reg("N", "1", RegistryValueType.DWord)),
            T("b", Reg("N", "0x1", RegistryValueType.DWord)),   // 0x1 == 1 → agreement, never a fake conflict
        }));

    [Fact]
    public void Detect_DeleteVersusWrite_IsAConflict()
    {
        var c = Assert.Single(TweakConflictDetector.Detect(new[]
        {
            T("del", Reg("N", apply: null)),   // apply=null → delete the value
            T("set", Reg("N", "1")),
        }));

        Assert.Equal(new[] { "1", "supprime la valeur" }, c.Values.Select(v => v.Value));
        Assert.Equal(new[] { "set" }, c.Values[0].TweakIds);
        Assert.Equal(new[] { "del" }, c.Values[1].TweakIds);
    }

    [Fact]
    public void Detect_Service_DivergentStartup_IsAConflict()
    {
        var c = Assert.Single(TweakConflictDetector.Detect(new[]
        {
            T("a", Svc("SysMain", "Disabled")),
            T("b", Svc("SysMain", "Manual")),
        }));

        Assert.Equal("Service", c.Kind);
        Assert.Equal("SysMain", c.Target);
        Assert.Equal(new[] { "Disabled", "Manual" }, c.Values.Select(v => v.Value));
    }

    [Fact]
    public void Detect_Service_SameStartup_DifferentCase_IsNotAConflict()
        => Assert.Empty(TweakConflictDetector.Detect(new[]
        {
            T("a", Svc("SysMain", "Disabled")),
            T("b", Svc("SysMain", "disabled")),   // startup compared case-insensitively, like the engine
        }));

    [Fact]
    public void Detect_RegistryTarget_IsCaseInsensitive_StillConflictsOnValue()
    {
        var c = Assert.Single(TweakConflictDetector.Detect(new[]
        {
            T("a", Reg("Throttle", "1", key: @"Software\X")),
            T("b", Reg("throttle", "0", key: @"SOFTWARE\x")),   // same target, different case
        }));

        Assert.Equal(@"HKLM\Software\X\Throttle", c.Target);    // first occurrence's literal is shown
        Assert.Equal(2, c.Values.Count);
    }

    [Fact]
    public void Detect_ShellOps_AreNeverFlagged()
        => Assert.Empty(TweakConflictDetector.Detect(new[]
        {
            T("a", Pwsh("Set-ThingA")),
            T("b", Pwsh("Set-ThingB")),   // no deterministic readback → honesty rule excludes them
        }));

    [Fact]
    public void Detect_DifferentTargets_RaiseNoConflict()
        => Assert.Empty(TweakConflictDetector.Detect(new[]
        {
            T("a", Reg("N1", "1")),
            T("b", Reg("N2", "0")),
        }));

    [Fact]
    public void Detect_AgreeingTweaks_AreGroupedTogether_TheOddOneOut_Alone()
    {
        var c = Assert.Single(TweakConflictDetector.Detect(new[]
        {
            T("a", Reg("N", "1")),
            T("b", Reg("N", "1")),
            T("c", Reg("N", "0")),
        }));

        Assert.Equal(new[] { "0", "1" }, c.Values.Select(v => v.Value));
        Assert.Equal(new[] { "c" }, c.Values[0].TweakIds);          // 0 ← c
        Assert.Equal(new[] { "a", "b" }, c.Values[1].TweakIds);     // 1 ← a, b (ordered)
    }

    [Fact]
    public void Detect_SingleTweakContradictingItself_IsNotReported()
        => Assert.Empty(TweakConflictDetector.Detect(new[]
        {
            T("solo", Reg("N", "1"), Reg("N", "0")),   // catalog bug, not a SELECTION conflict → out of scope
        }));

    [Fact]
    public void Detect_EmptySelection_HasNoConflicts()
        => Assert.Empty(TweakConflictDetector.Detect(System.Array.Empty<Tweak>()));

    [Fact]
    public void Detect_OrdersConflicts_ByKindThenTarget()
    {
        var conflicts = TweakConflictDetector.Detect(new[]
        {
            T("a", Svc("Zsvc", "Disabled"), Reg("Bbb", "1"), Reg("Aaa", "1")),
            T("b", Svc("Zsvc", "Manual"), Reg("Bbb", "0"), Reg("Aaa", "0")),
        });

        Assert.Equal(
            new[] { ("Registre", @"HKLM\K\Aaa"), ("Registre", @"HKLM\K\Bbb"), ("Service", "Zsvc") },
            conflicts.Select(c => (c.Kind, c.Target)));
    }

    [Fact]
    public void ConflictingValue_TweakIdsLabel_JoinsForDisplay()
    {
        var c = Assert.Single(TweakConflictDetector.Detect(new[]
        {
            T("alpha", Reg("N", "1")),
            T("beta", Reg("N", "1")),
            T("gamma", Reg("N", "0")),
        }));

        Assert.Equal("alpha, beta", c.Values.Single(v => v.Value == "1").TweakIdsLabel);
    }
}
