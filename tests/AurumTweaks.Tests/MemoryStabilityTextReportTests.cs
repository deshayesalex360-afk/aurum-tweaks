using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="MemoryStabilityTextReport"/> — the shareable « Test de stabilité RAM » paste. Honesty contract: a
/// run that never executed prints « Aucun test exécuté » (never a fabricated « STABLE »); the verdict line is the
/// shared <see cref="MemoryStabilityVerdict.Describe"/> so the paste matches the page exactly; caught bit-flips are
/// listed as evidence with explicit windowing; and the load-bearing « ce test rapide ne remplace pas TM5/Karhu »,
/// « mémoire LIBRE seulement » and « offset logique » caveats are always in the footer so a « STABLE » can never be
/// read as an overnight validation. Numbers are fr-FR-formatted by the renderer, so the asserted text is
/// locale-independent.
/// </summary>
public class MemoryStabilityTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 22, 14, 30, 0, DateTimeKind.Utc);

    private static MemoryError Err(
        long offset = 0x1000, ulong expected = 0xAAAAAAAAAAAAAAAA, ulong actual = 0x5555555555555555,
        string pattern = "0xAA")
        => new() { ByteOffset = offset, Expected = expected, Actual = actual, Pattern = pattern };

    private static MemoryTestResult Run(
        bool completed = true, bool cancelled = false, int sizeMb = 2048, int passes = 2,
        double durationSec = 30, int errorCount = 0, double avgMbps = 48000,
        IReadOnlyList<MemoryError>? errors = null, IReadOnlyList<string>? notes = null) => new()
    {
        Completed = completed,
        Cancelled = cancelled,
        SizeMbTested = sizeMb,
        PassesCompleted = passes,
        DurationSec = durationSec,
        ErrorCount = errorCount,
        AvgThroughputMbps = avgMbps,
        Errors = errors ?? Array.Empty<MemoryError>(),
        Notes = notes ?? Array.Empty<string>(),
    };

    private static string Render(MemoryTestResult result) => MemoryStabilityTextReport.Render(result, When);

    // --- Header / empty ---

    [Fact]
    public void Header_CarriesTitle()
        => Assert.Contains("Aurum Tweaks — Test de stabilité RAM", Render(Run()));

    [Fact]
    public void NoRun_SaysSo_WithoutFabricatingAVerdict()
    {
        var text = Render(new MemoryTestResult());   // HasRun false
        Assert.Contains("Aucun test exécuté", text);
        Assert.DoesNotContain("VERDICT", text);
        Assert.DoesNotContain("STABLE", text);
    }

    // --- Verdict + parameters ---

    [Fact]
    public void Stable_RendersTheSharedVerdict_AndTheParameters()
    {
        var text = Render(Run(completed: true, sizeMb: 2048, passes: 2, errorCount: 0, avgMbps: 48000));
        Assert.Contains("VERDICT : STABLE sur ce test", text);
        Assert.Contains("PARAMÈTRES", text);
        Assert.Contains("Mémoire testée", text);
        Assert.Contains("2048", text);
        Assert.Contains("Passes terminées", text);
        Assert.Contains("Débit moyen", text);
        Assert.Contains("48,0 Go/s", text);   // fr-FR forced by the renderer, in the parameters row
    }

    [Fact]
    public void SubGigabyteThroughput_ShownInMoPerSec_InTheParametersRow()
    {
        // The Throughput helper switches to Mo/s below 1000 Mo/s; the parameters row proves that branch.
        // (The verdict sentence always divides by 1000, so Go/s legitimately appears there — not asserted here.)
        var text = Render(Run(avgMbps: 800));
        Assert.Contains("800 Mo/s", text);
    }

    // --- Errors as evidence ---

    [Fact]
    public void Errors_AreListedAsEvidence_NeverHidden()
    {
        var text = Render(Run(completed: true, errorCount: 1,
            errors: new[] { Err(offset: 0x1000, expected: 0x1234, actual: 0x5678, pattern: "0xAA") }));
        Assert.Contains("ERREURS MÉMOIRE (1)", text);
        Assert.Contains("offset 0x1000", text);
        Assert.Contains("1234", text);
        Assert.Contains("5678", text);
        Assert.Contains("motif 0xAA", text);
    }

    [Fact]
    public void Errors_AreWindowed_WithTheTrueTotalInTheHeader()
    {
        var many = Enumerable.Range(0, 10).Select(i => Err(offset: i * 0x1000)).ToList();
        var text = Render(Run(completed: true, errorCount: 10, errors: many));
        Assert.Contains("ERREURS MÉMOIRE (10)", text);   // header keeps the TRUE total
        Assert.Contains("8 sur 10 détaillées", text);     // windowing is explicit, never silent
    }

    [Fact]
    public void ErrorCountWithoutDetail_SaysDetailNotKept_RatherThanInventOne()
    {
        // ErrorCount > 0 but no per-error records: honest about the gap, never a fabricated row.
        var text = Render(Run(completed: true, errorCount: 2, errors: Array.Empty<MemoryError>()));
        Assert.Contains("ERREURS MÉMOIRE (2)", text);
        Assert.Contains("détail non conservé", text);
    }

    [Fact]
    public void BlankPattern_IsOmitted_NotRenderedAsEmptyMotif()
    {
        // The footer legitimately contains « des motifs en RAM », so the omission is checked on the « (motif » token.
        var text = Render(Run(completed: true, errorCount: 1, errors: new[] { Err(pattern: "") }));
        Assert.DoesNotContain("(motif", text);
    }

    // --- Notes + the load-bearing caveat ---

    [Fact]
    public void Notes_TravelWithThePaste()
    {
        var text = Render(Run(notes: new[] { "Allocation partielle : 900 Mo sur 1024 demandés." }));
        Assert.Contains("NOTES", text);
        Assert.Contains("Allocation partielle", text);
    }

    [Fact]
    public void Footer_KeepsTheNotTM5Caveat_AndTheLogicalOffsetHonesty()
    {
        var text = Render(Run(completed: true, errorCount: 0));
        Assert.Contains("ne remplace PAS plusieurs heures de TM5", text);
        Assert.Contains("Karhu", text);
        Assert.Contains("LIBRE", text);               // only the FREE memory is covered
        Assert.Contains("offset est logique", text);  // not a physical DIMM address
    }

    [Fact]
    public void Cancelled_NeverReadsAsStable()
    {
        var text = Render(Run(completed: false, cancelled: true, errorCount: 0));
        Assert.Contains("Interrompu", text);
        Assert.DoesNotContain("STABLE sur ce test", text);
    }
}
