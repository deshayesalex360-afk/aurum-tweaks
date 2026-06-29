using System;
using System.Collections.Generic;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="BenchmarkTextReport"/> — the shareable « Benchmark (frame-times) » paste. Honesty contract: a result
/// with no frames prints « Aucune donnée » (never a sheet of zeros that reads as a real run), the provenance line is the
/// result's own Source (ETW capture vs CSV import) and its Notes travel with the paste, and an A/B regression is labelled
/// « régression » — never buffed into a win. The before→after numbers and signs come from the real
/// <see cref="BenchmarkComparer"/>, so this also pins that the renderer reports the comparer faithfully. Numbers are
/// fr-FR-formatted by the renderer, so the asserted decimal commas are locale-independent.
/// </summary>
public class BenchmarkTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 22, 14, 30, 0, DateTimeKind.Utc);

    private static FrameTimeStats Stats(
        int frames = 1200, double durationSec = 20, double avgFps = 120, double minFps = 90, double maxFps = 144,
        double p1Low = 95, double p01Low = 80, double stutterPct = 0.5) => new()
    {
        FrameCount = frames,
        DurationSec = durationSec,
        AvgFps = avgFps,
        MinFps = minFps,
        MaxFps = maxFps,
        P1LowFps = p1Low,
        P01LowFps = p01Low,
        StutterPct = stutterPct,
    };

    private static BenchmarkResult Result(
        FrameTimeStats? stats = null, string source = "ETW DXGI · game.exe", string process = "game.exe",
        IReadOnlyList<string>? notes = null) => new()
    {
        Source = source,
        TargetProcess = process,
        CapturedAt = new DateTime(2026, 6, 22, 14, 28, 0, DateTimeKind.Local),
        Stats = stats ?? Stats(),
        Notes = notes ?? Array.Empty<string>(),
    };

    private static string Render(BenchmarkResult result, BenchmarkComparison? comparison = null)
        => BenchmarkTextReport.Render(result, comparison, When);

    [Fact]
    public void FewFrames_HedgesThatTheTailLowsAreUnreliable()
    {
        // A 600-frame run is below the 1000-frame floor: its « 0,1% low » rests on ~1 sample. The paste must say
        // so — the same honesty the A/B comparer applies, here on a single run.
        var report = Render(Result(Stats(frames: 600)));
        Assert.Contains("Peu d'images (600)", report);
    }

    [Fact]
    public void AmpleFrames_DoNotCarryTheTailLowHedge()
    {
        // 1200 frames clears the floor, so the lows stand on their own — no hedge to add.
        var report = Render(Result(Stats(frames: 1200)));
        Assert.DoesNotContain("Peu d'images", report);
    }

    // --- Header / empty ---

    [Fact]
    public void Header_CarriesTitle()
        => Assert.Contains("Aurum Tweaks — Benchmark (frame-times)", Render(Result()));

    [Fact]
    public void NoData_SaysSo_WithoutFabricatingAResultSheet()
    {
        var text = Render(new BenchmarkResult());   // FrameCount 0 → HasData false
        Assert.Contains("Aucune donnée", text);
        Assert.DoesNotContain("RÉSULTAT", text);
        Assert.DoesNotContain("FPS moyen", text);
    }

    // --- Provenance ---

    [Fact]
    public void Source_RendersTheResultProvenanceVerbatim()
    {
        var text = Render(Result(source: "CSV · capture.csv", process: "game.exe"));
        Assert.Contains("SOURCE", text);
        Assert.Contains("CSV · capture.csv", text);   // « importé » must never read as « capturé »
        Assert.Contains("Process cible", text);
        Assert.Contains("game.exe", text);
    }

    [Fact]
    public void BlankProcess_OmitsTheProcessLine_RatherThanShowingADash()
        => Assert.DoesNotContain("Process cible", Render(Result(process: "   ")));

    [Fact]
    public void Notes_TravelWithThePaste()
    {
        var text = Render(Result(notes: new[] { "Frame-times dérivés d'une colonne cumulative." }));
        Assert.Contains("NOTES", text);
        Assert.Contains("Frame-times dérivés d'une colonne cumulative.", text);
    }

    // --- Result metrics (fr-FR, deterministic) ---

    [Fact]
    public void Result_RendersTheCoreMetricsInFrench()
    {
        var text = Render(Result(stats: Stats(frames: 2400, durationSec: 20, avgFps: 120, p1Low: 95, p01Low: 80)));
        Assert.Contains("RÉSULTAT", text);
        Assert.Contains("2400 sur 20,0 s", text);
        Assert.Contains("FPS moyen", text);
        Assert.Contains("120,0", text);          // fr-FR comma, forced by the renderer
        Assert.Contains("1% low", text);
        Assert.Contains("95,0", text);
        Assert.Contains("0,1% low", text);
        Assert.Contains("80,0", text);
    }

    // --- Comparison (built from the REAL comparer) ---

    [Fact]
    public void Comparison_Improvement_IsLabelledAmélioration_WithBeforeAfterAndSignedDelta()
    {
        var before = Result(stats: Stats(avgFps: 100), process: "game.exe");
        var after = Result(stats: Stats(avgFps: 120), process: "game.exe");
        var text = Render(after, BenchmarkComparer.Compare(before, after));

        Assert.Contains("COMPARAISON (Avant → Après)", text);
        Assert.Contains("FPS moyen", text);
        Assert.Contains("100,0 → 120,0", text);
        Assert.Contains("+20,0", text);          // signed delta — a gain shows its sign
        Assert.Contains("amélioration", text);
    }

    [Fact]
    public void Comparison_Regression_IsReportedHonestly_NeverBuffedIntoAWin()
    {
        // Average FPS dropped; every other metric is flat (0 → 0). The headline must read « régression »,
        // and nothing in the paste may claim « amélioration ».
        var before = Result(stats: Stats(avgFps: 120, minFps: 0, maxFps: 0, p1Low: 0, p01Low: 0, stutterPct: 0));
        var after = Result(stats: Stats(avgFps: 100, minFps: 0, maxFps: 0, p1Low: 0, p01Low: 0, stutterPct: 0));
        var text = Render(after, BenchmarkComparer.Compare(before, after));

        Assert.Contains("régression", text);
        Assert.DoesNotContain("amélioration", text);
    }

    [Fact]
    public void Comparison_CarriesTheComparabilityCaveats()
    {
        // Two different processes → the comparer flags an indicative (not strict A/B) comparison; that hedge must
        // survive into the shared paste.
        var before = Result(stats: Stats(durationSec: 20), process: "gameA.exe");
        var after = Result(stats: Stats(durationSec: 20), process: "gameB.exe");
        var text = Render(after, BenchmarkComparer.Compare(before, after));

        Assert.Contains("Réserves :", text);
        Assert.Contains("process différents", text);
    }

    [Fact]
    public void NoComparison_OmitsTheComparisonSection()
        => Assert.DoesNotContain("COMPARAISON", Render(Result()));

    // --- Régularité verdict (the shared FrameConsistencyVerdict label rides into the paste) ---

    [Fact]
    public void Consistency_Verdict_AppearsInTheReport_AsTheSharedLabel()
    {
        // Default stats: 1% low 95 / avg 120 → ratio 0,79 → « Correcte » (the shared FrameConsistencyVerdict label).
        var text = Render(Result());
        Assert.Contains("RÉGULARITÉ", text);
        Assert.Contains("Correcte", text);
    }

    [Fact]
    public void Consistency_Choppy_IsLabelledIrrégulière_NotBuffed()
    {
        // 1% low 50 / avg 120 → ratio 0,42 → the lows fall away → « Irrégulière ».
        var text = Render(Result(stats: Stats(p1Low: 50)));
        Assert.Contains("Irrégulière", text);
        Assert.DoesNotContain("Diffusion régulière", text);   // the choppy verdict must not read as the smooth one
    }

    // --- Footer / honesty ---

    [Fact]
    public void Footer_StatesItIsLocalAndExplainsTheLowsConvention()
    {
        var text = Render(Result());
        Assert.Contains("mesurés localement et jamais envoyés", text);
        Assert.Contains("convention centile", text);
    }

    [Fact]
    public void Footer_SpellsOutThatRegularityIsNotAnFpsJudgement()
        => Assert.Contains("pas si le niveau de FPS est suffisant", Render(Result()));
}
