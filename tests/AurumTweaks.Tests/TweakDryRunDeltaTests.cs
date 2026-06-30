using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure dry-run renderer behind the Tweaks page preview. It must describe the exact action the engine
/// dispatches, compare registry values with the same numeric rules as detection, and disclose readback limits instead
/// of inventing a current state for shell-only operations.
/// </summary>
public class TweakDryRunDeltaTests
{
    private static TweakOperation Reg(string? apply = "1", string? revert = "0", RegistryValueType type = RegistryValueType.DWord)
        => new() { Type = OperationType.Registry, Hive = "HKLM", Key = "K", Name = "N", Apply = apply, Revert = revert, ValueType = type };

    private static TweakOperation Svc(string apply = "Disabled", string revert = "Automatic")
        => new() { Type = OperationType.Service, ServiceName = "DiagTrack", StartupApply = apply, StartupRevert = revert };

    [Fact]
    public void RegistryWrite_UsesNumericComparison_ForAlreadyTarget()
    {
        var delta = TweakDryRunDelta.Build(Reg(apply: "0x1", revert: "0x0"), OperationCurrent.Present("1"));

        Assert.Equal("Registre", delta.Kind);
        Assert.Equal(@"HKLM\K\N", delta.Target);
        Assert.Equal("1", delta.Current);
        Assert.Equal("écrit 0x1 (DWORD)", delta.Apply);
        Assert.Equal(OperationDeltaStatus.AlreadyTarget, delta.Status);
        Assert.Equal("Déjà conforme", delta.StatusLabel);
    }

    [Fact]
    public void RegistryWrite_WhenCurrentDiffers_IsAConcreteDelta()
    {
        var delta = TweakDryRunDelta.Build(Reg(apply: "1", revert: "0"), OperationCurrent.Present("0"));

        Assert.Equal("écrit 1 (DWORD)", delta.Apply);
        Assert.Equal("écrit 0 (DWORD)", delta.Revert);
        Assert.Equal(OperationDeltaStatus.WillChange, delta.Status);
    }

    [Fact]
    public void RegistryDelete_ShowsDeleteAndMatchingRevertWrite()
    {
        var delta = TweakDryRunDelta.Build(Reg(apply: null, revert: "1"), OperationCurrent.Present("1"));

        Assert.Equal("supprime la valeur", delta.Apply);
        Assert.Equal("écrit 1 (DWORD)", delta.Revert);
        Assert.Equal(OperationDeltaStatus.WillChange, delta.Status);
    }

    [Fact]
    public void RegistryMissingOrUnreadable_DisclosesTheReadLimit()
    {
        var delta = TweakDryRunDelta.Build(Reg(apply: "1", revert: "0"), OperationCurrent.MissingOrUnreadable);

        Assert.Equal("absent(e) ou non lisible", delta.Current);
        Assert.Equal(OperationDeltaStatus.Unknown, delta.Status);
        Assert.Equal("Lecture non confirmée", delta.StatusLabel);
    }

    [Fact]
    public void ServiceDelta_UsesRawStartupTargets_AndComparesCaseInsensitively()
    {
        var same = TweakDryRunDelta.Build(Svc(apply: "Disabled", revert: "Automatic"), OperationCurrent.Present("disabled"));
        var changing = TweakDryRunDelta.Build(Svc(apply: "Disabled", revert: "Automatic"), OperationCurrent.Present("Automatic"));

        Assert.Equal("démarrage → Disabled", same.Apply);
        Assert.Equal("démarrage → Automatic", same.Revert);
        Assert.Equal(OperationDeltaStatus.AlreadyTarget, same.Status);
        Assert.Equal(OperationDeltaStatus.WillChange, changing.Status);
    }

    [Fact]
    public void AppxDelta_RendersTheExactShellCommands_FromTheSharedBuilder()
    {
        var op = new TweakOperation { Type = OperationType.AppX, AppxPackage = "Microsoft.XboxGamingOverlay" };

        var delta = TweakDryRunDelta.Build(op, OperationCurrent.NotRead);

        Assert.Equal("non relu par le moteur", delta.Current);
        Assert.Contains("powershell.exe -NoProfile -NonInteractive", delta.Apply);
        Assert.Contains("Remove-AppxPackage", delta.Apply);
        Assert.Contains("*Microsoft.XboxGamingOverlay*", delta.Apply);
        Assert.Contains("Add-AppxPackage", delta.Revert);
        Assert.Contains("*Microsoft.XboxGamingOverlay*", delta.Revert);
        Assert.Equal(OperationDeltaStatus.Unknown, delta.Status);
    }

    [Fact]
    public void EmptyRevertShellCommand_IsDisclosedAsNotAutomaticallyReversible()
    {
        var op = new TweakOperation { Type = OperationType.Bcdedit, Script = "/set foo 1" };

        var delta = TweakDryRunDelta.Build(op, OperationCurrent.NotRead);

        Assert.Equal("bcdedit.exe /set foo 1", delta.Apply);
        Assert.Equal(TweakOperationSummary.NoRevert, delta.Revert);
        Assert.True(delta.IsIrreversible);
    }
}
