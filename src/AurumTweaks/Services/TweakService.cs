using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using AurumTweaks.Models;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>
/// Pure builder for the shell-based tweak operations (PowerShell / Cmd / Bcdedit / AppX / ScheduledTask):
/// turns a <see cref="TweakOperation"/> + apply/revert direction into the exact <c>(fileName, arguments)</c>
/// that <see cref="TweakService"/> would run — but runs nothing itself.
///
/// Extracted from <c>TweakService.ExecuteAsync</c> so the reversibility contract is testable without
/// spawning processes. The contract is the honesty surface: <b>apply and revert must be genuine inverses</b>.
/// The two that matter most ship in the catalog today — AppX debloat (apply = Remove-AppxPackage, revert =
/// re-register the package, so "we removed it, we can put it back" stays true) and PowerShell/Cmd/Bcdedit
/// (apply runs <c>Script</c>, revert runs <c>RevertScript</c> — never the same one twice). A regression that
/// silently ran the apply branch on revert would break reversibility without throwing; these strings pin it.
///
/// Returns <c>null</c> for non-shell ops (Registry/Service/File) and for shell ops missing a required field
/// (AppX without a package, ScheduledTask without a task path) — matching the old early <c>return false</c>.
/// </summary>
public static class TweakShellCommand
{
    public static (string FileName, string Arguments)? Build(TweakOperation op, bool applying)
    {
        switch (op.Type)
        {
            case OperationType.PowerShell:
                return ("powershell.exe",
                    $"-NoProfile -NonInteractive -Command \"{(applying ? op.Script : op.RevertScript)}\"");

            case OperationType.Cmd:
                return ("cmd.exe", $"/c {(applying ? op.Script : op.RevertScript)}");

            case OperationType.Bcdedit:
                return ("bcdedit.exe", applying ? op.Script ?? string.Empty : op.RevertScript ?? string.Empty);

            case OperationType.AppX:
                if (op.AppxPackage is null) return null;
                return ("powershell.exe", applying
                    ? $"-NoProfile -NonInteractive -Command \"Get-AppxPackage *{op.AppxPackage}* | Remove-AppxPackage -ErrorAction SilentlyContinue\""
                    : $"-NoProfile -NonInteractive -Command \"Get-AppxPackage -AllUsers *{op.AppxPackage}* | ForEach-Object {{ Add-AppxPackage -DisableDevelopmentMode -Register \\\"$($_.InstallLocation)\\AppxManifest.xml\\\" -ErrorAction SilentlyContinue }}\"");

            case OperationType.ScheduledTask:
                if (op.TaskPath is null) return null;
                return ("schtasks.exe", applying
                    ? $"/Change /TN \"{op.TaskPath}\" /Disable"
                    : $"/Change /TN \"{op.TaskPath}\" /Enable");

            default:
                return null;
        }
    }
}

/// <summary>
/// Maps an operation tally to the honest <see cref="TweakApplyResult"/> for a tweak's apply/revert. Pulled out
/// as a pure function (no I/O) because it is the honesty surface: success is reported ONLY when zero ops
/// failed, and a partial failure carries the user-facing "N/M opération(s) ont échoué" truth-claim. Living in
/// one place — and unit-testable without the registry — keeps ApplyAsync, RevertAsync, and the message the UI
/// shows from ever drifting apart.
/// </summary>
public static class TweakApplyOutcome
{
    public static TweakApplyResult From(int failed, int total, bool requiresReboot) =>
        failed == 0
            ? new TweakApplyResult(true, null, requiresReboot)
            : new TweakApplyResult(false, $"{failed}/{total} opération(s) ont échoué", requiresReboot);
}

/// <summary>
/// Pure fold from per-operation probes to a tweak's honest <see cref="TweakAppliedState"/>. Each probe is a
/// nullable bool: <c>true</c> = that op is in its applied state, <c>false</c> = it differs, <c>null</c> = it
/// can't be read back (a shell op, a missing service). Extracted as I/O-free logic because the precedence IS
/// the honesty rule and must be pinned: one KNOWN-off op makes the whole tweak <see cref="TweakAppliedState.NotApplied"/>;
/// otherwise any unreadable op forces <see cref="TweakAppliedState.Indeterminate"/> (we never claim Applied on
/// partial knowledge); only when every op is readable AND applied do we assert <see cref="TweakAppliedState.Applied"/>.
/// </summary>
public static class TweakDetection
{
    public static TweakAppliedState Aggregate(IReadOnlyList<bool?> probes)
    {
        bool anyUnreadable = false, anyApplied = false;
        foreach (var probe in probes)
        {
            if (probe == false) return TweakAppliedState.NotApplied; // a single confirmed-off op dominates
            if (probe is null) anyUnreadable = true; else anyApplied = true;
        }
        if (anyUnreadable) return TweakAppliedState.Indeterminate;    // something we couldn't read → won't guess
        return anyApplied ? TweakAppliedState.Applied : TweakAppliedState.Indeterminate; // empty → Indeterminate
    }

    /// <summary>
    /// The DUAL of <see cref="Aggregate"/>, for the question a revert asks: "is this tweak now OFF?". The state
    /// still means "is it applied?" — only the precedence flips. <see cref="Aggregate"/> answers "is it FULLY
    /// applied?" so a single confirmed-OFF op wins; here a single confirmed-ON op wins (→ <see cref="TweakAppliedState.Applied"/>,
    /// i.e. STILL ACTIVE — the alarming "revert reported done but it's still here"). Crucially this can't be done
    /// with <see cref="Aggregate"/>: its off-op-dominates rule would let one genuinely-reverted op MASK a sibling
    /// that reported success yet is still in its applied state. Only when EVERY op reads OFF do we confirm
    /// <see cref="TweakAppliedState.NotApplied"/> (fully reverted); any unreadable op (and none still active), or
    /// no probes at all, forces <see cref="TweakAppliedState.Indeterminate"/> — never a fabricated "fully reverted".
    /// </summary>
    public static TweakAppliedState AggregateAfterRevert(IReadOnlyList<bool?> probes)
    {
        bool anyUnreadable = false, anyConfirmedOff = false;
        foreach (var probe in probes)
        {
            if (probe == true) return TweakAppliedState.Applied;     // a single still-on op dominates → still active
            if (probe is null) anyUnreadable = true; else anyConfirmedOff = true;
        }
        if (anyUnreadable) return TweakAppliedState.Indeterminate;   // can't read an op back → won't claim fully off
        return anyConfirmedOff ? TweakAppliedState.NotApplied : TweakAppliedState.Indeterminate; // empty → Indeterminate
    }
}

/// <summary>
/// Pure fold from a post-apply re-probe to an honest <see cref="VerificationReport"/>. Sorts each probed
/// (tweakId, state) into exactly one bucket — <see cref="TweakAppliedState.Applied"/> → confirmed live,
/// <see cref="TweakAppliedState.NotApplied"/> → didn't stick (the alarming truth we must not hide behind a ✓),
/// <see cref="TweakAppliedState.Indeterminate"/> → unverifiable (shell-only, no readback) — preserving input
/// order. I/O-free so the verification semantics are pinned without a real system: this is what lets the Tweaks
/// page show a *genuine* "verified" signal instead of echoing the engine's own "I ran it" claim.
/// </summary>
public static class TweakVerifier
{
    public static VerificationReport Build(IEnumerable<(string TweakId, TweakAppliedState State)> probed)
    {
        var confirmed = new List<string>();
        var unconfirmed = new List<string>();
        var unverifiable = new List<string>();
        foreach (var (id, state) in probed)
        {
            switch (state)
            {
                case TweakAppliedState.Applied: confirmed.Add(id); break;
                case TweakAppliedState.NotApplied: unconfirmed.Add(id); break;
                default: unverifiable.Add(id); break;
            }
        }
        return new VerificationReport(confirmed, unconfirmed, unverifiable);
    }
}

/// <summary>
/// The revert twin of <see cref="TweakVerifier"/>: folds a post-revert re-probe (states from
/// <see cref="TweakDetection.AggregateAfterRevert"/>) into the SAME honest <see cref="VerificationReport"/>, but
/// with the mapping inverted because the desired end-state is now OFF. <see cref="TweakAppliedState.NotApplied"/>
/// (confirmed off) → <see cref="VerificationReport.Confirmed"/>; <see cref="TweakAppliedState.Applied"/> (STILL
/// ACTIVE despite the engine reporting the revert ran) → <see cref="VerificationReport.Unconfirmed"/>, the alarming
/// bucket we must surface rather than hide behind a clean "tout restauré"; <see cref="TweakAppliedState.Indeterminate"/>
/// (shell-only, no readback) → <see cref="VerificationReport.Unverifiable"/>. I/O-free so the revert-side honesty is
/// pinned without a real system — the mirror of the post-apply check, so a revert is verified, not taken on faith.
/// </summary>
public static class RevertVerifier
{
    public static VerificationReport Build(IEnumerable<(string TweakId, TweakAppliedState State)> probed)
    {
        var reverted = new List<string>();      // Confirmed: the machine reads this back as off now
        var stillActive = new List<string>();   // Unconfirmed: revert reported done, yet it's still applied
        var unverifiable = new List<string>();
        foreach (var (id, state) in probed)
        {
            switch (state)
            {
                case TweakAppliedState.NotApplied: reverted.Add(id); break;
                case TweakAppliedState.Applied: stillActive.Add(id); break;
                default: unverifiable.Add(id); break;
            }
        }
        return new VerificationReport(reverted, stillActive, unverifiable);
    }
}

public sealed class TweakService : ITweakService
{
    private readonly IRegistryService _registry;
    private readonly IServiceManagerService _services;
    private readonly IRestorePointService _restorePoints;
    private readonly IAppSettingsStore _settings;

    public TweakService(IRegistryService registry, IServiceManagerService services, IRestorePointService restorePoints, IAppSettingsStore settings)
    {
        _registry = registry;
        _services = services;
        _restorePoints = restorePoints;
        _settings = settings;
    }

    public async Task<TweakApplyResult> ApplyAsync(Tweak tweak)
    {
        try
        {
            int failed = 0;
            foreach (var op in tweak.Operations)
            {
                if (!await ExecuteAsync(op, applying: true))
                {
                    failed++;
                    Log.Warning("Tweak {Id}: apply op {Type} failed", tweak.Id, op.Type);
                }
            }
            // Honesty: a tweak is "applied" only when EVERY op landed. A half-applied tweak is NOT applied —
            // claiming so would paint the UI's IsApplied toggle a clean "✓" over a system that only changed
            // halfway. (Revert deliberately does the opposite: it only CLEARS the flag on full success.)
            tweak.IsApplied = failed == 0;
            return TweakApplyOutcome.From(failed, tweak.Operations.Count, tweak.RequiresReboot);
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Failed to apply tweak {Id}", tweak.Id);
            return new TweakApplyResult(false, ex.Message);
        }
    }

    public async Task<TweakApplyResult> RevertAsync(Tweak tweak)
    {
        try
        {
            int failed = 0;
            foreach (var op in tweak.Operations)
            {
                if (!await ExecuteAsync(op, applying: false))
                {
                    failed++;
                    Log.Warning("Tweak {Id}: revert op {Type} failed", tweak.Id, op.Type);
                }
            }
            // Symmetric honesty (mirror of Apply): only CLEAR IsApplied when the revert FULLY succeeded. A
            // half-reverted tweak still has changes on the box, so flipping it to "not applied" would be a
            // fabricated "restored to default". On partial failure we leave the flag set and report it.
            if (failed == 0) tweak.IsApplied = false;
            return TweakApplyOutcome.From(failed, tweak.Operations.Count, tweak.RequiresReboot);
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Failed to revert tweak {Id}", tweak.Id);
            return new TweakApplyResult(false, ex.Message);
        }
    }

    public Task<TweakAppliedState> IsAppliedAsync(Tweak tweak) => Task.FromResult(DetectState(tweak));

    public Task<IReadOnlyList<TweakAppliedState>> DetectStatesAsync(IReadOnlyList<Tweak> tweaks) => Task.Run(() =>
    {
        // Every registry/SCM read happens here, off the UI thread. We return the full tri-state per tweak so the
        // caller can tell "confirmed off" (NotApplied) from "couldn't read" (Indeterminate) — the distinction the
        // boolean overload throws away. Callers that only need a ✓ flag use DetectAppliedAsync below.
        var result = new TweakAppliedState[tweaks.Count];
        for (var i = 0; i < tweaks.Count; i++)
            result[i] = DetectState(tweaks[i]);
        return (IReadOnlyList<TweakAppliedState>)result;
    });

    public Task<IReadOnlyList<TweakAppliedState>> DetectAfterRevertAsync(IReadOnlyList<Tweak> tweaks) => Task.Run(() =>
    {
        // Same per-op reads as DetectStatesAsync, off the UI thread — but folded with the revert-safe rule so a
        // tweak left STILL ACTIVE by a revert that reported success can't be masked by a sibling op that did revert.
        var result = new TweakAppliedState[tweaks.Count];
        for (var i = 0; i < tweaks.Count; i++)
            result[i] = DetectResidualState(tweaks[i]);
        return (IReadOnlyList<TweakAppliedState>)result;
    });

    public async Task<IReadOnlyList<bool>> DetectAppliedAsync(IReadOnlyList<Tweak> tweaks)
    {
        // The boolean view of the probe: Indeterminate collapses to false alongside NotApplied — we never light a
        // ✓ we couldn't verify. Delegating keeps one detection path so the flag and the tri-state never disagree.
        var states = await DetectStatesAsync(tweaks);
        var flags = new bool[states.Count];
        for (var i = 0; i < states.Count; i++)
            flags[i] = states[i] == TweakAppliedState.Applied;
        return flags;
    }

    public async Task<VerificationReport?> VerifyAppliedAsync(IReadOnlyList<Tweak> attempted)
    {
        // Re-probe ONLY the tweaks the engine actually reported applied. A failed op leaves IsApplied false, and its
        // absence on the machine is consistent with that failure — flagging it as "didn't stick" would be a fabricated
        // alarm. Filtering first also avoids a wasted readback of ops we know didn't run.
        var applied = new List<Tweak>(attempted.Count);
        foreach (var t in attempted)
            if (t.IsApplied) applied.Add(t);
        if (applied.Count == 0) return null;   // nothing genuinely applied → no banner, no claim

        var states = await DetectStatesAsync(applied);
        var probed = new List<(string, TweakAppliedState)>(applied.Count);
        for (var i = 0; i < applied.Count; i++)
            probed.Add((applied[i].Id, states[i]));
        return TweakVerifier.Build(probed);
    }

    // One whole-tweak probe, synchronous and read-only. Probe EVERY op (not just the first), then fold honestly:
    // a tweak with op A applied but op B still at default is half-applied → NotApplied, not a green check.
    private TweakAppliedState DetectState(Tweak tweak)
    {
        var probes = new List<bool?>(tweak.Operations.Count);
        foreach (var op in tweak.Operations)
            probes.Add(ProbeApplied(op));
        return TweakDetection.Aggregate(probes);
    }

    // Same per-op probe as DetectState, folded for the revert question ("is any of this still on?") so a partial
    // revert can't read as fully restored. Shares ProbeApplied so the apply and revert views can't disagree per-op.
    private TweakAppliedState DetectResidualState(Tweak tweak)
    {
        var probes = new List<bool?>(tweak.Operations.Count);
        foreach (var op in tweak.Operations)
            probes.Add(ProbeApplied(op));
        return TweakDetection.AggregateAfterRevert(probes);
    }

    // One op's current truth: true = it's in its applied state, false = it differs, null = we can't read it back.
    // Kept separate so the per-op honesty is explicit and TweakDetection.Aggregate stays a pure fold.
    private bool? ProbeApplied(TweakOperation op)
    {
        switch (op.Type)
        {
            case OperationType.Registry:
                if (op.Hive is null || op.Key is null || op.Name is null) return null;
                var present = _registry.TryReadValue(op.Hive, op.Key, op.Name, out var current);
                // Apply == null means "apply = delete this value" → applied exactly when the value is now ABSENT.
                if (op.Apply is null) return !present;
                // Compare by value type: a DWord written as "0x1" reads back as "1" and must still match.
                return present && RegistryValue.Matches(current, op.Apply, op.ValueType);

            case OperationType.Service:
                if (op.ServiceName is null) return null;
                // A service that isn't installed has no startup type to compare → unreadable, not "not applied".
                if (!_services.TryGetStartupType(op.ServiceName, out var startup)) return null;
                return string.Equals(startup, op.StartupApply, System.StringComparison.OrdinalIgnoreCase);

            default:
                // PowerShell/Cmd/Bcdedit/AppX/ScheduledTask/File: no reliable readback → we won't pretend to know.
                return null;
        }
    }

    public async Task<BatchTweakResult> ApplyManyAsync(IEnumerable<Tweak> tweaks)
    {
        // Honesty: the Settings toggle must actually mean something. When the user turns OFF
        // "create a restore point before applying tweaks", we genuinely skip it — we don't quietly
        // create one anyway. (On by default; this is the safety net and we strongly recommend it.)
        if (_settings.Current.CreateRestorePointBeforeTweaks)
        {
            // The restore point is the user's undo. If it was REQUIRED but couldn't be created, applying anyway
            // would strand them with un-backed changes while the UI elsewhere claims a point was made — a
            // fabricated-safety lie. So we abort and touch NOTHING, returning the flag that lets the caller speak
            // the honest reason. (CreateAsync returns false only on genuine failure: Windows' 24h checkpoint
            // throttle still exits 0, i.e. reads as success — so a false here means System Restore is off/broken.)
            if (!await _restorePoints.CreateAsync("Aurum Tweaks — apply batch"))
                return new BatchTweakResult(0, 0, RestorePointFailed: true);
        }
        int ok = 0, failed = 0;
        foreach (var t in tweaks)
        {
            if ((await ApplyAsync(t)).Success) ok++; else failed++;
        }
        return new BatchTweakResult(ok, failed);
    }

    public async Task<BatchTweakResult> RevertAllAsync(IEnumerable<Tweak> tweaks)
    {
        int ok = 0, failed = 0;
        foreach (var t in tweaks)
        {
            if ((await RevertAsync(t)).Success) ok++; else failed++;
        }
        return new BatchTweakResult(ok, failed);
    }

    private Task<bool> ExecuteAsync(TweakOperation op, bool applying) => Task.Run(() =>
    {
        switch (op.Type)
        {
            case OperationType.Registry:
                if (op.Hive is null || op.Key is null || op.Name is null) return false;
                if (applying)
                {
                    if (op.Apply is null) return _registry.DeleteValue(op.Hive, op.Key, op.Name);
                    return _registry.WriteValue(op.Hive, op.Key, op.Name, op.Apply, op.ValueType);
                }
                else
                {
                    if (op.Revert is null) return _registry.DeleteValue(op.Hive, op.Key, op.Name);
                    return _registry.WriteValue(op.Hive, op.Key, op.Name, op.Revert, op.ValueType);
                }

            case OperationType.Service:
                if (op.ServiceName is null) return false;
                var target = applying ? op.StartupApply : op.StartupRevert;
                if (string.IsNullOrEmpty(target)) return false;
                return _services.SetStartupType(op.ServiceName, target);

            case OperationType.PowerShell:
            case OperationType.Cmd:
            case OperationType.Bcdedit:
            case OperationType.AppX:
            case OperationType.ScheduledTask:
                // Build the (fileName, args) purely (so the apply/revert inversion is testable), then run it.
                // A null command means a required field is missing → no-op false, as before.
                return TweakShellCommand.Build(op, applying) is { } cmd && RunShell(cmd.FileName, cmd.Arguments);
            default:
                return false;
        }
    });

    private static bool RunShell(string fileName, string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return false;
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
            if (p is null) return false;

            // Drain both pipes off-thread so a chatty child can't fill the ~4KB pipe buffer and block on
            // write while we block on WaitForExit (the classic redirect deadlock). Output is intentionally
            // discarded — the redirect exists only to keep child console output out of the WPF host.
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(60_000))
            {
                // Honesty: a hung op must report failure AND not leak a runaway elevated process. `using`
                // disposes the wrapper, not the OS process, so kill the whole tree before bailing. Reading
                // p.ExitCode here would instead throw ("process has not exited") and falsely report failure
                // while the elevated child kept running.
                try { p.Kill(entireProcessTree: true); }
                catch { /* already exiting, gone, or access denied — best-effort cleanup */ }
                Log.Warning("Shell op timed out after 60s and was killed: {File}", fileName);
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
