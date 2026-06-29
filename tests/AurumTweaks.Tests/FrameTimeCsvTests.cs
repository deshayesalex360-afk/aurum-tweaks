using System;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="FrameTimeCsv"/> — the raw frame-time export that lets a live ETW capture (which otherwise lives
/// only in memory) be archived, re-opened in CapFrameX, or re-imported here. The honesty property is the round-trip:
/// what we write, our own <see cref="FrameTimeCsvParser"/> must read back BIT-EXACT, so a re-import is the same run and
/// not a lossy approximation dressed up as the original. Also pinned: provenance rides along as « # » comment lines the
/// parser skips (documenting the file without ever being mistaken for a data row), blank provenance is omitted rather
/// than printed empty, and values use the invariant « . » decimal the parser expects — never the fr-FR comma.
/// </summary>
public class FrameTimeCsvTests
{
    private static BenchmarkResult Result(
        double[]? frames = null,
        string source = "ETW DXGI · game.exe",
        string process = "game.exe")
    {
        frames ??= new[] { 16.7, 16.6, 17.1, 16.9 };
        return new BenchmarkResult
        {
            Source = source,
            TargetProcess = process,
            CapturedAt = new DateTime(2026, 6, 25, 14, 30, 0),
            FrameTimesMs = frames,
            Stats = new FrameTimeStats { FrameCount = frames.Length },
        };
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    // --- The body the parser reads ---

    [Fact]
    public void Body_HasTheParsersCanonicalFrameTimeHeader()
        => Assert.Contains("FrameTime", Lines(FrameTimeCsv.Render(Result())));

    [Fact]
    public void Values_UseInvariantDecimalPoint_NotAFrenchComma()
    {
        // 1000/60 has a long binary-fraction tail — a perfect probe for « shortest round-trippable, '.' decimal ».
        var text = FrameTimeCsv.Render(Result(frames: new[] { 1000.0 / 60.0 }));
        Assert.Contains("16.666666666666668", text);
        Assert.DoesNotContain("16,666", text);   // fr-FR comma decimal would break the invariant-culture re-import
    }

    // --- Provenance: documented, but never read as data ---

    [Fact]
    public void Provenance_RidesAlongAsCommentLines_NotDataRows()
    {
        var lines = Lines(FrameTimeCsv.Render(Result()));
        Assert.Contains(lines, l => l.StartsWith("#") && l.Contains("Source") && l.Contains("ETW DXGI"));
        Assert.Contains(lines, l => l.StartsWith("#") && l.Contains("Process") && l.Contains("game.exe"));
        Assert.Contains(lines, l => l.StartsWith("#") && l.Contains("Frames") && l.Contains("4"));
        // Anything that isn't a number or the header must be a '#' comment — so the parser drops it, never reads it.
        Assert.All(lines, l => Assert.True(
            l.Length == 0 || l == "FrameTime" || l.StartsWith("#") || char.IsDigit(l[0]),
            $"Unexpected non-comment, non-data line: « {l} »"));
    }

    [Fact]
    public void BlankSourceAndProcess_AreOmitted_NotPrintedEmpty()
    {
        var lines = Lines(FrameTimeCsv.Render(Result(source: "", process: "")));
        Assert.DoesNotContain(lines, l => l.Contains("Source"));
        Assert.DoesNotContain(lines, l => l.Contains("Process"));
        Assert.Contains(lines, l => l.StartsWith("# Capturé"));   // date + count still document the file
        Assert.Contains(lines, l => l.StartsWith("# Frames"));
    }

    [Fact]
    public void NoFrames_StillWritesHeaderAndZeroCount_WithoutCrashing()
    {
        var lines = Lines(FrameTimeCsv.Render(Result(frames: Array.Empty<double>())));
        Assert.Contains("FrameTime", lines);
        Assert.Contains(lines, l => l.StartsWith("# Frames") && l.Contains("0"));
        Assert.DoesNotContain(lines, l => l.Length > 0 && char.IsDigit(l[0]));   // no data rows
    }

    // --- The honesty property: a re-import is the SAME run, bit-for-bit ---

    [Fact]
    public void RoundTrip_ThroughTheAppsOwnParser_IsBitExact()
    {
        // Values chosen for non-terminating binary fractions — if the export weren't lossless, these would drift.
        var original = new[] { 16.666666666666668, 1.0 / 3.0, 0.001, 1234.5678, 8.333333333333334, 16.7 };
        var rendered = FrameTimeCsv.Render(Result(frames: original));

        var parsed = FrameTimeCsvParser.Parse(Lines(rendered));

        Assert.False(parsed.Differenced);             // read straight from the FrameTime column, not derived
        Assert.Equal("FrameTime", parsed.Column);
        Assert.Equal(0, parsed.SkippedRows);          // the '#' provenance + header consumed, never miscounted as data
        Assert.Equal("game.exe", parsed.Process);     // identity recovered from the « # Process » provenance comment
        Assert.Equal(original.Length, parsed.FrameTimesMs.Count);
        for (int i = 0; i < original.Length; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(original[i]),
                         BitConverter.DoubleToInt64Bits(parsed.FrameTimesMs[i]));
    }

    [Fact]
    public void Provenance_Process_IsRecoveredFromTheComment_NotLostOnReimport()
    {
        // An Aurum export's body is a bare « FrameTime » column with no per-row process value, so the run's identity
        // can only survive a round-trip if the parser reads it back from the « # Process : … » provenance comment.
        var rendered = FrameTimeCsv.Render(Result(process: "Cyberpunk2077.exe"));

        var parsed = FrameTimeCsvParser.Parse(Lines(rendered));

        Assert.Equal("Cyberpunk2077.exe", parsed.Process);
    }

    [Fact]
    public void Provenance_BlankProcess_StaysEmpty_NoCommentToRecover()
    {
        // Blank process → FrameTimeCsv omits the « # Process » line entirely, so there is nothing to recover and the
        // parser must not invent one. (Pinning a fabricated identity would violate the honesty mandate.)
        var parsed = FrameTimeCsvParser.Parse(Lines(FrameTimeCsv.Render(Result(process: ""))));
        Assert.Equal(string.Empty, parsed.Process);
    }
}
