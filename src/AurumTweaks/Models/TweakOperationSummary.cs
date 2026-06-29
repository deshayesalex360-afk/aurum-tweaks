using System.Collections.Generic;
using System.Linq;

namespace AurumTweaks.Models;

/// <summary>
/// One operation rendered for human eyes: a French <paramref name="Kind"/> chip ("Registre", "Service", …), the
/// concrete <paramref name="Target"/> it touches (a registry path, a service name — empty for inline commands,
/// whose text lives in <paramref name="Apply"/>), and the verb phrases for applying and reverting it.
/// </summary>
public sealed record OperationSummary(string Kind, string Target, string Apply, string Revert);

/// <summary>
/// Turns a tweak's <see cref="Tweak.Operations"/> into the exact, French, technical disclosure the Tweaks card
/// shows under "Détails techniques". The honesty point: this is derived from the SAME operation data the engine
/// dispatches on (<c>TweakService.ExecuteAsync</c> / <c>TweakShellCommand.Build</c>), so the "what this changes"
/// the user reads can never drift from what apply/revert actually does — it isn't a hand-written paraphrase that
/// could lie. Pure (no I/O), so each rendering tier is pinned by a value table in the tests rather than eyeballed.
///
/// Non-reversibility is disclosed, not hidden: an operation the engine has no revert action for (a script with
/// no <c>revertScript</c>, a service with no <c>startupRevert</c>) reports <see cref="NoRevert"/> rather than a
/// blank — stating the limit is the honest move.
/// </summary>
public static class TweakOperationSummary
{
    /// <summary>Shown for an operation the engine cannot automatically undo (no revert action is declared).</summary>
    public const string NoRevert = "aucun rétablissement automatique";

    public static IReadOnlyList<OperationSummary> Summarize(Tweak tweak)
        => tweak.Operations.Select(Describe).ToList();

    /// <summary>
    /// The concrete things a tweak touches — registry paths, service names, AppX packages, task and file paths —
    /// as one space-joined string for the command palette's search keywords, so a power user who knows the
    /// registry value or service but not the tweak's French name can still find it (type "NetworkThrottlingIndex"
    /// or "DiagTrack" and the tweak that changes it surfaces). Reuses the same <see cref="Summarize"/> Targets the
    /// "Détails techniques" disclosure shows, so search recall can't drift from the real operations. Inline-command
    /// ops (PowerShell/Cmd/Bcdedit) have no structured target and contribute nothing here — their intent is already
    /// carried by the tweak's name and description keywords.
    /// </summary>
    public static string SearchTargets(Tweak tweak)
        => string.Join(' ', Summarize(tweak)
            .Select(s => s.Target)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct());

    public static OperationSummary Describe(TweakOperation op)
    {
        var raw = op.Type switch
        {
            OperationType.Registry => DescribeRegistry(op),
            OperationType.Service => DescribeService(op),
            OperationType.PowerShell => new OperationSummary("PowerShell", "", op.Script ?? "", op.RevertScript ?? ""),
            OperationType.Cmd => new OperationSummary("Invite de commandes", "", op.Script ?? "", op.RevertScript ?? ""),
            OperationType.Bcdedit => new OperationSummary("Bcdedit", "", op.Script ?? "", op.RevertScript ?? ""),
            OperationType.AppX => new OperationSummary("Application", op.AppxPackage ?? "", "supprime le paquet", "réenregistre le paquet"),
            OperationType.ScheduledTask => new OperationSummary("Tâche planifiée", op.TaskPath ?? "", "désactive la tâche", "réactive la tâche"),
            // File ops aren't dispatched by the engine today, so we don't claim an effect we don't perform.
            OperationType.File => new OperationSummary("Fichier", op.Path ?? "", "", ""),
            _ => new OperationSummary(op.Type.ToString(), "", "", "")
        };

        // An empty revert means no undo action exists for this op — say so plainly rather than render a blank.
        return raw.Revert.Length == 0 ? raw with { Revert = NoRevert } : raw;
    }

    private static OperationSummary DescribeRegistry(TweakOperation op)
    {
        // Join only the present parts so a malformed op (null hive/key/name) never yields stray "\\" separators.
        var target = string.Join('\\', new[] { op.Hive, op.Key, op.Name }.Where(p => !string.IsNullOrEmpty(p)));
        var apply = op.Apply is null ? "supprime la valeur" : $"écrit {op.Apply} ({RegTypeLabel(op.ValueType)})";
        var revert = op.Revert is null ? "supprime la valeur" : $"écrit {op.Revert}";
        return new OperationSummary("Registre", target, apply, revert);
    }

    private static OperationSummary DescribeService(TweakOperation op)
    {
        var apply = op.StartupApply is null ? "" : $"démarrage → {StartupLabel(op.StartupApply)}";
        var revert = op.StartupRevert is null ? "" : $"démarrage → {StartupLabel(op.StartupRevert)}";
        return new OperationSummary("Service", op.ServiceName ?? "", apply, revert);
    }

    // Mirror the SCM startup-type strings the JSON uses (and the engine writes) into French for display.
    private static string StartupLabel(string startup) => startup switch
    {
        "Disabled" => "Désactivé",
        "Manual" => "Manuel",
        "Automatic" => "Automatique",
        "DelayedAuto" => "Automatique (différé)",
        "Boot" => "Démarrage noyau",
        "System" => "Système",
        _ => startup
    };

    private static string RegTypeLabel(RegistryValueType type) => type switch
    {
        RegistryValueType.DWord => "DWORD",
        RegistryValueType.QWord => "QWORD",
        RegistryValueType.String => "chaîne",
        RegistryValueType.ExpandString => "chaîne extensible",
        RegistryValueType.Binary => "binaire",
        RegistryValueType.MultiString => "chaînes multiples",
        _ => type.ToString()
    };
}
