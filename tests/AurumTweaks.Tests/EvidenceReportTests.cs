using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Guards the unified « Preuve avant / après » composer — the honesty core of the « testable sans peur, preuve à
/// l'appui » promise. The load-bearing rules, each pinned here:
/// <list type="bullet">
/// <item>a frame-time movement is rendered with the EXACT same <see cref="BenchmarkTextReport.MetricLine"/> as the
/// Benchmark page's own paste, so the same A/B can't be worded two ways across surfaces;</item>
/// <item>a regression is labelled « régression », never buffed into a win;</item>
/// <item>a missing surface prints « Non disponible — comment la produire », never a fabricated delta or a sheet of
/// zeros that would read as a real measurement — and the footer states plainly that « non disponible » ≠ échec.</item>
/// </list>
/// Pure arithmetic over the three comparison records, so no I/O — same precedent as <see cref="BenchmarkTextReport"/>.
/// </summary>
public class EvidenceReportTests
{
    private static readonly System.DateTime Generated = new(2026, 6, 27, 10, 0, 0, System.DateTimeKind.Utc);

    private static BenchmarkResult Run(double avgFps, double p1Low, string process = "game")
        => new()
        {
            TargetProcess = process,
            Stats = new FrameTimeStats { FrameCount = 1000, DurationSec = 20, AvgFps = avgFps, P1LowFps = p1Low }
        };

    // A real comparison through the tested comparer, so the report consumes exactly what the page produces.
    private static BenchmarkComparison Perf(double beforeFps, double afterFps)
        => BenchmarkComparer.Compare(Run(beforeFps, beforeFps * 0.8), Run(afterFps, afterFps * 0.8));

    private static SnapshotChange Improved(string name)
        => new(name, name, TweakAppliedState.NotApplied, TweakAppliedState.Applied);

    private static SnapshotChange Regressed(string name)
        => new(name, name, TweakAppliedState.Applied, TweakAppliedState.NotApplied);

    private static SnapshotComparison Settings(
        IReadOnlyList<SnapshotChange>? improvements = null,
        IReadOnlyList<SnapshotChange>? regressions = null,
        string summary = "1 optimisation(s) désormais active(s)")
        => new()
        {
            Improvements = improvements ?? System.Array.Empty<SnapshotChange>(),
            Regressions = regressions ?? System.Array.Empty<SnapshotChange>(),
            Summary = summary
        };

    // A scorecard through the real pure core: `applied` live, the rest readable-but-off → HasData true.
    private static OptimizationScorecard Score(int applied, int verifiable)
    {
        var inputs = new List<ScoreInput>();
        for (var i = 0; i < verifiable; i++)
            inputs.Add(new ScoreInput(TweakCategory.PerformanceMultimedia, 1,
                i < applied ? TweakAppliedState.Applied : TweakAppliedState.NotApplied));
        return OptimizationScore.Compute(inputs);
    }

    // A "clean" rig — no honest tells trip (SMT on, RAM at rated, matched sticks) — so a MACHINE block from
    // it is plain. Tests that want a tell mutate one field (e.g. RamConfiguredMhz) off this baseline.
    private static HardwareInfo Machine() => new()
    {
        CpuName = "AMD Ryzen 7 7800X3D",
        CpuCores = 8,
        CpuThreads = 16,
        CpuMaxThreads = 16,                 // == CpuThreads ⇒ SmtCapableButOff stays false
        GpuPrimary = "NVIDIA GeForce RTX 4070",
        GpuDriverVersion = "551.86",
        TotalRamBytes = 32L * 1024 * 1024 * 1024,
        RamType = "DDR5",
        RamConfiguredMhz = 6000,
        RamRatedMhz = 6000,                 // == configured ⇒ RamRunningBelowRated stays false
        RamModuleCount = 2,
        OsCaption = "Windows 11 Pro",
        OsBuild = "22631"
    };

    [Fact]
    public void Render_WithAllThree_HasHeaderEverySection_AndReusesTheBenchmarkMetricLine()
    {
        var perf = Perf(beforeFps: 100, afterFps: 120);
        var inputs = new EvidenceInputs(
            Settings(new[] { Improved("Tweak A") }, new[] { Regressed("Tweak B") }, "résumé du diff"),
            "Avant MAJ", "maintenant",
            perf,
            Score(applied: 7, verifiable: 10),
            ScoreProgress.None);

        var text = EvidenceReport.Render(inputs, Generated);

        Assert.Contains("Preuve avant / après", text);
        Assert.Contains("PERFORMANCE", text);
        Assert.Contains("RÉGLAGES (Avant MAJ → maintenant)", text);
        Assert.Contains("SCORE D'OPTIMISATION", text);

        // Anti-drift: the headline line is byte-for-byte the Benchmark page's own MetricLine.
        Assert.Contains(BenchmarkTextReport.MetricLine(perf.Headline), text);

        // The settings story and the score, drawn from the shared sources (Summary, names, GradeLabel, counts).
        Assert.Contains("résumé du diff", text);
        Assert.Contains("Tweak A", text);
        Assert.Contains("Tweak B", text);
        Assert.Contains("7 / 10", text);

        Assert.DoesNotContain("Non disponible", text);
    }

    [Fact]
    public void Render_PerformanceRegression_IsLabelledRegression_NeverBuffedIntoAWin()
    {
        var perf = Perf(beforeFps: 120, afterFps: 100);   // FPS dropped after the tweak
        Assert.True(perf.Headline.Regressed);             // guard: the fixture really is a regression

        var text = EvidenceReport.Render(EvidenceInputs.Empty with { Performance = perf }, Generated);

        Assert.Contains("régression", text);
        Assert.Contains(BenchmarkTextReport.MetricLine(perf.Headline), text);   // same words as the page
    }

    [Fact]
    public void Render_Performance_DisclosesRunProvenance_SourceAndLength_ForCredibility()
    {
        // A forum reader's first question is « mesuré comment, sur quoi, combien de temps ? ». The proof must
        // disclose each run's Source token and length, verbatim from the captures — so a short hand-made CSV can't
        // pass for a long capture. The « Réserves » flag mismatches; this discloses the provenance even when clean.
        var before = new BenchmarkResult
        {
            Source = "ETW DXGI · game.exe", TargetProcess = "game.exe",
            Stats = new FrameTimeStats { FrameCount = 1500, DurationSec = 30, AvgFps = 100, P1LowFps = 80 }
        };
        var after = new BenchmarkResult
        {
            Source = "ETW DXGI · game.exe", TargetProcess = "game.exe",
            Stats = new FrameTimeStats { FrameCount = 1600, DurationSec = 32, AvgFps = 120, P1LowFps = 96 }
        };
        var text = EvidenceReport.Render(
            EvidenceInputs.Empty with { Performance = BenchmarkComparer.Compare(before, after) }, Generated);

        Assert.Contains("ETW DXGI · game.exe", text);      // the capture method + process, verbatim
        Assert.Contains("1500 images sur 30,0 s", text);   // before run length (fr-FR, like the Benchmark page)
        Assert.Contains("1600 images sur 32,0 s", text);   // after run length
    }

    [Fact]
    public void Render_Performance_DisclosesEachRunCaptureDate_SoAReloadedABCantPassAsFresh()
    {
        // The A/B is the one durable proof (it survives a restart), so a comparison reloaded days later must not read
        // as fresh under today's « Généré le ». Each run's capture date rides into the provenance, verbatim and local.
        var before = new BenchmarkResult
        {
            TargetProcess = "game", CapturedAt = new System.DateTime(2026, 6, 20, 14, 0, 0, System.DateTimeKind.Local),
            Stats = new FrameTimeStats { FrameCount = 1000, DurationSec = 20, AvgFps = 100, P1LowFps = 80 }
        };
        var after = new BenchmarkResult
        {
            TargetProcess = "game", CapturedAt = new System.DateTime(2026, 6, 21, 9, 0, 0, System.DateTimeKind.Local),
            Stats = new FrameTimeStats { FrameCount = 1000, DurationSec = 20, AvgFps = 120, P1LowFps = 96 }
        };

        var text = EvidenceReport.Render(
            EvidenceInputs.Empty with { Performance = BenchmarkComparer.Compare(before, after) }, Generated);

        Assert.Contains("le 20/06/2026", text);
        Assert.Contains("le 21/06/2026", text);
    }

    [Fact]
    public void Render_Performance_WhenSourceAbsent_FallsBackToProcessName_NeverFabricatesACaptureMethod()
    {
        // The fixture's runs carry a process but no rich Source token. Provenance must degrade to the bare process
        // name + length, never invent a capture method (« ETW … ») the run never had.
        var text = EvidenceReport.Render(EvidenceInputs.Empty with { Performance = Perf(100, 110) }, Generated);

        Assert.Contains("game — 1000 images sur 20,0 s", text);
        Assert.DoesNotContain("ETW", text);
    }

    [Fact]
    public void Render_WithNothingMeasured_IsWellFormed_EverySectionGuidesNotFabricates()
    {
        var text = EvidenceReport.Render(EvidenceInputs.Empty, Generated);

        // Still a real document: header + the three section headers + footer.
        Assert.Contains("Preuve avant / après", text);
        Assert.Contains("PERFORMANCE", text);
        Assert.Contains("RÉGLAGES", text);
        Assert.Contains("SCORE D'OPTIMISATION", text);

        // Each absent surface guides the user to produce it — three honest "Non disponible", no invented numbers.
        Assert.Equal(3, Occurrences(text, "Non disponible"));
        Assert.Contains("Benchmark", text);
        Assert.Contains("Instantanés", text);
        Assert.Contains("Tableau de bord", text);

        // No fabricated score: a "/ 100" must never appear without real data behind it.
        Assert.DoesNotContain("/ 100", text);
    }

    [Fact]
    public void Render_OnlyPerformance_LeavesSettingsAndScoreAsNonDisponible()
    {
        var inputs = EvidenceInputs.Empty with { Performance = Perf(100, 110) };
        var text = EvidenceReport.Render(inputs, Generated);

        Assert.Contains(BenchmarkTextReport.MetricLine(inputs.Performance!.Headline), text);
        Assert.Equal(2, Occurrences(text, "Non disponible"));   // settings + score only
    }

    [Fact]
    public void Render_OnlySettings_LeavesPerformanceAndScoreAsNonDisponible()
    {
        var inputs = EvidenceInputs.Empty with
        {
            Settings = Settings(new[] { Improved("Tweak A") }),
            SettingsBaselineLabel = "Avant",
            SettingsTargetLabel = "maintenant"
        };
        var text = EvidenceReport.Render(inputs, Generated);

        Assert.Contains("Tweak A", text);
        Assert.Equal(2, Occurrences(text, "Non disponible"));   // performance + score only
    }

    [Fact]
    public void Render_OnlyScore_LeavesPerformanceAndSettingsAsNonDisponible()
    {
        var inputs = EvidenceInputs.Empty with { Score = Score(applied: 5, verifiable: 5) };
        var text = EvidenceReport.Render(inputs, Generated);

        Assert.Contains("5 / 5", text);
        Assert.Contains("100 / 100", text);                     // full set → a 100/100 headline
        Assert.Equal(2, Occurrences(text, "Non disponible"));   // performance + settings only
    }

    [Fact]
    public void Render_NoDataScore_IsTreatedAsAbsent_NotAFabricatedZero()
    {
        // A scorecard exists but rests on nothing verifiable. It must read « non disponible », never « 0/100 ».
        var inputs = EvidenceInputs.Empty with { Score = OptimizationScorecard.Empty, ScoreTrend = ScoreProgress.None };
        Assert.False(inputs.HasScore);

        var text = EvidenceReport.Render(inputs, Generated);

        Assert.DoesNotContain("0 / 100", text);
        Assert.Equal(3, Occurrences(text, "Non disponible"));
    }

    [Fact]
    public void Render_IndeterminateTweaks_AreDisclosed_NotHidden()
    {
        // 6 applied, 4 readable-off, 3 unverifiable → the score excludes the 3 but DISCLOSES them.
        var inputs = new List<ScoreInput>();
        for (var i = 0; i < 6; i++) inputs.Add(new ScoreInput(TweakCategory.Gaming, 1, TweakAppliedState.Applied));
        for (var i = 0; i < 4; i++) inputs.Add(new ScoreInput(TweakCategory.Gaming, 1, TweakAppliedState.NotApplied));
        for (var i = 0; i < 3; i++) inputs.Add(new ScoreInput(TweakCategory.Gaming, 1, TweakAppliedState.Indeterminate));
        var card = OptimizationScore.Compute(inputs);
        Assert.Equal(3, card.IndeterminateCount);

        var text = EvidenceReport.Render(EvidenceInputs.Empty with { Score = card }, Generated);

        Assert.Contains("6 / 10", text);
        Assert.Contains("3 non vérifiable", text);
    }

    [Fact]
    public void Render_LongBucket_IsCapped_WithAnHonestRemainderCount()
    {
        var improvements = Enumerable.Range(1, 20).Select(n => Improved($"Tweak {n:00}")).ToArray();
        var inputs = EvidenceInputs.Empty with
        {
            Settings = Settings(improvements, summary: "20 optimisation(s) désormais active(s)"),
            SettingsBaselineLabel = "Avant",
            SettingsTargetLabel = "maintenant"
        };

        var text = EvidenceReport.Render(inputs, Generated);

        Assert.Contains("Tweak 01", text);
        Assert.Contains("Tweak 12", text);     // the 12th (cap) is shown
        Assert.DoesNotContain("Tweak 13", text);
        Assert.Contains("… +8 de plus", text); // 20 − 12 = 8, honestly counted
    }

    [Fact]
    public void Render_ScoreTrend_AppearsOnlyWhenThereIsRealMovement()
    {
        var withTrend = new ScoreProgress(true, Current: 82, Previous: 69, Delta: 13, SinceUtc: Generated.AddDays(-3));
        var text = EvidenceReport.Render(
            EvidenceInputs.Empty with { Score = Score(8, 10), ScoreTrend = withTrend }, Generated);

        Assert.Contains(withTrend.TrendLine, text);   // the shared trend line, verbatim
        Assert.Contains("En hausse", text);
    }

    [Fact]
    public void Render_FooterAlwaysStates_LocalOnly_AndNonDisponibleIsNotAFailure()
    {
        foreach (var inputs in new[] { EvidenceInputs.Empty, EvidenceInputs.Empty with { Score = Score(3, 4) } })
        {
            var text = EvidenceReport.Render(inputs, Generated);
            Assert.Contains("jamais envoyée", text);
            Assert.Contains("aucune valeur n'est simulée", text);
            Assert.Contains("jamais un échec", text);
        }
    }

    [Fact]
    public void Render_WithHardware_EmitsMachineSection_ReusingSystemReportLinesVerbatim()
    {
        // Whose rig produced these numbers is the context a « +12 % de 1% low » paste needs to be credible.
        // Anti-drift: the four lines are byte-for-byte SystemReport's own CpuLine/GpuLine/RamLine/OsLine, so the
        // proof and the full system report can never disagree on the hardware (same mandate as the MetricLine reuse).
        var hw = Machine();
        var text = EvidenceReport.Render(EvidenceInputs.Empty, Generated, hw);

        Assert.Contains("MACHINE", text);
        Assert.Contains(SystemReport.CpuLine(hw), text);
        Assert.Contains(SystemReport.GpuLine(hw), text);
        Assert.Contains(SystemReport.RamLine(hw), text);
        Assert.Contains(SystemReport.OsLine(hw), text);
    }

    [Fact]
    public void Render_WithoutHardware_OmitsMachineSection_AndFabricatesNoSpec()
    {
        // The rig is auto-detected, not user-produced, so a null one is simply omitted — never a fabricated
        // « non disponible » spec (which would wrongly read as a measurement the user failed to take). Omitting it
        // also adds no honesty debt: the three measured surfaces still print exactly their three « Non disponible ».
        var withHw = EvidenceReport.Render(EvidenceInputs.Empty, Generated, Machine());
        var without = EvidenceReport.Render(EvidenceInputs.Empty, Generated);

        Assert.Contains("MACHINE", withHw);
        Assert.DoesNotContain("MACHINE", without);
        Assert.Equal(3, Occurrences(without, "Non disponible"));
        Assert.Equal(3, Occurrences(withHw, "Non disponible"));   // the MACHINE block is not a fourth absence
    }

    [Fact]
    public void Render_WithHardware_CarriesTheHonestTells_IntoTheProof()
    {
        // RAM left at JEDEC (running well under its rated speed) is the classic « EXPO/XMP jamais activé » tell.
        // The rig context is only credible if its warts show too, so the tell must ride into the proof verbatim —
        // and through the shared RamLine, so the proof and the system report word the same wart identically.
        var hw = Machine();
        hw.RamConfiguredMhz = 4800;
        hw.RamRatedMhz = 6000;
        Assert.True(hw.RamRunningBelowRated);   // guard: the fixture really trips the tell

        var text = EvidenceReport.Render(EvidenceInputs.Empty, Generated, hw);

        Assert.Contains("EXPO/XMP probablement désactivé", text);
        Assert.Contains(SystemReport.RamLine(hw), text);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void HasAnyEvidence_IsTrue_WhenAnyRealSurfaceIsPresent(
        bool settings, bool perf, bool score, bool expected)
    {
        var inputs = new EvidenceInputs(
            settings ? Settings(new[] { Improved("a") }) : null,
            settings ? "Avant" : null,
            settings ? "maintenant" : null,
            perf ? Perf(100, 110) : null,
            score ? Score(3, 4) : null,
            ScoreProgress.None);

        Assert.Equal(expected, inputs.HasAnyEvidence);
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
