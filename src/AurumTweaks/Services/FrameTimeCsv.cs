using System.Globalization;
using System.Text;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer that writes a captured run's RAW frame-times back out as a CSV — the way a live ETW capture (which
/// otherwise lives only in memory) is kept, re-opened in CapFrameX, or re-imported here. No I/O: it serialises the real
/// <see cref="BenchmarkResult.FrameTimesMs"/> the page already holds, nothing invented.
///
/// <para>Round-trip contract (the honesty property, unit-tested): the body is a single « FrameTime » column the app's
/// own <see cref="FrameTimeCsvParser"/> reads back, and each value is written LOSSLESSLY with the invariant culture
/// (shortest round-trippable form, « . » decimal) so a re-import yields the exact same frames. Provenance (source,
/// process, capture time, frame count) rides along as « # » comment lines, which the parser skips — documenting the
/// file without ever being read as a data row.</para>
/// </summary>
public static class FrameTimeCsv
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Render(BenchmarkResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Aurum Tweaks — frame-times (export brut)");
        if (!string.IsNullOrWhiteSpace(result.Source))
            sb.AppendLine($"# Source : {result.Source.Trim()}");
        if (!string.IsNullOrWhiteSpace(result.TargetProcess))
            sb.AppendLine($"# Process : {result.TargetProcess.Trim()}");
        sb.AppendLine($"# Capturé le : {result.CapturedAt.ToString("dd/MM/yyyy HH:mm", Fr)}");
        sb.AppendLine($"# Frames : {result.FrameTimesMs.Count}");

        // The parser's canonical column name + one lossless value per line (invariant « . » decimal). Writing the
        // shortest round-trippable form keeps a re-import bit-exact and stays readable by CapFrameX / Excel.
        sb.AppendLine("FrameTime");
        foreach (double ms in result.FrameTimesMs)
            sb.AppendLine(ms.ToString(CultureInfo.InvariantCulture));

        return sb.ToString();
    }
}
