using System;
using System.Diagnostics.Eventing.Reader;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>Result of scanning the Windows event log for a GPU driver-reset (TDR) in a time window.</summary>
public readonly record struct TdrProbeResult(bool TdrObserved, bool ProbeFailed);

/// <summary>Detects a GPU driver reset (Timeout Detection and Recovery) — the definitive "your overclock
/// is unstable" signal — from the Windows System event log. Injected so the stability VM is testable with
/// a fake instead of the real log.</summary>
public interface IGpuTdrProbe
{
    /// <summary>Scan the last <paramref name="windowMinutes"/> for a driver-reset/bugcheck record.
    /// Must return ProbeFailed=true (never a false "no TDR") when the log can't be read.</summary>
    TdrProbeResult Probe(int windowMinutes);
}

/// <summary>
/// Real probe over the System log. Keys ONLY on genuine GPU-TDR signals (the generic "Display" provider's
/// Event ID 4101/4102 "display driver ... stopped responding and has recovered", the WER bugcheck 1001, and
/// vendor kernel-driver errors) — deliberately NOT disk Event 153 or Group-Policy 4098, which are unrelated
/// and would be false instability flags. If the query throws, it reports ProbeFailed (so the verdict core
/// degrades to Indeterminate) rather than a false clean pass.
/// </summary>
public sealed class GpuTdrProbe : IGpuTdrProbe
{
    public TdrProbeResult Probe(int windowMinutes)
    {
        long ms = Math.Max(1, (long)windowMinutes) * 60_000;
        string xpath =
            "*[System[" +
              "(Provider[@Name='Display'] and (EventID=4101 or EventID=4102 or EventID=4103)) or " +
              "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or " +
              "((Provider[@Name='nvlddmkm'] or Provider[@Name='amdkmdag'] or Provider[@Name='amdwddmg']) " +
                "and (EventID=14 or EventID=13))" +
            "] and System[TimeCreated[timediff(@SystemTime) <= " + ms + "]]]";

        try
        {
            var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            using EventRecord? rec = reader.ReadEvent();   // one matching record is enough to flag a reset
            return new TdrProbeResult(TdrObserved: rec != null, ProbeFailed: false);
        }
        catch (Exception ex)
        {
            // Access/channel/query error → we CANNOT certify a clean run; never report a false "no TDR".
            Log.Warning(ex, "GPU TDR event-log probe failed");
            return new TdrProbeResult(TdrObserved: false, ProbeFailed: true);
        }
    }
}

/// <summary>Test/no-op probe: configurable outcome, no event log touched.</summary>
public sealed class StubGpuTdrProbe : IGpuTdrProbe
{
    private readonly TdrProbeResult _result;
    public StubGpuTdrProbe(bool tdrObserved = false, bool probeFailed = false)
        => _result = new TdrProbeResult(tdrObserved, probeFailed);
    public TdrProbeResult Probe(int windowMinutes) => _result;
}
