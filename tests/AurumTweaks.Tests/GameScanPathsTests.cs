using System;
using System.IO;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the drive-aware launcher path composition behind <see cref="GameDetectionService"/>. Previously the
/// Riot / Battle.net / EA / Ubisoft scanners hardcoded "C:\…", so a PC with Windows or Program Files on
/// another drive silently detected none of those games. These tests prove every default path is now built
/// from the resolved <see cref="GameScanRoots"/> — so a relocated/D: install is probed at the RIGHT drive,
/// never a literal C: — without touching the real filesystem.
/// </summary>
public class GameScanPathsTests
{
    // Deliberately off the C: drive so any lingering hardcoded "C:\" would jump out.
    private static readonly GameScanRoots Relocated =
        new(ProgramFilesX86: @"D:\Program Files (x86)",
            ProgramFiles: @"D:\Program Files",
            ProgramData: @"D:\ProgramData",
            SystemDrive: @"E:\");

    [Fact]
    public void BattleNet_LauncherAndGames_AreUnderProgramFilesX86()
    {
        Assert.Equal(@"D:\Program Files (x86)\Battle.net", GameScanPaths.BattleNetLauncherDir(Relocated));
        Assert.Equal(@"D:\Program Files (x86)\Overwatch", GameScanPaths.BattleNetGameDir(Relocated, "Overwatch"));
    }

    [Fact]
    public void Ea_GamesDir_IsUnderProgramFiles64()
        => Assert.Equal(@"D:\Program Files\EA Games", GameScanPaths.EaGamesDir(Relocated));

    [Fact]
    public void Ubisoft_GamesDir_IsUnderProgramFilesX86()
        => Assert.Equal(@"D:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
                        GameScanPaths.UbisoftGamesDir(Relocated));

    [Fact]
    public void Epic_ManifestsDir_IsUnderProgramData()
        => Assert.Equal(@"D:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests",
                        GameScanPaths.EpicManifestsDir(Relocated));

    [Fact]
    public void Riot_GameDirs_FollowTheSystemDrive()
    {
        var dirs = GameScanPaths.RiotGameDirs(Relocated);
        Assert.Collection(dirs,
            p => Assert.Equal(@"E:\Riot Games\League of Legends", p),
            p => Assert.Equal(@"E:\Riot Games\VALORANT", p));
    }

    [Fact]
    public void Xbox_GamesDirs_FollowEachFixedDrive()
    {
        // The Xbox app installs Game Pass titles to {drive}\XboxGames on ANY user-chosen fixed drive, so the
        // scanner must probe every fixed drive — not one hardcoded C:\ root.
        var dirs = GameScanPaths.XboxGamesDirs(new[] { @"C:\", @"D:\", @"E:\" });
        Assert.Collection(dirs,
            p => Assert.Equal(@"C:\XboxGames", p),
            p => Assert.Equal(@"D:\XboxGames", p),
            p => Assert.Equal(@"E:\XboxGames", p));
    }

    [Fact]
    public void Xbox_GamesDirs_AreFullyQualified_AndEmptyWhenNoDrives()
    {
        Assert.Empty(GameScanPaths.XboxGamesDirs(Array.Empty<string>()));
        Assert.All(GameScanPaths.XboxGamesDirs(new[] { @"C:\", @"D:\" }),
                   p => Assert.True(Path.IsPathFullyQualified(p), $"not rooted: {p}"));
    }

    [Fact]
    public void NoComposedPath_RetainsAHardcodedCDrive_WhenRootsAreElsewhere()
    {
        var all = new[]
        {
            GameScanPaths.BattleNetLauncherDir(Relocated),
            GameScanPaths.BattleNetGameDir(Relocated, "Diablo IV"),
            GameScanPaths.EaGamesDir(Relocated),
            GameScanPaths.UbisoftGamesDir(Relocated),
            GameScanPaths.EpicManifestsDir(Relocated),
        }.Concat(GameScanPaths.RiotGameDirs(Relocated));

        Assert.All(all, p => Assert.DoesNotContain(@"C:\", p, StringComparison.OrdinalIgnoreCase));
        Assert.All(all, p => Assert.True(Path.IsPathFullyQualified(p), $"not rooted: {p}"));
    }

    // ---- FromEnvironment: resolves real folders and avoids the Path.Combine("C:", …) drive-relative trap ----

    [Fact]
    public void FromEnvironment_ResolvesRealProgramFiles_NotHardcoded()
    {
        var r = GameScanRoots.FromEnvironment();
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), r.ProgramFilesX86);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), r.ProgramFiles);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), r.ProgramData);
    }

    [Fact]
    public void FromEnvironment_SystemDriveIsRooted_SoRiotPathsAreFullyQualified()
    {
        var r = GameScanRoots.FromEnvironment();
        // Must end in a separator: Path.Combine("C:", "x") => drive-relative "C:x"; "C:\" => rooted "C:\x".
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), r.SystemDrive);
        Assert.All(GameScanPaths.RiotGameDirs(r), p => Assert.True(Path.IsPathFullyQualified(p), $"not rooted: {p}"));
    }
}
