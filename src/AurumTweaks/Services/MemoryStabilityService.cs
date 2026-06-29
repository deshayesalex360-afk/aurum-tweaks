using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// A built-in, user-mode RAM stability test. It allocates a block of managed memory and runs a
/// moving-inversions sweep (see <see cref="MemoryPatterns"/>) plus an own-address pass across it,
/// in parallel, reporting live progress and throughput.
///
/// <para><b>Honesty boundaries (important).</b> This is a <i>quick coverage</i> test, not a replacement
/// for an overnight TM5 (Anta777), Karhu or HCI MemTest run. Without a kernel driver we can only touch
/// memory the OS hands this process, the offsets we report are virtual (not mappable to a physical DIMM),
/// and we cannot exercise the rows the OS keeps for itself. A clean pass lowers the odds of gross
/// instability; it does not certify a 24/7 stable kit. Errors, on the other hand, are meaningful: real
/// RAM should never fail this.</para>
/// </summary>
public interface IMemoryStabilityService
{
    /// <summary>
    /// Run the test. Honours cancellation between (and within) phases, and never throws on
    /// out-of-memory or cancellation — it tests as much as it could allocate and reports the
    /// outcome (including <see cref="MemoryTestResult.Cancelled"/>) in the returned result.
    /// </summary>
    Task<MemoryTestResult> RunAsync(
        MemoryTestConfig config,
        IProgress<MemoryTestProgress>? progress,
        CancellationToken ct);
}

public sealed class MemoryStabilityService : IMemoryStabilityService
{
    private const int ChunkBytes = 32 * 1024 * 1024;   // 32 MiB per array → stays on the LOH (never moved by GC)
    private const int ChunkWords = ChunkBytes / 8;
    private const int MinSizeMb = 16;
    private const int MaxSizeMb = 61440;               // 60 GiB hard ceiling — sanity, not a recommendation
    private const int MinPasses = 1;
    private const int MaxPasses = 20;
    private const int MaxRecordedErrors = 25;          // keep a sample; the count is still exact

    // Each pass: 4 patterns × (fill + inversion↑ + inversion↓) + addressing (write + verify).
    private const int SweepsPerPass = 4 * 3 + 2;

    public Task<MemoryTestResult> RunAsync(
        MemoryTestConfig config,
        IProgress<MemoryTestProgress>? progress,
        CancellationToken ct)
        // NB: ct is deliberately not passed to Task.Run — we want a *Cancelled result*, not a faulted
        // task, even when the token is already cancelled. Run() observes the token and returns honestly.
        => Task.Run(() => Run(config, progress, ct));

    private static MemoryTestResult Run(
        MemoryTestConfig config,
        IProgress<MemoryTestProgress>? progress,
        CancellationToken ct)
    {
        int requestedMb = Math.Clamp(config.SizeMb, MinSizeMb, MaxSizeMb);
        int passes = Math.Clamp(config.Passes, MinPasses, MaxPasses);
        int threads = Math.Clamp(config.Threads, 1, Environment.ProcessorCount);

        var sw = Stopwatch.StartNew();

        // Everything BuildResult reads is declared up-front so the early cancel/OOM returns are valid.
        var errorLock = new object();
        var recorded = new List<MemoryError>();
        int totalErrors = 0;
        long processedWords = 0;
        int actualMb = 0;
        bool allocationShort = false;

        void RecordError(long wordIndex, ulong expected, ulong actual, string pattern)
        {
            lock (errorLock)
            {
                totalErrors++;
                if (recorded.Count < MaxRecordedErrors)
                {
                    recorded.Add(new MemoryError
                    {
                        ByteOffset = wordIndex * 8,
                        Expected = expected,
                        Actual = actual,
                        Pattern = pattern,
                    });
                }
            }
        }

        // ---- allocate as much as we can, honestly ----
        var chunks = new List<ulong[]>();
        var baseIndices = new List<long>();
        long wordCursor = 0;
        try
        {
            long remainingBytes = (long)requestedMb * 1024 * 1024;
            while (remainingBytes >= 8)
            {
                ct.ThrowIfCancellationRequested();
                int thisWords = (int)Math.Min(ChunkWords, remainingBytes / 8);
                var chunk = new ulong[thisWords];
                chunks.Add(chunk);
                baseIndices.Add(wordCursor);
                wordCursor += thisWords;
                remainingBytes -= (long)thisWords * 8;
            }
        }
        catch (OutOfMemoryException)
        {
            allocationShort = true;
            Serilog.Log.Warning("MemoryStability: allocation capped at {Mb} Mo of {Req} Mo requested (OOM).",
                wordCursor * 8 / (1024 * 1024), requestedMb);
        }
        catch (OperationCanceledException)
        {
            return BuildResult(completed: false, cancelled: true, passesDone: 0);
        }

        long totalWords = wordCursor;
        actualMb = (int)(totalWords * 8 / (1024 * 1024));

        if (totalWords == 0)
        {
            Serilog.Log.Warning("MemoryStability: could not allocate any test memory.");
            return BuildResult(completed: false, cancelled: false, passesDone: 0);
        }

        Serilog.Log.Information(
            "MemoryStability: testing {Mb} Mo across {Chunks} blocs, {Passes} passe(s), {Threads} thread(s).",
            actualMb, chunks.Count, passes, threads);

        // ---- plan & live counters ----
        long plannedWords = (long)passes * SweepsPerPass * totalWords;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = threads,
            CancellationToken = ct,
        };

        void Report(int pass, string phase)
        {
            if (progress is null) return;
            double elapsed = sw.Elapsed.TotalSeconds;
            long done = Interlocked.Read(ref processedWords);
            double mbps = elapsed > 0 ? done * 8d / 1_000_000d / elapsed : 0;
            double pct = plannedWords > 0 ? Math.Min(100d, done * 100d / plannedWords) : 0;
            progress.Report(new MemoryTestProgress
            {
                Pass = pass,
                TotalPasses = passes,
                Phase = phase,
                Percent = pct,
                BytesTested = done * 8,
                ThroughputMbps = mbps,
                Errors = Volatile.Read(ref totalErrors),
            });
        }

        // One parallel sweep over every chunk; `work` returns the first mismatch it found in that chunk.
        void Sweep(int pass, string phase, Func<ulong[], long, MemoryPatterns.ScanResult> work)
        {
            Parallel.For(0, chunks.Count, parallelOptions, ci =>
            {
                ulong[] chunk = chunks[ci];
                long baseIdx = baseIndices[ci];
                MemoryPatterns.ScanResult res = work(chunk, baseIdx);
                if (res.HasError)
                    RecordError(baseIdx + res.FirstBadIndex, res.Expected, res.Actual, phase);
                Interlocked.Add(ref processedWords, chunk.Length);
            });
            Report(pass, phase);
        }

        // ---- the actual test ----
        try
        {
            for (int pass = 1; pass <= passes; pass++)
            {
                foreach (var (name, value) in MemoryPatterns.WordPatterns)
                {
                    ulong complement = ~value;

                    Sweep(pass, $"{name} · écriture", (buf, _) =>
                    {
                        MemoryPatterns.Fill(buf, value);
                        return MemoryPatterns.ScanResult.Ok;
                    });

                    Sweep(pass, $"{name} · inversion ↑", (buf, _) =>
                        MemoryPatterns.CheckThenWrite(buf, value, complement, ascending: true));

                    Sweep(pass, $"{name} · inversion ↓", (buf, _) =>
                        MemoryPatterns.CheckThenWrite(buf, complement, value, ascending: false));
                }

                Sweep(pass, "Adressage · écriture", (buf, baseIdx) =>
                {
                    MemoryPatterns.FillAddressing(buf, baseIdx);
                    return MemoryPatterns.ScanResult.Ok;
                });

                Sweep(pass, "Adressage · vérification", (buf, baseIdx) =>
                    MemoryPatterns.VerifyAddressing(buf, baseIdx));
            }
        }
        catch (OperationCanceledException)
        {
            Serilog.Log.Information("MemoryStability: cancelled after {Sec:F1}s, {Errors} erreur(s).",
                sw.Elapsed.TotalSeconds, totalErrors);
            return BuildResult(completed: false, cancelled: true, passesDone: passes);
        }

        Serilog.Log.Information("MemoryStability: completed {Mb} Mo · {Passes} passe(s) · {Errors} erreur(s) en {Sec:F1}s.",
            actualMb, passes, totalErrors, sw.Elapsed.TotalSeconds);

        return BuildResult(completed: true, cancelled: false, passesDone: passes);

        // ----- local helper that closes over the counters -----
        MemoryTestResult BuildResult(bool completed, bool cancelled, int passesDone)
        {
            double dur = sw.Elapsed.TotalSeconds;
            double avg = dur > 0 ? Interlocked.Read(ref processedWords) * 8d / 1_000_000d / dur : 0;
            return new MemoryTestResult
            {
                Completed = completed,
                Cancelled = cancelled,
                SizeMbTested = actualMb,
                PassesCompleted = completed ? passesDone : 0,
                DurationSec = dur,
                ErrorCount = totalErrors,
                Errors = recorded.ToArray(),
                Notes = BuildNotes(completed, cancelled, totalErrors, actualMb, requestedMb, allocationShort),
                AvgThroughputMbps = avg,
            };
        }
    }

    private static IReadOnlyList<string> BuildNotes(
        bool completed, bool cancelled, int errors, int actualMb, int requestedMb, bool allocationShort)
    {
        var notes = new List<string>();

        if (errors > 0)
        {
            notes.Add($"INSTABLE — {errors} erreur(s) détectée(s). De la vraie RAM ne doit JAMAIS échouer ici : " +
                      "desserre tes timings (ou monte un cran la VDIMM / le VSOC) et relance.");
        }
        else if (cancelled)
        {
            notes.Add("Test interrompu avant la fin — résultat partiel, il ne prouve rien.");
        }
        else if (completed)
        {
            notes.Add($"Aucune erreur sur {actualMb} Mo testés.");
        }
        else
        {
            notes.Add("Le test n'a pas pu s'exécuter (mémoire insuffisante).");
        }

        if (allocationShort || (actualMb < requestedMb && actualMb > 0))
        {
            notes.Add($"N'a pu allouer que {actualMb} Mo sur les {requestedMb} Mo demandés (RAM libre insuffisante au moment du test).");
        }

        if (completed && errors == 0)
        {
            notes.Add("⚠️ Ce n'est PAS équivalent à une nuit de TM5 (config Anta777), Karhu ou HCI MemTest — " +
                      "c'est un test de couverture rapide. Pour valider un OC RAM 24/7, fais tourner un vrai memtest plusieurs heures.");
        }

        notes.Add("Sans pilote noyau, le test ne couvre que la mémoire allouable par l'application et les offsets " +
                  "affichés sont virtuels (pas de correspondance directe avec un DIMM physique).");

        return notes;
    }
}
