using System.IO;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="ShellLauncher"/>'s allow-list — the gate that stops an elevated (requireAdministrator)
/// app from ShellExecute-ing whatever string a UI binding hands it. The honesty point: every link the UI
/// opens must be a known-safe scheme (http/https/ms-settings/mailto), and every "local" target a real
/// file/folder or a bare *.msc/*.cpl console — a "file:" URL or a bare "evil.exe" must be REFUSED, not
/// launched as admin. These exercise the pure predicates only; nothing is spawned.
/// </summary>
public class ShellLauncherTests
{
    [Theory]
    [InlineData("https://www.microsoft.com/")]
    [InlineData("http://example.com/path?q=1")]
    [InlineData("ms-settings:gaming-gamemode")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("mailto:support@aurum.app")]
    public void IsAllowedLink_AcceptsSafeAbsoluteSchemes(string url)
        => Assert.True(ShellLauncher.IsAllowedLink(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://host/file")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]   // file scheme: refused — this is the elevation sink we close
    [InlineData(@"C:\Windows\System32\cmd.exe")]          // bare path parses as a file: URI → also refused
    public void IsAllowedLink_RefusesEverythingElse(string? url)
        => Assert.False(ShellLauncher.IsAllowedLink(url));

    [Theory]
    [InlineData("devmgmt.msc")]
    [InlineData("DEVMGMT.MSC")]   // case-insensitive
    [InlineData("main.cpl")]
    public void IsAllowedLocal_AcceptsBareConsolesAndApplets(string target)
        => Assert.True(ShellLauncher.IsAllowedLocal(target));

    [Fact]
    public void IsAllowedLocal_AcceptsAnExistingDirectory()
        => Assert.True(ShellLauncher.IsAllowedLocal(Path.GetTempPath()));   // how OpenLocalAppData's folder passes

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("evil.exe")]                     // a bare exe is NOT a console/applet → refused
    [InlineData(@"..\..\evil.bat")]              // relative path that doesn't exist → refused
    [InlineData(@"C:\does\not\exist\nope.msc")]  // ".msc" but path-qualified + non-existent → refused (only BARE consoles pass)
    public void IsAllowedLocal_RefusesExecutablesAndMissingPaths(string? target)
        => Assert.False(ShellLauncher.IsAllowedLocal(target));
}
