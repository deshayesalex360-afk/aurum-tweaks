using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

/// <summary>
/// One Windows power scheme exactly as <c>powercfg</c> reports it. <see cref="Id"/> is the GUID that uniquely
/// identifies the scheme (and is what we hand back to <c>powercfg /setactive</c>); <see cref="Name"/> is the
/// friendly form shown to the user; <see cref="IsActive"/> mirrors the <c>*</c> marker powercfg prints. Nothing
/// here is synthesised — a scheme that isn't installed never appears.
/// </summary>
public sealed record PowerScheme(Guid Id, string Name, bool IsActive, string? Advice = null)
{
    public string IdString => Id.ToString();

    /// <summary>The badge text for the currently-active plan (empty otherwise).</summary>
    public string StateDisplay => IsActive ? "● Actif" : string.Empty;

    public string ActivateLabel => "Activer";
}

/// <summary>The live power-plan picture: every installed scheme, the active one's display name, and whether
/// « Performances ultimes » is already present (so the page can word its button honestly).</summary>
public sealed record PowerPlanReport(IReadOnlyList<PowerScheme> Schemes, string? ActiveName, bool UltimatePresent);

/// <summary>
/// Parses <c>powercfg /list</c> (and <c>-duplicatescheme</c>) output — pure, so the parsing is pinned by tests.
/// The honesty point: scheme identity rides on the <b>GUID</b>, which is pure ASCII and therefore immune to the
/// console code-page mojibake that can mangle the localized friendly name on a non-English Windows. A line
/// without a GUID (the header, the dashes) contributes no scheme; the active plan is the one powercfg flags
/// with a trailing <c>*</c>.
/// </summary>
public static class PowerSchemeParser
{
    private static readonly Regex GuidRx = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    public static IReadOnlyList<PowerScheme> ParseList(string? stdout)
    {
        var result = new List<PowerScheme>();
        if (string.IsNullOrEmpty(stdout)) return result;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();               // also strips the trailing '\r' from CRLF output
            var match = GuidRx.Match(line);
            if (!match.Success || !Guid.TryParse(match.Value, out var id)) continue;

            // powercfg marks the active scheme with a trailing '*' after the "(name)".
            var isActive = line.EndsWith("*", StringComparison.Ordinal);
            result.Add(new PowerScheme(id, ExtractName(line), isActive));
        }
        return result;
    }

    /// <summary>The first GUID in <c>powercfg -duplicatescheme</c> output — the freshly-created scheme to activate.</summary>
    public static Guid? FirstGuid(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;
        var match = GuidRx.Match(stdout);
        return match.Success && Guid.TryParse(match.Value, out var id) ? id : null;
    }

    // The friendly name powercfg prints in parentheses after the GUID, e.g. "(Balanced)" → "Balanced".
    private static string ExtractName(string line)
    {
        int open = line.IndexOf('(');
        int close = line.LastIndexOf(')');
        return open >= 0 && close > open ? line.Substring(open + 1, close - open - 1).Trim() : string.Empty;
    }
}

/// <summary>
/// The well-known Windows power schemes and stable French labels/advice for them — pure and tested. We prefer
/// these labels over powercfg's localized name (which can arrive mojibaked through the console code page), and
/// fall back to the reported name only for genuinely custom schemes. The advice is factual guidance about what a
/// plan does to CPU frequency behaviour — never a fabricated FPS/latency figure.
/// </summary>
public static class PowerSchemeCatalog
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>A stable French label for a known scheme, or <c>null</c> for an unrecognised/custom one.</summary>
    public static string? Label(Guid id) =>
        id == Balanced ? "Utilisation normale (équilibré)"
        : id == HighPerformance ? "Performances élevées"
        : id == PowerSaver ? "Économie d'énergie"
        : id == Ultimate ? "Performances ultimes"
        : null;

    /// <summary>Plain-language guidance for a known scheme, or <c>null</c> for an unrecognised/custom one.</summary>
    public static string? Advice(Guid id) =>
        id == Balanced ? "Le mode par défaut de Windows — bon compromis performances / consommation."
        : id == HighPerformance ? "Empêche le throttling du CPU et garde les fréquences hautes — recommandé en jeu sur PC de bureau."
        : id == PowerSaver ? "Bride les performances pour économiser la batterie — déconseillé en jeu."
        : id == Ultimate ? "Latence minimale (éditions Pro / Workstation) — idéal en jeu sur secteur, consommation plus élevée."
        : null;
}

/// <summary>What it takes to surface and activate « Performances ultimes », decided purely from the current
/// scheme list. Two paths because Ultimate is hidden on many Windows editions: when it's already installed we
/// just activate it; otherwise we must DUPLICATE the well-known base scheme (which yields a fresh GUID) — the
/// Microsoft-sanctioned way to surface it.</summary>
public enum UltimateActionKind { ActivateExisting, Duplicate }

public sealed record UltimateAction(UltimateActionKind Kind, Guid Scheme)
{
    public static UltimateAction Resolve(IReadOnlyList<PowerScheme> schemes)
    {
        // Present by the well-known GUID (native on Pro/Workstation) → just activate it.
        var byId = schemes.FirstOrDefault(s => s.Id == PowerSchemeCatalog.Ultimate);
        if (byId is not null) return new UltimateAction(UltimateActionKind.ActivateExisting, byId.Id);

        // A previously-duplicated Ultimate keeps the ASCII (code-page-safe) "Ultimate" name but a fresh GUID.
        var byName = schemes.FirstOrDefault(s => s.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return new UltimateAction(UltimateActionKind.ActivateExisting, byName.Id);

        // Hidden on this edition → duplicate the base scheme to surface it, then the service activates the copy.
        return new UltimateAction(UltimateActionKind.Duplicate, PowerSchemeCatalog.Ultimate);
    }
}

/// <summary>
/// Reads the (AC, DC) "current power setting index" pair from a single-setting <c>powercfg /q SCHEME_CURRENT SUB SETTING</c>
/// dump — pure, so the parse is pinned by tests. Robust and locale-independent by construction: for ONE setting powercfg
/// always prints the "possible range" hex values first, then exactly the two "current index" hex values, AC then DC, in
/// that order. We read only the ASCII <c>0x…</c> tokens (never the localized labels, which mojibake through a non-English
/// console code page) and take the LAST TWO. Fewer than two tokens — an error, a denied query, an unknown setting — yields
/// <c>(null, null)</c>: an honest « indisponible », never a fabricated zero.
/// </summary>
public static class PowerCfgProcessorQuery
{
    private static readonly Regex HexRx = new(@"0x[0-9a-fA-F]+", RegexOptions.Compiled);

    public static (int? Ac, int? Dc) ParseCurrentAcDc(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return (null, null);
        var hex = HexRx.Matches(stdout);
        if (hex.Count < 2) return (null, null);
        return (ParseHex(hex[hex.Count - 2].Value), ParseHex(hex[hex.Count - 1].Value));
    }

    private static int? ParseHex(string token) =>
        int.TryParse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>
/// The active power plan's processor knobs, read live from <c>powercfg</c> « sur secteur » : minimum and maximum processor
/// state, and the core-parking floor. Every value is the real powercfg index or « — » when it couldn't be read; nothing
/// is synthesised, and no FPS is promised — only the factual frequency behaviour each setting produces.
/// </summary>
public sealed record ProcessorPowerDetail(int? MinThrottlePercent, int? MaxThrottlePercent, int? MinUnparkedCoresPercent, bool QueryOk)
{
    public string MinStateDisplay => Pct(MinThrottlePercent);
    public string MaxStateDisplay => Pct(MaxThrottlePercent);

    /// <summary>Core parking read honestly: a 100 % unparked floor means parking is OFF; below that Windows may park cores.</summary>
    public string CoreParkingDisplay => MinUnparkedCoresPercent switch
    {
        null => "—",
        >= 100 => "Désactivé — tous les cœurs restent actifs",
        var p => $"Actif — Windows peut parquer jusqu'à {100 - p} % des cœurs au repos"
    };

    /// <summary>A factual one-liner about what these values do to CPU frequency — never an invented metric.</summary>
    public string Interpretation
    {
        get
        {
            if (!QueryOk)
                return "Détail indisponible — powercfg n'a pas pu lire les paramètres processeur du plan actif (droits insuffisants ou source absente).";
            if (MaxThrottlePercent is { } max && max < 100)
                return $"Fréquence maximale bridée à {max} % par le plan — un plafond inhabituel, pénalisant en jeu.";
            if (MinThrottlePercent is 100)
                return "État minimal à 100 % : le CPU ne réduit jamais sa fréquence — latence minimale, mais consommation et chaleur plus élevées au repos.";
            if (MinThrottlePercent is { } min)
                return $"État minimal à {min} % : le CPU réduit sa fréquence au repos — comportement normal et économe.";
            return "Paramètres processeur lus depuis le plan actif.";
        }
    }

    private static string Pct(int? v) => v is { } n ? $"{n} %" : "—";
}

public sealed record ProcessorPowerTuning(int MinThrottlePercent, int MaxThrottlePercent, int MinUnparkedCoresPercent)
{
    public static ProcessorPowerTuning? FromDetail(ProcessorPowerDetail detail)
    {
        if (!detail.QueryOk ||
            detail.MinThrottlePercent is not { } min ||
            detail.MaxThrottlePercent is not { } max ||
            detail.MinUnparkedCoresPercent is not { } cores)
            return null;

        var tuning = new ProcessorPowerTuning(min, max, cores);
        return ProcessorPowerTuningPlan.IsValid(tuning) ? tuning : null;
    }
}

public sealed record ProcessorPowerWrite(string Label, Guid Setting, int Value);

/// <summary>
/// Pure planner for the editable processor power-management sliders. It writes only documented <c>powercfg</c>
/// PPM settings for the active AC plan: minimum/maximum processor state and the minimum unparked-core floor.
/// No MSR, driver, firmware, NVRAM, or registry path is ever produced here; apply and revert use the same writes
/// with different values, so the UI can restore the exact baseline it read.
/// </summary>
public static class ProcessorPowerTuningPlan
{
    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid ProcThrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    public static readonly Guid ProcThrottleMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    public static readonly Guid ProcParkMinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");

    public const string ApplyCurrentSchemeArgs = "/setactive SCHEME_CURRENT";

    public static bool IsValid(ProcessorPowerTuning tuning) =>
        IsPercent(tuning.MinThrottlePercent) &&
        IsPercent(tuning.MaxThrottlePercent) &&
        IsPercent(tuning.MinUnparkedCoresPercent) &&
        tuning.MinThrottlePercent <= tuning.MaxThrottlePercent;

    public static IReadOnlyList<ProcessorPowerWrite> Build(ProcessorPowerTuning tuning) =>
        !IsValid(tuning)
            ? Array.Empty<ProcessorPowerWrite>()
            : new[]
            {
                new ProcessorPowerWrite("État processeur minimal", ProcThrottleMin, tuning.MinThrottlePercent),
                new ProcessorPowerWrite("État processeur maximal", ProcThrottleMax, tuning.MaxThrottlePercent),
                new ProcessorPowerWrite("Cœurs actifs minimum", ProcParkMinCores, tuning.MinUnparkedCoresPercent),
            };

    public static string BuildSetAcArgs(ProcessorPowerWrite write) =>
        $"/setacvalueindex SCHEME_CURRENT {SubProcessor} {write.Setting} {write.Value}";

    public static string CoreParkingDraftDisplay(int minUnparkedPercent) =>
        minUnparkedPercent >= 100
            ? "parcage désactivé : 100 % des cœurs restent disponibles"
            : $"Windows peut parquer jusqu'à {100 - minUnparkedPercent} % des cœurs au repos";

    private static bool IsPercent(int value) => value is >= 0 and <= 100;
}

/// <summary>
/// The "plan d'alimentation" manager — thin <c>powercfg</c> glue around the pure cores above. Honest by
/// construction: it reports the live active scheme powercfg gives us, every switch is a real
/// <c>powercfg /setactive</c> the caller re-reads afterwards, and « Performances ultimes » is surfaced only via
/// the sanctioned duplicate-scheme step (no registry poking, no fabricated metric).
/// </summary>
public sealed class PowerPlanService : IPowerPlanService
{
    public Task<PowerPlanReport> GetReportAsync() => Task.Run(GetReport);
    public Task<bool> ActivateAsync(Guid scheme) => Task.Run(() => Activate(scheme));
    public Task<bool> EnableUltimateAsync() => Task.Run(EnableUltimate);
    public Task<ProcessorPowerDetail> GetProcessorDetailAsync() => Task.Run(GetProcessorDetail);
    public Task<bool> SetProcessorTuningAsync(ProcessorPowerTuning tuning) => Task.Run(() => SetProcessorTuning(tuning));

    private static ProcessorPowerDetail GetProcessorDetail()
    {
        int? min = QueryAcIndex(ProcessorPowerTuningPlan.ProcThrottleMin);
        int? max = QueryAcIndex(ProcessorPowerTuningPlan.ProcThrottleMax);
        int? cores = QueryAcIndex(ProcessorPowerTuningPlan.ProcParkMinCores);
        // QueryOk if at least one knob read back — a total failure (no rights, source absent) hides the card honestly.
        return new ProcessorPowerDetail(min, max, cores, min.HasValue || max.HasValue || cores.HasValue);
    }

    private static int? QueryAcIndex(Guid setting)
    {
        var (exit, stdout) = RunCapture("powercfg.exe", $"/q SCHEME_CURRENT {ProcessorPowerTuningPlan.SubProcessor} {setting}");
        return exit == 0 ? PowerCfgProcessorQuery.ParseCurrentAcDc(stdout).Ac : null;
    }

    private static PowerPlanReport GetReport()
    {
        var (_, stdout) = RunCapture("powercfg.exe", "/list");
        // Decorate known schemes with stable French labels/advice; keep powercfg's own name for custom ones.
        var schemes = PowerSchemeParser.ParseList(stdout)
            .Select(s => s with
            {
                Name = PowerSchemeCatalog.Label(s.Id) ?? (string.IsNullOrEmpty(s.Name) ? "Plan personnalisé" : s.Name),
                Advice = PowerSchemeCatalog.Advice(s.Id)
            })
            .ToList();

        var active = schemes.FirstOrDefault(s => s.IsActive);
        bool ultimatePresent = schemes.Any(s =>
            s.Id == PowerSchemeCatalog.Ultimate || s.Name.Contains("ultim", StringComparison.OrdinalIgnoreCase));
        return new PowerPlanReport(schemes, active?.Name, ultimatePresent);
    }

    private static bool Activate(Guid scheme)
    {
        if (scheme == Guid.Empty) return false;
        var (exit, _) = RunCapture("powercfg.exe", $"/setactive {scheme}");
        return exit == 0;
    }

    private static bool EnableUltimate()
    {
        var action = UltimateAction.Resolve(GetReport().Schemes);
        if (action.Kind == UltimateActionKind.ActivateExisting) return Activate(action.Scheme);

        var (exit, stdout) = RunCapture("powercfg.exe", $"-duplicatescheme {action.Scheme}");
        if (exit != 0) return false;
        // Activate the copy powercfg just created; if its GUID couldn't be read back, the base id is the fallback.
        return Activate(PowerSchemeParser.FirstGuid(stdout) ?? action.Scheme);
    }

    private static bool SetProcessorTuning(ProcessorPowerTuning tuning)
    {
        var writes = ProcessorPowerTuningPlan.Build(tuning);
        if (writes.Count == 0) return false;

        bool allOk = true;
        foreach (var write in writes)
        {
            var (exit, _) = RunCapture("powercfg.exe", ProcessorPowerTuningPlan.BuildSetAcArgs(write));
            allOk &= exit == 0;
        }

        var (applyExit, _) = RunCapture("powercfg.exe", ProcessorPowerTuningPlan.ApplyCurrentSchemeArgs);
        return allOk && applyExit == 0;
    }

    // Unlike the tweak engine's fire-and-forget shell, we must CAPTURE powercfg's stdout to parse the scheme list.
    private static (int ExitCode, string StdOut) RunCapture(string fileName, string args) =>
        ProcessRunner.Capture(fileName, args);
}
