using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AurumTweaks.Services;

/// <summary>Where a startup program is registered — the two scopes Windows honours at logon.</summary>
public enum StartupScope
{
    /// <summary>HKCU Run / the per-user Startup folder — affects only the signed-in user.</summary>
    CurrentUser,

    /// <summary>HKLM Run / the all-users Startup folder — affects every account on the machine.</summary>
    AllUsers
}

/// <summary>How a startup program is launched — the mechanism dictates how we (reversibly) disable it.</summary>
public enum StartupSource
{
    /// <summary>A value under <c>…\CurrentVersion\Run</c>. Disabled by moving the value to Aurum's backup key.</summary>
    RegistryRun,

    /// <summary>A shortcut/script in a Startup folder. Disabled by moving the file into an <c>AurumDisabled</c> subfolder.</summary>
    StartupFolder
}

/// <summary>
/// A coarse, deliberately-heuristic bucket for a startup program, used only to colour the UI and word the
/// "safe to disable?" guidance. It is never authoritative: an unrecognised program is honestly <see cref="Other"/>,
/// and the only bucket we refuse to call safe-to-disable is <see cref="SecurityOrDriver"/>.
/// </summary>
public enum StartupCategory
{
    Launcher,
    Communication,
    Cloud,
    Media,
    Updater,
    Peripheral,
    SecurityOrDriver,
    Other
}

/// <summary>
/// One real program that runs at logon. Every field mirrors something actually present on disk or in the
/// registry — nothing here is synthesised. <see cref="RawName"/> is the exact registry value name or shortcut
/// file name the service needs to move the entry between its live location and Aurum's reversible backup;
/// <see cref="Name"/> is the friendly form shown to the user.
/// </summary>
public sealed record StartupEntry(
    string Name,
    string RawName,
    string Command,
    string ExecutablePath,
    string Arguments,
    StartupScope Scope,
    StartupSource Source,
    StartupCategory Category,
    bool IsEnabled)
{
    /// <summary>Heuristic — a driver/security helper is the one thing we won't paint as safe to switch off.</summary>
    public bool SafeToDisable => StartupClassifier.IsSafeToDisable(Category);

    public string ScopeDisplay => Scope == StartupScope.AllUsers ? "Tous les utilisateurs" : "Utilisateur actuel";
    public string SourceDisplay => Source == StartupSource.RegistryRun ? "Registre (Run)" : "Dossier Démarrage";
    public string CategoryDisplay => StartupClassifier.Describe(Category);
    public string StateDisplay => IsEnabled ? "Actif" : "Désactivé";
    public string Advice => StartupClassifier.Advice(Category);

    /// <summary>The action label the toggle button should show, given the current state.</summary>
    public string ToggleLabel => IsEnabled ? "Désactiver" : "Réactiver";
}

/// <summary>
/// Splits a raw <c>Run</c> command line into (executable, arguments) — pure, so the parsing is pinned by tests.
/// The honesty point: registry Run values are authored in two shapes — a quoted path (<c>"C:\…\app.exe" -flag</c>)
/// or a bare path (<c>C:\Windows\System32\rundll32.exe shell32.dll,Func</c>) — and a naive "split on first space"
/// mangles every program installed under <c>C:\Program Files\</c>. We honour the quotes when present, otherwise
/// cut after the first <c>.exe</c> token so spaced install paths survive, and never invent arguments.
/// </summary>
public static class StartupCommand
{
    public static (string Executable, string Arguments) Parse(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return (string.Empty, string.Empty);

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            if (end > 0)
                return (s.Substring(1, end - 1), s[(end + 1)..].Trim());
            return (s.Trim('"'), string.Empty);   // unbalanced quote — take the rest as the path
        }

        const string ext = ".exe";
        int idx = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            int cut = idx + ext.Length;
            return (s[..cut], s[cut..].Trim());
        }

        // No quotes and no ".exe" (e.g. a bare "OneDrive" or a script) — first whitespace-delimited token is the target.
        int space = s.IndexOf(' ');
        return space < 0 ? (s, string.Empty) : (s[..space], s[(space + 1)..].Trim());
    }
}

/// <summary>
/// Maps a startup program to a coarse category and plain-language guidance — pure, table-driven, and tested.
/// This is the honesty-bearing core of the page: the category is what tells the user "launcher, safe to switch
/// off" vs. "pilote, à conserver". Matching is a case-insensitive substring scan over "name + executable", in a
/// deliberate priority order (drivers/security first, so a vendor audio/GPU helper is never mislabelled "safe").
/// Unknown programs fall through to <see cref="StartupCategory.Other"/> — we guide, we never pretend certainty.
/// </summary>
public static class StartupClassifier
{
    // First token found wins, so order encodes priority. Tokens are matched against a lower-cased "name exe" string.
    private static readonly (string Token, StartupCategory Category)[] Signatures =
    {
        // Drivers / security first — these must never be bucketed into a "safe to disable" category by accident.
        ("nvcontainer", StartupCategory.SecurityOrDriver),
        ("nvidia", StartupCategory.SecurityOrDriver),
        ("rtkaud", StartupCategory.SecurityOrDriver),
        ("realtek", StartupCategory.SecurityOrDriver),
        ("securityhealth", StartupCategory.SecurityOrDriver),
        ("defender", StartupCategory.SecurityOrDriver),
        ("windefend", StartupCategory.SecurityOrDriver),
        ("igfxtray", StartupCategory.SecurityOrDriver),
        ("synaptics", StartupCategory.SecurityOrDriver),

        // Updaters before everything else app-ish — "…Update"/"…Updater" is the classic disable target.
        ("googleupdate", StartupCategory.Updater),
        ("edgeupdate", StartupCategory.Updater),
        ("adobearm", StartupCategory.Updater),
        ("acrotray", StartupCategory.Updater),
        ("jusched", StartupCategory.Updater),
        ("update", StartupCategory.Updater),

        // Peripheral / RGB / vendor configurators.
        ("icue", StartupCategory.Peripheral),
        ("synapse", StartupCategory.Peripheral),
        ("razer", StartupCategory.Peripheral),
        ("lghub", StartupCategory.Peripheral),
        ("logi", StartupCategory.Peripheral),
        ("armoury", StartupCategory.Peripheral),
        ("nzxt", StartupCategory.Peripheral),
        ("openrgb", StartupCategory.Peripheral),
        ("steelseries", StartupCategory.Peripheral),

        // Game launchers.
        ("steam", StartupCategory.Launcher),
        ("epicgames", StartupCategory.Launcher),
        ("battle.net", StartupCategory.Launcher),
        ("battlenet", StartupCategory.Launcher),
        ("eadesktop", StartupCategory.Launcher),
        ("origin", StartupCategory.Launcher),
        ("ubisoft", StartupCategory.Launcher),
        ("uplay", StartupCategory.Launcher),
        ("galaxyclient", StartupCategory.Launcher),
        ("riot", StartupCategory.Launcher),

        // Communication.
        ("discord", StartupCategory.Communication),
        ("teams", StartupCategory.Communication),
        ("slack", StartupCategory.Communication),
        ("skype", StartupCategory.Communication),
        ("zoom", StartupCategory.Communication),
        ("telegram", StartupCategory.Communication),

        // Cloud sync.
        ("onedrive", StartupCategory.Cloud),
        ("dropbox", StartupCategory.Cloud),
        ("googledrive", StartupCategory.Cloud),
        ("icloud", StartupCategory.Cloud),
        ("megasync", StartupCategory.Cloud),

        // Media.
        ("spotify", StartupCategory.Media),
        ("itunes", StartupCategory.Media),
        ("quicktime", StartupCategory.Media),
        ("vlc", StartupCategory.Media),
    };

    public static StartupCategory Classify(string? name, string? executablePath)
    {
        var hay = ((name ?? string.Empty) + " " + (executablePath ?? string.Empty)).ToLowerInvariant();
        if (hay.Trim().Length == 0) return StartupCategory.Other;
        foreach (var (token, category) in Signatures)
            if (hay.Contains(token, StringComparison.Ordinal))
                return category;
        return StartupCategory.Other;
    }

    /// <summary>Everything is fair game to disable except a security tool or device driver.</summary>
    public static bool IsSafeToDisable(StartupCategory category) => category is not StartupCategory.SecurityOrDriver;

    public static string Describe(StartupCategory category) => category switch
    {
        StartupCategory.Launcher => "Launcher de jeux",
        StartupCategory.Communication => "Communication",
        StartupCategory.Cloud => "Synchronisation cloud",
        StartupCategory.Media => "Multimédia",
        StartupCategory.Updater => "Mise à jour automatique",
        StartupCategory.Peripheral => "Logiciel de périphérique (RGB, souris, audio)",
        StartupCategory.SecurityOrDriver => "Sécurité / pilote",
        _ => "Autre"
    };

    public static string Advice(StartupCategory category) => category switch
    {
        StartupCategory.SecurityOrDriver => "À conserver : sécurité ou pilote — le désactiver peut nuire au système.",
        StartupCategory.Updater => "Désactivable : pense à mettre à jour le logiciel manuellement de temps en temps.",
        StartupCategory.Launcher => "Désactivable : le launcher s'ouvrira quand tu lanceras le jeu.",
        StartupCategory.Communication => "Désactivable : ouvre l'app à la main quand tu en as besoin.",
        StartupCategory.Cloud => "Désactivable, mais la synchro ne tournera plus en arrière-plan au démarrage.",
        StartupCategory.Media => "Désactivable sans risque.",
        StartupCategory.Peripheral => "Souvent désactivable, mais certains réglages RGB/macros ne s'appliquent qu'app ouverte.",
        _ => "Impact inconnu : désactive si tu ne reconnais pas ce programme, réactive si quelque chose manque."
    };
}

/// <summary>
/// Enumerates and reversibly toggles the programs Windows launches at logon — the "gestionnaire de démarrage"
/// every PC-optimizer ships. Honesty &amp; reversibility are the whole point:
/// <list type="bullet">
/// <item>It reads the four real sources (HKCU/HKLM <c>Run</c> + the per-user and all-users Startup folders); a
///   program that isn't there is never invented.</item>
/// <item>Disabling never deletes anything. A registry entry is <b>moved</b> to <c>HK*\Software\AurumTweaks\
///   StartupDisabled\Run</c> with its exact value kind preserved (so a <c>REG_EXPAND_SZ</c> like
///   <c>%ProgramFiles%\…</c> round-trips byte-for-byte); a folder shortcut is moved into an <c>AurumDisabled</c>
///   subfolder. Re-enabling just moves it back — a genuine inverse, same as the tweak engine's promise.</item>
/// </list>
/// The decision logic (command parsing, categorisation) lives in the pure cores above and is unit-tested; this
/// class is the thin registry/file I/O glue around them.
/// </summary>
public sealed class StartupManagerService : IStartupManagerService
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DisabledRunSubKey = @"Software\AurumTweaks\StartupDisabled\Run";
    private const string DisabledFolderName = "AurumDisabled";

    public Task<IReadOnlyList<StartupEntry>> ScanAsync() => Task.Run(Scan);

    public Task<bool> SetEnabledAsync(StartupEntry entry, bool enable) => Task.Run(() => SetEnabled(entry, enable));

    private static IReadOnlyList<StartupEntry> Scan()
    {
        var entries = new List<StartupEntry>();
        // Track (scope, source, rawName) we've already seen ENABLED, so a stale leftover in the backup — e.g. an
        // app the user re-installed after Aurum disabled it — doesn't show up as a confusing second "désactivé" row.
        var seen = new HashSet<(StartupScope, StartupSource, string)>();

        ReadRegistry(RegistryHive.CurrentUser, StartupScope.CurrentUser, entries, seen);
        ReadRegistry(RegistryHive.LocalMachine, StartupScope.AllUsers, entries, seen);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupScope.CurrentUser, entries, seen);
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupScope.AllUsers, entries, seen);

        // Enabled first, then by category, then name — so the actionable bloat surfaces above the "à conserver" rows.
        return entries
            .OrderByDescending(e => e.IsEnabled)
            .ThenBy(e => e.Category)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadRegistry(RegistryHive hive, StartupScope scope, List<StartupEntry> into,
        HashSet<(StartupScope, StartupSource, string)> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            AddRegistryValues(baseKey, RunSubKey, scope, isEnabled: true, into, seen);
            AddRegistryValues(baseKey, DisabledRunSubKey, scope, isEnabled: false, into, seen);
        }
        catch
        {
            // A hive we can't open (rights, redirection) simply contributes nothing — never throws the whole scan.
        }
    }

    private static void AddRegistryValues(RegistryKey baseKey, string subKey, StartupScope scope, bool isEnabled,
        List<StartupEntry> into, HashSet<(StartupScope, StartupSource, string)> seen)
    {
        using var key = baseKey.OpenSubKey(subKey);
        if (key is null) return;

        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;                 // skip the unnamed default value
            if (!seen.Add((scope, StartupSource.RegistryRun, name))) continue;   // already listed as enabled

            // Read without expanding env vars: we display and re-store exactly what's authored.
            var command = key.GetValue(name, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? string.Empty;
            var (exe, args) = StartupCommand.Parse(command);
            into.Add(new StartupEntry(name, name, command, exe, args, scope, StartupSource.RegistryRun,
                StartupClassifier.Classify(name, exe), isEnabled));
        }
    }

    private static void ReadFolder(string folder, StartupScope scope, List<StartupEntry> into,
        HashSet<(StartupScope, StartupSource, string)> seen)
    {
        if (string.IsNullOrEmpty(folder)) return;
        AddFolderFiles(folder, scope, isEnabled: true, into, seen);
        AddFolderFiles(Path.Combine(folder, DisabledFolderName), scope, isEnabled: false, into, seen);
    }

    private static void AddFolderFiles(string folder, StartupScope scope, bool isEnabled,
        List<StartupEntry> into, HashSet<(StartupScope, StartupSource, string)> seen)
    {
        if (!Directory.Exists(folder)) return;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder); }
        catch { return; }

        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add((scope, StartupSource.StartupFolder, fileName))) continue;

            var name = Path.GetFileNameWithoutExtension(fileName);
            // We classify on the shortcut's name and show the file itself (no fragile .lnk-target COM resolution).
            into.Add(new StartupEntry(name, fileName, path, path, string.Empty, scope, StartupSource.StartupFolder,
                StartupClassifier.Classify(name, path), isEnabled));
        }
    }

    private static bool SetEnabled(StartupEntry entry, bool enable)
    {
        if (entry.IsEnabled == enable) return true;   // nothing to do — keep the operation idempotent
        try
        {
            return entry.Source == StartupSource.RegistryRun
                ? ToggleRegistry(entry, enable)
                : ToggleFolder(entry, enable);
        }
        catch
        {
            return false;   // the VM re-scans and shows the real, unchanged state — never a fake "done"
        }
    }

    private static bool ToggleRegistry(StartupEntry entry, bool enable)
    {
        var hive = entry.Scope == StartupScope.AllUsers ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        var (fromSub, toSub) = enable ? (DisabledRunSubKey, RunSubKey) : (RunSubKey, DisabledRunSubKey);

        using var from = baseKey.OpenSubKey(fromSub, writable: true);
        var data = from?.GetValue(entry.RawName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (from is null || data is null) return false;
        var kind = from.GetValueKind(entry.RawName);

        using var to = baseKey.CreateSubKey(toSub, writable: true);
        if (to is null) return false;
        to.SetValue(entry.RawName, data, kind);   // preserve REG_SZ vs REG_EXPAND_SZ exactly
        from.DeleteValue(entry.RawName, throwOnMissingValue: false);
        return true;
    }

    private static bool ToggleFolder(StartupEntry entry, bool enable)
    {
        var folder = entry.Scope == StartupScope.AllUsers
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
            : Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrEmpty(folder)) return false;

        var disabledDir = Path.Combine(folder, DisabledFolderName);
        var (fromDir, toDir) = enable ? (disabledDir, folder) : (folder, disabledDir);
        var src = Path.Combine(fromDir, entry.RawName);
        if (!File.Exists(src)) return false;

        Directory.CreateDirectory(toDir);
        File.Move(src, Path.Combine(toDir, entry.RawName), overwrite: true);
        return true;
    }
}
