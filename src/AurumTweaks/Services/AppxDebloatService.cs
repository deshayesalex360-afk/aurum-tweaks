using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

/// <summary>
/// A coarse bucket for a curated preinstalled app — drives the French label/advice and the one honest distinction
/// the « Applications préinstallées » page makes: everything here is consumer bloat that's <b>safe to uninstall</b>
/// except <see cref="Gaming"/>, which is genuinely useful to this app's audience (the Win+G Game Bar overlay /
/// recording, the Xbox app for Game Pass, Xbox account sign-in) and flagged « à conserver » rather than pushed as
/// junk. Removal is per-user and reversible only by reinstalling from the Microsoft Store — the page says so.
/// </summary>
public enum AppxCategory
{
    PromoGames,
    News,
    Assistance,
    OfficePromo,
    MixedReality,
    Media,
    Communication,
    Productivity,
    Gaming
}

/// <summary>One curated preinstalled app we let the user manage. <see cref="Name"/> is the version-independent
/// Appx package identity name (e.g. <c>Microsoft.BingNews</c>) — the stable key we curate and match against the
/// live <c>Get-AppxPackage</c> list; the versioned <c>PackageFullName</c> needed to actually uninstall is read live.</summary>
public sealed record AppxInfo(string Name, string Label, AppxCategory Category);

/// <summary>A live package the system reports installed for the current user: the stable <see cref="Name"/>, the
/// versioned <see cref="PackageFullName"/> that <c>Remove-AppxPackage -Package</c> wants, and whether Windows marks
/// it non-removable (a protected app we must not offer a dead "uninstall" button for).</summary>
public sealed record AppxLivePackage(string Name, string PackageFullName, bool NonRemovable);

/// <summary>Whether a curated app is live on this machine — read from <c>Get-AppxPackage</c>, never guessed.</summary>
public enum AppxLiveState
{
    /// <summary>Installed for the current user (we hold its versioned full name and can uninstall it).</summary>
    Installed,

    /// <summary>Not installed on this account — already removed or never shipped here; shown honestly, never acted on.</summary>
    Absent
}

/// <summary>
/// The curated set of well-known Windows preinstalled apps worth surfacing, plus the stable French label/advice for
/// each category — pure and tested. Deliberately a <b>short, hand-picked allow-list</b> of consumer bloat (promo
/// games, Bing news/weather, assistance tiles, Office/Skype promos, mixed-reality, redundant media players, Phone
/// Link/Teams, Power Automate) rather than a dump of every package: blindly removing Appx packages can break Windows
/// (the Store, shell, runtimes). System-critical packages are therefore <b>never</b> listed — a test pins that. The
/// one non-bloat bucket, <see cref="AppxCategory.Gaming"/>, is included precisely so the page can tell the truth —
/// « à conserver », not « désinstalle » — about the Game Bar / Xbox app a gamer may actually use.
/// </summary>
public static class AppxCatalog
{
    public static IReadOnlyList<AppxInfo> Apps { get; } = new[]
    {
        // Promotional games — preinstalled purely for monetisation, no system role.
        new AppxInfo("king.com.CandyCrushSaga", "Candy Crush Saga", AppxCategory.PromoGames),
        new AppxInfo("king.com.CandyCrushSodaSaga", "Candy Crush Soda Saga", AppxCategory.PromoGames),
        new AppxInfo("king.com.BubbleWitch3Saga", "Bubble Witch 3 Saga", AppxCategory.PromoGames),
        new AppxInfo("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection", AppxCategory.PromoGames),

        // Bing News / Weather — web equivalents exist.
        new AppxInfo("Microsoft.BingNews", "Actualités (Bing)", AppxCategory.News),
        new AppxInfo("Microsoft.BingWeather", "Météo (Bing)", AppxCategory.News),

        // Assistance / feedback tiles — mostly promotional.
        new AppxInfo("Microsoft.GetHelp", "Obtenir de l'aide", AppxCategory.Assistance),
        new AppxInfo("Microsoft.Getstarted", "Astuces (Conseils)", AppxCategory.Assistance),
        new AppxInfo("Microsoft.WindowsFeedbackHub", "Hub de commentaires", AppxCategory.Assistance),

        // Office / Skype promos — do not affect a separately-installed desktop Office.
        new AppxInfo("Microsoft.MicrosoftOfficeHub", "Office (tuile promotionnelle)", AppxCategory.OfficePromo),
        new AppxInfo("Microsoft.SkypeApp", "Skype", AppxCategory.OfficePromo),

        // Windows Mixed Reality — discontinued by Microsoft.
        new AppxInfo("Microsoft.MixedReality.Portal", "Portail de réalité mixte", AppxCategory.MixedReality),

        // Redundant media players — most users already have a preferred player.
        new AppxInfo("Microsoft.ZuneMusic", "Groove Musique", AppxCategory.Media),
        new AppxInfo("Microsoft.ZuneVideo", "Films et TV", AppxCategory.Media),
        new AppxInfo("Clipchamp.Clipchamp", "Clipchamp", AppxCategory.Media),

        // Communication apps preinstalled on Windows 11.
        new AppxInfo("Microsoft.YourPhone", "Mobile connecté (Phone Link)", AppxCategory.Communication),
        new AppxInfo("MicrosoftTeams", "Teams (personnel)", AppxCategory.Communication),

        // Productivity tool preinstalled but unused by most.
        new AppxInfo("Microsoft.PowerAutomateDesktop", "Power Automate Desktop", AppxCategory.Productivity),

        // Xbox & gaming — genuinely useful to gamers, listed only so the page can say « à conserver » honestly.
        new AppxInfo("Microsoft.XboxGamingOverlay", "Game Bar (overlay Win+G)", AppxCategory.Gaming),
        new AppxInfo("Microsoft.GamingApp", "Application Xbox (Game Pass)", AppxCategory.Gaming),
        new AppxInfo("Microsoft.XboxApp", "Xbox Console Companion", AppxCategory.Gaming),
        new AppxInfo("Microsoft.XboxIdentityProvider", "Connexion au compte Xbox", AppxCategory.Gaming),
    };

    public static string CategoryLabel(AppxCategory category) => category switch
    {
        AppxCategory.PromoGames => "Jeux promotionnels",
        AppxCategory.News => "Actualités & météo",
        AppxCategory.Assistance => "Assistance & commentaires",
        AppxCategory.OfficePromo => "Promotion Office",
        AppxCategory.MixedReality => "Réalité mixte",
        AppxCategory.Media => "Lecteurs multimédias",
        AppxCategory.Communication => "Communication",
        AppxCategory.Productivity => "Productivité",
        AppxCategory.Gaming => "Xbox & jeu",
        _ => "Autre"
    };

    public static string Advice(AppxCategory category) => category switch
    {
        AppxCategory.PromoGames => "Jeux préinstallés à but promotionnel. Aucun rôle système — désinstallation sans risque, réinstallable depuis le Store.",
        AppxCategory.News => "Apps Bing Actualités/Météo — des équivalents web existent. Désinstallation sans risque.",
        AppxCategory.Assistance => "Aide, Astuces et Hub de commentaires — surtout promotionnels. Désinstallation sans risque.",
        AppxCategory.OfficePromo => "Tuiles promotionnelles Office et Skype. Désinstallables — n'affecte pas un Office de bureau installé séparément.",
        AppxCategory.MixedReality => "Portail Windows Mixed Reality — technologie abandonnée par Microsoft. Désinstallation sans risque.",
        AppxCategory.Media => "Groove Musique, Films et TV, Clipchamp — souvent redondants avec ton lecteur habituel. Réinstallables depuis le Store.",
        AppxCategory.Communication => "Mobile connecté (Phone Link) et Teams personnel. Désinstallables — réinstallables depuis le Store si tu changes d'avis.",
        AppxCategory.Productivity => "Power Automate Desktop — préinstallé mais inutile à la plupart. Désinstallation sans risque.",
        AppxCategory.Gaming => "À conserver si tu joues : Game Bar (overlay Win+G, enregistrement / compteur FPS), app Xbox (Game Pass) et connexion au compte Xbox. Ne les retire que si tu ne t'en sers pas.",
        _ => "Impact inconnu."
    };

    /// <summary>Everything here is recommended for removal except the one genuinely-useful Xbox/gaming bucket.</summary>
    public static bool RecommendedToRemove(AppxCategory category) =>
        category is not AppxCategory.Gaming;
}

/// <summary>
/// Parses the CSV that <c>Get-AppxPackage | Select Name,PackageFullName,NonRemovable | ConvertTo-Csv</c> emits into
/// a "package name → live package" map — pure, so it's pinned by tests. The honesty points: we read the live list
/// rather than assume what's installed (an app we can't find is simply absent from the map, never invented), we key
/// on the version-independent <c>Name</c> so the catalog matches across Windows builds, and we carry the versioned
/// <c>PackageFullName</c> verbatim because that is exactly what <c>Remove-AppxPackage -Package</c> requires.
/// </summary>
public static class AppxStateParser
{
    public static IReadOnlyDictionary<string, AppxLivePackage> Parse(string? csv)
    {
        var map = new Dictionary<string, AppxLivePackage>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(csv)) return map;

        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim();                        // also strips the trailing '\r' from CRLF output
            if (line.Length == 0 || line[0] == '#') continue;  // blank / a stray #TYPE header

            var fields = CsvRow.Split(line);
            if (fields.Count < 2) continue;                    // need at least Name + PackageFullName
            if (fields[0].Equals("Name", StringComparison.Ordinal)) continue; // the column header row

            var name = fields[0];
            var fullName = fields[1];
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(fullName)) continue;

            bool nonRemovable = fields.Count >= 3 && IsTrue(fields[2]);
            map[name] = new AppxLivePackage(name, fullName, nonRemovable);
        }
        return map;
    }

    // Get-AppxPackage's NonRemovable is a bool → "True"/"False" (or its numeric form, or blank on older builds).
    // Anything not affirmatively true means removable — the safe default (a real refusal still surfaces on re-read).
    private static bool IsTrue(string value)
    {
        var s = value.Trim();
        return s.Equals("True", StringComparison.OrdinalIgnoreCase) || s == "1";
    }
}

/// <summary>One curated app joined with its live on-system state — everything shown is real (the app is installed
/// and reports this full name) or honestly marked <see cref="AppxLiveState.Absent"/>. There is no « réactiver »:
/// uninstalling is one-way per user, reinstall is a Microsoft Store action, and the page says so.</summary>
public sealed record AppxEntry(AppxInfo Info, AppxLiveState State, string PackageFullName, bool NonRemovable)
{
    public string Label => Info.Label;
    public string PackageName => Info.Name;
    public string CategoryDisplay => AppxCatalog.CategoryLabel(Info.Category);
    public string Advice => AppxCatalog.Advice(Info.Category);
    public bool RecommendedToRemove => AppxCatalog.RecommendedToRemove(Info.Category);

    public bool IsInstalled => State == AppxLiveState.Installed;
    public bool IsAbsent => State == AppxLiveState.Absent;

    public string StateDisplay => State switch
    {
        AppxLiveState.Installed => "Installée",
        _ => "Absente sur ce PC"
    };

    // View visibility flags — kept as explicit props so the XAML stays declarative. We offer an uninstall only for
    // an installed app Windows lets us remove; a protected (NonRemovable) app is shown but never gets a dead button.
    public bool ShowRemove => IsInstalled && !NonRemovable;
    public bool ShowKeepBadge => IsInstalled && !RecommendedToRemove;
    public bool ShowAbsentBadge => IsAbsent;
    public bool ShowSystemBadge => IsInstalled && NonRemovable;
}

/// <summary>The live preinstalled-app picture: every catalog app joined with its state, plus whether the query
/// itself succeeded (so the page can tell "nothing installed" apart from "couldn't read the system").</summary>
public sealed record AppxReport(IReadOnlyList<AppxEntry> Entries, bool QueryOk)
{
    public int InstalledCount => Entries.Count(e => e.IsInstalled);

    /// <summary>Installed, removable, AND recommended-off — the actionable "bloat still present" count the status line shows.</summary>
    public int RemovableRecommendedCount =>
        Entries.Count(e => e.IsInstalled && !e.NonRemovable && e.RecommendedToRemove);
}

/// <summary>
/// Joins the curated <see cref="AppxCatalog"/> with a live "name → package" map into ordered entries — pure, so the
/// join and ordering are pinned by tests. Ordering surfaces the actionable rows first: installed removable bloat,
/// then the « à conserver » Xbox apps, then installed-but-protected, then absent.
/// </summary>
public static class AppxResolver
{
    public static IReadOnlyList<AppxEntry> Resolve(
        IReadOnlyList<AppxInfo> catalog,
        IReadOnlyDictionary<string, AppxLivePackage> liveByName)
    {
        return catalog
            .Select(info => liveByName.TryGetValue(info.Name, out var live)
                ? new AppxEntry(info, AppxLiveState.Installed, live.PackageFullName, live.NonRemovable)
                : new AppxEntry(info, AppxLiveState.Absent, string.Empty, false))
            .OrderBy(Rank)
            .ThenBy(e => e.Info.Category)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // 0 = actionable bloat installed, 1 = useful app installed (à conserver), 2 = installed but protected, 3 = absent.
    private static int Rank(AppxEntry e)
    {
        if (!e.IsInstalled) return 3;
        if (!e.RecommendedToRemove) return 1;
        return e.NonRemovable ? 2 : 0;
    }
}

/// <summary>
/// The « Applications préinstallées » manager — thin glue around the pure cores above. Honest by construction: it
/// reads the live set via <c>Get-AppxPackage</c> (an app that isn't installed is shown absent, never invented), and
/// every removal is a genuine <c>Remove-AppxPackage</c> after which the caller re-reads the system — a refusal shows
/// the app still present rather than a fabricated success. Scope is stated plainly: removal is per-user and reversed
/// only by reinstalling from the Microsoft Store (unlike the registry tweaks, this is not a clean inverse).
/// </summary>
public sealed class AppxDebloatService : IAppxDebloatService
{
    public Task<AppxReport> GetReportAsync() => Task.Run(GetReport);

    public Task<bool> RemoveAsync(string packageFullName) => Task.Run(() => Remove(packageFullName));

    private static AppxReport GetReport()
    {
        var (_, stdout) = ProcessRunner.Capture("powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Select-Object Name,PackageFullName,NonRemovable | ConvertTo-Csv -NoTypeInformation\"");

        var live = AppxStateParser.Parse(stdout);
        var entries = AppxResolver.Resolve(AppxCatalog.Apps, live);
        // A healthy account lists dozens of packages; an empty map means the query failed (no module / error).
        return new AppxReport(entries, QueryOk: live.Count > 0);
    }

    private static bool Remove(string packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName)) return false;
        // Real per-user uninstall. -ErrorAction Stop makes a refusal a non-zero exit; the caller re-reads regardless,
        // so the truth comes from Get-AppxPackage afterwards, never from this return value alone.
        var (exit, _) = ProcessRunner.Capture("powershell.exe",
            $"-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '{packageFullName}' -ErrorAction Stop\"",
            timeoutMs: 60_000);
        return exit == 0;
    }
}
