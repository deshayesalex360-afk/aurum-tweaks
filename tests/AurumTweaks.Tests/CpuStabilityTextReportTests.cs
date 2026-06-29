using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="CpuStabilityTextReport"/> — the shareable « Test de stabilité CPU » paste. Honesty contract: a run
/// that never executed prints « Aucun test exécuté » (never a fabricated « STABLE »); the verdict line is the shared
/// <see cref="CpuStabilityVerdict.Describe"/> so the paste matches the page exactly; caught miscalculations are listed
/// as evidence with explicit windowing; and the load-bearing « ce test bref ne remplace pas Prime95/OCCT » caveat is
/// always in the footer so a « STABLE » can never be read as an hours-long validation. Numbers are fr-FR-formatted by
/// the renderer, so the asserted text is locale-independent.
/// </summary>
public class CpuStabilityTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 22, 14, 30, 0, DateTimeKind.Utc);

    private static CpuComputeError Err(int thread = 0, ulong expected = 0x1234, ulong actual = 0x5678, double atSec = 1.0)
        => new() { Thread = thread, Expected = expected, Actual = actual, AtSec = atSec };

    private static CpuTestResult Run(
        bool completed = true, bool cancelled = false, bool avx2 = true, int threads = 16,
        double durationSec = 30, long batches = 1000, int errorCount = 0,
        IReadOnlyList<CpuComputeError>? errors = null, IReadOnlyList<string>? notes = null,
        double avgItPerSec = 1.5e9) => new()
    {
        Completed = completed,
        Cancelled = cancelled,
        Avx2Used = avx2,
        ThreadsUsed = threads,
        DurationSec = durationSec,
        Batches = batches,
        ErrorCount = errorCount,
        Errors = errors ?? Array.Empty<CpuComputeError>(),
        Notes = notes ?? Array.Empty<string>(),
        AvgIterationsPerSec = avgItPerSec,
    };

    private static string Render(CpuTestResult result) => CpuStabilityTextReport.Render(result, When);

    // --- Header / empty ---

    [Fact]
    public void Header_CarriesTitle()
        => Assert.Contains("Aurum Tweaks — Test de stabilité CPU", Render(Run()));

    [Fact]
    public void NoRun_SaysSo_WithoutFabricatingAVerdict()
    {
        var text = Render(new CpuTestResult());   // HasRun false
        Assert.Contains("Aucun test exécuté", text);
        Assert.DoesNotContain("VERDICT", text);
        Assert.DoesNotContain("STABLE", text);
    }

    // --- Verdict + parameters ---

    [Fact]
    public void Stable_RendersTheSharedVerdict_AndTheParameters()
    {
        var text = Render(Run(completed: true, threads: 16, durationSec: 30, avx2: true, errorCount: 0));
        Assert.Contains("VERDICT : STABLE sur ce test", text);
        Assert.Contains("PARAMÈTRES", text);
        Assert.Contains("Threads chargés", text);
        Assert.Contains("16", text);
        Assert.Contains("AVX2 (256-bit)", text);
        Assert.Contains("Débit moyen", text);
        Assert.Contains("G it/s", text);   // honest workload throughput, never fake "FLOPS"
    }

    [Fact]
    public void ScalarKernel_IsNamedTruthfully_NotAssumedAvx2()
    {
        var text = Render(Run(avx2: false));
        Assert.Contains("scalaire", text);
        Assert.DoesNotContain("AVX2 (256-bit)", text);
    }

    // --- Errors as evidence ---

    [Fact]
    public void Errors_AreListedAsEvidence_NeverHidden()
    {
        var text = Render(Run(completed: true, errorCount: 1, errors: new[] { Err(thread: 3, expected: 0x1234, actual: 0x5678, atSec: 12.5) }));
        Assert.Contains("ERREURS DE CALCUL (1)", text);
        Assert.Contains("thread 3", text);
        Assert.Contains("1234", text);
        Assert.Contains("5678", text);
    }

    [Fact]
    public void Errors_AreWindowed_WithTheTrueTotalInTheHeader()
    {
        var many = Enumerable.Range(0, 10).Select(i => Err(thread: i, atSec: i)).ToList();
        var text = Render(Run(completed: true, errorCount: 10, errors: many));
        Assert.Contains("ERREURS DE CALCUL (10)", text);   // header keeps the TRUE total
        Assert.Contains("8 sur 10 détaillées", text);       // windowing is explicit, never silent
    }

    [Fact]
    public void ErrorCountWithoutDetail_SaysDetailNotKept_RatherThanInventOne()
    {
        // ErrorCount > 0 but no per-error records: honest about the gap, never a fabricated row.
        var text = Render(Run(completed: true, errorCount: 2, errors: Array.Empty<CpuComputeError>()));
        Assert.Contains("ERREURS DE CALCUL (2)", text);
        Assert.Contains("détail non conservé", text);
    }

    // --- Notes + the load-bearing caveat ---

    [Fact]
    public void Notes_TravelWithThePaste()
    {
        var text = Render(Run(notes: new[] { "Le code managé ne pilote pas une torture AVX-512 dédiée." }));
        Assert.Contains("NOTES", text);
        Assert.Contains("torture AVX-512 dédiée", text);
    }

    [Fact]
    public void Footer_KeepsTheNotPrime95Caveat_SoAStableNeverOverclaims()
    {
        var text = Render(Run(completed: true, errorCount: 0));
        Assert.Contains("ne remplace PAS plusieurs heures de Prime95", text);
        Assert.Contains("AVX-512", text);
        Assert.Contains("températures", text);
    }

    [Fact]
    public void Cancelled_NeverReadsAsStable()
    {
        var text = Render(Run(completed: false, cancelled: true, errorCount: 0));
        Assert.Contains("Interrompu", text);
        Assert.DoesNotContain("STABLE sur ce test", text);
    }
}
