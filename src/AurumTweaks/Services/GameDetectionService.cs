using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AurumTweaks.Services;

/// <summary>
/// The real install roots a game scan probes, resolved from the environment rather than hardcoded to C:.
/// Extracted (the pure-core pattern) so the launcher path composition can be pinned by tests proving a
/// Windows-on-D: / relocated-Program-Files box is scanned at the RIGHT drive — never a literal "C:\".
/// </summary>
public sealed record GameScanRoots(string ProgramFilesX86, string ProgramFiles, string ProgramData, string SystemDrive)
{
    public static GameScanRoots FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        NormalizeDriveRoot(Environment.GetEnvironmentVariable("SystemDrive")));

    /// <summary>
    /// A drive root must end with a separator: <c>Path.Combine("C:", "x")</c> yields the drive-relative
    /// "C:x" (a real bug), whereas <c>Path.Combine("C:\\", "x")</c> yields the rooted "C:\x".
    /// </summary>
    private static string NormalizeDriveRoot(string? systemDrive)
    {
        if (string.IsNullOrWhiteSpace(systemDrive))
            return @"C:\";
        return systemDrive.EndsWith(Path.DirectorySeparatorChar) ? systemDrive : systemDrive + Path.DirectorySeparatorChar;
    }
}

/// <summary>
/// Pure launcher-path composition over <see cref="GameScanRoots"/>. The honesty/robustness core of game
/// detection: every default install path is built from the resolved roots so detection follows the user's
/// real drive layout instead of silently missing everything on a non-C: install.
/// </summary>
public static class GameScanPaths
{
    public static string BattleNetLauncherDir(GameScanRoots r) => Path.Combine(r.ProgramFilesX86, "Battle.net");
    public static string BattleNetGameDir(GameScanRoots r, string gameName) => Path.Combine(r.ProgramFilesX86, gameName);
    public static string EaGamesDir(GameScanRoots r) => Path.Combine(r.ProgramFiles, "EA Games");
    public static string UbisoftGamesDir(GameScanRoots r) => Path.Combine(r.ProgramFilesX86, "Ubisoft", "Ubisoft Game Launcher", "games");
    public static string EpicManifestsDir(GameScanRoots r) => Path.Combine(r.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static IReadOnlyList<string> RiotGameDirs(GameScanRoots r) => new[]
    {
        Path.Combine(r.SystemDrive, "Riot Games", "League of Legends"),
        Path.Combine(r.SystemDrive, "Riot Games", "VALORANT"),
    };

    /// <summary>
    /// The Xbox app / Game Pass install roots. Since ~2021 the Xbox app installs to
    /// {drive}\XboxGames\{Game}\Content on a user-chosen fixed drive (ANY fixed drive, not a single
    /// hardcoded path) — so detection enumerates XboxGames on every fixed drive. Pure composition: the live
    /// fixed-drive list is supplied by the caller so this stays testable without touching the filesystem.
    /// </summary>
    public static IReadOnlyList<string> XboxGamesDirs(IEnumerable<string> fixedDriveRoots) =>
        fixedDriveRoots.Select(d => Path.Combine(d, "XboxGames")).ToList();
}

/// <summary>
/// Minimal pure parser for the slice of Valve's KeyValues text (the <c>libraryfolders.vdf</c> and
/// <c>appmanifest_*.acf</c> files) the Steam scan reads — flat <c>"key" "value"</c> lines. Extracted from
/// <see cref="GameDetectionService"/> (the pure-core pattern) so the fragile string handling, which the old
/// inline "naive parse" got subtly wrong, is unit-testable without a real Steam install. It is NOT a general
/// VDF parser: it ignores nesting/braces and only reads the flat keys the scan needs (path / name / installdir).
/// </summary>
public static class SteamVdf
{
    /// <summary>
    /// Parse one line as a quoted <c>"key" "value"</c> pair. Returns false for brace, blank, or key-only lines.
    /// Unescapes the two sequences Valve actually emits in these files: <c>\\</c> → <c>\</c> and <c>\"</c> → <c>"</c>
    /// — so a Windows path stored as <c>"D:\\Games"</c> comes back as <c>D:\Games</c>, and the closing quote is
    /// found correctly even past an escaped one.
    /// </summary>
    public static bool TryParsePair(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        if (string.IsNullOrEmpty(line)) return false;
        if (!TryReadQuoted(line, 0, out key, out int afterKey)) return false;
        return TryReadQuoted(line, afterKey, out value, out _);   // key-only line ⇒ no value ⇒ false
    }

    /// <summary>Every non-blank <c>"path"</c> value in a libraryfolders.vdf, unescaped. Drive-correct: the path
    /// comes straight from the file, so a D:/relocated Steam library is honoured as written.</summary>
    public static IEnumerable<string> LibraryPaths(string vdfContent)
    {
        foreach (var line in SplitLines(vdfContent))
            if (TryParsePair(line, out var k, out var v)
                && k.Equals("path", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(v))
                yield return v;
    }

    /// <summary>
    /// The (name, installDir) of an appmanifest_*.acf — either is null when its key is absent or blank. The
    /// blank-guard is the bug the old parser had: an empty <c>"name" ""</c> used to yield the inter-quote
    /// whitespace as a bogus game name; here it is null, so the scanner correctly skips the entry.
    /// </summary>
    public static (string? Name, string? InstallDir) ParseAppManifest(string acfContent)
    {
        string? name = null, installDir = null;
        foreach (var line in SplitLines(acfContent))
        {
            if (!TryParsePair(line, out var k, out var v)) continue;
            if (k.Equals("name", StringComparison.OrdinalIgnoreCase)) name = NullIfBlank(v);
            else if (k.Equals("installdir", StringComparison.OrdinalIgnoreCase)) installDir = NullIfBlank(v);
        }
        return (name, installDir);
    }

    /// <summary>Read the next double-quoted token starting at/after <paramref name="start"/>, unescaping \\ and \".</summary>
    private static bool TryReadQuoted(string s, int start, out string content, out int afterIndex)
    {
        content = string.Empty;
        afterIndex = start;

        int i = start;
        while (i < s.Length && s[i] != '"') i++;
        if (i >= s.Length) return false;   // no opening quote
        i++;                               // step past the opening quote

        var sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && (s[i + 1] == '\\' || s[i + 1] == '"'))
            {
                sb.Append(s[i + 1]);       // \\ → \   ,   \" → "
                i += 2;
                continue;
            }
            if (c == '"')
            {
                content = sb.ToString();
                afterIndex = i + 1;
                return true;               // closing quote
            }
            sb.Append(c);
            i++;
        }
        return false;                      // unterminated
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        if (string.IsNullOrEmpty(content)) yield break;
        foreach (var line in content.Split('\n'))
            yield return line;
    }

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>
/// Detects installed games across Steam, Epic, Battle.net, Riot, EA, Ubisoft, GOG, Xbox / Game Pass.
/// </summary>
public sealed class GameDetectionService : IGameDetectionService
{
    public Task<IReadOnlyList<DetectedGame>> ScanAsync() => Task.Run(() =>
    {
        var games = new List<DetectedGame>();
        var roots = GameScanRoots.FromEnvironment();
        TryScanSteam(games);
        TryScanEpic(games, roots);
        TryScanRiot(games, roots);
        TryScanBattleNet(games, roots);
        TryScanEa(games, roots);
        TryScanUbisoft(games, roots);
        TryScanGog(games);
        TryScanXbox(games);
        return (IReadOnlyList<DetectedGame>)games;
    });

    private static void TryScanSteam(List<DetectedGame> games)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                          ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var steamPath = key?.GetValue("InstallPath") as string;
            if (steamPath is null) return;
            var libraryFolders = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFolders)) return;

            // The install dir is always a library; SteamVdf.LibraryPaths adds the user's extra (drive-correct) ones.
            var libs = new List<string> { Path.Combine(steamPath, "steamapps") };
            foreach (var path in SteamVdf.LibraryPaths(File.ReadAllText(libraryFolders)))
            {
                var p = Path.Combine(path, "steamapps");
                if (Directory.Exists(p)) libs.Add(p);
            }
            foreach (var lib in libs.Distinct())
            {
                foreach (var manifest in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
                {
                    var (name, installDir) = SteamVdf.ParseAppManifest(File.ReadAllText(manifest));
                    if (name is null) continue;
                    games.Add(new DetectedGame
                    {
                        Name = name,
                        Platform = "Steam",
                        InstallDirectory = installDir is null ? string.Empty : Path.Combine(lib, "common", installDir)
                    });
                }
            }
        }
        catch { /* swallow */ }
    }

    private static void TryScanEpic(List<DetectedGame> games, GameScanRoots roots)
    {
        try
        {
            var dat = GameScanPaths.EpicManifestsDir(roots);
            if (!Directory.Exists(dat)) return;
            foreach (var manifest in Directory.EnumerateFiles(dat, "*.item"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                    var root = doc.RootElement;
                    var name = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                    var location = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                    if (name is null) continue;
                    games.Add(new DetectedGame
                    {
                        Name = name,
                        Platform = "Epic",
                        InstallDirectory = location ?? string.Empty
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    private static void TryScanRiot(List<DetectedGame> games, GameScanRoots roots)
    {
        foreach (var p in GameScanPaths.RiotGameDirs(roots))
        {
            if (Directory.Exists(p))
            {
                var name = Path.GetFileName(p);
                games.Add(new DetectedGame
                {
                    Name = name,
                    Platform = "Riot",
                    InstallDirectory = p,
                    HasAntiCheat = true,
                    AntiCheatName = "Vanguard"
                });
            }
        }
    }

    private static void TryScanBattleNet(List<DetectedGame> games, GameScanRoots roots)
    {
        if (!Directory.Exists(GameScanPaths.BattleNetLauncherDir(roots))) return;
        var commonGames = new[] { "Overwatch", "Call of Duty", "World of Warcraft", "Diablo IV", "Hearthstone", "StarCraft II" };
        foreach (var g in commonGames)
        {
            var p = GameScanPaths.BattleNetGameDir(roots, g);
            if (Directory.Exists(p))
            {
                games.Add(new DetectedGame { Name = g, Platform = "Battle.net", InstallDirectory = p });
            }
        }
    }

    private static void TryScanEa(List<DetectedGame> games, GameScanRoots roots)
    {
        var p = GameScanPaths.EaGamesDir(roots);
        if (!Directory.Exists(p)) return;
        foreach (var d in Directory.EnumerateDirectories(p))
        {
            games.Add(new DetectedGame { Name = Path.GetFileName(d), Platform = "EA", InstallDirectory = d });
        }
    }

    private static void TryScanUbisoft(List<DetectedGame> games, GameScanRoots roots)
    {
        var p = GameScanPaths.UbisoftGamesDir(roots);
        if (!Directory.Exists(p)) return;
        foreach (var d in Directory.EnumerateDirectories(p))
        {
            games.Add(new DetectedGame { Name = Path.GetFileName(d), Platform = "Ubisoft", InstallDirectory = d });
        }
    }

    private static void TryScanGog(List<DetectedGame> games)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var g = key.OpenSubKey(sub);
                var name = g?.GetValue("gameName") as string;
                var path = g?.GetValue("path") as string;
                if (name is null) continue;
                games.Add(new DetectedGame { Name = name, Platform = "GOG", InstallDirectory = path ?? string.Empty });
            }
        }
        catch { }
    }

    private static void TryScanXbox(List<DetectedGame> games)
    {
        foreach (var xboxRoot in GameScanPaths.XboxGamesDirs(FixedDriveRoots()))
        {
            try
            {
                if (!Directory.Exists(xboxRoot)) continue;
                foreach (var d in Directory.EnumerateDirectories(xboxRoot))
                {
                    // A real Game Pass install puts the playable game under a "Content" child; gating on it
                    // keeps stray/empty XboxGames subfolders (uninstall leftovers) from showing as games.
                    var content = Path.Combine(d, "Content");
                    if (!Directory.Exists(content)) continue;
                    games.Add(new DetectedGame
                    {
                        Name = Path.GetFileName(d),
                        Platform = "Xbox",
                        InstallDirectory = content
                    });
                }
            }
            catch { /* a single unreadable drive must not sink the whole scan */ }
        }
    }

    private static IReadOnlyList<string> FixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.Name)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}
