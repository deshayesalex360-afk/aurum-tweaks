using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  « Services Windows » — a curated, reversible service manager built ON TOP of the low-level
//  IServiceManagerService (which already reads/writes the Start DWORD locale-invariantly and stops a
//  service). Everything here is real and honest: each curated service's startup type and running state are
//  read straight from Windows, every change is a genuine SetStartupType the ViewModel RE-READS afterwards so a
//  refused write surfaces the unchanged real state (never a fabricated "done"), the list is a short hand-picked
//  ALLOW-LIST (a blind "disable services" dump is a footgun that bricks Windows), gaming/perf services are
//  flagged « à conserver » as the honesty counterweight, and the page promises privacy/lightness — never FPS.
//  Modern best practice drives the recommendations: « Manuel (déclenché) » is preferred over « Désactivé »
//  wherever a feature is only occasionally useful — it costs nothing idle yet still starts on demand.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A coarse bucket for a curated service — drives the French label/advice and, via
/// <see cref="ServiceCatalog.RecommendedAction"/>, the single honest recommendation the page makes for it.
/// Each category maps 1:1 to one <see cref="ServiceRecommendation"/> so advice and action can never disagree.
/// </summary>
public enum ServiceCategory
{
    /// <summary>Phones usage data home — safe to disable (DiagTrack, dmwappushservice).</summary>
    Telemetry,

    /// <summary>Remote-access attack surface, off by default, rarely useful on a gaming PC — disable.</summary>
    RemoteAccess,

    /// <summary>Retail/store-demo only — disable.</summary>
    Demo,

    /// <summary>Crash-report queue — « Manuel » is enough, it starts on demand.</summary>
    ErrorReporting,

    /// <summary>Printing — keep if you print, else « Manuel »: starts only when an app asks.</summary>
    Printing,

    /// <summary>Occasional features (maps, geolocation, media sharing) — « Manuel »: start only when used.</summary>
    OnDemand,

    /// <summary>Indexing/prefetch — genuinely useful depending on usage; flagged « à conserver ».</summary>
    Performance,

    /// <summary>Xbox / Game Pass / controllers — « à conserver » for gamers (the honesty counterweight).</summary>
    Xbox
}

/// <summary>The single recommendation the page makes for a service — pure, derived from its category.</summary>
public enum ServiceRecommendation
{
    /// <summary>Set startup to « Désactivé » (and stop it now).</summary>
    Disable,

    /// <summary>Set startup to « Manuel (déclenché) » — the light-touch modern default.</summary>
    Manual,

    /// <summary>Leave it alone — « à conserver ».</summary>
    Keep
}

/// <summary>One known Windows service we let the user manage. <see cref="ServiceName"/> is the exact short
/// service name handed verbatim to the registry/ServiceController (e.g. <c>DiagTrack</c>, not a display name).</summary>
public sealed record ManagedServiceInfo(string ServiceName, string Label, ServiceCategory Category);

/// <summary>
/// The curated set of well-known, non-critical Windows services worth surfacing, plus the stable French
/// label/advice/recommendation per category — pure and tested. Deliberately a <b>short, hand-picked allow-list</b>:
/// a "disable all services" dump is a footgun that can leave Windows unbootable, so anything load-bearing
/// (RPC, DCOM, Base Filtering Engine, Defender, networking stack, profile/audio/crypto, Event Log, the task
/// scheduler itself, …) is intentionally absent and pinned out by <c>ServiceControlTests.Catalog_ExcludesCriticalServices</c>.
/// </summary>
public static class ServiceCatalog
{
    public static IReadOnlyList<ManagedServiceInfo> Services { get; } = new[]
    {
        // Telemetry — the big, safe wins (DiagTrack is the master telemetry pipe; both default Automatic).
        new ManagedServiceInfo("DiagTrack", "Expériences des utilisateurs connectés et télémétrie", ServiceCategory.Telemetry),
        new ManagedServiceInfo("dmwappushservice", "Routage des messages Push WAP (télémétrie)", ServiceCategory.Telemetry),

        // Remote-access surface — disabled by default on modern Windows; if on, recommend off.
        new ManagedServiceInfo("RemoteRegistry", "Registre à distance", ServiceCategory.RemoteAccess),
        new ManagedServiceInfo("RemoteAccess", "Routage et accès à distance", ServiceCategory.RemoteAccess),

        // Store demo — only ever wanted on a shop display machine.
        new ManagedServiceInfo("RetailDemo", "Service de démonstration en magasin", ServiceCategory.Demo),

        // Error reporting — useful occasionally; Manual is the right trigger-start posture.
        new ManagedServiceInfo("WerSvc", "Rapports d'erreurs Windows", ServiceCategory.ErrorReporting),

        // Printing — keep if you print; otherwise Manual (starts on an app's print request).
        new ManagedServiceInfo("Spooler", "Spouleur d'impression", ServiceCategory.Printing),
        new ManagedServiceInfo("Fax", "Télécopie (Fax)", ServiceCategory.Printing),

        // On-demand features — light-touch Manual; each starts only when its feature is actually used.
        new ManagedServiceInfo("MapsBroker", "Gestionnaire de cartes téléchargées", ServiceCategory.OnDemand),
        new ManagedServiceInfo("lfsvc", "Service de géolocalisation", ServiceCategory.OnDemand),
        new ManagedServiceInfo("WMPNetworkSvc", "Partage réseau du Lecteur Windows Media", ServiceCategory.OnDemand),
        new ManagedServiceInfo("PhoneSvc", "Service de téléphonie (Téléphone)", ServiceCategory.OnDemand),

        // Performance — genuinely debated; included so the page can say « à conserver » honestly, not as bloat.
        new ManagedServiceInfo("SysMain", "SysMain (Superfetch / préchargement)", ServiceCategory.Performance),
        new ManagedServiceInfo("WSearch", "Windows Search (indexation / recherche)", ServiceCategory.Performance),

        // Xbox — the gamer counterweight: disabling these breaks Game Pass, cloud saves and Xbox controllers.
        new ManagedServiceInfo("XblAuthManager", "Gestionnaire d'authentification Xbox Live", ServiceCategory.Xbox),
        new ManagedServiceInfo("XblGameSave", "Sauvegarde des jeux Xbox Live", ServiceCategory.Xbox),
        new ManagedServiceInfo("XboxGipSvc", "Gestion des accessoires Xbox (manettes)", ServiceCategory.Xbox),
        new ManagedServiceInfo("XboxNetApiSvc", "Service réseau Xbox Live", ServiceCategory.Xbox),
    };

    public static string CategoryLabel(ServiceCategory category) => category switch
    {
        ServiceCategory.Telemetry => "Télémétrie",
        ServiceCategory.RemoteAccess => "Accès distant (sécurité)",
        ServiceCategory.Demo => "Démonstration magasin",
        ServiceCategory.ErrorReporting => "Rapports d'erreurs",
        ServiceCategory.Printing => "Impression",
        ServiceCategory.OnDemand => "Fonctions à la demande",
        ServiceCategory.Performance => "Performance",
        ServiceCategory.Xbox => "Xbox / jeux",
        _ => "Autre"
    };

    public static string Advice(ServiceCategory category) => category switch
    {
        ServiceCategory.Telemetry => "Envoie des données d'usage à Microsoft. Désactivation sans risque pour le système.",
        ServiceCategory.RemoteAccess => "Surface d'accès distant rarement utile sur un PC de jeu, désactivée par défaut. Désactivation recommandée.",
        ServiceCategory.Demo => "Sert uniquement aux PC de démonstration en magasin. Désactivation sans risque.",
        ServiceCategory.ErrorReporting => "Met en file et envoie les rapports de plantage. « Manuel » suffit — il démarre à la demande.",
        ServiceCategory.Printing => "À conserver si tu imprimes. Sinon « Manuel » : il ne démarrera qu'à la demande d'une application.",
        ServiceCategory.OnDemand => "Fonction occasionnelle (cartes, géolocalisation, partage média). « Manuel » : démarrage uniquement quand c'est utilisé.",
        ServiceCategory.Performance => "À conserver : peut aider selon ton usage (disque dur, indexation de la recherche). Ne la désactive pas sans raison précise.",
        ServiceCategory.Xbox => "À conserver pour les joueurs : Game Pass, sauvegardes cloud et manettes Xbox en dépendent.",
        _ => "Impact inconnu."
    };

    /// <summary>The one recommendation per category. Telemetry/remote/demo → disable; error-reporting/print/on-demand
    /// → manual (trigger-start); performance/Xbox → keep.</summary>
    public static ServiceRecommendation RecommendedAction(ServiceCategory category) => category switch
    {
        ServiceCategory.Telemetry => ServiceRecommendation.Disable,
        ServiceCategory.RemoteAccess => ServiceRecommendation.Disable,
        ServiceCategory.Demo => ServiceRecommendation.Disable,
        ServiceCategory.ErrorReporting => ServiceRecommendation.Manual,
        ServiceCategory.Printing => ServiceRecommendation.Manual,
        ServiceCategory.OnDemand => ServiceRecommendation.Manual,
        ServiceCategory.Performance => ServiceRecommendation.Keep,
        ServiceCategory.Xbox => ServiceRecommendation.Keep,
        _ => ServiceRecommendation.Keep
    };

    /// <summary>The canonical startup target a recommendation maps to, or "" for Keep (no target to set).</summary>
    public static string RecommendedTarget(ServiceRecommendation recommendation) => recommendation switch
    {
        ServiceRecommendation.Disable => "Disabled",
        ServiceRecommendation.Manual => "Manual",
        _ => ""
    };
}

/// <summary>Stable French labels for the canonical <see cref="ServiceStartup"/> vocabulary — pure, so the
/// display never depends on Windows' localized service text (which mojibakes on a non-EN code page).</summary>
public static class ServiceStartupDisplay
{
    public static string Label(string? startupType) => startupType switch
    {
        "Boot" => "Démarrage noyau (boot)",
        "System" => "Système",
        "Automatic" => "Automatique",
        "DelayedAuto" => "Automatique (différé)",
        "Manual" => "Manuel (déclenché)",
        "Disabled" => "Désactivé",
        _ => "Inconnu"
    };
}

/// <summary>Whether a curated service is live on this machine and how — read from the registry + a
/// ServiceController pass, never guessed. <see cref="StartupType"/> is a canonical <see cref="ServiceStartup"/>
/// string (or null when <see cref="Exists"/> is false).</summary>
public readonly record struct ServiceLiveState(string ServiceName, string? StartupType, bool Exists, bool IsRunning);

/// <summary>One curated service joined with its live on-system state — everything shown is real (the service
/// exists and reports this startup/running state) or honestly marked absent. The display props and, crucially,
/// the <c>CanSet*</c> gates (a startup button is offered only when it would actually change something — no dead
/// control) are pure, so they're pinned by tests.</summary>
public sealed record ServiceEntry(ManagedServiceInfo Info, ServiceLiveState Live)
{
    public string Label => Info.Label;
    public string ServiceName => Info.ServiceName;
    public string CategoryDisplay => ServiceCatalog.CategoryLabel(Info.Category);
    public string Advice => ServiceCatalog.Advice(Info.Category);
    public ServiceRecommendation Recommendation => ServiceCatalog.RecommendedAction(Info.Category);

    public bool IsPresent => Live.Exists;
    public bool IsRunning => Live.IsRunning;
    public string? StartupType => Live.StartupType;
    public string StartupDisplay => ServiceStartupDisplay.Label(Live.StartupType);

    public string StateDisplay =>
        !IsPresent ? "Absent sur ce PC" : IsRunning ? "En cours d'exécution" : "Arrêté";

    /// <summary>True only for the user-mode start types our trio of buttons may safely write. A driver-like
    /// Boot/System service (or an unreadable Unknown) is shown read-only — we never offer to flip its mode.</summary>
    public bool IsTunable => IsPresent && StartupType is "Automatic" or "DelayedAuto" or "Manual" or "Disabled";

    /// <summary>Action buttons are shown for tunable, non-Keep services. A « à conserver » service is never
    /// nudged toward disabling — the user can still override it via services.msc (offered as the honest complement).</summary>
    public bool ShowActions => IsTunable && Recommendation != ServiceRecommendation.Keep;

    // A trio button is enabled only when it differs from the live state — so clicking always does real work.
    public bool CanSetAuto => ShowActions && !string.Equals(StartupType, "Automatic", StringComparison.OrdinalIgnoreCase);
    public bool CanSetManual => ShowActions && !string.Equals(StartupType, "Manual", StringComparison.OrdinalIgnoreCase);
    public bool CanSetDisabled => ShowActions && !string.Equals(StartupType, "Disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>The canonical target this service's recommendation points at ("Manual"/"Disabled"/"").</summary>
    public string RecommendedTarget => ServiceCatalog.RecommendedTarget(Recommendation);

    /// <summary>True when the live startup type already matches the recommendation (so there's nothing to do).</summary>
    public bool IsAtRecommended =>
        Recommendation != ServiceRecommendation.Keep &&
        string.Equals(StartupType, RecommendedTarget, StringComparison.OrdinalIgnoreCase);

    public string RecommendChip => Recommendation switch
    {
        ServiceRecommendation.Disable => "Recommandé : Désactivé",
        ServiceRecommendation.Manual => "Recommandé : Manuel",
        _ => "À conserver"
    };

    // View visibility flags — explicit props keep the XAML declarative.
    public bool ShowKeepBadge => IsPresent && Recommendation == ServiceRecommendation.Keep;
    public bool ShowAbsentBadge => !IsPresent;
    public bool ShowOptimizedBadge => IsAtRecommended;
}

/// <summary>The live service picture: every curated service joined with its state, plus whether the system read
/// succeeded (so the page can tell "everything already optimal" apart from "couldn't read the services").</summary>
public sealed record ServiceControlReport(IReadOnlyList<ServiceEntry> Entries, bool QueryOk)
{
    public int PresentCount => Entries.Count(e => e.IsPresent);

    /// <summary>Present, tunable, non-Keep AND not yet at the recommended target — the actionable count shown.</summary>
    public int ActionableCount => Entries.Count(e => e.ShowActions && !e.IsAtRecommended);
}

/// <summary>
/// Joins the curated <see cref="ServiceCatalog"/> with a live "name → state" map into ordered entries — pure,
/// so the join and ordering are pinned by tests. Ordering surfaces the actionable rows first: present &amp;
/// tunable &amp; not-yet-at-recommended, then the « à conserver » services, then already-optimised, then absent.
/// </summary>
public static class ServiceResolver
{
    public static IReadOnlyList<ServiceEntry> Resolve(
        IReadOnlyList<ManagedServiceInfo> catalog,
        IReadOnlyDictionary<string, ServiceLiveState> liveByName)
    {
        return catalog
            .Select(info =>
            {
                ServiceLiveState live = liveByName.TryGetValue(info.ServiceName, out var l)
                    ? l
                    : new ServiceLiveState(info.ServiceName, null, Exists: false, IsRunning: false);
                return new ServiceEntry(info, live);
            })
            .OrderBy(Rank)
            .ThenBy(e => e.Info.Category)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // 0 = actionable (still not at recommended), 1 = à conserver, 2 = already optimised, 3 = absent.
    private static int Rank(ServiceEntry e)
    {
        if (!e.IsPresent) return 3;
        if (e.Recommendation == ServiceRecommendation.Keep) return 1;
        return e.IsAtRecommended ? 2 : 0;
    }
}

/// <summary>
/// The « Services Windows » manager — thin glue around the pure cores above and the low-level
/// <see cref="IServiceManagerService"/> it reuses for every read/write. Honest &amp; reversible by construction:
/// it reads each curated service's REAL startup type (registry Start DWORD, locale-invariant) and running state
/// (a single <see cref="ServiceController.GetServices"/> pass — no localized text parsed), and every change is a
/// genuine <c>SetStartupType</c> the caller re-reads. Setting « Désactivé » also stops the running service so the
/// change is true now, not merely next boot; « Manuel » leaves a running service alone (it just won't auto-start).
/// </summary>
public sealed class ServiceControlService : IServiceControlService
{
    private readonly IServiceManagerService _services;

    public ServiceControlService(IServiceManagerService services) => _services = services;

    public Task<ServiceControlReport> GetReportAsync() => Task.Run(GetReport);

    public Task<bool> SetStartupAsync(string serviceName, string canonicalStartupType) =>
        Task.Run(() => SetStartup(serviceName, canonicalStartupType));

    private ServiceControlReport GetReport()
    {
        // One pass over all Win32 services for running state. We read the Status enum, never localized text,
        // so a French Windows is read correctly; a failure here leaves running empty (startup type still reads).
        var running = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var sc in ServiceController.GetServices())
            {
                try { running[sc.ServiceName] = sc.Status == ServiceControllerStatus.Running; }
                catch { /* a single service that won't report status mustn't sink the whole pass */ }
                finally { sc.Dispose(); }
            }
        }
        catch { /* GetServices itself failed — fall through with an empty running map */ }

        var live = new Dictionary<string, ServiceLiveState>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in ServiceCatalog.Services)
        {
            bool exists = _services.TryGetStartupType(info.ServiceName, out var startup);
            bool isRunning = running.TryGetValue(info.ServiceName, out var r) && r;
            live[info.ServiceName] = new ServiceLiveState(info.ServiceName, exists ? startup : null, exists, isRunning);
        }

        var entries = ServiceResolver.Resolve(ServiceCatalog.Services, live);
        // Nearly every curated service exists on any Windows; zero present means the registry read failed.
        return new ServiceControlReport(entries, QueryOk: entries.Any(e => e.IsPresent));
    }

    private bool SetStartup(string serviceName, string canonicalStartupType)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return false;
        // Never write a non-round-trippable target — that would make on-system detection read a false "not applied".
        if (!ServiceStartup.IsCanonical(canonicalStartupType)) return false;

        bool ok = _services.SetStartupType(serviceName, canonicalStartupType);

        // Disabling only blocks the NEXT boot unless we also stop a service that's running right now.
        if (ok && canonicalStartupType.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            _services.StopService(serviceName);

        return ok;
    }
}
