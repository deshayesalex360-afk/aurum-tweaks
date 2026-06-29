using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// The « Performance Options » radio Windows stores for the whole dialog. The numbers are stable across
/// Windows versions (the .cpl writes exactly these), and we read the raw DWord through <see cref="RegistryValue"/>
/// so "0x3"/"3" both resolve — never a guessed mode.
/// </summary>
public enum VisualFxMode { Unknown = -1, LetWindowsDecide = 0, BestAppearance = 1, BestPerformance = 2, Custom = 3 }

public static class VisualFxModeInfo
{
    /// <summary>Parse the live <c>VisualFXSetting</c> DWord; anything outside 0–3 (or unreadable) is honestly Unknown.</summary>
    public static VisualFxMode Parse(string? raw) =>
        RegistryValue.TryParseDword(raw, out var v) && v is >= 0 and <= 3 ? (VisualFxMode)v : VisualFxMode.Unknown;

    /// <summary>The DWord string we write back. Only ever called with a concrete 0–3 mode, never Unknown.</summary>
    public static string ToRegistryValue(VisualFxMode mode) => ((int)mode).ToString(CultureInfo.InvariantCulture);

    public static string Label(VisualFxMode mode) => mode switch
    {
        VisualFxMode.LetWindowsDecide => "Choix automatique de Windows",
        VisualFxMode.BestAppearance   => "Ajusté pour une meilleure apparence",
        VisualFxMode.BestPerformance  => "Ajusté pour de meilleures performances",
        VisualFxMode.Custom           => "Paramètres personnalisés",
        _                             => "Inconnu",
    };
}

/// <summary>
/// One visual effect this page can toggle. Each maps to a single, individually-named registry value with
/// documented, version-stable semantics (DragFullWindows, MinAnimate, TaskbarAnimations, …) — deliberately
/// NOT a bit inside the opaque <c>UserPreferencesMask</c> blob, whose per-bit meaning we'd be claiming without
/// being able to verify the exact byte on this machine. <see cref="AppearanceValue"/> is the pretty/on value
/// (also the Windows default when the key is absent); <see cref="PerformanceValue"/> the fast/off value.
/// <see cref="KeepOn"/> marks an effect we advise keeping enabled (ClearType) — the performance preset leaves it on.
/// </summary>
public sealed record VisualEffect(
    string Id,
    string Label,
    string Advice,
    string Hive,
    string Key,
    string ValueName,
    RegistryValueType Kind,
    string AppearanceValue,
    string PerformanceValue,
    bool KeepOn);

public static class VisualEffectsCatalog
{
    public const string Desktop = @"Control Panel\Desktop";
    public const string WindowMetrics = @"Control Panel\Desktop\WindowMetrics";
    public const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string Dwm = @"Software\Microsoft\Windows\DWM";

    // The dialog-wide radio (0 auto / 1 appearance / 2 performance / 3 custom). Managed by the service directly,
    // not part of the per-effect list — it records WHICH preset is selected, not a visual effect of its own.
    public const string VisualFxKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
    public const string VisualFxValue = "VisualFXSetting";

    public static IReadOnlyList<VisualEffect> Effects { get; } = new[]
    {
        new VisualEffect("drag-full-windows", "Afficher le contenu des fenêtres pendant le déplacement",
            "Effet purement visuel. Désactivé, seul le contour suit la souris — un peu moins de travail GPU sur un PC modeste.",
            "HKCU", Desktop, "DragFullWindows", RegistryValueType.String, "1", "0", false),

        new VisualEffect("min-animate", "Animer les fenêtres lors de la réduction et de l'agrandissement",
            "Supprime la courte animation d'ouverture/fermeture des fenêtres. Interface ressentie plus directe.",
            "HKCU", WindowMetrics, "MinAnimate", RegistryValueType.String, "1", "0", false),

        new VisualEffect("taskbar-animations", "Animations de la barre des tâches",
            "Supprime les fondus et glissements des boutons de la barre des tâches.",
            "HKCU", ExplorerAdvanced, "TaskbarAnimations", RegistryValueType.DWord, "1", "0", false),

        new VisualEffect("listview-alpha-select", "Rectangle de sélection translucide",
            "Le rectangle de sélection à la souris devient plein plutôt que translucide. Gain minime.",
            "HKCU", ExplorerAdvanced, "ListviewAlphaSelect", RegistryValueType.DWord, "1", "0", false),

        new VisualEffect("listview-shadow", "Ombres portées des étiquettes d'icônes du bureau",
            "Supprime l'ombre sous le texte des icônes du bureau.",
            "HKCU", ExplorerAdvanced, "ListviewShadow", RegistryValueType.DWord, "1", "0", false),

        new VisualEffect("aero-peek", "Aperçu du bureau (Aero Peek)",
            "Désactive l'aperçu transparent du bureau au survol du coin de la barre des tâches.",
            "HKCU", Dwm, "EnableAeroPeek", RegistryValueType.DWord, "1", "0", false),

        new VisualEffect("font-smoothing", "Lissage des polices (ClearType)",
            "À conserver : ClearType garde le texte net. Le désactiver dégrade fortement la lisibilité — le préréglage Performances le laisse activé.",
            "HKCU", Desktop, "FontSmoothing", RegistryValueType.String, "2", "0", true),
    };

    public static VisualEffect? Find(string? id) =>
        Effects.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The live state of one effect, derived from a registry read. The honesty rules live here: an <b>absent</b>
/// key means Windows is using its default — and for every catalog effect that default is the appearance value,
/// so absence reads as « activé », never as a fabricated « désactivé ». Comparison goes through
/// <see cref="RegistryValue.Matches"/> so a DWord read back as "1" matches an expected "1"/"0x1" numerically.
/// The <c>Can*</c> gates refuse to re-write the value the key already holds, so no button is ever a no-op.
/// </summary>
public sealed record EffectState(VisualEffect Effect, string? LiveValue, bool IsPresent)
{
    public string Id => Effect.Id;
    public string Label => Effect.Label;
    public string Advice => Effect.Advice;
    public string ValueName => Effect.ValueName;
    public bool KeepOn => Effect.KeepOn;

    public bool IsAppearance => !IsPresent || RegistryValue.Matches(LiveValue, Effect.AppearanceValue, Effect.Kind);
    public bool IsPerformance => IsPresent && RegistryValue.Matches(LiveValue, Effect.PerformanceValue, Effect.Kind);
    public bool IsCustomValue => IsPresent && !IsAppearance && !IsPerformance;

    public bool CanEnable => !IsAppearance;
    public bool CanDisable => !IsPerformance;

    public bool ShowPerformanceBadge => IsPerformance;
    public bool ShowKeepHint => Effect.KeepOn;

    public string StateDisplay =>
        !IsPresent      ? "Non défini · défaut Windows (activé)"
        : IsPerformance ? "Désactivé (performance)"
        : IsAppearance  ? "Activé (apparence)"
        : $"Valeur personnalisée : {LiveValue}";
}

/// <summary>One concrete write a preset wants to make: set <see cref="Effect"/>'s value to <see cref="Value"/>.</summary>
public readonly record struct EffectWrite(VisualEffect Effect, string Value);

public static class VisualEffectsPlan
{
    /// <summary>
    /// Performance preset: every effect goes to its performance value EXCEPT <see cref="VisualEffect.KeepOn"/>
    /// ones (ClearType), which stay at appearance — so the preset never contradicts the per-row « à conserver »
    /// advice by silently uglifying text.
    /// </summary>
    public static IReadOnlyList<EffectWrite> ForPerformance(IReadOnlyList<VisualEffect> effects) =>
        effects.Select(e => new EffectWrite(e, e.KeepOn ? e.AppearanceValue : e.PerformanceValue)).ToList();

    /// <summary>Appearance preset: every effect back to its pretty/on value.</summary>
    public static IReadOnlyList<EffectWrite> ForAppearance(IReadOnlyList<VisualEffect> effects) =>
        effects.Select(e => new EffectWrite(e, e.AppearanceValue)).ToList();
}

public sealed record VisualEffectsReport(VisualFxMode Mode, bool ModeKnown, IReadOnlyList<EffectState> Effects)
{
    public int Total => Effects.Count;
    public int PerformanceCount => Effects.Count(e => e.IsPerformance);
    public int AppearanceCount => Effects.Count(e => e.IsAppearance);
    public int CustomCount => Effects.Count(e => e.IsCustomValue);

    // "Fully optimised" ignores KeepOn effects: with ClearType deliberately left on, all-performance means every
    // non-KeepOn effect sits at its performance value.
    public bool AllPerformance => Effects.Where(e => !e.Effect.KeepOn).All(e => e.IsPerformance);
    public bool AllAppearance => Effects.All(e => e.IsAppearance);

    public string ModeDisplay => ModeKnown ? VisualFxModeInfo.Label(Mode) : "Inconnu";
}

/// <summary>
/// « Effets visuels » — the interactive equivalent of Windows' classic Performance Options dialog. Every control
/// performs a real, reversible HKCU registry write that the page RE-READS, so a write Windows rejects comes back
/// as the unchanged truth, never a fabricated « done ». Scope is stated honestly: these are per-user UI effects
/// (some need a session sign-out or Explorer restart to fully take visual effect); the gain is a snappier, lower
/// latency desktop on modest hardware — not in-game FPS.
/// </summary>
public sealed class VisualEffectsService : IVisualEffectsService
{
    private readonly IRegistryService _registry;

    public VisualEffectsService(IRegistryService registry) => _registry = registry;

    public Task<VisualEffectsReport> GetReportAsync() => Task.Run(GetReport);

    private VisualEffectsReport GetReport()
    {
        bool modeKnown = _registry.TryReadValue("HKCU", VisualEffectsCatalog.VisualFxKey, VisualEffectsCatalog.VisualFxValue, out var rawMode);
        var mode = modeKnown ? VisualFxModeInfo.Parse(rawMode) : VisualFxMode.Unknown;

        var states = new List<EffectState>(VisualEffectsCatalog.Effects.Count);
        foreach (var e in VisualEffectsCatalog.Effects)
        {
            bool present = _registry.TryReadValue(e.Hive, e.Key, e.ValueName, out var live);
            states.Add(new EffectState(e, present ? live : null, present));
        }
        return new VisualEffectsReport(mode, modeKnown, states);
    }

    public Task<bool> SetEffectAsync(string effectId, bool appearance) => Task.Run(() => SetEffect(effectId, appearance));

    private bool SetEffect(string effectId, bool appearance)
    {
        var e = VisualEffectsCatalog.Find(effectId);
        if (e is null) return false;

        var value = appearance ? e.AppearanceValue : e.PerformanceValue;
        bool ok = _registry.WriteValue(e.Hive, e.Key, e.ValueName, value, e.Kind);
        // A manual change moves the dialog's radio to « Personnalisé », exactly as the Windows dialog does.
        if (ok) WriteMode(VisualFxMode.Custom);
        return ok;
    }

    public Task<bool> ApplyPresetAsync(bool performance) => Task.Run(() => ApplyPreset(performance));

    private bool ApplyPreset(bool performance)
    {
        var plan = performance
            ? VisualEffectsPlan.ForPerformance(VisualEffectsCatalog.Effects)
            : VisualEffectsPlan.ForAppearance(VisualEffectsCatalog.Effects);

        bool allOk = true;
        foreach (var w in plan)
            allOk &= _registry.WriteValue(w.Effect.Hive, w.Effect.Key, w.Effect.ValueName, w.Value, w.Effect.Kind);

        WriteMode(performance ? VisualFxMode.BestPerformance : VisualFxMode.BestAppearance);
        return allOk;
    }

    private void WriteMode(VisualFxMode mode) =>
        _registry.WriteValue("HKCU", VisualEffectsCatalog.VisualFxKey, VisualEffectsCatalog.VisualFxValue,
            VisualFxModeInfo.ToRegistryValue(mode), RegistryValueType.DWord);
}
