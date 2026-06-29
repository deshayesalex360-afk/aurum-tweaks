using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « Instantané monitoring » paste — the live CPU/GPU/RAM read-back a user drops when the
/// FR scene asks « tes temps / ta charge en jeu ? ». No I/O: it takes the recent REAL <see cref="MonitoringSnapshot"/>
/// samples the page already polled (LibreHardwareMonitor, ~1/s) and lays out, per metric, <b>actuel · moyenne · pic</b> over
/// that window — the peak is what matters for throttling (temp) and for boost (clock), and the average over a window stops a
/// single idle instant from reading as a load figure. Honesty-bearing and therefore unit-tested:
/// <list type="bullet">
/// <item>A temperature or clock of 0 (or below) means the sensor wasn't exposed/read — a running PC is never 0 °C and an
/// active core never 0 MHz — so it renders « non lu », never a fabricated « 0 °C » / « 0 MHz », and the stats are computed
/// only over the real (&gt;0) samples.</item>
/// <item>A load of 0 % IS a legitimate idle reading, so loads are never filtered.</item>
/// <item>RAM and GPU VRAM with a total of 0 mean « not read » → « — », never a fabricated « 0 / 0 Go ».</item>
/// <item>An empty window says so plainly instead of inventing a row.</item>
/// </list>
/// Read-only, never sent, no FPS gain. Mirrors the other <c>*TextReport</c> renderers; the clipboard write is thin glue
/// in the VM, which keeps the bounded real-sample buffer this consumes.
/// </summary>
public static class MonitoringSnapshotTextReport
{
    private const int LabelWidth = 11;

    public static string Render(IReadOnlyList<MonitoringSnapshot> samples, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Instantané monitoring");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', 48));
        sb.AppendLine();

        if (samples.Count == 0)
        {
            sb.AppendLine("Aucune mesure encore disponible — laisse le monitoring tourner une seconde puis réessaie.");
            AppendFooter(sb);
            return sb.ToString();
        }

        sb.AppendLine($"Fenêtre : {samples.Count} mesure(s) (~1/s) — actuel · moyenne · pic");

        sb.AppendLine();
        sb.AppendLine("CPU");
        sb.AppendLine(Row("Charge", LoadLine(samples, s => s.CpuUsagePercent)));
        sb.AppendLine(Row("Température", RealValuedLine(samples, s => s.CpuTempC, "°C")));
        sb.AppendLine(Row("Fréquence", RealValuedLine(samples, s => s.CpuClockMhz, "MHz")));

        sb.AppendLine();
        sb.AppendLine("GPU");
        sb.AppendLine(Row("Charge", LoadLine(samples, s => s.GpuUsagePercent)));
        sb.AppendLine(Row("Température", RealValuedLine(samples, s => s.GpuTempC, "°C")));
        sb.AppendLine(Row("Fréquence", RealValuedLine(samples, s => s.GpuClockMhz, "MHz")));
        sb.AppendLine(Row("VRAM", VramLine(samples)));

        sb.AppendLine();
        sb.AppendLine("RAM");
        sb.AppendLine(Row("Utilisée", RamLine(samples)));

        AppendFooter(sb);
        return sb.ToString();
    }

    private static void AppendFooter(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("Mesures live LibreHardwareMonitor, lues localement et jamais envoyées. Lecture seule : aucun gain de FPS.");
        sb.AppendLine("Un instantané dépend de l'instant : compare à charge égale. Une température / fréquence à 0 = capteur non lu.");
    }

    // Load: 0 % is a real idle reading, so every sample counts.
    private static string LoadLine(IReadOnlyList<MonitoringSnapshot> s, Func<MonitoringSnapshot, float> sel)
    {
        var cur = sel(s[^1]);
        var avg = s.Average(sel);
        var peak = s.Max(sel);
        return $"{cur:F0} %  ·  moy {avg:F0} %  ·  pic {peak:F0} %";
    }

    // Temperature and active-core clock share one honesty rule: a value of 0 means the sensor wasn't read (a running
    // PC is never 0 °C, an active core never 0 MHz), so the stats use only the real (>0) samples and an all-zero
    // series honestly reads « non lu » rather than a fabricated reading. Load is different (0 % idle is real) and
    // keeps its own line. Rendered in whole units (MHz, not GHz) to stay locale-independent — no decimal separator.
    private static string RealValuedLine(IReadOnlyList<MonitoringSnapshot> s, Func<MonitoringSnapshot, float> sel, string unit)
    {
        float sum = 0, peak = float.MinValue, cur = 0;
        int n = 0;
        foreach (var x in s)
        {
            var v = sel(x);
            if (v <= 0) continue;
            sum += v;
            if (v > peak) peak = v;
            cur = v;            // iteration is chronological → ends on the most recent real reading
            n++;
        }
        return n == 0 ? "non lu" : $"{cur:F0} {unit}  ·  moy {sum / n:F0} {unit}  ·  pic {peak:F0} {unit}";
    }

    // RAM: a total of 0 means « not read » → « — » (never a fabricated « 0 / 0 Go »). Peak % spans the window.
    private static string RamLine(IReadOnlyList<MonitoringSnapshot> s)
    {
        var last = s[^1];
        if (last.RamTotalGb <= 0) return "—";
        var peakPct = s.Max(x => x.RamUsagePercent);
        return $"{last.RamUsedGb:F1} / {last.RamTotalGb:F1} Go ({last.RamUsagePercent:F0} %)  ·  pic {peakPct:F0} %";
    }

    // VRAM mirrors system RAM's honesty: a total of 0 means « not read » → « — » (never a fabricated « 0 / 0 Go »).
    // A saturated VRAM is a real stutter cause (texture thrashing), so the peak % over the window is the tell.
    private static string VramLine(IReadOnlyList<MonitoringSnapshot> s)
    {
        var last = s[^1];
        if (last.GpuVramTotalGb <= 0) return "—";
        var peakPct = s.Max(x => x.GpuVramUsagePercent);
        return $"{last.GpuVramUsedGb:F1} / {last.GpuVramTotalGb:F1} Go ({last.GpuVramUsagePercent:F0} %)  ·  pic {peakPct:F0} %";
    }

    private static string Row(string label, string value) => $"    {label.PadRight(LabelWidth)}: {value}";
}
