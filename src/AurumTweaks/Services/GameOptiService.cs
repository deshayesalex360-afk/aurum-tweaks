using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>The area a game/responsiveness tweak belongs to — drives the FR section label shown on the page.</summary>
public enum GameTweakCategory { Network, Responsiveness, GameRecording }

/// <summary>
/// One curated, well-documented gaming/responsiveness system tweak this page can toggle. Each maps to a single,
/// individually-named registry value with stable public semantics — never an opaque blob. <see cref="OptimizedValue"/>
/// is the performance-tuned value; <see cref="DefaultValue"/> is the value Windows holds (or behaves as) when nothing
/// is configured, so an absent key honestly reads as « défaut Windows », never as a fabricated « optimisé ».
/// <see cref="Note"/> carries a per-tweak caveat (e.g. "needs a reboot", "policy-level") shown verbatim in the UI.
/// </summary>
public sealed record GameTweak(
    string Id,
    string Label,
    string Advice,
    GameTweakCategory Category,
    string Hive,
    string Key,
    string ValueName,
    RegistryValueType Kind,
    string OptimizedValue,
    string DefaultValue,
    string? Note = null);

/// <summary>
/// The curated set of gaming/responsiveness tweaks. Deliberately scoped to a handful of documented, reversible,
/// machine- or user-wide registry values whose Windows default is unambiguous (so the absent-key = default honesty
/// model holds) — pinned by the load-bearing <c>AllTweaks_TargetKnownGamingKeys</c> guard. Tweaks whose default
/// varies per machine or that need GPU-support detection (e.g. HAGS / HwSchMode) are deliberately NOT toggled here;
/// the page hands those off to Windows' own control instead of faking a state we can't reliably read. These values
/// also appear in the JSON tweak catalog — this page is the dedicated, state-aware front-end, the same relationship
/// « Effets visuels » has with its JSON entries.
/// </summary>
public static class GameTweakCatalog
{
    public const string SystemProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    public const string GameConfigStore = @"System\GameConfigStore";
    public const string GameDvrUser = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string GameDvrPolicy = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";

    public static IReadOnlyList<GameTweak> Tweaks { get; } = new[]
    {
        new GameTweak("network-throttling", "Limitation réseau du planificateur multimédia",
            "Désactive le throttling réseau (par défaut Windows limite à ~10 paquets/ms quand une appli multimédia tourne). Gain variable selon la carte réseau ; aucune promesse de FPS.",
            GameTweakCategory.Network, "HKLM", SystemProfile, "NetworkThrottlingIndex", RegistryValueType.DWord, "0xFFFFFFFF", "10",
            "« Optimisé » écrit 0xFFFFFFFF (désactivé). Effet surtout perceptible sur connexions rapides ; ce n'est pas un gain de bande passante magique."),

        new GameTweak("system-responsiveness", "Réservation CPU pour l'arrière-plan",
            "Réduit la part de CPU que Windows réserve aux tâches d'arrière-plan (20 % → 10 %), au profit du premier plan et du multimédia.",
            GameTweakCategory.Responsiveness, "HKLM", SystemProfile, "SystemResponsiveness", RegistryValueType.DWord, "10", "20",
            "Certains guides utilisent 0 ; « 10 » est un compromis plus sûr qui ne prive pas totalement les tâches d'arrière-plan."),

        new GameTweak("gamedvr-enabled", "Enregistrement de jeu en arrière-plan (Game DVR)",
            "Désactive l'enregistrement de jeu en arrière-plan, qui consomme du CPU/GPU pendant que vous jouez.",
            GameTweakCategory.GameRecording, "HKCU", GameConfigStore, "GameDVR_Enabled", RegistryValueType.DWord, "0", "1"),

        new GameTweak("app-capture", "Capture d'arrière-plan (Xbox Game Bar)",
            "Désactive la capture vidéo d'arrière-plan de la Xbox Game Bar — le complément par-utilisateur de Game DVR.",
            GameTweakCategory.GameRecording, "HKCU", GameDvrUser, "AppCaptureEnabled", RegistryValueType.DWord, "0", "1"),

        new GameTweak("allow-gamedvr", "Game DVR (politique système)",
            "Désactive Game DVR pour toute la machine via une politique système — complète les réglages par-utilisateur ci-dessus.",
            GameTweakCategory.GameRecording, "HKLM", GameDvrPolicy, "AllowGameDVR", RegistryValueType.DWord, "0", "1",
            "Politique système (HKLM) : s'applique à tous les comptes de cette machine."),
    };

    public static string CategoryLabel(GameTweakCategory category) => category switch
    {
        GameTweakCategory.Network        => "Réseau",
        GameTweakCategory.Responsiveness => "Réactivité CPU",
        GameTweakCategory.GameRecording  => "Enregistrement de jeu",
        _                                => "Optimisations jeu",
    };

    public static GameTweak? Find(string? id) =>
        Tweaks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The live state of one tweak, derived from a registry read. The honesty rules live here: an <b>absent</b> key means
/// Windows is using its default behaviour, so absence reads as « défaut Windows », never as a fabricated « optimisé ».
/// Comparison goes through <see cref="RegistryValue.Matches"/> so a DWord read back as "-1"/"0x0" matches the expected
/// "0xFFFFFFFF"/"0" numerically. The <c>Can*</c> gates refuse a write that wouldn't change anything (re-optimising, or
/// "restoring" an absent key), so no button is ever a no-op and no row is ever fully dead.
/// </summary>
public sealed record GameTweakState(GameTweak Tweak, string? LiveValue, bool IsPresent)
{
    public string Id => Tweak.Id;
    public string Label => Tweak.Label;
    public string Advice => Tweak.Advice;
    public string ValueName => Tweak.ValueName;
    public string CategoryLabel => GameTweakCatalog.CategoryLabel(Tweak.Category);
    public string? Note => Tweak.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Tweak.Note);

    public bool IsOptimized => IsPresent && RegistryValue.Matches(LiveValue, Tweak.OptimizedValue, Tweak.Kind);
    public bool IsDefault => !IsPresent || RegistryValue.Matches(LiveValue, Tweak.DefaultValue, Tweak.Kind);
    public bool IsCustomValue => IsPresent && !IsOptimized && !IsDefault;

    // Optimise is offered unless already optimised; Restore only when a key is actually present AND not already at the
    // Windows default (writing the default onto an absent key would be a semantic no-op → button stays disabled).
    public bool CanOptimize => !IsOptimized;
    public bool CanRestore => IsPresent && !IsDefault;

    public bool ShowOptimizedBadge => IsOptimized;

    public string StateDisplay =>
        !IsPresent    ? "Non configuré · défaut Windows"
        : IsOptimized ? "Optimisé"
        : IsDefault   ? "Défaut Windows"
        : $"Valeur personnalisée : {LiveValue}";
}

/// <summary>One concrete write a bulk action wants to make: set <see cref="Tweak"/>'s value to <see cref="Value"/>.</summary>
public readonly record struct GameTweakWrite(GameTweak Tweak, string Value);

public static class GameTweakPlan
{
    /// <summary>« Tout optimiser » — every tweak to its performance-tuned value.</summary>
    public static IReadOnlyList<GameTweakWrite> OptimizeAll(IReadOnlyList<GameTweak> tweaks) =>
        tweaks.Select(t => new GameTweakWrite(t, t.OptimizedValue)).ToList();

    /// <summary>« Tout rétablir » — every tweak back to its Windows default value.</summary>
    public static IReadOnlyList<GameTweakWrite> RestoreAll(IReadOnlyList<GameTweak> tweaks) =>
        tweaks.Select(t => new GameTweakWrite(t, t.DefaultValue)).ToList();
}

/// <summary>An on / off / unknown reading of a registry-backed display-GPU setting — the read-only honesty model for the
/// two settings this page surfaces as information but deliberately never writes.</summary>
public enum GpuToggleState { Unknown, Enabled, Disabled }

/// <summary>
/// Pure, READ-ONLY interpretation of two display/GPU settings the FR gaming scene chases but that « Optimisations jeu »
/// deliberately does NOT toggle (their safe default varies per GPU/pilote and a write needs a reboot): Hardware-Accelerated
/// GPU Scheduling (HAGS — <c>HwSchMode</c> under GraphicsDrivers) and Multi-Plane Overlay (MPO — <c>OverlayTestMode</c>
/// under Dwm). Both are read straight from the registry and interpreted by their well-known DWord semantics; anything we
/// can't grade confidently is « non configuré / défaut » or « valeur inhabituelle », never a fabricated on/off. These keys
/// are intentionally NOT part of the writable <see cref="GameTweakCatalog"/> (the page still only ever writes the four
/// documented gaming keys) — it merely reads these two extra values to show the user where they stand, and hands the actual
/// change off to Windows. Comparison goes through <see cref="RegistryValue.Matches"/> so "0x2"/"2" read identically.
/// </summary>
public sealed record DisplayGpuState(bool HagsPresent, string? HagsRaw, bool MpoPresent, string? MpoRaw)
{
    public const string HagsKey = @"System\CurrentControlSet\Control\GraphicsDrivers";
    public const string HagsValueName = "HwSchMode";
    public const string MpoKey = @"SOFTWARE\Microsoft\Windows\Dwm";
    public const string MpoValueName = "OverlayTestMode";

    // HwSchMode: 2 = enabled, 1 = disabled. Absent (or any other value) = the GPU/driver default, which we can't grade.
    public GpuToggleState Hags =>
        !HagsPresent ? GpuToggleState.Unknown
        : RegistryValue.Matches(HagsRaw, "2", RegistryValueType.DWord) ? GpuToggleState.Enabled
        : RegistryValue.Matches(HagsRaw, "1", RegistryValueType.DWord) ? GpuToggleState.Disabled
        : GpuToggleState.Unknown;

    public string HagsDisplay => Hags switch
    {
        GpuToggleState.Enabled  => "Activé",
        GpuToggleState.Disabled => "Désactivé",
        _ => HagsPresent ? $"Valeur inhabituelle ({HagsRaw})" : "Non configuré — défaut du pilote graphique"
    };

    // OverlayTestMode == 5 is the documented « disable MPO » workaround; absent or anything else = Windows default (on).
    public bool MpoDisabled => MpoPresent && RegistryValue.Matches(MpoRaw, "5", RegistryValueType.DWord);
    public string MpoDisplay => MpoDisabled ? "Désactivé (OverlayTestMode = 5)" : "Activé — défaut Windows";
}

public sealed record GameTweakReport(IReadOnlyList<GameTweakState> Tweaks, DisplayGpuState? DisplayGpu = null)
{
    public int Total => Tweaks.Count;
    public int OptimizedCount => Tweaks.Count(t => t.IsOptimized);
    public int DefaultCount => Tweaks.Count(t => t.IsDefault);
    public int CustomCount => Tweaks.Count(t => t.IsCustomValue);

    public bool AllOptimized => Tweaks.Count > 0 && Tweaks.All(t => t.IsOptimized);
    public bool NoneOptimized => Tweaks.All(t => !t.IsOptimized);
}

/// <summary>
/// « Optimisations jeu » — a curated front-end over documented gaming/responsiveness registry tweaks. Every control
/// performs a real, reversible registry write that the page RE-READS, so a write Windows rejects comes back as the
/// unchanged truth, never a fabricated « fait ». Scope is stated honestly: these are documented tweaks whose gain is
/// variable and configuration-dependent — no FPS figure is promised, and a reboot/relog may be needed for some to take
/// full effect. GPU scheduling (HAGS) is handed off to Windows' own settings rather than toggled with a faked state.
/// </summary>
public sealed class GameOptiService : IGameOptiService
{
    private readonly IRegistryService _registry;

    public GameOptiService(IRegistryService registry) => _registry = registry;

    public Task<GameTweakReport> GetReportAsync() => Task.Run(GetReport);

    private GameTweakReport GetReport()
    {
        var states = new List<GameTweakState>(GameTweakCatalog.Tweaks.Count);
        foreach (var t in GameTweakCatalog.Tweaks)
        {
            bool present = _registry.TryReadValue(t.Hive, t.Key, t.ValueName, out var live);
            states.Add(new GameTweakState(t, present ? live : null, present));
        }

        // READ-ONLY extras: the two settings the page shows but never writes (HAGS / MPO). Reading is honest; we report
        // « non configuré / défaut » when absent rather than fabricate an on/off, and the change is handed off to Windows.
        bool hagsPresent = _registry.TryReadValue("HKLM", DisplayGpuState.HagsKey, DisplayGpuState.HagsValueName, out var hagsRaw);
        bool mpoPresent = _registry.TryReadValue("HKLM", DisplayGpuState.MpoKey, DisplayGpuState.MpoValueName, out var mpoRaw);
        var gpu = new DisplayGpuState(hagsPresent, hagsPresent ? hagsRaw : null, mpoPresent, mpoPresent ? mpoRaw : null);

        return new GameTweakReport(states, gpu);
    }

    public Task<bool> SetOptimizedAsync(string tweakId, bool optimize) => Task.Run(() => SetOptimized(tweakId, optimize));

    private bool SetOptimized(string tweakId, bool optimize)
    {
        var t = GameTweakCatalog.Find(tweakId);
        if (t is null) return false;
        var value = optimize ? t.OptimizedValue : t.DefaultValue;
        return _registry.WriteValue(t.Hive, t.Key, t.ValueName, value, t.Kind);
    }

    public Task<bool> ApplyAllAsync(bool optimize) => Task.Run(() => ApplyAll(optimize));

    private bool ApplyAll(bool optimize)
    {
        var plan = optimize
            ? GameTweakPlan.OptimizeAll(GameTweakCatalog.Tweaks)
            : GameTweakPlan.RestoreAll(GameTweakCatalog.Tweaks);

        bool allOk = true;
        foreach (var w in plan)
            allOk &= _registry.WriteValue(w.Tweak.Hive, w.Tweak.Key, w.Tweak.ValueName, w.Value, w.Tweak.Kind);
        return allOk;
    }
}
