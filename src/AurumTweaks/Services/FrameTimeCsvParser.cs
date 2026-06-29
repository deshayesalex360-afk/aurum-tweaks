using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure parser for frame-time CSVs so the benchmark feature works with zero privileges: capture with
/// PresentMon or CapFrameX (or anything that exports a frame-time column), then import here.
///
/// <para>Recognised frame-time columns (case/spacing-insensitive), in priority order:
/// <c>msBetweenPresents</c> (PresentMon), <c>msBetweenDisplayChange</c>, <c>FrameTime</c>,
/// <c>FrameTime(ms)</c>, or a literal <c>ms</c> column. A header-less single column of numbers is
/// treated as raw frame times. Both <c>,</c> (US) and <c>;</c> (FR/Excel, with <c>,</c> decimals)
/// delimiters are handled. Unparseable rows are skipped and counted — never guessed.</para>
///
/// <para>When no per-frame column is present, a CUMULATIVE-timestamp column is differenced instead —
/// Fraps' <c>Time (ms)</c> or PresentMon's <c>TimeInSeconds</c> (×1000): each frame-time is the gap
/// between consecutive timestamps (tᵢ − tᵢ₋₁). The transform is exact, never a fabrication, and the
/// result sets <see cref="FrameCsvParseResult.Differenced"/> so the derivation is surfaced, not hidden.</para>
/// </summary>
public static class FrameTimeCsvParser
{
    // Normalised, in priority order (most specific / most trustworthy first).
    private static readonly string[] FrameTimeCols =
        { "msbetweenpresents", "msbetweendisplaychange", "frametime", "frametimems", "ms" };

    // Cumulative-timestamp columns, consulted ONLY when no per-frame delta column exists (a Fraps log has
    // just "Frame, Time (ms)"; a stripped PresentMon export, just "TimeInSeconds"). Frame-times are
    // reconstructed by differencing; the scale converts the column's native unit to milliseconds.
    private static readonly (string Norm, double ScaleToMs)[] CumulativeCols =
        { ("timems", 1.0), ("timeinseconds", 1000.0) };

    private static readonly string[] ProcessCols = { "application", "processname", "process" };

    public static FrameCsvParseResult Parse(IEnumerable<string> lines)
    {
        var all = lines is null
            ? new List<string>()
            : lines.Select(l => l?.Trim() ?? string.Empty).ToList();

        // Recover the target process from our own « # Process : … » provenance comment (written by
        // FrameTimeCsv.Render) before the comments are dropped. Used only as a FALLBACK below — a real process
        // data-column always wins — so a re-imported Aurum export keeps its run identity even though its body is a
        // bare FrameTime column with no per-row process value.
        string commentProcess = FindCommentProcess(all);

        var kept = all
            .Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("//"))
            .ToList();

        if (kept.Count == 0) return new FrameCsvParseResult();

        bool semicolon = kept[0].Contains(';') && !kept[0].Contains(',');
        char delim = semicolon ? ';' : ',';

        var firstTokens = Split(kept[0], delim);
        bool firstIsHeader = firstTokens.Any(t => !TryNum(t, semicolon, out _));

        int ftCol, procCol, dataStart;
        bool cumulative = false;
        double cumulativeScaleToMs = 1.0;
        if (firstIsHeader)
        {
            ftCol = FindColumn(firstTokens, FrameTimeCols);
            procCol = FindColumn(firstTokens, ProcessCols);
            dataStart = 1;

            if (ftCol < 0)
            {
                // No per-frame delta column — fall back to a cumulative-timestamp column (Fraps "Time (ms)",
                // PresentMon "TimeInSeconds") and reconstruct frame-times by differencing it after the read.
                (ftCol, cumulativeScaleToMs) = FindCumulativeColumn(firstTokens);
                cumulative = ftCol >= 0;

                // Header present but neither a delta nor a cumulative column → nothing we can trust.
                if (ftCol < 0) return new FrameCsvParseResult { SkippedRows = kept.Count - 1 };
            }
        }
        else
        {
            // Head-less: assume a single column (or first column) of raw frame times in ms.
            ftCol = 0;
            procCol = -1;
            dataStart = 0;
        }

        var frames = new List<double>(kept.Count);
        string process = string.Empty;
        int skipped = 0;

        for (int i = dataStart; i < kept.Count; i++)
        {
            var tok = Split(kept[i], delim);
            if (ftCol >= tok.Length) { skipped++; continue; }

            if (TryNum(tok[ftCol], semicolon, out double ms)) frames.Add(ms);
            else { skipped++; continue; }

            if (procCol >= 0 && process.Length == 0 && procCol < tok.Length)
                process = tok[procCol].Trim().Trim('"');
        }

        if (cumulative)
            frames = DifferenceCumulative(frames, cumulativeScaleToMs, ref skipped);

        // No process data-column → fall back to the provenance comment so an Aurum re-import isn't anonymous.
        if (process.Length == 0)
            process = commentProcess;

        string column = firstIsHeader && ftCol >= 0 ? firstTokens[ftCol].Trim().Trim('"') : "col 1";
        return new FrameCsvParseResult
        {
            FrameTimesMs = frames,
            Column = column,
            Process = process,
            SkippedRows = skipped,
            Differenced = cumulative
        };
    }

    private static string[] Split(string line, char delim) => line.Split(delim);

    private static int FindColumn(string[] headerTokens, string[] normalizedCandidates)
    {
        foreach (var cand in normalizedCandidates)
            for (int i = 0; i < headerTokens.Length; i++)
                if (Norm(headerTokens[i]) == cand) return i;
        return -1;
    }

    /// <summary>
    /// Recover a process name from a leading « # … : value » provenance comment whose label is one of
    /// <see cref="ProcessCols"/> (e.g. « # Process : game.exe », as written by <see cref="FrameTimeCsv"/>). Pure
    /// string work over the comment lines; returns the first match, or empty when no such comment is present.
    /// </summary>
    private static string FindCommentProcess(IReadOnlyList<string> lines)
    {
        foreach (var raw in lines)
        {
            if (raw.Length == 0 || raw[0] != '#') continue;
            string body = raw.TrimStart('#').Trim();
            int colon = body.IndexOf(':');
            if (colon <= 0) continue;
            if (Array.IndexOf(ProcessCols, Norm(body[..colon])) < 0) continue;
            string value = body[(colon + 1)..].Trim().Trim('"');
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    private static (int Index, double ScaleToMs) FindCumulativeColumn(string[] headerTokens)
    {
        foreach (var (name, scaleToMs) in CumulativeCols)
            for (int i = 0; i < headerTokens.Length; i++)
                if (Norm(headerTokens[i]) == name) return (i, scaleToMs);
        return (-1, 1.0);
    }

    /// <summary>
    /// Reconstruct per-frame times from a monotonic cumulative-timestamp column: each frame-time is the gap
    /// to the previous sample, scaled to ms. The first sample is the baseline (it yields no frame), and a
    /// non-increasing pair — a duplicate or garbled timestamp — is dropped and counted, never turned into a
    /// zero/negative frame. Exact arithmetic on the captured timestamps: nothing is invented.
    /// </summary>
    private static List<double> DifferenceCumulative(List<double> cumulative, double scaleToMs, ref int skipped)
    {
        var deltas = new List<double>(cumulative.Count);
        for (int i = 1; i < cumulative.Count; i++)
        {
            double dt = (cumulative[i] - cumulative[i - 1]) * scaleToMs;
            if (dt > 0) deltas.Add(dt);
            else skipped++;
        }
        return deltas;
    }

    private static string Norm(string s)
    {
        var arr = s.Trim().Trim('"').ToLowerInvariant()
                   .Where(c => c is not (' ' or '_' or '(' or ')' or '[' or ']'))
                   .ToArray();
        return new string(arr);
    }

    private static bool TryNum(string token, bool semicolon, out double value)
    {
        token = token.Trim().Trim('"');
        // No AllowThousands: frame times never carry a thousands separator, and allowing it would make
        // the invariant parser read the FR decimal "16,6" as 166 before the comma-decimal fallback runs.
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        // FR/Excel files use ';' as delimiter and ',' as the decimal mark.
        if (semicolon && double.TryParse(token.Replace(',', '.'), NumberStyles.Float,
                                         CultureInfo.InvariantCulture, out value))
            return true;

        value = 0;
        return false;
    }
}
