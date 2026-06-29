using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>The Windows Update behaviour a toggle governs — drives the FR section label shown on the page.</summary>
public enum WindowsUpdateCategory { Bandwidth, Reboot, Drivers }

/// <summary>
/// One curated, documented Windows Update policy this page can toggle. Each maps to a single, individually-named
/// registry value with stable public semantics — never an opaque blob. <see cref="OptimizedValue"/> is the
/// gaming-friendly value (stop uploading updates to strangers, stop forced mid-session reboots, stop Windows Update
/// overwriting a hand-picked driver); <see cref="DefaultValue"/> is the value that restores Windows' documented
/// default behaviour, so an absent key honestly reads as « défaut Windows », never as a fabricated « appliqué ».
/// All targets are machine-wide (HKLM) policy/registry DWords that require elevation — a managed/enterprise device
/// may refuse the write, which the page surfaces honestly instead of faking success. <see cref="Note"/> carries the
/// per-toggle caveat (e.g. "gpupdate/redémarrage requis") shown verbatim in the UI.
/// </summary>
public sealed record WindowsUpdateTweak(
    string Id,
    string Label,
    string Advice,
    WindowsUpdateCategory Category,
    string Hive,
    string Key,
    string ValueName,
    RegistryValueType Kind,
    string OptimizedValue,
    string DefaultValue,
    string? Note = null);

/// <summary>
/// The curated set of Windows Update toggles. Deliberately scoped to documented, reversible, machine-wide policy
/// values whose Windows default is unambiguous (so the absent-key = default honesty model holds) — pinned by the
/// load-bearing <c>AllTweaks_TargetKnownWindowsUpdateKeys</c> guard. This shares the exact honesty model of
/// <c>GameTweakCatalog</c> (absent ⇒ « défaut Windows », every action gated so no button is ever a no-op) applied to
/// a different domain: the Update behaviours most hostile to gaming. Restore writes the documented default <i>value</i>
/// (it does not delete the policy key) — to fully clear a policy, the user uses gpedit.msc (disclosed in the UI).
/// </summary>
public static class WindowsUpdateCatalog
{
    public const string DeliveryOptimization = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
    public const string WindowsUpdate = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    public const string WindowsUpdateAu = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    public const string DriverSearching = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";

    public static IReadOnlyList<WindowsUpdateTweak> Tweaks { get; } = new[]
    {
        new WindowsUpdateTweak("delivery-optimization-p2p", "Partage P2P des mises à jour",
            "Coupe l'envoi de tes mises à jour Windows vers d'autres PC sur Internet (Delivery Optimization). Tu récupères ta bande passante montante — utile en jeu en ligne et sur une connexion à upload limité.",
            WindowsUpdateCategory.Bandwidth, "HKLM", DeliveryOptimization, "DODownloadMode", RegistryValueType.DWord, "0", "1",
            "Modes : 0 = HTTP seul (aucun P2P) · 1 = réseau local (défaut moderne) · 3 = Internet (envoi vers des inconnus). « Optimisé » écrit 0. Réglage aussi visible dans Paramètres → Windows Update → Optimisation de la distribution."),

        new WindowsUpdateTweak("no-auto-reboot", "Redémarrage automatique forcé",
            "Empêche Windows de redémarrer tout seul pour finir une mise à jour tant que tu es connecté. Fini le reboot surprise en pleine partie ; la mise à jour s'installe à ton prochain redémarrage manuel.",
            WindowsUpdateCategory.Reboot, "HKLM", WindowsUpdateAu, "NoAutoRebootWithLoggedOnUsers", RegistryValueType.DWord, "1", "0",
            "« Optimisé » écrit 1 (pas de redémarrage automatique tant qu'un utilisateur est connecté). Ne bloque pas l'installation des mises à jour — seulement le redémarrage imposé."),

        new WindowsUpdateTweak("exclude-wu-drivers", "Pilotes via les mises à jour qualité",
            "Empêche Windows Update de remplacer tes pilotes (GPU, chipset, audio) pendant les mises à jour qualité. Tu gardes le pilote que TU as choisi, sans écrasement surprise.",
            WindowsUpdateCategory.Drivers, "HKLM", WindowsUpdate, "ExcludeWUDriversInQualityUpdate", RegistryValueType.DWord, "1", "0",
            "« Optimisé » écrit 1 (exclut les pilotes des mises à jour qualité). Ne touche pas aux mises à jour de sécurité Windows ; les pilotes restent installables manuellement."),

        new WindowsUpdateTweak("driver-search-config", "Recherche de pilotes sur Windows Update",
            "Réglage hérité « installation des périphériques » : empêche Windows de chercher automatiquement un pilote sur Windows Update quand tu branches un matériel. Complète le réglage ci-dessus.",
            WindowsUpdateCategory.Drivers, "HKLM", DriverSearching, "SearchOrderConfig", RegistryValueType.DWord, "0", "1",
            "« Optimisé » écrit 0 (ne jamais chercher de pilote sur Windows Update). Hors stratégie : c'est le réglage natif de l'installation de périphériques."),
    };

    public static string CategoryLabel(WindowsUpdateCategory category) => category switch
    {
        WindowsUpdateCategory.Bandwidth => "Bande passante",
        WindowsUpdateCategory.Reboot    => "Redémarrage",
        WindowsUpdateCategory.Drivers   => "Pilotes",
        _                               => "Windows Update",
    };

    public static WindowsUpdateTweak? Find(string? id) =>
        Tweaks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The live state of one toggle, derived from a registry read. The honesty rules live here: an <b>absent</b> key means
/// Windows is using its default behaviour, so absence reads as « défaut Windows », never as a fabricated « appliqué ».
/// Comparison goes through <see cref="RegistryValue.Matches"/> so a DWord read back as "0x1"/"1" matches numerically.
/// The <c>Can*</c> gates refuse a write that wouldn't change anything (re-applying, or "restoring" an absent key), so
/// no button is ever a no-op and no row is ever fully dead.
/// </summary>
public sealed record WindowsUpdateTweakState(WindowsUpdateTweak Tweak, string? LiveValue, bool IsPresent)
{
    public string Id => Tweak.Id;
    public string Label => Tweak.Label;
    public string Advice => Tweak.Advice;
    public string ValueName => Tweak.ValueName;
    public string CategoryLabel => WindowsUpdateCatalog.CategoryLabel(Tweak.Category);
    public string? Note => Tweak.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Tweak.Note);

    public bool IsOptimized => IsPresent && RegistryValue.Matches(LiveValue, Tweak.OptimizedValue, Tweak.Kind);
    public bool IsDefault => !IsPresent || RegistryValue.Matches(LiveValue, Tweak.DefaultValue, Tweak.Kind);
    public bool IsCustomValue => IsPresent && !IsOptimized && !IsDefault;

    // Apply is offered unless already applied; Restore only when a key is actually present AND not already at the
    // Windows default (writing the default onto an absent key would be a semantic no-op → button stays disabled).
    public bool CanOptimize => !IsOptimized;
    public bool CanRestore => IsPresent && !IsDefault;

    public bool ShowOptimizedBadge => IsOptimized;

    public string StateDisplay =>
        !IsPresent    ? "Non configuré · défaut Windows"
        : IsOptimized ? "Appliqué"
        : IsDefault   ? "Défaut Windows"
        : $"Valeur personnalisée : {LiveValue}";
}

/// <summary>One concrete write a bulk action wants to make: set <see cref="Tweak"/>'s value to <see cref="Value"/>.</summary>
public readonly record struct WindowsUpdateWrite(WindowsUpdateTweak Tweak, string Value);

public static class WindowsUpdatePlan
{
    /// <summary>« Tout optimiser » — every toggle to its gaming-friendly value.</summary>
    public static IReadOnlyList<WindowsUpdateWrite> OptimizeAll(IReadOnlyList<WindowsUpdateTweak> tweaks) =>
        tweaks.Select(t => new WindowsUpdateWrite(t, t.OptimizedValue)).ToList();

    /// <summary>« Tout rétablir » — every toggle back to its Windows default value.</summary>
    public static IReadOnlyList<WindowsUpdateWrite> RestoreAll(IReadOnlyList<WindowsUpdateTweak> tweaks) =>
        tweaks.Select(t => new WindowsUpdateWrite(t, t.DefaultValue)).ToList();
}

/// <summary>
/// The honest result of a bulk write. These are HKLM policy values: on a managed/enterprise device (or without the
/// rights) a write can be refused, so the page reports exactly how many of the writes the system accepted rather than
/// claiming a blanket success. <see cref="Summary"/> is the load-bearing honesty string surfaced in the UI.
/// </summary>
public sealed record WindowsUpdateApplyOutcome(int Total, int Accepted)
{
    public int Refused => Total - Accepted;
    public bool AllAccepted => Total > 0 && Accepted == Total;

    public string Summary =>
        Total == 0    ? "Aucun réglage à appliquer."
        : AllAccepted ? $"Terminé · {Accepted}/{Total} écriture(s) acceptée(s)."
        :               $"{Accepted}/{Total} écriture(s) acceptée(s) · {Refused} refusée(s) (stratégie gérée ou périphérique d'entreprise ?).";
}

public sealed record WindowsUpdateReport(IReadOnlyList<WindowsUpdateTweakState> Tweaks)
{
    public int Total => Tweaks.Count;
    public int OptimizedCount => Tweaks.Count(t => t.IsOptimized);
    public int DefaultCount => Tweaks.Count(t => t.IsDefault);
    public int CustomCount => Tweaks.Count(t => t.IsCustomValue);

    public bool AllOptimized => Tweaks.Count > 0 && Tweaks.All(t => t.IsOptimized);
    public bool NoneOptimized => Tweaks.All(t => !t.IsOptimized);
}

/// <summary>
/// « Windows Update » — a curated front-end over documented Windows Update policy values that govern the behaviours
/// most hostile to gaming (P2P upload of updates, forced mid-session reboots, drivers silently replaced). Every control
/// performs a real, reversible registry write that the page RE-READS, so a write Windows/a domain policy rejects comes
/// back as the unchanged truth, never a fabricated « fait ». Scope is stated honestly: these are machine-wide policy
/// writes, they don't delete a policy on restore (they set its default value), and a gpupdate/reboot may be needed to
/// take full effect. Browsing/installing updates is handed off to Windows' own pages rather than faked in-app.
/// </summary>
public sealed class WindowsUpdateService : IWindowsUpdateService
{
    private readonly IRegistryService _registry;

    public WindowsUpdateService(IRegistryService registry) => _registry = registry;

    public Task<WindowsUpdateReport> GetReportAsync() => Task.Run(GetReport);

    private WindowsUpdateReport GetReport()
    {
        var states = new List<WindowsUpdateTweakState>(WindowsUpdateCatalog.Tweaks.Count);
        foreach (var t in WindowsUpdateCatalog.Tweaks)
        {
            bool present = _registry.TryReadValue(t.Hive, t.Key, t.ValueName, out var live);
            states.Add(new WindowsUpdateTweakState(t, present ? live : null, present));
        }
        return new WindowsUpdateReport(states);
    }

    public Task<bool> SetOptimizedAsync(string tweakId, bool optimize) => Task.Run(() => SetOptimized(tweakId, optimize));

    private bool SetOptimized(string tweakId, bool optimize)
    {
        var t = WindowsUpdateCatalog.Find(tweakId);
        if (t is null) return false;
        var value = optimize ? t.OptimizedValue : t.DefaultValue;
        return _registry.WriteValue(t.Hive, t.Key, t.ValueName, value, t.Kind);
    }

    public Task<WindowsUpdateApplyOutcome> ApplyAllAsync(bool optimize) => Task.Run(() => ApplyAll(optimize));

    private WindowsUpdateApplyOutcome ApplyAll(bool optimize)
    {
        var plan = optimize
            ? WindowsUpdatePlan.OptimizeAll(WindowsUpdateCatalog.Tweaks)
            : WindowsUpdatePlan.RestoreAll(WindowsUpdateCatalog.Tweaks);

        int accepted = 0;
        foreach (var w in plan)
            if (_registry.WriteValue(w.Tweak.Hive, w.Tweak.Key, w.Tweak.ValueName, w.Value, w.Tweak.Kind))
                accepted++;
        return new WindowsUpdateApplyOutcome(plan.Count, accepted);
    }
}
