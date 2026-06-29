using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  « Mémoire virtuelle » — the pagefile (fichier d'échange) as Windows actually reports it, plus the ONE write we
//  can do safely AND verify. State is read from WMI: Win32_ComputerSystem.AutomaticManagedPagefile (the master
//  checkbox), Win32_PageFileUsage (the LIVE allocation / current usage / peak, in MB) and Win32_PageFileSetting
//  (the CONFIGURED initial/max, in MB; 0 = "system-managed size"). Nothing is guessed — an unreadable query reports
//  honestly instead of fabricating numbers. The only in-app mutation is "restore Windows' automatic management" —
//  the SAFE default — and its success is MEASURED by re-reading AutomaticManagedPagefile afterwards, so a silently
//  refused WMI Put reports failure, never a fake success. Custom/fixed sizing is the risky direction and is honestly
//  handed off to Windows' own Virtual Memory dialog. Honesty line, stated on the page: a fixed pagefile is NOT an FPS
//  boost, and DISABLING the pagefile can make games and apps crash or refuse to launch.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>How the pagefile is configured, classified from the WMI facts. Unknown only when nothing could be read.</summary>
public enum PagefileMode { Automatic, SystemManagedSize, CustomFixed, Disabled, Unknown }

/// <summary>French labels for <see cref="PagefileMode"/> — pure, so the wording is pinned by a test.</summary>
public static class PagefileModeInfo
{
    public static string Describe(PagefileMode mode) => mode switch
    {
        PagefileMode.Automatic         => "Géré automatiquement par Windows",
        PagefileMode.SystemManagedSize => "Taille gérée par le système",
        PagefileMode.CustomFixed       => "Taille personnalisée",
        PagefileMode.Disabled          => "Désactivé",
        _                              => "Indéterminé"
    };
}

/// <summary>
/// Classify the pagefile mode from three WMI-derived facts. Pure so the (honest) classification is unit-pinned:
/// "automatic" always wins; with auto off, no active pagefile means it is genuinely Disabled; an explicit
/// initial/max is a CustomFixed size; otherwise the system manages the size of an existing pagefile.
/// </summary>
public static class PagefileModeClassifier
{
    public static PagefileMode Classify(bool automaticManaged, bool anyActive, bool anyConfiguredSize)
    {
        if (automaticManaged) return PagefileMode.Automatic;
        if (!anyActive) return PagefileMode.Disabled;
        return anyConfiguredSize ? PagefileMode.CustomFixed : PagefileMode.SystemManagedSize;
    }
}

/// <summary>
/// Format a size given in MEGABYTES (the unit WMI uses for pagefiles) through the shared base-1024
/// <see cref="ByteSize"/> formatter. A negative value means "unknown" → « — » (never a fabricated 0); a real 0 stays
/// « 0 o » because zero usage is a meaningful, measured value (the pagefile simply hasn't been touched).
/// </summary>
public static class PagefileSize
{
    public static string Format(long megabytes) =>
        megabytes < 0 ? "—" : ByteSize.Format(megabytes * 1024L * 1024L);
}

/// <summary>
/// One pagefile as WMI reports it: its path, the live allocation/usage/peak (Win32_PageFileUsage, MB) and the
/// configured initial/max (Win32_PageFileSetting, MB; 0/0 = "system-managed size", −1 = no explicit setting).
/// Pure display derivation, all unit-tested.
/// </summary>
public sealed record PagefileEntry(
    string Path, long AllocatedMb, long CurrentMb, long PeakMb, long InitialMb, long MaxMb)
{
    /// <summary>Drive root (e.g. "C:") parsed from the path; falls back to the raw path, then « — ».</summary>
    public string Drive
    {
        get
        {
            var root = System.IO.Path.GetPathRoot(Path);
            if (!string.IsNullOrWhiteSpace(root)) return root.TrimEnd('\\', '/');
            return string.IsNullOrWhiteSpace(Path) ? "—" : Path;
        }
    }

    public string PathDisplay => string.IsNullOrWhiteSpace(Path) ? "—" : Path;
    public string AllocatedDisplay => PagefileSize.Format(AllocatedMb);
    public string CurrentDisplay => PagefileSize.Format(CurrentMb);
    public string PeakDisplay => PagefileSize.Format(PeakMb);

    /// <summary>True when an explicit fixed size is configured (so the report can call the mode CustomFixed).</summary>
    public bool HasConfiguredSize => InitialMb > 0 || MaxMb > 0;

    /// <summary>Configured size as text: « — » when unknown, « Géré par le système » when both 0, else « init – max ».</summary>
    public string ConfiguredDisplay =>
        InitialMb < 0 || MaxMb < 0 ? "—"
        : InitialMb == 0 && MaxMb == 0 ? "Géré par le système"
        : $"{PagefileSize.Format(InitialMb)} – {PagefileSize.Format(MaxMb)}";
}

/// <summary>The honest verdict severity (mirrors the tri-state colored dot used by DriveHealth / MemoryModules).</summary>
public enum PagefileVerdict { Ok, Info, Warning }

/// <summary>The indicative recommendation text. Pure, so the wording is pinned by tests.</summary>
public sealed record PagefileRecommendation(PagefileVerdict Verdict, string Headline, string Detail);

/// <summary>
/// Turns a <see cref="PagefileMode"/> into an honest recommendation. It NEVER claims a fixed pagefile boosts FPS, and
/// it WARNS that disabling the pagefile can crash apps — the load-bearing honesty content of the page.
/// </summary>
public static class PagefileAdvisor
{
    public static PagefileRecommendation Assess(PagefileMode mode) => mode switch
    {
        PagefileMode.Automatic => new(PagefileVerdict.Ok,
            "Gestion automatique — le réglage recommandé",
            "Windows ajuste la taille du fichier d'échange selon les besoins. C'est le réglage conseillé pour la "
            + "quasi-totalité des configurations ; aucune action n'est nécessaire."),

        PagefileMode.Disabled => new(PagefileVerdict.Warning,
            "Fichier d'échange désactivé — déconseillé",
            "Certains jeux et applications réservent de la mémoire virtuelle et peuvent planter, refuser de se lancer "
            + "ou perdre des données sans fichier d'échange. Le gain ressenti est négligeable. Réactive la gestion "
            + "automatique ci-dessous, sauf besoin précis."),

        PagefileMode.CustomFixed => new(PagefileVerdict.Info,
            "Taille personnalisée",
            "Une taille fixe est valable si tu sais pourquoi (limiter l'usure d'un petit SSD, contrôler l'espace…). "
            + "Ce n'est pas un gain de FPS. En cas de doute, la gestion automatique évite les erreurs « mémoire "
            + "insuffisante » lors des gros pics."),

        PagefileMode.SystemManagedSize => new(PagefileVerdict.Info,
            "Taille gérée par le système (case auto décochée)",
            "La taille est laissée au système, mais la case « gérer automatiquement » est décochée. C'est équivalent "
            + "au défaut dans les faits ; tu peux réactiver la gestion automatique pour revenir à l'état recommandé."),

        _ => new(PagefileVerdict.Info,
            "État indéterminé",
            "Impossible de lire la configuration du fichier d'échange. Ouvre les options de mémoire virtuelle de "
            + "Windows pour la vérifier.")
    };
}

/// <summary>
/// Result of the one safe write (restore automatic management). MEASURED: <see cref="FromVerified"/> sets Ok only when
/// a fresh re-read confirms AutomaticManagedPagefile is now true — a silently-ignored WMI Put reports failure here,
/// never a fabricated success. The message is honest that the effective resize happens at the next reboot.
/// </summary>
public sealed record PagefileActionOutcome(bool Ok, string Message)
{
    public static PagefileActionOutcome FromVerified(bool nowAutomatic) => nowAutomatic
        ? new(true, "Gestion automatique réactivée. Le redimensionnement effectif s'applique au redémarrage.")
        : new(false, "Impossible de réactiver la gestion automatique (modification refusée par Windows).");

    public static PagefileActionOutcome Failed { get; } =
        new(false, "Échec de la modification du fichier d'échange.");
}

/// <summary>
/// The display-ready report. <see cref="QueryOk"/> false (couldn't read WMI) is kept DISTINCT from a real empty list,
/// and <see cref="CanRestoreAutomatic"/> is false when Windows already auto-manages — so the one write button is never
/// a no-op. Built by the pure <see cref="From"/> factory, which is what the tests pin.
/// </summary>
public sealed record PagefileReport(
    PagefileMode Mode, bool AutomaticManaged, IReadOnlyList<PagefileEntry> Entries,
    long TotalAllocatedMb, PagefileRecommendation Recommendation, bool QueryOk)
{
    public bool HasEntries => Entries.Count > 0;
    public int EntryCount => Entries.Count;
    public string ModeDisplay => PagefileModeInfo.Describe(Mode);
    public string TotalAllocatedDisplay => PagefileSize.Format(TotalAllocatedMb);

    /// <summary>The one safe write must never be a no-op: it is offered only when Windows is NOT already auto-managing.</summary>
    public bool CanRestoreAutomatic => !AutomaticManaged;

    public bool VerdictOk => Recommendation.Verdict == PagefileVerdict.Ok;
    public bool VerdictWarn => Recommendation.Verdict == PagefileVerdict.Warning;
    public bool VerdictInfo => Recommendation.Verdict == PagefileVerdict.Info;

    public string Headline
    {
        get
        {
            if (!QueryOk) return "Lecture de la configuration impossible.";
            if (!HasEntries)
                return Mode == PagefileMode.Disabled
                    ? "Aucun fichier d'échange actif (désactivé)."
                    : "Aucun fichier d'échange actif détecté.";
            return EntryCount == 1
                ? $"1 fichier d'échange · {TotalAllocatedDisplay} alloué(s)."
                : $"{EntryCount} fichiers d'échange · {TotalAllocatedDisplay} alloués au total.";
        }
    }

    public static PagefileReport From(bool queryOk, bool automaticManaged, IEnumerable<PagefileEntry> entries)
    {
        var list = entries?.ToList() ?? new List<PagefileEntry>();
        bool anyActive = list.Any(e => e.AllocatedMb > 0);
        bool anyConfiguredSize = list.Any(e => e.HasConfiguredSize);
        var mode = !queryOk
            ? PagefileMode.Unknown
            : PagefileModeClassifier.Classify(automaticManaged, anyActive, anyConfiguredSize);
        long total = list.Where(e => e.AllocatedMb > 0).Sum(e => e.AllocatedMb);
        return new PagefileReport(mode, automaticManaged, list, total, PagefileAdvisor.Assess(mode), queryOk);
    }

    public static PagefileReport Failed { get; } =
        From(queryOk: false, automaticManaged: false, entries: Array.Empty<PagefileEntry>());
}

/// <summary>
/// The I/O service behind « Mémoire virtuelle ». The decision logic (classification, advice, size formatting, the
/// measured-outcome rule) lives in the pure cores above and is what the tests pin; this only samples WMI and fires the
/// one safe Put, then re-reads so the reported result is real.
/// </summary>
public sealed class PagefileService : IPagefileService
{
    public Task<PagefileReport> GetReportAsync() => Task.Run(ReadReport);

    public Task<PagefileActionOutcome> RestoreAutomaticAsync() => Task.Run(RestoreAutomatic);

    private static PagefileReport ReadReport()
    {
        try
        {
            bool automatic = ReadAutomaticManaged();
            var settings = ReadSettings();                 // path → (initial, max), in MB
            var entries = ReadUsage(settings);
            return PagefileReport.From(queryOk: true, automaticManaged: automatic, entries: entries);
        }
        catch
        {
            return PagefileReport.Failed;                  // honest "lecture impossible", never fabricated numbers
        }
    }

    private static bool ReadAutomaticManaged()
    {
        using var searcher = new ManagementObjectSearcher("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
        using var cs = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        return cs?["AutomaticManagedPagefile"] is bool b && b;
    }

    private static Dictionary<string, (long Initial, long Max)> ReadSettings()
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher =
                new ManagementObjectSearcher("SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
                using (mo)
                {
                    string name = mo["Name"] as string ?? "";
                    if (!string.IsNullOrWhiteSpace(name))
                        map[name] = (ToLong(mo["InitialSize"]), ToLong(mo["MaximumSize"]));
                }
        }
        catch { /* Win32_PageFileSetting is legitimately empty when Windows auto-manages — not an error */ }
        return map;
    }

    private static List<PagefileEntry> ReadUsage(Dictionary<string, (long Initial, long Max)> settings)
    {
        var list = new List<PagefileEntry>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");
        foreach (var mo in searcher.Get().Cast<ManagementObject>())
            using (mo)
            {
                string name = mo["Name"] as string ?? "";
                long initial = -1, max = -1;
                if (name.Length > 0 && settings.TryGetValue(name, out var s)) { initial = s.Initial; max = s.Max; }
                list.Add(new PagefileEntry(
                    name, ToLong(mo["AllocatedBaseSize"]), ToLong(mo["CurrentUsage"]), ToLong(mo["PeakUsage"]),
                    initial, max));
            }
        return list;
    }

    /// <summary>Best-effort WMI value → long; null/garbage degrades to −1 so the display shows « — », not a fake 0.</summary>
    private static long ToLong(object? value)
    {
        try { return value is null ? -1 : Convert.ToInt64(value); }
        catch { return -1; }
    }

    private static PagefileActionOutcome RestoreAutomatic()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\cimv2");
            scope.Options.EnablePrivileges = true;         // enable the held SeCreatePagefilePrivilege for this call
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_ComputerSystem"));
            using var cs = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (cs is null) return PagefileActionOutcome.Failed;

            cs["AutomaticManagedPagefile"] = true;
            cs.Put(new PutOptions { Type = PutType.UpdateOnly });

            // Honesty: never trust the Put — re-read and report the MEASURED state. A refused write reads back false.
            return PagefileActionOutcome.FromVerified(ReadAutomaticManaged());
        }
        catch
        {
            return PagefileActionOutcome.Failed;
        }
    }
}
