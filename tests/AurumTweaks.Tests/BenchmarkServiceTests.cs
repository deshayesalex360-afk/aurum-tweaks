using System;
using System.IO;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Tests for the glue around the benchmark backend — specifically <see cref="BenchmarkService.AnalyzeCsv"/>,
/// the always-available, zero-privilege import path that most users actually hit. The pure parsing/metrics
/// are covered by <see cref="FrameTimeCsvParserTests"/> / <see cref="FrameTimeAnalyzerTests"/>; here we verify
/// the service wires them together and — crucially — fails honestly (no fabricated frames) on bad input.
///
/// <para>The live ETW capture path is intentionally not tested: it needs an elevated process and a running
/// DirectX game, neither of which exists in CI. We ship it as real, defensive code (same precedent as NVAPI)
/// and let it fail honestly at runtime rather than fake a result.</para>
/// </summary>
public class BenchmarkServiceTests
{
    private static BenchmarkResultPair Analyze(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"aurum-bench-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, lines);
        try
        {
            return new BenchmarkResultPair(new BenchmarkService().AnalyzeCsv(path), Path.GetFileName(path));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private readonly record struct BenchmarkResultPair(BenchmarkResult Result, string FileName);

    [Fact]
    public void AnalyzeCsv_PresentMonCapture_ProducesStatsAndProvenance()
    {
        var (r, fileName) = Analyze(
            "Application,ProcessID,TimeInSeconds,msBetweenPresents",
            "game.exe,1234,0.000,16.6",
            "game.exe,1234,0.017,16.7",
            "game.exe,1234,0.033,16.5");

        Assert.True(r.HasData);
        Assert.Equal(3, r.Stats.FrameCount);
        Assert.Equal("game.exe", r.TargetProcess);
        Assert.Contains(fileName, r.Source);          // honest provenance: which file it came from
        Assert.NotEmpty(r.Notes);                     // always explains how the numbers were derived
        Assert.True(r.Stats.AvgFps > 59 && r.Stats.AvgFps < 61);   // ~16.6 ms ⇒ ~60 FPS
    }

    [Fact]
    public void AnalyzeCsv_HeaderlessSingleColumn_IsTreatedAsRawFrameTimes()
    {
        var (r, _) = Analyze("16.6", "16.7", "17.0");

        Assert.True(r.HasData);
        Assert.Equal(3, r.Stats.FrameCount);
    }

    [Fact]
    public void AnalyzeCsv_MissingFile_FailsHonestly_NoFabrication()
    {
        var r = new BenchmarkService().AnalyzeCsv(
            Path.Combine(Path.GetTempPath(), "does-not-exist-aurum.csv"));

        Assert.False(r.HasData);
        Assert.Equal(0, r.Stats.FrameCount);
        Assert.NotEmpty(r.Notes);                     // says why, never invents data
    }

    [Fact]
    public void AnalyzeCsv_UnrecognisedHeader_FailsHonestly()
    {
        var (r, _) = Analyze("foo,bar", "1,2", "3,4");

        Assert.False(r.HasData);
        Assert.NotEmpty(r.Notes);
    }

    [Fact]
    public void GetStatus_ReportsLiveAvailabilityAsElevation_WithAMessage()
    {
        var status = new BenchmarkService().GetStatus();

        // Honest contract: live capture is available iff we're elevated, and there is always an explanation.
        Assert.Equal(status.IsElevated, status.LiveCaptureAvailable);
        Assert.False(string.IsNullOrWhiteSpace(status.Message));
    }

    [Fact]
    public void GetCandidateProcesses_NeverThrows_ReturnsList()
    {
        var list = new BenchmarkService().GetCandidateProcesses();
        Assert.NotNull(list);   // contents are environment-dependent; the point is it degrades gracefully
    }
}
