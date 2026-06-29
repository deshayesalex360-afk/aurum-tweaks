using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="StartupCommand.Parse"/> — the split of a raw registry <c>Run</c> command line into
/// (executable, arguments) behind the Démarrage page. The load-bearing case is the spaced install path: a naive
/// "split on the first space" mangles every program under <c>C:\Program Files\</c>, so a quoted path is honoured
/// whole and a bare path is cut after its first <c>.exe</c>. Arguments are read off the command, never invented.
/// </summary>
public class StartupCommandTests
{
    [Fact]
    public void Parse_QuotedPathWithArgs_SplitsAtClosingQuote()
    {
        var (exe, args) = StartupCommand.Parse("\"C:\\Program Files\\App\\app.exe\" --minimized -x");
        Assert.Equal("C:\\Program Files\\App\\app.exe", exe);
        Assert.Equal("--minimized -x", args);
    }

    [Fact]
    public void Parse_QuotedPathNoArgs()
    {
        var (exe, args) = StartupCommand.Parse("\"C:\\Tools\\thing.exe\"");
        Assert.Equal("C:\\Tools\\thing.exe", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void Parse_BareSpacedPath_SplitsAfterExe()
    {
        var (exe, args) = StartupCommand.Parse("C:\\Program Files\\App\\app.exe -minimized");
        Assert.Equal("C:\\Program Files\\App\\app.exe", exe);
        Assert.Equal("-minimized", args);
    }

    [Fact]
    public void Parse_Rundll32Style_KeepsExeAndArgs()
    {
        var (exe, args) = StartupCommand.Parse("C:\\Windows\\System32\\rundll32.exe shell32.dll,Control_RunDLL");
        Assert.Equal("C:\\Windows\\System32\\rundll32.exe", exe);
        Assert.Equal("shell32.dll,Control_RunDLL", args);
    }

    [Fact]
    public void Parse_BareExe_NoArgs()
    {
        var (exe, args) = StartupCommand.Parse("OneDrive.exe");
        Assert.Equal("OneDrive.exe", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void Parse_NoExtensionToken_FirstTokenIsTarget()
    {
        var (exe, args) = StartupCommand.Parse("SomeProgram /background");
        Assert.Equal("SomeProgram", exe);
        Assert.Equal("/background", args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyOrNull_ReturnsEmpties(string? raw)
    {
        var (exe, args) = StartupCommand.Parse(raw);
        Assert.Equal("", exe);
        Assert.Equal("", args);
    }

    [Fact]
    public void Parse_TrimsSurroundingWhitespace()
    {
        var (exe, args) = StartupCommand.Parse("   \"C:\\a\\b.exe\"  -k  ");
        Assert.Equal("C:\\a\\b.exe", exe);
        Assert.Equal("-k", args);
    }

    [Fact]
    public void Parse_ExeMatchIsCaseInsensitive()
    {
        var (exe, args) = StartupCommand.Parse("C:\\X\\Tool.EXE /silent");
        Assert.Equal("C:\\X\\Tool.EXE", exe);
        Assert.Equal("/silent", args);
    }
}
