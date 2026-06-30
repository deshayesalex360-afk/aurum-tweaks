using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  « Priorité & affinité CPU » — a Process-Lasso-class manager. Everything here is real and honest:
//  the priority class and CPU-affinity mask are read straight from Windows (System.Diagnostics.Process),
//  every change is a genuine SetPriorityClass / SetProcessAffinityMask the ViewModel RE-READS afterwards so
//  a refused write surfaces the unchanged real state (never a fabricated "done"), a process Windows won't let
//  us touch (anti-cheat / protected / higher integrity) is shown as inaccessible WITHOUT action buttons (no
//  dead control), and the page never promises FPS — only scheduler consistency. Persistence is opt-in through
//  a visible Windows scheduled task and local JSON rules; no hidden service, ring-0 driver, injection, or firmware
//  path is involved.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Windows process priority classes, plus Unknown for a class we couldn't read.</summary>
public enum ProcessPriorityLevel { Unknown, Idle, BelowNormal, Normal, AboveNormal, High, Realtime }

/// <summary>
/// Pure mapping between our <see cref="ProcessPriorityLevel"/> and the Win32 <see cref="ProcessPriorityClass"/>,
/// plus the load-bearing honesty decision of which levels the UI is allowed to SET. Realtime is deliberately
/// never offered (it outranks the OS threads that service keyboard/mouse/audio — setting it can freeze the box),
/// and Idle/BelowNormal aren't offered on a "boost a game" page (they would slow it). Both still parse on the
/// read side so an already-Realtime process is labelled truthfully rather than mislabelled.
/// </summary>
public static class PriorityLevels
{
    public static ProcessPriorityLevel FromClass(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.Idle        => ProcessPriorityLevel.Idle,
        ProcessPriorityClass.BelowNormal => ProcessPriorityLevel.BelowNormal,
        ProcessPriorityClass.Normal      => ProcessPriorityLevel.Normal,
        ProcessPriorityClass.AboveNormal => ProcessPriorityLevel.AboveNormal,
        ProcessPriorityClass.High        => ProcessPriorityLevel.High,
        ProcessPriorityClass.RealTime    => ProcessPriorityLevel.Realtime,
        _                                => ProcessPriorityLevel.Unknown
    };

    public static ProcessPriorityClass ToClass(ProcessPriorityLevel l) => l switch
    {
        ProcessPriorityLevel.Idle        => ProcessPriorityClass.Idle,
        ProcessPriorityLevel.BelowNormal => ProcessPriorityClass.BelowNormal,
        ProcessPriorityLevel.Normal      => ProcessPriorityClass.Normal,
        ProcessPriorityLevel.AboveNormal => ProcessPriorityClass.AboveNormal,
        ProcessPriorityLevel.High        => ProcessPriorityClass.High,
        ProcessPriorityLevel.Realtime    => ProcessPriorityClass.RealTime,
        _                                => ProcessPriorityClass.Normal   // never write Unknown back
    };

    /// <summary>The only levels a button may apply — Normal (reset), AboveNormal, High. See type remarks for why.</summary>
    public static readonly IReadOnlyList<ProcessPriorityLevel> Offered = new[]
    {
        ProcessPriorityLevel.Normal, ProcessPriorityLevel.AboveNormal, ProcessPriorityLevel.High
    };

    public static bool IsOffered(ProcessPriorityLevel l) => Offered.Contains(l);

    public static string Label(ProcessPriorityLevel l) => l switch
    {
        ProcessPriorityLevel.Idle        => "Inactive",
        ProcessPriorityLevel.BelowNormal => "Sous la normale",
        ProcessPriorityLevel.Normal      => "Normale",
        ProcessPriorityLevel.AboveNormal => "Au-dessus de la normale",
        ProcessPriorityLevel.High        => "Haute",
        ProcessPriorityLevel.Realtime    => "Temps réel",
        _                                => "Inconnue"
    };

    /// <summary>Short label for a compact button.</summary>
    public static string ShortLabel(ProcessPriorityLevel l) => l switch
    {
        ProcessPriorityLevel.Normal      => "Normale",
        ProcessPriorityLevel.AboveNormal => "Au-dessus",
        ProcessPriorityLevel.High        => "Haute",
        _                                => Label(l)
    };
}

/// <summary>
/// The CPU's logical-core layout. <see cref="PerformanceCoreIndices"/> is the set of logical processors on the
/// highest efficiency class (the P-cores on an Intel hybrid part); it is empty when the probe failed OR when every
/// core shares one efficiency class (a classic non-hybrid CPU) — in both cases there is honestly no P/E choice to
/// offer, so <see cref="IsHybrid"/> is false and only « all cores » is proposed.
/// </summary>
public sealed record CpuLayout(int LogicalCount, IReadOnlyList<int> PerformanceCoreIndices)
{
    public bool IsHybrid => PerformanceCoreIndices.Count > 0 && PerformanceCoreIndices.Count < LogicalCount;
    public int PerformanceCoreCount => PerformanceCoreIndices.Count;
    public int EfficiencyCoreCount => IsHybrid ? LogicalCount - PerformanceCoreIndices.Count : 0;
    public static CpuLayout Flat(int logicalCount) => new(Math.Max(0, logicalCount), Array.Empty<int>());
}

/// <summary>The affinity presets the page can apply.</summary>
public enum AffinityStrategy { AllCores, PerformanceCores }

/// <summary>
/// Pure affinity-mask math over a <see cref="CpuLayout"/>. The « all cores » reset and the « performance cores »
/// preset are the only two — and the latter is only valid on a hybrid CPU. Every mask is validated to be non-empty
/// (an all-zero affinity is rejected by Windows and would be a footgun), and the &gt;=64-logical case is handled so a
/// 64-thread part can't trip the <c>1 &lt;&lt; 64</c> shift trap.
/// </summary>
public static class AffinityPlan
{
    public static ulong AllMask(CpuLayout layout) =>
        layout.LogicalCount >= 64 ? ulong.MaxValue : (1UL << layout.LogicalCount) - 1UL;

    public static ulong Build(AffinityStrategy strategy, CpuLayout layout)
    {
        ulong all = AllMask(layout);
        if (strategy == AffinityStrategy.PerformanceCores && layout.IsHybrid)
        {
            ulong m = 0;
            foreach (var i in layout.PerformanceCoreIndices)
                if (i is >= 0 and < 64) m |= 1UL << i;
            return m == 0 ? all : m;   // never hand Windows an empty (invalid) mask
        }
        return all;
    }

    public static IReadOnlyList<AffinityStrategy> Offered(CpuLayout layout) =>
        layout.IsHybrid
            ? new[] { AffinityStrategy.AllCores, AffinityStrategy.PerformanceCores }
            : new[] { AffinityStrategy.AllCores };

    public static bool IsOffered(AffinityStrategy s, CpuLayout layout) => Offered(layout).Contains(s);
}

/// <summary>Pure, culture-free formatting of an affinity mask into a readable French core description.</summary>
public static class AffinityFormat
{
    public static int CoreCount(ulong mask) => BitOperations.PopCount(mask);

    public static string Describe(ulong mask, CpuLayout layout)
    {
        ulong all = AffinityPlan.AllMask(layout);
        ulong effective = mask & all;
        if (effective == all && layout.LogicalCount > 0)
            return $"Tous les cœurs ({layout.LogicalCount})";

        var cores = SetBits(effective, layout.LogicalCount);
        if (cores.Count == 0) return "—";
        return $"{cores.Count} cœur(s) : {Ranges(cores)}";
    }

    /// <summary>The set bit indices below <paramref name="limit"/>, ascending.</summary>
    public static IReadOnlyList<int> SetBits(ulong mask, int limit)
    {
        var list = new List<int>();
        int max = Math.Min(limit <= 0 ? 64 : limit, 64);
        for (int i = 0; i < max; i++)
            if ((mask & (1UL << i)) != 0) list.Add(i);
        return list;
    }

    /// <summary>Collapse a sorted index list into compact ranges, e.g. [0,1,2,3,6,8,9] → "0-3, 6, 8-9".</summary>
    public static string Ranges(IReadOnlyList<int> sorted)
    {
        if (sorted.Count == 0) return string.Empty;
        var parts = new List<string>();
        int start = sorted[0], prev = sorted[0];
        for (int k = 1; k < sorted.Count; k++)
        {
            int cur = sorted[k];
            if (cur == prev + 1) { prev = cur; continue; }
            parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            start = prev = cur;
        }
        parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Pure cross-reference of a running process's executable path against the installed-game list, so a running
/// game (and its anti-cheat) can be flagged. Matching is by install-directory containment (a game's exe lives
/// under its InstallDirectory) — drive- and case-insensitive, with a trailing separator so « …\Valorant » can't
/// spuriously match « …\ValorantTool ».
/// </summary>
public static class ProcessGameMatch
{
    public static DetectedGame? Match(string? executablePath, IEnumerable<DetectedGame> games)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        var exe = Normalize(executablePath);
        foreach (var g in games)
        {
            if (string.IsNullOrWhiteSpace(g.InstallDirectory)) continue;
            if (PathStartsWith(exe, Normalize(g.InstallDirectory))) return g;
        }
        return null;
    }

    private static bool PathStartsWith(string normalizedPath, string normalizedDir)
    {
        if (normalizedDir.Length == 0) return false;
        var dir = normalizedDir.TrimEnd('\\');
        if (normalizedPath.Equals(dir, StringComparison.OrdinalIgnoreCase)) return true;
        return normalizedPath.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string s) => s.Replace('/', '\\').Trim();
}

/// <summary>The recommended priority+affinity for a process, plus an optional honest warning.</summary>
public sealed record ControlAdvice(
    ProcessPriorityLevel RecommendedPriority,
    AffinityStrategy RecommendedAffinity,
    string? Warning,
    string Rationale);

/// <summary>
/// Pure recommendation core. A game gets High priority (so background tasks interrupt it less) and, on a hybrid
/// CPU, the performance cores (so the scheduler can't strand it on E-cores); a normal process is left to Windows.
/// An anti-cheat-protected game carries the load-bearing tampering warning — modifying a protected process's
/// priority/affinity from outside can be read by kernel anti-cheat as interference, so the user is told before acting.
/// </summary>
public static class PriorityAffinityAdvice
{
    public static ControlAdvice For(bool isGame, bool hasAntiCheat, CpuLayout layout)
    {
        var priority = isGame ? ProcessPriorityLevel.High : ProcessPriorityLevel.Normal;
        var affinity = isGame && layout.IsHybrid ? AffinityStrategy.PerformanceCores : AffinityStrategy.AllCores;

        string? warning = hasAntiCheat
            ? "Ce jeu utilise un anticheat. Modifier la priorité ou l'affinité d'un processus protégé PEUT être "
              + "interprété comme une altération — à appliquer en connaissance de cause, de préférence hors parties classées."
            : null;

        string rationale = isGame
            ? (layout.IsHybrid
                ? "Jeu : priorité haute pour limiter les interruptions des tâches de fond, et cœurs performance pour "
                  + "éviter que Windows place le jeu sur des cœurs efficients."
                : "Jeu : priorité haute pour limiter les interruptions des tâches de fond.")
            : "Processus standard : Windows gère déjà bien (priorité normale, tous les cœurs).";

        return new ControlAdvice(priority, affinity, warning, rationale);
    }
}

/// <summary>One running process as the page sees it, with its real priority/affinity and all display state.</summary>
public sealed record RunningProcessInfo(
    int Pid,
    string Name,
    string? ExecutablePath,
    bool Accessible,
    ProcessPriorityLevel Priority,
    ulong AffinityMask,
    bool IsGame,
    string Platform,
    bool HasAntiCheat,
    string AntiCheatName,
    long WorkingSetBytes,
    CpuLayout Layout,
    ControlAdvice Advice)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"PID {Pid}" : Name;
    public string PriorityDisplay => PriorityLevels.Label(Priority);
    public string AffinityDisplay => Accessible ? AffinityFormat.Describe(AffinityMask, Layout) : "—";
    public string MemoryDisplay => ByteSize.Format(WorkingSetBytes);
    public string StateDisplay => $"Priorité : {PriorityDisplay}  ·  Affinité : {AffinityDisplay}";

    public string KindDisplay => IsGame
        ? (string.IsNullOrWhiteSpace(Platform) ? "Jeu" : $"Jeu · {Platform}")
        : "Application";

    public string SubtitleDisplay => $"{KindDisplay}  ·  {MemoryDisplay}  ·  PID {Pid}";

    public string RationaleDisplay => Advice.Rationale;
    public bool ShowWarning => Advice.Warning is not null;
    public string WarningDisplay => Advice.Warning ?? string.Empty;

    public bool ShowGameBadge => IsGame;
    public bool ShowAntiCheat => IsGame && HasAntiCheat;
    public string AntiCheatDisplay => AntiCheatName;

    // Honesty: a process we can't read/modify gets an explanation, never a row of dead buttons.
    public bool ShowActions => Accessible;
    public bool ShowInaccessible => !Accessible;
    public bool ShowPerformanceCores => Accessible && Layout.IsHybrid;
    public bool ShowOptimize => Accessible && IsGame;
}

/// <summary>The full picture: every listed process, the detected CPU layout, and whether enumeration worked.</summary>
public sealed record ProcessControlReport(IReadOnlyList<RunningProcessInfo> Processes, CpuLayout Layout, bool QueryOk)
{
    public int Count => Processes.Count;
    public int GameCount => Processes.Count(p => p.IsGame);

    public string CpuSummary => Layout.IsHybrid
        ? $"{Layout.LogicalCount} cœurs logiques · hybride ({Layout.PerformanceCoreCount} performance / {Layout.EfficiencyCoreCount} efficients)"
        : $"{Layout.LogicalCount} cœurs logiques";
}

public sealed record PersistentProcessRule(
    string ProcessName,
    string DisplayName,
    ProcessPriorityLevel Priority,
    long AffinityMask,
    Guid? PowerPlanWhileRunning,
    Guid? PowerPlanWhenIdle,
    DateTime CreatedUtc)
{
    public string PriorityDisplay => PriorityLevels.Label(Priority);
    public string AffinityDisplay => AffinityMask == 0 ? "—" : $"0x{unchecked((ulong)AffinityMask):X}";
    public bool HasPowerPlan => PowerPlanWhileRunning.HasValue;
    public string PowerPlanDisplay => HasPowerPlan
        ? $"Plan perf pendant l'exécution · retour {PowerPlanWhenIdle?.ToString() ?? "non défini"}"
        : "Plan d'alimentation non modifié";
    public string SummaryDisplay => $"{DisplayName} · {PriorityDisplay} · affinité {AffinityDisplay} · {PowerPlanDisplay}";
}

public sealed record ProcessPersistenceReport(
    IReadOnlyList<PersistentProcessRule> Rules,
    bool TaskInstalled,
    string RulesPath,
    string ScriptPath)
{
    public int Count => Rules.Count;
    public string StateDisplay => TaskInstalled
        ? $"Tâche planifiée active · {Count} règle(s) persistante(s)."
        : $"Tâche planifiée absente · {Count} règle(s) enregistrée(s).";
}

/// <summary>
/// Pure persistent-rule planner. It emits a visible scheduled-task contract and an inspectable PowerShell script:
/// match by process name, set a safe priority class, set the exact affinity mask, optionally switch the global Windows
/// power plan while a matching process runs, then restore the captured idle plan when none run. No driver, service,
/// ring-0 helper, injection, or hidden background agent is produced here.
/// </summary>
public static class ProcessPersistencePlan
{
    public const string TaskName = @"\Aurum Tweaks\Process Rules";
    public const string RulesFileName = "process-rules.json";
    public const string ScriptFileName = "ApplyProcessRules.ps1";
    public static readonly Guid HighPerformancePlan = PowerSchemeCatalog.HighPerformance;

    public const string UiLimit =
        "Règles opt-in stockées localement et appliquées par une tâche planifiée Windows visible. Le plan d'alimentation Windows reste global : Aurum le bascule pendant qu'un processus correspondant est détecté, puis restaure le plan capturé quand aucun processus de règle n'est actif. Correspondance par nom de processus, sans driver ni service caché.";

    public static PersistentProcessRule BuildRule(
        RunningProcessInfo process,
        bool includeHighPerformancePlan,
        Guid? currentPowerPlan,
        DateTime createdUtc)
    {
        var mask = AffinityPlan.Build(process.Advice.RecommendedAffinity, process.Layout);
        return new PersistentProcessRule(
            process.Name.Trim(),
            process.DisplayName,
            process.Advice.RecommendedPriority,
            unchecked((long)mask),
            includeHighPerformancePlan ? HighPerformancePlan : null,
            includeHighPerformancePlan ? currentPowerPlan : null,
            createdUtc);
    }

    public static IReadOnlyList<PersistentProcessRule> Upsert(
        IReadOnlyList<PersistentProcessRule> existing,
        PersistentProcessRule rule)
    {
        return existing
            .Where(r => !string.Equals(r.ProcessName, rule.ProcessName, StringComparison.OrdinalIgnoreCase))
            .Append(rule)
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<PersistentProcessRule> Remove(
        IReadOnlyList<PersistentProcessRule> existing,
        string processName) =>
        existing
            .Where(r => !string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public static string RenderScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Aurum Tweaks - règles persistantes priorité/affinité");
        sb.AppendLine("# Visible via la tâche planifiée " + TaskName);
        sb.AppendLine("# Limites: correspondance par nom de processus; power plan global; aucun driver/service caché.");
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine("$RulesPath = Join-Path $PSScriptRoot '" + RulesFileName + "'");
        sb.AppendLine("if (-not (Test-Path -LiteralPath $RulesPath)) { return }");
        sb.AppendLine("$rules = Get-Content -LiteralPath $RulesPath -Raw | ConvertFrom-Json");
        sb.AppendLine("if ($null -eq $rules) { return }");
        sb.AppendLine("if ($rules -isnot [System.Array]) { $rules = @($rules) }");
        sb.AppendLine("function Convert-AurumPriority([int]$value) {");
        sb.AppendLine("    switch ($value) {");
        sb.AppendLine("        3 { 'Normal'; break }");
        sb.AppendLine("        4 { 'AboveNormal'; break }");
        sb.AppendLine("        5 { 'High'; break }");
        sb.AppendLine("        default { $null; break }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("$runningPlan = $null");
        sb.AppendLine("$idlePlan = $null");
        sb.AppendLine("foreach ($rule in $rules) {");
        sb.AppendLine("    $matches = Get-Process -Name $rule.ProcessName -ErrorAction SilentlyContinue");
        sb.AppendLine("    if ($matches) {");
        sb.AppendLine("        foreach ($p in $matches) {");
        sb.AppendLine("            $priority = Convert-AurumPriority ([int]$rule.Priority)");
        sb.AppendLine("            if ($priority) { try { $p.PriorityClass = $priority } catch {} }");
        sb.AppendLine("            if ($rule.AffinityMask -ne 0) { try { $p.ProcessorAffinity = [IntPtr]([Int64]$rule.AffinityMask) } catch {} }");
        sb.AppendLine("        }");
        sb.AppendLine("        if ($rule.PowerPlanWhileRunning -and -not $runningPlan) { $runningPlan = [string]$rule.PowerPlanWhileRunning }");
        sb.AppendLine("    } elseif ($rule.PowerPlanWhenIdle -and -not $idlePlan) {");
        sb.AppendLine("        $idlePlan = [string]$rule.PowerPlanWhenIdle");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("if ($runningPlan) { powercfg.exe /setactive $runningPlan | Out-Null }");
        sb.AppendLine("elseif ($idlePlan) { powercfg.exe /setactive $idlePlan | Out-Null }");
        return sb.ToString();
    }

    public static string BuildCreateTaskArgs(string scriptPath)
    {
        var tr = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \\\"" + scriptPath + "\\\"";
        return "/Create /TN \"" + TaskName + "\" /SC MINUTE /MO 1 /RL HIGHEST /TR \"" + tr + "\" /F";
    }

    public static string BuildDeleteTaskArgs() => "/Delete /TN \"" + TaskName + "\" /F";
    public static string BuildQueryTaskArgs() => "/Query /TN \"" + TaskName + "\"";
}

/// <summary>
/// Reads the CPU's per-logical-processor efficiency classes via the documented Win32 GetSystemCpuSetInformation,
/// so the « performance cores » affinity preset targets the real P-cores. Pure glue, fully guarded: any failure
/// (older Windows, denied, malformed buffer) or a non-hybrid CPU returns a flat layout, and the testable decision
/// (which mask each preset yields) lives in <see cref="AffinityPlan"/>, not here.
/// </summary>
internal static class CpuTopologyProbe
{
    public static CpuLayout Query()
    {
        int logical = Environment.ProcessorCount;
        try { return new CpuLayout(logical, QueryPerformanceCores((uint)logical)); }
        catch { return CpuLayout.Flat(logical); }
    }

    private static IReadOnlyList<int> QueryPerformanceCores(uint logical)
    {
        uint len = 0;
        GetSystemCpuSetInformation(IntPtr.Zero, 0, ref len, IntPtr.Zero, 0);
        if (len == 0) return Array.Empty<int>();

        IntPtr buffer = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, len, ref len, IntPtr.Zero, 0))
                return Array.Empty<int>();

            var items = new List<(int Index, int Eff)>();
            int offset = 0;
            // Each SYSTEM_CPU_SET_INFORMATION is variable-size; walk by its Size field. For Type==CpuSetInformation(0),
            // LogicalProcessorIndex sits at +14 and EfficiencyClass at +18 (the CpuSet sub-struct after Size+Type+Id+Group).
            while (offset + 20 <= (int)len)
            {
                IntPtr entry = buffer + offset;
                int size = Marshal.ReadInt32(entry, 0);
                if (size <= 0) break;
                int type = Marshal.ReadInt32(entry, 4);
                if (type == 0)
                {
                    int index = Marshal.ReadByte(entry, 14);
                    int eff   = Marshal.ReadByte(entry, 18);
                    items.Add((index, eff));
                }
                offset += size;
            }

            if (items.Count == 0) return Array.Empty<int>();
            int maxEff = items.Max(x => x.Eff);
            int minEff = items.Min(x => x.Eff);
            if (maxEff == minEff) return Array.Empty<int>();   // single efficiency class ⇒ not hybrid ⇒ no P/E choice

            return items.Where(x => x.Eff == maxEff).Select(x => x.Index).Distinct().OrderBy(i => i).ToList();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength,
        ref uint returnedLength, IntPtr process, uint flags);
}

/// <summary>
/// The I/O service behind « Priorité & affinité ». Enumerates the running processes worth showing (detected games
/// first, then windowed apps), reading each one's real priority and affinity; applies a change with the managed
/// Process API and reports success so the caller can re-read the truth. Installed games are scanned once and cached
/// for the session (they don't change between refreshes), so each refresh only re-walks the cheap process list.
/// </summary>
public sealed class ProcessControlService : IProcessControlService
{
    private readonly IGameDetectionService _games;
    private IReadOnlyList<DetectedGame>? _gamesCache;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ProcessControlService(IGameDetectionService games) => _games = games;

    public async Task<ProcessControlReport> GetReportAsync()
    {
        _gamesCache ??= await _games.ScanAsync();
        var games = _gamesCache;
        return await Task.Run(() => BuildReport(games));
    }

    public Task<bool> SetPriorityAsync(int pid, ProcessPriorityLevel level) => Task.Run(() =>
    {
        if (!PriorityLevels.IsOffered(level)) return false;   // the UI may never push Realtime/Idle through
        try
        {
            using var p = Process.GetProcessById(pid);
            p.PriorityClass = PriorityLevels.ToClass(level);
            return true;
        }
        catch { return false; }
    });

    public Task<bool> SetAffinityAsync(int pid, AffinityStrategy strategy) => Task.Run(() =>
    {
        var layout = CpuTopologyProbe.Query();
        if (!AffinityPlan.IsOffered(strategy, layout)) return false;
        ulong mask = AffinityPlan.Build(strategy, layout);
        if (mask == 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.ProcessorAffinity = (IntPtr)(long)mask;
            return true;
        }
        catch { return false; }
    });

    public Task<ProcessPersistenceReport> GetPersistenceReportAsync() => Task.Run(GetPersistenceReport);

    public Task<bool> AddPersistentRuleAsync(RunningProcessInfo process, bool includeHighPerformancePowerPlan) =>
        Task.Run(() => AddPersistentRule(process, includeHighPerformancePowerPlan));

    public Task<bool> RemovePersistentRuleAsync(string processName) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var rules = ProcessPersistencePlan.Remove(ReadRules(), processName);
        return WriteRules(rules);
    });

    public Task<bool> SetPersistenceTaskEnabledAsync(bool enabled) => Task.Run(() =>
    {
        if (!enabled)
        {
            var (exit, _) = ProcessRunner.Capture("schtasks.exe", ProcessPersistencePlan.BuildDeleteTaskArgs(), 20_000);
            return exit == 0;
        }

        var rules = ReadRules();
        if (rules.Count == 0) return false;
        if (!WriteRules(rules)) return false;
        var (createExit, _) = ProcessRunner.Capture("schtasks.exe", ProcessPersistencePlan.BuildCreateTaskArgs(ScriptPath), 20_000);
        return createExit == 0;
    });

    private static bool AddPersistentRule(RunningProcessInfo process, bool includeHighPerformancePowerPlan)
    {
        if (!process.Accessible || string.IsNullOrWhiteSpace(process.Name)) return false;
        Guid? currentPlan = includeHighPerformancePowerPlan ? QueryActivePowerPlan() : null;
        if (includeHighPerformancePowerPlan && currentPlan is null) return false;

        var rule = ProcessPersistencePlan.BuildRule(process, includeHighPerformancePowerPlan, currentPlan, DateTime.UtcNow);
        var rules = ProcessPersistencePlan.Upsert(ReadRules(), rule);
        return WriteRules(rules);
    }

    private static ProcessPersistenceReport GetPersistenceReport() =>
        new(ReadRules(), TaskInstalled(), RulesPath, ScriptPath);

    private static bool TaskInstalled()
    {
        var (exit, _) = ProcessRunner.Capture("schtasks.exe", ProcessPersistencePlan.BuildQueryTaskArgs(), 15_000);
        return exit == 0;
    }

    private static Guid? QueryActivePowerPlan()
    {
        var (exit, stdout) = ProcessRunner.Capture("powercfg.exe", "/getactivescheme", 10_000);
        return exit == 0 ? PowerSchemeParser.FirstGuid(stdout) : null;
    }

    private static IReadOnlyList<PersistentProcessRule> ReadRules()
    {
        try
        {
            if (!File.Exists(RulesPath)) return Array.Empty<PersistentProcessRule>();
            return JsonSerializer.Deserialize<List<PersistentProcessRule>>(File.ReadAllText(RulesPath), JsonOptions)
                   ?? new List<PersistentProcessRule>();
        }
        catch { return Array.Empty<PersistentProcessRule>(); }
    }

    private static bool WriteRules(IReadOnlyList<PersistentProcessRule> rules)
    {
        try
        {
            Directory.CreateDirectory(PersistenceDirectory);
            File.WriteAllText(RulesPath, JsonSerializer.Serialize(rules, JsonOptions));
            File.WriteAllText(ScriptPath, ProcessPersistencePlan.RenderScript(), Encoding.UTF8);
            return true;
        }
        catch { return false; }
    }

    private static string PersistenceDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AurumTweaks", "ProcessRules");
    private static string RulesPath => Path.Combine(PersistenceDirectory, ProcessPersistencePlan.RulesFileName);
    private static string ScriptPath => Path.Combine(PersistenceDirectory, ProcessPersistencePlan.ScriptFileName);

    private static ProcessControlReport BuildReport(IReadOnlyList<DetectedGame> games)
    {
        var layout = CpuTopologyProbe.Query();
        var list = new List<RunningProcessInfo>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var info = Inspect(p, games, layout);
                    if (info is not null) list.Add(info);
                }
                catch { /* one bad process must not sink the enumeration */ }
            }
        }

        var ordered = list
            .OrderByDescending(x => x.IsGame)
            .ThenByDescending(x => x.WorkingSetBytes)
            .ToList();

        return new ProcessControlReport(ordered, layout, QueryOk: ordered.Count > 0);
    }

    private static RunningProcessInfo? Inspect(Process p, IReadOnlyList<DetectedGame> games, CpuLayout layout)
    {
        int pid;
        try { pid = p.Id; } catch { return null; }
        if (pid <= 4) return null;   // Idle (0) / System (4) — not user-tunable

        string name = SafeName(p, pid);

        bool hasWindow;
        try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { hasWindow = false; }

        string? exe = SafeExePath(p);
        var game = ProcessGameMatch.Match(exe, games);
        bool isGame = game is not null;

        // Keep the list relevant: detected games (even if minimised) + apps with a visible window. Everything
        // else (hundreds of background/system processes) is omitted rather than dumped.
        if (!isGame && !hasWindow) return null;

        ProcessPriorityLevel priority;
        ulong affinity;
        bool accessible;
        try
        {
            priority = PriorityLevels.FromClass(p.PriorityClass);
            affinity = unchecked((ulong)p.ProcessorAffinity.ToInt64());
            accessible = true;
        }
        catch
        {
            priority = ProcessPriorityLevel.Unknown;
            affinity = 0;
            accessible = false;
        }

        long ws;
        try { ws = p.WorkingSet64; } catch { ws = 0; }

        bool hasAntiCheat = game?.HasAntiCheat ?? false;
        var advice = PriorityAffinityAdvice.For(isGame, hasAntiCheat, layout);

        return new RunningProcessInfo(
            pid, name, exe, accessible, priority, affinity, isGame,
            game?.Platform ?? string.Empty, hasAntiCheat, game?.AntiCheatName ?? string.Empty,
            ws, layout, advice);
    }

    private static string SafeName(Process p, int pid)
    {
        try { return p.ProcessName; } catch { return $"PID {pid}"; }
    }

    private static string? SafeExePath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }
}
