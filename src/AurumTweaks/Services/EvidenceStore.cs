using System;
using System.IO;
using System.Text.Json;
using AurumTweaks.Models;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>
/// File-backed <see cref="IEvidenceStore"/>: persists the frame-time A/B to
/// <c>%LOCALAPPDATA%\AurumTweaks\evidence-performance.json</c> so the headline « preuve avant / après » a user built
/// before a reboot is still there to paste afterwards — precisely when reboot-requiring tweaks have just taken effect
/// and the proof matters most. Mirrors the <see cref="AppSettingsStore"/> idiom (shared JSON options, best-effort I/O
/// that degrades to an honest no-op instead of crashing the page that published).
///
/// <para>Two honesty rules are load-bearing here:</para>
/// <list type="bullet">
/// <item>Only a comparison whose BOTH runs still carry real data is saved or resurrected — a hollow A/B is dropped,
/// never reloaded as an empty « proof ». A published null deletes the file, so a cleared comparison stays cleared
/// across launches (the restart-time twin of the ledger's in-memory « a null clears a slot »).</item>
/// <item>The raw per-frame arrays are trimmed before writing. The proof cites only the computed
/// <see cref="FrameTimeStats"/> (and the deltas already frozen into the comparison), never raw frames — so dropping
/// them keeps the evidence file small while preserving every number the report actually shows.</item>
/// </list>
/// </summary>
public sealed class EvidenceStore : IEvidenceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public EvidenceStore()
        : this(Path.Combine(Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\AurumTweaks"), "evidence-performance.json"))
    {
    }

    // Test seam: point the store at a temp file so a round-trip can be exercised without touching the real profile.
    internal EvidenceStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public BenchmarkComparison? LoadPerformance()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var c = JsonSerializer.Deserialize<BenchmarkComparison>(File.ReadAllText(_path), JsonOpts);
            // A reload is honest only if both runs still measure something; a hollow record is dropped, not shown.
            return c is { Before.HasData: true, After.HasData: true } ? c : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load persisted performance evidence; treating as none");
            return null;
        }
    }

    public void SavePerformance(BenchmarkComparison? comparison)
    {
        try
        {
            if (comparison is not { Before.HasData: true, After.HasData: true })
            {
                if (File.Exists(_path)) File.Delete(_path);   // cleared/closed/hollow A/B must not resurrect next launch
                return;
            }
            File.WriteAllText(_path, JsonSerializer.Serialize(Trim(comparison), JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist performance evidence");
        }
    }

    // Drop the raw frame arrays (the unbounded part) before persisting; the report renders only from Stats and the
    // already-computed deltas, so nothing the proof displays is lost.
    private static BenchmarkComparison Trim(BenchmarkComparison c) => c with
    {
        Before = c.Before with { FrameTimesMs = Array.Empty<double>() },
        After = c.After with { FrameTimesMs = Array.Empty<double>() }
    };
}

/// <summary>
/// A no-op <see cref="IEvidenceStore"/>: the <see cref="EvidenceLedger"/> falls back to it when no durable backing is
/// wired (unit tests via <c>new EvidenceLedger()</c>), keeping the ledger's behaviour pure, in-memory and deterministic.
/// </summary>
public sealed class NullEvidenceStore : IEvidenceStore
{
    public static readonly NullEvidenceStore Instance = new();

    public BenchmarkComparison? LoadPerformance() => null;
    public void SavePerformance(BenchmarkComparison? comparison) { }
}
