using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the Steam KeyValues parsing (<see cref="SteamVdf"/>) extracted from the game scanner. Steam is the
/// most-used launcher, so its libraryfolders.vdf / appmanifest_*.acf parse is the highest-impact one to get
/// right — yet it was previously an inline "naive parse" with no tests. These prove the two things the old
/// split-on-quote code got wrong: a Windows path's <c>\\</c> escaping is undone exactly once (so a D:/relocated
/// library resolves to the real drive), and an empty <c>"name" ""</c> yields a null name (the scanner skips it)
/// instead of a bogus whitespace game title.
/// </summary>
public class SteamVdfTests
{
    [Fact]
    public void TryParsePair_ReadsKeyAndValue()
    {
        Assert.True(SteamVdf.TryParsePair(@"        ""path""        ""D:\\Games""", out var k, out var v));
        Assert.Equal("path", k);
        Assert.Equal(@"D:\Games", v);   // \\ unescaped to a single separator — the real on-disk path
    }

    [Fact]
    public void TryParsePair_UnescapesBackslashesAndQuotes()
    {
        // \" must NOT be mistaken for the closing quote, and \\ collapses to one backslash.
        Assert.True(SteamVdf.TryParsePair(@"""name"" ""Tom \""X\"" \\ Game""", out _, out var v));
        Assert.Equal(@"Tom ""X"" \ Game", v);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"appid\"")]            // key only, no value
    public void TryParsePair_RejectsNonPairLines(string line)
        => Assert.False(SteamVdf.TryParsePair(line, out _, out _));

    private const string Vdf = @"""libraryfolders""
{
    ""0""
    {
        ""path""        ""C:\\Program Files (x86)\\Steam""
        ""label""       """"
    }
    ""1""
    {
        ""path""        ""D:\\SteamLibrary""
    }
}";

    [Fact]
    public void LibraryPaths_ReturnsEveryPath_DriveCorrect_SkippingOtherKeys()
    {
        var paths = SteamVdf.LibraryPaths(Vdf).ToArray();
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, paths);
    }

    [Fact]
    public void LibraryPaths_SkipsBlankPathValues()
    {
        // A degenerate file with an empty path must not yield "" (which would compose a bogus relative dir).
        Assert.Empty(SteamVdf.LibraryPaths("\t\"path\"\t\"\""));
    }

    [Fact]
    public void ParseAppManifest_ExtractsNameAndInstallDir()
    {
        const string acf = @"""AppState""
{
    ""appid""       ""570""
    ""name""        ""Dota 2""
    ""installdir""  ""dota 2 beta""
    ""StateFlags""  ""4""
}";
        var (name, installDir) = SteamVdf.ParseAppManifest(acf);
        Assert.Equal("Dota 2", name);
        Assert.Equal("dota 2 beta", installDir);
    }

    [Fact]
    public void ParseAppManifest_EmptyName_IsNull_SoTheScannerSkipsIt()
    {
        // The load-bearing bug fix: the old parser returned the inter-quote whitespace as the game name.
        const string acf = @"""AppState""
{
    ""appid""       ""730""
    ""name""        """"
    ""installdir""  ""Counter-Strike Global Offensive""
}";
        var (name, installDir) = SteamVdf.ParseAppManifest(acf);
        Assert.Null(name);
        Assert.Equal("Counter-Strike Global Offensive", installDir);
    }

    [Fact]
    public void ParseAppManifest_MissingInstallDir_IsNull()
    {
        var (name, installDir) = SteamVdf.ParseAppManifest("\"AppState\"\n{\n\"name\" \"Portal 2\"\n}");
        Assert.Equal("Portal 2", name);
        Assert.Null(installDir);
    }
}
