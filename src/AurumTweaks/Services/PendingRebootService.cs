using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AurumTweaks.Services;

/// <summary>
/// The four standard Windows "a restart is queued" signals as raw booleans. Pulling them out of the registry probe is
/// what lets the honesty-bearing verdict (<see cref="PendingRebootEvaluator"/>) be unit-tested without touching the
/// machine — the project's « test the decision, not the world » pattern.
/// </summary>
public sealed record PendingRebootSignals(
    bool ComponentBasedServicing,
    bool WindowsUpdate,
    bool PendingFileRename,
    bool ComputerRename);

/// <summary>
/// The honest verdict: whether a reboot is queued, one plain-French reason per detected signal, and a one-line summary.
/// A "not pending" result is deliberately worded « aucun redémarrage en attente détecté (signaux Windows standards) » —
/// it reflects the well-known signals we can read, never a guarantee that no application anywhere wants a restart.
/// </summary>
public sealed record PendingRebootStatus(bool IsPending, IReadOnlyList<string> Reasons, string Summary);

/// <summary>
/// Pure decision core for pending-reboot detection: the standard Windows signals → an honest verdict. No I/O, fully
/// unit-tested. Mirrors the project's pure-core extraction (the renderer/evaluator is tested; the registry probe is thin
/// glue). It never fabricates a pending state: only a signal that genuinely read as set yields a reason.
/// </summary>
public static class PendingRebootEvaluator
{
    public static PendingRebootStatus Evaluate(PendingRebootSignals signals)
    {
        var reasons = new List<string>();
        if (signals.ComponentBasedServicing)
            reasons.Add("Servicing de composants Windows (CBS) : une installation de composant attend un redémarrage.");
        if (signals.WindowsUpdate)
            reasons.Add("Windows Update a installé une mise à jour qui demande un redémarrage.");
        if (signals.PendingFileRename)
            reasons.Add("Des fichiers verrouillés seront remplacés au prochain démarrage (PendingFileRenameOperations).");
        if (signals.ComputerRename)
            reasons.Add("Le nom de l'ordinateur a changé et ne sera effectif qu'après un redémarrage.");

        if (reasons.Count == 0)
            return new PendingRebootStatus(false, reasons,
                "Aucun redémarrage en attente détecté (signaux Windows standards).");

        var noun = reasons.Count > 1 ? "signaux détectés" : "signal détecté";
        return new PendingRebootStatus(true, reasons, $"Redémarrage en attente — {reasons.Count} {noun}.");
    }
}

/// <summary>
/// Thin I/O glue: reads the four standard Windows pending-reboot signals straight from the registry (mirroring the other
/// specialised probes — <see cref="ServiceManagerService"/>, <see cref="RestorePointService"/> — which open keys
/// directly rather than going through <see cref="IRegistryService"/>) and hands them to the pure
/// <see cref="PendingRebootEvaluator"/>. Read-only. A key or value that can't be read is treated as « signal absent »
/// rather than throwing, so a locked-down machine reports « aucun » instead of crashing the report.
/// </summary>
public sealed class PendingRebootService : IPendingRebootService
{
    public Task<PendingRebootStatus> GetStatusAsync()
        => Task.Run(() => PendingRebootEvaluator.Evaluate(ProbeSignals()));

    private static PendingRebootSignals ProbeSignals() => new(
        ComponentBasedServicing: KeyExists(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"),
        WindowsUpdate: KeyExists(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"),
        PendingFileRename: HasNonEmptyMultiString(
            @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"),
        ComputerRename: ComputerRenamePending());

    private static bool KeyExists(string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            return key is not null;
        }
        catch { return false; }
    }

    private static bool HasNonEmptyMultiString(string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            // A REG_MULTI_SZ carrying at least one non-blank entry means a rename/replace is queued; Windows often
            // leaves the value present but empty, which is NOT a pending operation — so an empty array reads as absent.
            if (key?.GetValue(valueName) is string[] entries)
                return entries.Any(e => !string.IsNullOrWhiteSpace(e));
            return false;
        }
        catch { return false; }
    }

    private static bool ComputerRenamePending()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var active = baseKey.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName");
            using var pending = baseKey.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName");
            var activeName = active?.GetValue("ComputerName") as string;
            var pendingName = pending?.GetValue("ComputerName") as string;
            // Only a confident, both-readable mismatch counts; a missing key must never fabricate a rename.
            return !string.IsNullOrEmpty(activeName)
                && !string.IsNullOrEmpty(pendingName)
                && !string.Equals(activeName, pendingName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
