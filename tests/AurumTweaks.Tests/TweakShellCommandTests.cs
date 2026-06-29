using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="TweakShellCommand.Build"/> — the pure <c>(fileName, args)</c> builder for the shell-based
/// tweak operations (PowerShell / Cmd / Bcdedit / AppX / ScheduledTask), extracted from
/// <c>TweakService.ExecuteAsync</c> so the contract is testable without spawning a single process.
///
/// The honesty surface is REVERSIBILITY: apply and revert must be genuine inverses, never the same command.
/// The headline is AppX debloat — 11 of these ship in the catalog today — where revert must actually
/// re-register the package that apply removed, or the app's "we removed it, we can put it back" promise is a
/// lie. Build constructs the string and runs nothing, so these assertions are pure and deterministic.
/// </summary>
public class TweakShellCommandTests
{
    private static TweakOperation AppX(string pkg) => new() { Type = OperationType.AppX, AppxPackage = pkg };

    // ---- AppX: the debloat reversibility promise ----------------------------

    [Fact]
    public void AppX_Apply_RemovesThePackage_AndDoesNotReAddIt()
    {
        var cmd = TweakShellCommand.Build(AppX("Microsoft.XboxGamingOverlay"), applying: true);

        Assert.NotNull(cmd);
        var (file, args) = cmd!.Value;
        Assert.Equal("powershell.exe", file);
        Assert.Contains("Remove-AppxPackage", args);
        Assert.Contains("*Microsoft.XboxGamingOverlay*", args);
        Assert.DoesNotContain("Add-AppxPackage", args);
    }

    [Fact]
    public void AppX_Revert_ReregistersTheSamePackage_AndDoesNotRemoveItAgain()
    {
        var cmd = TweakShellCommand.Build(AppX("Microsoft.XboxGamingOverlay"), applying: false);

        Assert.NotNull(cmd);
        var (file, args) = cmd!.Value;
        Assert.Equal("powershell.exe", file);
        Assert.Contains("Add-AppxPackage", args);
        Assert.Contains("-Register", args);
        Assert.Contains("AppxManifest.xml", args);
        Assert.Contains("-AllUsers", args);                       // required to find the de-provisioned package
        Assert.Contains("*Microsoft.XboxGamingOverlay*", args);
        Assert.DoesNotContain("Remove-AppxPackage", args);
    }

    [Fact]
    public void AppX_ApplyAndRevert_AreDifferentCommands_NotTheSameOneTwice()
    {
        var apply = TweakShellCommand.Build(AppX("Microsoft.549981C3F5F10"), applying: true);   // Cortana
        var revert = TweakShellCommand.Build(AppX("Microsoft.549981C3F5F10"), applying: false);
        Assert.NotEqual(apply!.Value.Arguments, revert!.Value.Arguments);
    }

    [Fact]
    public void AppX_WithNoPackage_BuildsNoCommand_RatherThanAMalformedOne()
        => Assert.Null(TweakShellCommand.Build(new TweakOperation { Type = OperationType.AppX }, applying: true));

    // ---- PowerShell / Cmd / Bcdedit: apply runs Script, revert runs RevertScript ----

    [Fact]
    public void PowerShell_Apply_RunsScript_Revert_RunsRevertScript()
    {
        var op = new TweakOperation { Type = OperationType.PowerShell, Script = "Set-Foo -On", RevertScript = "Set-Foo -Off" };

        var apply = TweakShellCommand.Build(op, applying: true);
        var revert = TweakShellCommand.Build(op, applying: false);

        Assert.Equal("powershell.exe", apply!.Value.FileName);
        Assert.Contains("-NonInteractive", apply.Value.Arguments);
        Assert.Contains("Set-Foo -On", apply.Value.Arguments);
        Assert.DoesNotContain("Set-Foo -Off", apply.Value.Arguments);

        Assert.Contains("Set-Foo -Off", revert!.Value.Arguments);
        Assert.DoesNotContain("Set-Foo -On", revert.Value.Arguments);
    }

    [Fact]
    public void Cmd_Apply_RunsScript_Revert_RunsRevertScript()
    {
        var op = new TweakOperation { Type = OperationType.Cmd, Script = "ipconfig /flushdns", RevertScript = "echo noop" };

        var apply = TweakShellCommand.Build(op, applying: true);
        var revert = TweakShellCommand.Build(op, applying: false);

        Assert.Equal("cmd.exe", apply!.Value.FileName);
        Assert.Equal("/c ipconfig /flushdns", apply.Value.Arguments);
        Assert.Equal("/c echo noop", revert!.Value.Arguments);
    }

    [Fact]
    public void Bcdedit_Apply_RunsScript_Revert_RunsRevertScript()
    {
        var op = new TweakOperation { Type = OperationType.Bcdedit, Script = "/set foo 1", RevertScript = "/deletevalue foo" };

        var apply = TweakShellCommand.Build(op, applying: true);
        var revert = TweakShellCommand.Build(op, applying: false);

        Assert.Equal("bcdedit.exe", apply!.Value.FileName);
        Assert.Equal("/set foo 1", apply.Value.Arguments);
        Assert.Equal("/deletevalue foo", revert!.Value.Arguments);
    }

    [Fact]
    public void Bcdedit_WithNoScript_YieldsEmptyArgs_NotAFabricatedCommand()
    {
        // Honesty: a bcdedit op with a null script must not become "bcdedit.exe" + some guessed argument.
        // Empty args make TweakService.RunShell short-circuit to a no-op false, which is the honest outcome.
        var apply = TweakShellCommand.Build(new TweakOperation { Type = OperationType.Bcdedit }, applying: true);
        Assert.Equal("bcdedit.exe", apply!.Value.FileName);
        Assert.Equal(string.Empty, apply.Value.Arguments);
    }

    // ---- ScheduledTask: apply disables, revert enables ----------------------
    // No ScheduledTask op ships in the catalog today, but the code path exists. Pin the inversion so a future
    // task-disabling tweak can't ship with apply/revert swapped (which would re-enable the task on "apply").

    [Fact]
    public void ScheduledTask_Apply_Disables_Revert_Enables()
    {
        var op = new TweakOperation { Type = OperationType.ScheduledTask, TaskPath = @"\Microsoft\Windows\Foo\Bar" };

        var apply = TweakShellCommand.Build(op, applying: true);
        var revert = TweakShellCommand.Build(op, applying: false);

        Assert.Equal("schtasks.exe", apply!.Value.FileName);
        Assert.Contains(@"/TN ""\Microsoft\Windows\Foo\Bar""", apply.Value.Arguments);
        Assert.Contains("/Disable", apply.Value.Arguments);
        Assert.DoesNotContain("/Enable", apply.Value.Arguments);

        Assert.Contains("/Enable", revert!.Value.Arguments);
        Assert.DoesNotContain("/Disable", revert.Value.Arguments);
    }

    [Fact]
    public void ScheduledTask_WithNoTaskPath_BuildsNoCommand()
        => Assert.Null(TweakShellCommand.Build(new TweakOperation { Type = OperationType.ScheduledTask }, applying: true));

    // ---- Non-shell ops are not this builder's job (TweakService handles them directly) ----

    [Theory]
    [InlineData(OperationType.Registry)]
    [InlineData(OperationType.Service)]
    [InlineData(OperationType.File)]
    public void NonShellOps_BuildNoShellCommand(OperationType type)
        => Assert.Null(TweakShellCommand.Build(new TweakOperation { Type = type }, applying: true));
}
