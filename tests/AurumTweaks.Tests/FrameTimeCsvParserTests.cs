using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Tests for the frame-time CSV importer — the always-available, zero-privilege path into the
/// benchmark feature. Covers PresentMon-style headers, head-less single columns, FR/Excel
/// (<c>;</c> + comma decimals), unrecognised headers, and robust skipping of garbage rows.
/// </summary>
public class FrameTimeCsvParserTests
{
    [Fact]
    public void Parses_PresentMon_msBetweenPresents_Column()
    {
        string[] lines =
        {
            "Application,ProcessID,TimeInSeconds,msBetweenPresents",
            "game.exe,1234,0.000,16.6",
            "game.exe,1234,0.017,16.7",
            "game.exe,1234,0.033,16.5"
        };

        var r = FrameTimeCsvParser.Parse(lines);

        Assert.True(r.Ok);
        Assert.Equal(3, r.FrameTimesMs.Count);
        Assert.Equal("msBetweenPresents", r.Column);
        Assert.Equal("game.exe", r.Process);
        Assert.Equal(16.6, r.FrameTimesMs[0], 6);
        Assert.Equal(0, r.SkippedRows);
    }

    [Fact]
    public void Parses_HeaderlessSingleColumn_AsRawFrameTimes()
    {
        string[] lines = { "16.6", "16.7", "17.0" };
        var r = FrameTimeCsvParser.Parse(lines);

        Assert.True(r.Ok);
        Assert.Equal(3, r.FrameTimesMs.Count);
        Assert.Equal(16.7, r.FrameTimesMs[1], 6);
    }

    [Fact]
    public void Parses_FrenchExcel_SemicolonDelimiter_WithCommaDecimals()
    {
        string[] lines =
        {
            "FrameTime (ms);Dropped",
            "16,6;0",
            "16,7;0"
        };

        var r = FrameTimeCsvParser.Parse(lines);

        Assert.True(r.Ok);
        Assert.Equal(2, r.FrameTimesMs.Count);
        Assert.Equal(16.6, r.FrameTimesMs[0], 6);
        Assert.Equal(16.7, r.FrameTimesMs[1], 6);
    }

    [Fact]
    public void UnrecognisedHeader_ReturnsNotOk()
    {
        string[] lines = { "foo,bar", "1,2", "3,4" };
        var r = FrameTimeCsvParser.Parse(lines);

        Assert.False(r.Ok);
        Assert.Empty(r.FrameTimesMs);
    }

    [Fact]
    public void SkipsGarbageRows_AndCountsThem()
    {
        string[] lines =
        {
            "msBetweenPresents",
            "16.6",
            "not-a-number",
            "",                 // blank → ignored entirely (not counted as skipped)
            "16.7"
        };

        var r = FrameTimeCsvParser.Parse(lines);

        Assert.Equal(2, r.FrameTimesMs.Count);
        Assert.Equal(1, r.SkippedRows);     // only "not-a-number"
    }

    [Fact]
    public void IgnoresCommentLines()
    {
        string[] lines = { "# PresentMon capture", "msBetweenPresents", "16.6", "16.7" };
        var r = FrameTimeCsvParser.Parse(lines);
        Assert.Equal(2, r.FrameTimesMs.Count);
    }

    [Fact]
    public void Parses_Fraps_CumulativeTimeMs_ByDifferencing()
    {
        // Fraps' "Time (ms)" is a CUMULATIVE timestamp, not a per-frame delta — N rows yield N-1 frame-times,
        // each the gap to the previous sample. Reading it raw would be wildly wrong; differencing is exact.
        string[] lines =
        {
            "Frame, Time (ms)",
            "1, 0",
            "2, 16",
            "3, 33",
            "4, 49"
        };

        var r = FrameTimeCsvParser.Parse(lines);

        Assert.True(r.Ok);
        Assert.True(r.Differenced);
        Assert.Equal("Time (ms)", r.Column);
        Assert.Equal(new[] { 16.0, 17.0, 16.0 }, r.FrameTimesMs);   // 3 deltas from 4 timestamps
        Assert.Equal(0, r.SkippedRows);
    }

    [Fact]
    public void Parses_PresentMon_TimeInSeconds_WhenNoDeltaColumn_ScalesToMs()
    {
        // A stripped PresentMon export with only TimeInSeconds (cumulative, in SECONDS) → ×1000 after differencing.
        string[] lines =
        {
            "TimeInSeconds",
            "0.000",
            "0.016",
            "0.033"
        };

        var r = FrameTimeCsvParser.Parse(lines);

        Assert.True(r.Ok);
        Assert.True(r.Differenced);
        Assert.Equal(2, r.FrameTimesMs.Count);
        Assert.Equal(16.0, r.FrameTimesMs[0], 6);
        Assert.Equal(17.0, r.FrameTimesMs[1], 6);
    }

    [Fact]
    public void PrefersPerFrameDeltaColumn_OverCumulative_WhenBothPresent()
    {
        // PresentMon ships both TimeInSeconds (cumulative) and msBetweenPresents (delta). The delta column
        // wins — we read it directly and never difference, so Differenced stays false.
        string[] lines =
        {
            "TimeInSeconds,msBetweenPresents",
            "0.000,16.6",
            "0.016,16.7"
        };

        var r = FrameTimeCsvParser.Parse(lines);

        Assert.True(r.Ok);
        Assert.False(r.Differenced);
        Assert.Equal("msBetweenPresents", r.Column);
        Assert.Equal(16.6, r.FrameTimesMs[0], 6);
        Assert.Equal(16.7, r.FrameTimesMs[1], 6);
    }

    [Fact]
    public void Cumulative_NonIncreasingTimestamp_IsDroppedNotInverted()
    {
        // A garbled/backwards timestamp must never become a zero or negative frame — it's dropped and counted.
        string[] lines =
        {
            "Frame, Time (ms)",
            "1, 0",
            "2, 16",
            "3, 10",    // backwards → the 16→10 step is dropped, never reported as -6 ms
            "4, 30"
        };

        var r = FrameTimeCsvParser.Parse(lines);

        Assert.True(r.Ok);
        Assert.True(r.Differenced);
        Assert.Equal(new[] { 16.0, 20.0 }, r.FrameTimesMs);   // 0→16, then 10→30
        Assert.Equal(1, r.SkippedRows);
        Assert.All(r.FrameTimesMs, ms => Assert.True(ms > 0));
    }

    [Fact]
    public void Parses_Fraps_FrenchExcel_SemicolonCumulative_WithCommaDecimals()
    {
        // FR/Excel Fraps export: ';' delimiter, ',' decimals, cumulative "Time (ms)" — all three at once.
        string[] lines =
        {
            "Frame;Time (ms)",
            "1;0",
            "2;16,6",
            "3;33,3"
        };

        var r = FrameTimeCsvParser.Parse(lines);

        Assert.True(r.Ok);
        Assert.True(r.Differenced);
        Assert.Equal(2, r.FrameTimesMs.Count);
        Assert.Equal(16.6, r.FrameTimesMs[0], 6);
        Assert.Equal(16.7, r.FrameTimesMs[1], 6);
    }
}
