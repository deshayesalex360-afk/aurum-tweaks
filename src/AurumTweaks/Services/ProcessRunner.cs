using System.Diagnostics;

namespace AurumTweaks.Services;

/// <summary>
/// Runs a console tool and <b>captures</b> its stdout — the shared idiom behind every Aurum surface that has to
/// read a Windows CLI back (powercfg's scheme list, schtasks/Get-ScheduledTask state). It mirrors the tweak
/// engine's fire-and-forget <c>RunShell</c> (no shell, no window, drain stderr so a chatty child can't deadlock,
/// kill the whole tree on timeout) but returns the captured text instead of discarding it. Extracted so the
/// three callers don't each carry their own near-identical copy of this fiddly, deadlock-prone plumbing.
/// </summary>
internal static class ProcessRunner
{
    public static (int ExitCode, string StdOut) Capture(string fileName, string args, int timeoutMs = 15_000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, string.Empty);

            // Read stdout off the pipe; drain stderr too so a chatty child can't fill the buffer and deadlock.
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone / access denied — best effort */ }
                return (-1, string.Empty);
            }
            return (p.ExitCode, outTask.GetAwaiter().GetResult() ?? string.Empty);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }
}
