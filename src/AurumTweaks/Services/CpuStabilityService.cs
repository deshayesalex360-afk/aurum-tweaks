using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// A built-in CPU stability / coherence test. It pegs every logical core with the deterministic
/// <see cref="CpuWorkload"/> kernel and verifies each completed batch against a reference checksum
/// computed up-front on the same machine. A mismatch means the CPU produced a wrong result under load —
/// the signature of an unstable overclock or an over-aggressive undervolt / Curve Optimizer.
///
/// <para><b>Honesty boundaries.</b> This is a <i>quick coherence</i> test, not Prime95 small-FFT / OCCT /
/// y-cruncher run for hours. It is managed code, so it can't drive dedicated AVX-512 torture, and it reads
/// no thermal sensors. A clean pass lowers the odds of gross instability; it does not certify a 24/7 stable
/// chip. Detected miscalculations, however, are meaningful: healthy cores never fail this.</para>
/// </summary>
public interface ICpuStabilityService
{
    /// <summary>
    /// Run the test for the configured duration across the configured number of logical cores. Honours
    /// cancellation and never throws on cancellation — it returns an honest <see cref="CpuTestResult"/>
    /// (including <see cref="CpuTestResult.Cancelled"/>) describing what actually happened.
    /// </summary>
    Task<CpuTestResult> RunAsync(
        CpuTestConfig config,
        IProgress<CpuTestProgress>? progress,
        CancellationToken ct);
}

public sealed class CpuStabilityService : ICpuStabilityService
{
    private const int BatchIterations = 1_000_000;   // ~1 ms/batch ⇒ cancellation/duration stay responsive
    private const int MinDurationSec = 5;
    private const int MaxDurationSec = 3600;
    private const int MaxRecordedErrors = 25;        // keep a sample; the count stays exact

    public Task<CpuTestResult> RunAsync(
        CpuTestConfig config,
        IProgress<CpuTestProgress>? progress,
        CancellationToken ct)
        // ct deliberately not passed to Task.Run — we want a Cancelled *result*, never a faulted task.
        => Task.Run(() => Run(config, progress, ct));

    private static CpuTestResult Run(
        CpuTestConfig config,
        IProgress<CpuTestProgress>? progress,
        CancellationToken ct)
    {
        int durationSec = Math.Clamp(config.DurationSec, MinDurationSec, MaxDurationSec);
        int workerCount = Math.Clamp(config.Threads, 1, Environment.ProcessorCount);

        // Honour the AVX2 request only if the silicon actually supports it; otherwise fall back to the scalar
        // kernel and report it (no silent pretending — the verdict says which load really ran).
        bool avx2 = config.UseAvx2 && CpuWorkload.Avx2Available;
        ulong Kernel(ulong seed) => avx2
            ? CpuWorkload.ComputeVector(seed, BatchIterations)
            : CpuWorkload.Compute(seed, BatchIterations);

        // ---- error collection (count exact; recorded sample capped) ----
        var errorLock = new object();
        var recorded = new List<CpuComputeError>();
        int totalErrors = 0;
        long totalBatches = 0;

        // ---- reference checksums (one per worker seed), computed single-threaded up-front ----
        var references = new ulong[workerCount];
        for (int t = 0; t < workerCount; t++)
            references[t] = Kernel(CpuWorkload.DefaultSeed + (ulong)t);

        var sw = Stopwatch.StartNew();   // start timing AFTER precompute so the duration is honest

        void RecordError(int thread, ulong expected, ulong actual, double atSec)
        {
            lock (errorLock)
            {
                totalErrors++;
                if (recorded.Count < MaxRecordedErrors)
                    recorded.Add(new CpuComputeError { Thread = thread, Expected = expected, Actual = actual, AtSec = atSec });
            }
        }

        void Worker(int id)
        {
            ulong seed = CpuWorkload.DefaultSeed + (ulong)id;
            ulong expected = references[id];
            while (sw.Elapsed.TotalSeconds < durationSec && !ct.IsCancellationRequested)
            {
                ulong got = Kernel(seed);
                Interlocked.Increment(ref totalBatches);
                if (got != expected)
                    RecordError(id, expected, got, sw.Elapsed.TotalSeconds);
            }
        }

        void Report(bool final)
        {
            if (progress is null) return;
            double elapsed = sw.Elapsed.TotalSeconds;
            long batches = Interlocked.Read(ref totalBatches);
            double ips = elapsed > 0 ? batches * (double)BatchIterations / elapsed : 0;
            double pct = final ? 100d : Math.Min(100d, elapsed / durationSec * 100d);
            progress.Report(new CpuTestProgress
            {
                Percent = pct,
                ElapsedSec = elapsed,
                TotalSec = durationSec,
                IterationsPerSec = ips,
                Batches = batches,
                Errors = Volatile.Read(ref totalErrors),
                Threads = workerCount,
            });
        }

        Serilog.Log.Information("CpuStability: starting {Sec}s on {Threads} thread(s), kernel={Kernel}.",
            durationSec, workerCount, avx2 ? "AVX2" : "scalar");

        // Dedicated background threads guarantee every core is pegged immediately (no thread-pool ramp-up).
        var threads = new Thread[workerCount];
        for (int t = 0; t < workerCount; t++)
        {
            int id = t;
            threads[t] = new Thread(() => Worker(id))
            {
                IsBackground = true,
                Name = $"AurumCpuTest-{id}",
            };
        }
        foreach (var th in threads) th.Start();

        // Pump live progress while the workers grind.
        while (threads.Any(th => th.IsAlive))
        {
            Thread.Sleep(200);
            Report(final: false);
        }
        foreach (var th in threads) th.Join();

        bool cancelled = ct.IsCancellationRequested;
        bool completed = !cancelled;
        Report(final: completed);

        double dur = sw.Elapsed.TotalSeconds;
        long finalBatches = Interlocked.Read(ref totalBatches);
        double avgIps = dur > 0 ? finalBatches * (double)BatchIterations / dur : 0;

        Serilog.Log.Information("CpuStability: {State} after {Sec:F1}s · {Batches} lots · {Errors} erreur(s).",
            cancelled ? "cancelled" : "completed", dur, finalBatches, totalErrors);

        return new CpuTestResult
        {
            Completed = completed,
            Cancelled = cancelled,
            Avx2Used = avx2,
            ThreadsUsed = workerCount,
            DurationSec = dur,
            Batches = finalBatches,
            ErrorCount = totalErrors,
            Errors = recorded.ToArray(),
            Notes = BuildNotes(completed, cancelled, totalErrors, durationSec, workerCount, avx2, config.UseAvx2),
            AvgIterationsPerSec = avgIps,
        };
    }

    private static IReadOnlyList<string> BuildNotes(
        bool completed, bool cancelled, int errors, int durationSec, int threads, bool avx2Used, bool avx2Requested)
    {
        var notes = new List<string>();

        if (errors > 0)
        {
            notes.Add($"INSTABLE — {errors} résultat(s) de calcul faux détecté(s). Sous cette charge, le CPU calcule " +
                      "incorrectement : monte le vcore (ou réduis l'OC / un Curve Optimizer trop négatif), puis relance. " +
                      "Des cœurs sains ne se trompent JAMAIS ici.");
        }
        else if (cancelled)
        {
            notes.Add("Test interrompu avant la fin — résultat partiel, il ne prouve rien.");
        }
        else if (completed)
        {
            notes.Add($"Aucune erreur de calcul en {durationSec}s sur {threads} thread(s).");
        }
        else
        {
            notes.Add("Le test n'a pas pu s'exécuter.");
        }

        if (avx2Used)
        {
            notes.Add("Charge AVX2 (vectorielle 256-bit) : 4 voies par cœur, plus proche du SIMD des jeux / encodage / rendu. " +
                      "C'est typiquement ce qui fait tomber un undervolt stable en scalaire — un échec ici est très parlant.");
        }
        else if (avx2Requested)
        {
            notes.Add("AVX2 demandé mais indisponible sur ce CPU → repli honnête sur la charge scalaire (entière + FP). " +
                      "Le test reste valable, simplement moins représentatif des charges vectorielles.");
        }
        else
        {
            notes.Add("Charge scalaire (entière + FP, sans AVX2). Pour une charge vectorielle plus proche des jeux et de " +
                      "l'encodage, réactive l'option AVX2.");
        }

        notes.Add("⚠️ Test de cohérence rapide en code managé : il charge tous les cœurs logiques et vérifie chaque lot " +
                  "contre une référence, mais ce n'est PAS Prime95 small-FFT / OCCT / y-cruncher pendant des heures. " +
                  "Certaines instabilités (et le throttling thermique) n'apparaissent que sous AVX2/AVX-512 soutenu et sur la durée.");
        notes.Add("Surveille tes températures en parallèle (onglet Monitoring) : ce test ne lit aucun capteur et n'offre " +
                  "aucune protection thermique.");

        return notes;
    }
}
