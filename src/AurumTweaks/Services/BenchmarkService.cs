using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AurumTweaks.Models;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace AurumTweaks.Services;

/// <summary>
/// Frame-time benchmark backend. Two honest paths, zero injection (so it can never trip an anti-cheat):
/// <list type="bullet">
/// <item><b>CSV import</b> — always available, no privilege. Reads a PresentMon / CapFrameX-style frame-time
/// export through the pure <see cref="FrameTimeCsvParser"/> and <see cref="FrameTimeAnalyzer"/>.</item>
/// <item><b>Live ETW capture</b> — real, but admin-gated and experimental. Subscribes to the
/// Microsoft-Windows-DXGI provider's <c>PresentStart</c> events (the same signal PresentMon reports as
/// <c>msBetweenPresents</c>) for the target process. No DLL is injected into the game; we only read ETW.</item>
/// </list>
/// Nothing here fabricates numbers: when live capture is unavailable, the process is not running, or no
/// present events are seen (e.g. a Vulkan/OpenGL title this provider doesn't cover), it says so and points
/// at the CSV path — it never invents frames.
/// </summary>
public interface IBenchmarkService
{
    /// <summary>Whether live ETW capture is available right now (needs an elevated process), reported honestly.</summary>
    BenchmarkBackendStatus GetStatus();

    /// <summary>Parse + analyse a frame-time CSV. Always available, no privilege. Never throws.</summary>
    BenchmarkResult AnalyzeCsv(string filePath);

    /// <summary>
    /// Capture frame times live from <paramref name="targetProcess"/> via the DXGI ETW provider for
    /// <paramref name="duration"/>. Admin-gated; fails honestly (never fabricates) when unavailable.
    /// Cancelling returns the metrics for whatever was captured so far.
    /// </summary>
    Task<BenchmarkResult> CaptureLiveAsync(string? targetProcess, TimeSpan duration,
                                           IProgress<int>? progress, CancellationToken ct);

    /// <summary>
    /// Distinct names (without ".exe") of running processes that own a visible window — the candidate
    /// capture targets the user picks from. Reliable from inside our own app, unlike "foreground"
    /// detection (which would just return Aurum itself while the user is clicking in it).
    /// </summary>
    IReadOnlyList<string> GetCandidateProcesses();
}

/// <summary>
/// Default implementation. The pure metrics/parse work is delegated to <see cref="FrameTimeAnalyzer"/> and
/// <see cref="FrameTimeCsvParser"/> (both fully unit-tested); this class only adds the real-world I/O —
/// file reads, elevation detection, the ETW session and the foreground-window lookup — all defensively
/// wrapped so a failure degrades to an honest message instead of a crash or a fake result.
/// </summary>
public sealed class BenchmarkService : IBenchmarkService
{
    // Microsoft-Windows-DXGI. PresentStart (event id 42) inter-arrival per process == PresentMon msBetweenPresents.
    private static readonly Guid DxgiProviderGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private const int DxgiPresentStartEventId = 42;
    private const string SessionName = "AurumTweaksBenchmark";

    public BenchmarkBackendStatus GetStatus()
    {
        bool elevated = IsElevated();
        return new BenchmarkBackendStatus
        {
            IsElevated = elevated,
            LiveCaptureAvailable = elevated,
            Message = elevated
                ? "Capture live ETW disponible (provider DXGI, sans injection — sûr vis-à-vis des anti-triche). "
                + "Lance le jeu, indique son process puis capture. Couvre les titres DirectX (D3D9–12) ; "
                + "Vulkan/OpenGL ou plein-écran exclusif peuvent ne rien remonter — dans ce cas, importe un CSV "
                + "PresentMon/CapFrameX."
                : "Capture live ETW indisponible : Aurum n'est pas lancé en administrateur (une session ETW temps "
                + "réel l'exige). Relance en admin pour la capture live — ou importe un CSV PresentMon/CapFrameX, "
                + "toujours disponible et sans aucun privilège."
        };
    }

    public BenchmarkResult AnalyzeCsv(string filePath)
    {
        string name = string.IsNullOrWhiteSpace(filePath) ? "(vide)" : Path.GetFileName(filePath);
        string source = $"CSV · {name}";

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Fail(source, string.Empty, "Fichier introuvable.");

        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch (Exception ex)
        {
            return Fail(source, string.Empty, $"Lecture impossible : {ex.Message}");
        }

        var parsed = FrameTimeCsvParser.Parse(lines);
        if (!parsed.Ok)
        {
            var why = parsed.SkippedRows > 0
                ? $"Aucune valeur exploitable ({parsed.SkippedRows} ligne(s) ignorée(s))."
                : "Aucune colonne de frame-time reconnue (msBetweenPresents, FrameTime, ms…).";
            return Fail(source, string.Empty, why + " Vérifie l'export PresentMon/CapFrameX (une colonne de temps par frame en ms).");
        }

        var stats = FrameTimeAnalyzer.Compute(parsed.FrameTimesMs);
        if (stats.FrameCount == 0)
            return Fail(source, parsed.Process, "Colonne trouvée mais aucune frame valide (toutes ≤ 0 ou non numériques).");

        var notes = new List<string>
        {
            $"Source : colonne « {parsed.Column} » — {stats.FrameCount} frames sur {stats.DurationSec:0.0} s.",
            "Métriques calculées localement : 1% low = FPS au 99ᵉ centile du frame-time, 0,1% low = 99,9ᵉ, "
            + "stutter = part des frames > 2× la médiane."
        };
        if (parsed.Differenced)
            notes.Add($"Colonne d'horodatage cumulé « {parsed.Column} » (Fraps / PresentMon) : frame-times "
                + "reconstitués par différence des temps consécutifs — transformation exacte, aucune frame inventée.");
        if (parsed.SkippedRows > 0)
            notes.Add($"{parsed.SkippedRows} ligne(s) ignorée(s) (non numérique ou horodatage non croissant) — jamais devinées.");

        return new BenchmarkResult
        {
            Source = source,
            TargetProcess = parsed.Process,
            Stats = stats,
            FrameTimesMs = parsed.FrameTimesMs,
            Notes = notes
        };
    }

    public async Task<BenchmarkResult> CaptureLiveAsync(string? targetProcess, TimeSpan duration,
                                                        IProgress<int>? progress, CancellationToken ct)
    {
        const string source = "ETW DXGI";

        if (!IsElevated())
            return Fail(source, targetProcess,
                "Capture live indisponible : Aurum n'est pas en administrateur (session ETW temps réel requise). "
                + "Relance en admin, ou importe un CSV PresentMon/CapFrameX.");

        // Resolve the target name → the set of PIDs currently running under it.
        string wanted = (targetProcess ?? string.Empty).Trim();
        if (wanted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) wanted = wanted[..^4];
        if (wanted.Length == 0)
            return Fail(source, targetProcess,
                "Aucun process cible. Mets le jeu au premier plan puis « process actif », ou saisis son nom (ex. game.exe).");

        HashSet<int> targetPids;
        try
        {
            var procs = Process.GetProcessesByName(wanted);
            try { targetPids = procs.Select(p => p.Id).ToHashSet(); }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch (Exception ex)
        {
            return Fail(source, wanted, $"Impossible d'énumérer le process « {wanted} » : {ex.Message}");
        }

        if (targetPids.Count == 0)
            return Fail(source, wanted,
                $"Process « {wanted} » introuvable. Lance le jeu d'abord — il doit tourner pendant toute la capture.");

        // Clamp the window to something sane.
        if (duration < TimeSpan.FromSeconds(3)) duration = TimeSpan.FromSeconds(3);
        if (duration > TimeSpan.FromMinutes(10)) duration = TimeSpan.FromMinutes(10);

        var frames = new List<double>(8192);
        int presentEvents = 0;

        try
        {
            await Task.Run(() =>
            {
                // PresentStart timestamps grouped per PID so we never create a bogus tiny delta by
                // interleaving two processes that happen to share the name.
                var perPid = new Dictionary<int, List<double>>();

                using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
                session.EnableProvider(DxgiProviderGuid, TraceEventLevel.Informational, ulong.MaxValue);

                session.Source.Dynamic.All += data =>
                {
                    if ((int)data.ID != DxgiPresentStartEventId) return;
                    if (data.ProviderGuid != DxgiProviderGuid) return;
                    int pid = data.ProcessID;
                    if (!targetPids.Contains(pid)) return;

                    if (!perPid.TryGetValue(pid, out var list))
                    {
                        list = new List<double>(4096);
                        perPid[pid] = list;
                    }
                    list.Add(data.TimeStampRelativeMSec);
                };

                var sw = Stopwatch.StartNew();
                using var stopTimer = new Timer(_ => SafeStop(session), null, duration, Timeout.InfiniteTimeSpan);
                using var ctReg = ct.Register(() => SafeStop(session));
                using var progTimer = new Timer(_ =>
                {
                    if (progress is null) return;
                    int pct = (int)Math.Clamp(sw.Elapsed.TotalMilliseconds / duration.TotalMilliseconds * 100.0, 0, 99);
                    progress.Report(pct);
                }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

                // Blocks on this background thread until a timer / cancellation / duration stops the session.
                session.Source.Process();

                // Same thread, after the stream ended → no concurrent access to perPid.
                foreach (var list in perPid.Values)
                {
                    presentEvents += list.Count;
                    list.Sort();
                    for (int i = 1; i < list.Count; i++)
                    {
                        double dt = list[i] - list[i - 1];
                        if (dt > 0) frames.Add(dt);   // frame time in ms == PresentMon msBetweenPresents
                    }
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before any capture happened — handled by the empty-frames check below.
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(source, wanted, "Accès ETW refusé (élévation administrateur requise pour une session temps réel).");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Live ETW benchmark capture failed");
            return Fail(source, wanted,
                $"Échec de la capture ETW : {ex.Message}. Importe un CSV PresentMon/CapFrameX à la place.");
        }

        progress?.Report(100);

        if (frames.Count == 0)
        {
            if (ct.IsCancellationRequested)
                return Fail(source, wanted, "Capture annulée avant d'avoir assez de frames.");

            return Fail(source, wanted,
                $"Aucune frame DXGI captée pour « {wanted} » ({presentEvents} évènement(s) PresentStart). "
                + "Le titre est peut-être en Vulkan/OpenGL ou en plein-écran exclusif (non couverts par ce provider) — "
                + "importe un CSV PresentMon/CapFrameX. Aucune donnée n'est inventée.");
        }

        var stats = FrameTimeAnalyzer.Compute(frames);
        var notes = new List<string>
        {
            $"Capture ETW DXGI sans injection : {stats.FrameCount} frames sur {stats.DurationSec:0.0} s — "
            + "aucune DLL injectée, sûr vis-à-vis des anti-triche.",
            "Frame-time = écart entre deux évènements PresentStart DXGI (équivalent au msBetweenPresents de PresentMon).",
            "Couvre DirectX (D3D9–12). Vulkan/OpenGL non couverts par ce provider — passe par un CSV si la capture reste vide."
        };
        if (ct.IsCancellationRequested)
            notes.Add("Capture interrompue avant la fin — métriques calculées sur la portion réellement captée.");

        Serilog.Log.Information("Live ETW benchmark: {Frames} frames for {Process} over {Sec:0.0}s",
            stats.FrameCount, wanted, stats.DurationSec);

        return new BenchmarkResult
        {
            Source = $"ETW DXGI · {wanted}.exe",
            TargetProcess = wanted,
            Stats = stats,
            FrameTimesMs = frames,
            Notes = notes
        };
    }

    public IReadOnlyList<string> GetCandidateProcesses()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    // A visible main window is a good proxy for "an app the user might be benchmarking".
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;

                    string name = p.ProcessName;
                    if (IsShellOrSelf(name)) continue;
                    set.Add(name);
                }
                catch { /* process exited mid-enumeration or access denied */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* return whatever we managed to collect */ }

        return set.ToList();
    }

    private static bool IsShellOrSelf(string processName) =>
        processName.Equals("AurumTweaks", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase);

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void SafeStop(TraceEventSession session)
    {
        try { session.Stop(); } catch { /* already stopped / disposed */ }
    }

    private static BenchmarkResult Fail(string source, string? target, string message) => new()
    {
        Source = source,
        TargetProcess = (target ?? string.Empty).Trim(),
        Notes = new[] { message }
    };
}
