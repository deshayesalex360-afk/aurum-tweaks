using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

/// <summary>
/// A coarse bucket for a curated scheduled task — drives the French label/advice and, crucially, the one honest
/// distinction the page makes: everything here is telemetry/privacy noise that's <b>safe to disable</b> except
/// <see cref="Maintenance"/>, which is genuinely useful (drive optimisation) and flagged « à conserver » to push
/// back on the common bad advice that tells people to switch it off.
/// </summary>
public enum ScheduledTaskCategory
{
    Telemetry,
    CustomerExperience,
    Feedback,
    ErrorReporting,
    Maps,
    Diagnostics,
    Maintenance
}

/// <summary>One known Windows scheduled task we let the user manage. <see cref="FullPath"/> is the exact
/// Task Scheduler path (<c>\Microsoft\Windows\…\Name</c>) handed verbatim to <c>schtasks /Change /TN</c>.</summary>
public sealed record ScheduledTaskInfo(string FullPath, string Label, ScheduledTaskCategory Category);

/// <summary>Whether a curated task is live on this machine — read from <c>Get-ScheduledTask</c>, never guessed.</summary>
public enum ScheduledTaskLiveState
{
    /// <summary>Present and able to fire (PowerShell State Ready/Running/Queued).</summary>
    Enabled,

    /// <summary>Present but switched off (PowerShell State Disabled).</summary>
    Disabled,

    /// <summary>Not installed on this Windows edition — shown honestly, never toggled.</summary>
    Absent
}

/// <summary>
/// The curated set of well-known Windows scheduled tasks worth surfacing, plus the stable French label/advice for
/// each category — pure and tested. Deliberately a <b>short, hand-picked allow-list</b> (telemetry, CEIP, feedback,
/// error reporting, maps, diagnostics) rather than a dump of every task on the system: a blind "disable scheduled
/// tasks" list is a footgun that can break Windows. The one non-telemetry entry, <see cref="ScheduledTaskCategory.Maintenance"/>
/// (drive optimisation), is included precisely so the page can tell the truth — « à conserver », not « désactive ».
/// </summary>
public static class ScheduledTaskCatalog
{
    public static IReadOnlyList<ScheduledTaskInfo> Tasks { get; } = new[]
    {
        // Telemetry — the Application Experience appraiser feeds Microsoft's diagnostic pipeline.
        new ScheduledTaskInfo(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", "Évaluateur de compatibilité", ScheduledTaskCategory.Telemetry),
        new ScheduledTaskInfo(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater", "Mise à jour des données de compatibilité", ScheduledTaskCategory.Telemetry),

        // Customer Experience Improvement Program (CEIP / SQM).
        new ScheduledTaskInfo(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", "CEIP — Consolidator", ScheduledTaskCategory.CustomerExperience),
        new ScheduledTaskInfo(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip", "CEIP — périphériques USB", ScheduledTaskCategory.CustomerExperience),
        new ScheduledTaskInfo(@"\Microsoft\Windows\Autochk\Proxy", "CEIP — Autochk Proxy (SQM)", ScheduledTaskCategory.CustomerExperience),

        // Windows Feedback (SIUF).
        new ScheduledTaskInfo(@"\Microsoft\Windows\Feedback\Siuf\DmClient", "Commentaires Windows", ScheduledTaskCategory.Feedback),
        new ScheduledTaskInfo(@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload", "Commentaires Windows (scénarios)", ScheduledTaskCategory.Feedback),

        // Windows Error Reporting queue.
        new ScheduledTaskInfo(@"\Microsoft\Windows\Windows Error Reporting\QueueReporting", "Rapports d'erreurs Windows", ScheduledTaskCategory.ErrorReporting),

        // Offline Maps — network/data usage for an app most gamers never open.
        new ScheduledTaskInfo(@"\Microsoft\Windows\Maps\MapsUpdateTask", "Mise à jour des cartes hors connexion", ScheduledTaskCategory.Maps),
        new ScheduledTaskInfo(@"\Microsoft\Windows\Maps\MapsToastTask", "Notifications Cartes", ScheduledTaskCategory.Maps),

        // Diagnostics that phone home (SMART data, power-efficiency analysis).
        new ScheduledTaskInfo(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", "Collecte de diagnostics disque", ScheduledTaskCategory.Diagnostics),
        new ScheduledTaskInfo(@"\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem", "Diagnostic d'efficacité énergétique", ScheduledTaskCategory.Diagnostics),

        // Maintenance — genuinely useful, listed only so the page can say « à conserver » honestly.
        new ScheduledTaskInfo(@"\Microsoft\Windows\Defrag\ScheduledDefrag", "Optimisation des lecteurs (défrag HDD / TRIM SSD)", ScheduledTaskCategory.Maintenance),
    };

    public static string CategoryLabel(ScheduledTaskCategory category) => category switch
    {
        ScheduledTaskCategory.Telemetry => "Télémétrie",
        ScheduledTaskCategory.CustomerExperience => "Programme d'amélioration (CEIP)",
        ScheduledTaskCategory.Feedback => "Commentaires",
        ScheduledTaskCategory.ErrorReporting => "Rapports d'erreurs",
        ScheduledTaskCategory.Maps => "Cartes",
        ScheduledTaskCategory.Diagnostics => "Diagnostics",
        ScheduledTaskCategory.Maintenance => "Maintenance",
        _ => "Autre"
    };

    public static string Advice(ScheduledTaskCategory category) => category switch
    {
        ScheduledTaskCategory.Telemetry => "Envoie des données d'utilisation à Microsoft. Désactivation sans risque pour le système.",
        ScheduledTaskCategory.CustomerExperience => "Remonte des statistiques d'usage à Microsoft. Désactivation sans risque.",
        ScheduledTaskCategory.Feedback => "Sollicite et transmet tes commentaires à Microsoft. Désactivation sans risque.",
        ScheduledTaskCategory.ErrorReporting => "Met en file et envoie les rapports de plantage à Microsoft. Désactivable (l'Observateur d'événements reste consultable).",
        ScheduledTaskCategory.Maps => "Met à jour les cartes hors connexion et leurs notifications — consomme réseau et données. Désactivable si tu n'utilises pas l'app Cartes.",
        ScheduledTaskCategory.Diagnostics => "Collecte des diagnostics matériels/énergie et les remonte à Microsoft. Désactivation sans risque.",
        ScheduledTaskCategory.Maintenance => "À conserver : optimise tes lecteurs (défragmentation HDD, TRIM SSD). Utile — ne la désactive pas sans raison précise.",
        _ => "Impact inconnu."
    };

    /// <summary>Everything here is recommended off except the one genuinely-useful maintenance task.</summary>
    public static bool RecommendedToDisable(ScheduledTaskCategory category) =>
        category is not ScheduledTaskCategory.Maintenance;
}

/// <summary>
/// Parses the CSV that <c>Get-ScheduledTask | Select TaskPath,TaskName,State | ConvertTo-Csv</c> emits into a
/// "full task path → is it enabled?" map — pure, so it's pinned by tests. The honesty point: we read the live
/// <c>State</c> rather than assume one, and we treat a task as disabled <b>only</b> when PowerShell says so
/// (State "Disabled", or its numeric form "1"); a task we can't find is simply absent from the map, never
/// invented. We key on <c>TaskPath + TaskName</c> because that concatenation is exactly the path
/// <c>schtasks /Change /TN</c> expects.
/// </summary>
public static class ScheduledTaskStateParser
{
    public static IReadOnlyDictionary<string, bool> Parse(string? csv)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(csv)) return map;

        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim();                       // also strips the trailing '\r' from CRLF output
            if (line.Length == 0 || line[0] == '#') continue; // blank / a stray #TYPE header

            var fields = CsvRow.Split(line);
            if (fields.Count < 3) continue;
            if (fields[0].Equals("TaskPath", StringComparison.Ordinal)) continue; // the column header row

            // TaskPath already ends with '\', so TaskPath + TaskName is the full "\Microsoft\…\Name" path.
            var fullPath = fields[0] + fields[1];
            if (string.IsNullOrWhiteSpace(fullPath)) continue;

            map[fullPath] = !IsDisabledState(fields[2]);
        }
        return map;
    }

    // PowerShell's ScheduledTaskState enum renders "Disabled" (text) or "1" (numeric) for a switched-off task;
    // anything else (Ready/Running/Queued/Unknown) means it can still fire, so we treat it as enabled.
    private static bool IsDisabledState(string state)
    {
        var s = state.Trim();
        return s.Equals("Disabled", StringComparison.OrdinalIgnoreCase) || s == "1";
    }
}

/// <summary>One curated task joined with its live on-system state — everything shown is real (the task exists and
/// reports this state) or honestly marked <see cref="ScheduledTaskLiveState.Absent"/>.</summary>
public sealed record ScheduledTaskEntry(ScheduledTaskInfo Info, ScheduledTaskLiveState State)
{
    public string Label => Info.Label;
    public string FullPath => Info.FullPath;
    public string CategoryDisplay => ScheduledTaskCatalog.CategoryLabel(Info.Category);
    public string Advice => ScheduledTaskCatalog.Advice(Info.Category);
    public bool RecommendedToDisable => ScheduledTaskCatalog.RecommendedToDisable(Info.Category);

    public bool IsPresent => State != ScheduledTaskLiveState.Absent;
    public bool IsEnabled => State == ScheduledTaskLiveState.Enabled;

    public string StateDisplay => State switch
    {
        ScheduledTaskLiveState.Enabled => "Active",
        ScheduledTaskLiveState.Disabled => "Désactivée",
        _ => "Absente sur ce PC"
    };

    /// <summary>The toggle action label, given the current state.</summary>
    public string ToggleLabel => IsEnabled ? "Désactiver" : "Réactiver";

    // View visibility flags — kept as explicit props so the XAML stays declarative.
    public bool ShowToggle => IsPresent;
    public bool ShowDisabledBadge => State == ScheduledTaskLiveState.Disabled;
    public bool ShowAbsentBadge => State == ScheduledTaskLiveState.Absent;
    public bool ShowKeepBadge => IsPresent && !RecommendedToDisable;
}

/// <summary>The live scheduled-task picture: every catalog task joined with its state, plus whether the query
/// itself succeeded (so the page can tell "nothing active" apart from "couldn't read the system").</summary>
public sealed record ScheduledTaskReport(IReadOnlyList<ScheduledTaskEntry> Entries, bool QueryOk)
{
    public int PresentCount => Entries.Count(e => e.IsPresent);

    /// <summary>Present, enabled, AND recommended-off — the actionable "still leaking" count the status line shows.</summary>
    public int EnabledRecommendedCount => Entries.Count(e => e.IsPresent && e.IsEnabled && e.RecommendedToDisable);
}

/// <summary>
/// Joins the curated <see cref="ScheduledTaskCatalog"/> with a live "path → enabled?" map into ordered entries —
/// pure, so the join and ordering are pinned by tests. Ordering surfaces the actionable rows first: present &amp;
/// enabled &amp; recommended-off, then the « à conserver » maintenance task, then already-disabled, then absent.
/// </summary>
public static class ScheduledTaskResolver
{
    public static IReadOnlyList<ScheduledTaskEntry> Resolve(
        IReadOnlyList<ScheduledTaskInfo> catalog,
        IReadOnlyDictionary<string, bool> liveEnabledByPath)
    {
        return catalog
            .Select(info =>
            {
                ScheduledTaskLiveState state = liveEnabledByPath.TryGetValue(info.FullPath, out var enabled)
                    ? (enabled ? ScheduledTaskLiveState.Enabled : ScheduledTaskLiveState.Disabled)
                    : ScheduledTaskLiveState.Absent;
                return new ScheduledTaskEntry(info, state);
            })
            .OrderBy(Rank)
            .ThenBy(e => e.Info.Category)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // 0 = actionable telemetry still on, 1 = useful task still on (à conserver), 2 = already off, 3 = absent.
    private static int Rank(ScheduledTaskEntry e)
    {
        if (!e.IsPresent) return 3;
        if (!e.IsEnabled) return 2;
        return e.RecommendedToDisable ? 0 : 1;
    }
}

/// <summary>
/// The « Tâches planifiées » manager — thin glue around the pure cores above. Honest &amp; reversible by
/// construction: it reads the live state of a curated allow-list via <c>Get-ScheduledTask</c> (never inventing a
/// task), and every toggle is a real <c>schtasks /Change /TN … /Disable|/Enable</c> — exact inverses — after which
/// the caller re-reads the system. State is read from PowerShell's locale-invariant <c>State</c> enum rather than
/// schtasks' localized text, so a French Windows isn't misread.
/// </summary>
public sealed class ScheduledTaskService : IScheduledTaskService
{
    public Task<ScheduledTaskReport> GetReportAsync() => Task.Run(GetReport);

    public Task<bool> SetEnabledAsync(string fullPath, bool enable) => Task.Run(() => SetEnabled(fullPath, enable));

    private static ScheduledTaskReport GetReport()
    {
        // Select only the three columns we need; the State enum text (Ready/Disabled/…) is code-page-immune.
        var (_, stdout) = ProcessRunner.Capture("powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-ScheduledTask | Select-Object TaskPath,TaskName,State | ConvertTo-Csv -NoTypeInformation\"");

        var live = ScheduledTaskStateParser.Parse(stdout);
        var entries = ScheduledTaskResolver.Resolve(ScheduledTaskCatalog.Tasks, live);
        // A healthy machine lists 100+ tasks; an empty map means the query failed (no PowerShell module / error).
        return new ScheduledTaskReport(entries, QueryOk: live.Count > 0);
    }

    private static bool SetEnabled(string fullPath, bool enable)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;
        // Mirror the tweak engine's ScheduledTask op exactly: schtasks /Change /TN "<path>" /Enable|/Disable.
        var (exit, _) = ProcessRunner.Capture("schtasks.exe",
            $"/Change /TN \"{fullPath}\" /{(enable ? "Enable" : "Disable")}");
        return exit == 0;
    }
}
