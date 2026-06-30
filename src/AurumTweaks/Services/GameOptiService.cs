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

public enum GameFeatureSupport { Supported, NotSupported, NotVerified }

/// <summary>
/// One READ-ONLY eligibility row for Windows/GPU gaming features. "Supported" is only used when the local facts are
/// enough to say the platform side is present (OS/vendor/NVMe or an explicit registry state); "NotVerified" is the
/// honest middle ground for driver-, display- or per-game-owned features Aurum cannot read back. No row has an action:
/// this matrix diagnoses eligibility and points at the owner, it never pretends to enable Reflex/AFMF/APO/etc.
/// </summary>
public sealed record GameFeatureEligibility(
    string Id,
    string Name,
    GameFeatureSupport Support,
    string Evidence,
    string Limit)
{
    public string SupportDisplay => Support switch
    {
        GameFeatureSupport.Supported    => "Supporté",
        GameFeatureSupport.NotSupported => "Non supporté",
        _                               => "Non vérifié"
    };

    public string SummaryDisplay => $"{SupportDisplay} · {Evidence}";
    public bool HasLimit => !string.IsNullOrWhiteSpace(Limit);
}

public static class GameFeatureRegistry
{
    public const string GameBarKey = @"Software\Microsoft\GameBar";
    public const string AutoGameModeValueName = "AutoGameModeEnabled";
    public const string AllowGameModeValueName = "AllowAutoGameMode";
    public const string DirectXUserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string DirectXUserGlobalSettingsValueName = "DirectXUserGlobalSettings";
}

public sealed record GameFeatureEligibilityFacts(
    string OsCaption,
    string OsBuild,
    bool IsWindows11,
    string CpuName,
    CpuFamily CpuFamily,
    bool IsIntelCpu,
    GpuVendor GpuVendor,
    string GpuName,
    bool StorageReadOk,
    bool HasNvmeStorage,
    DisplayGpuState DisplayGpu,
    bool GameModeAutoPresent,
    string? GameModeAutoRaw,
    bool GameModeAllowPresent,
    string? GameModeAllowRaw,
    bool WindowedOptimizationsPresent,
    string? WindowedOptimizationsRaw)
{
    public int OsBuildNumber => int.TryParse(OsBuild, out var build) ? build : 0;
    public bool OsKnown => IsWindows11 || OsBuildNumber > 0 || !string.IsNullOrWhiteSpace(OsCaption);
    public bool IsWindows10OrNewer =>
        IsWindows11 || OsBuildNumber >= 10240 || Contains(OsCaption, "Windows 10") || Contains(OsCaption, "Windows 11");
    public bool IsWindows10_2004OrNewer => IsWindows11 || OsBuildNumber >= 19041;
    public bool IsWindows11OrNewer => IsWindows11 || Contains(OsCaption, "Windows 11");

    private static bool Contains(string text, string token) =>
        !string.IsNullOrWhiteSpace(text) && text.Contains(token, StringComparison.OrdinalIgnoreCase);
}

public static class GameFeatureEligibilityPlan
{
    public static IReadOnlyList<GameFeatureEligibility> Build(GameFeatureEligibilityFacts facts)
    {
        var rows = new List<GameFeatureEligibility>
        {
            Hags(facts),
            GameMode(facts),
            AutoHdr(facts),
            Vrr(facts),
            DirectStorage(facts),
            IntelApo(facts),
            Reflex(facts),
            SmoothMotion(facts),
            Afmf(facts),
        };
        return rows;
    }

    private static GameFeatureEligibility Hags(GameFeatureEligibilityFacts f)
    {
        const string limit = "Aurum lit le réglage Windows ; la prise en charge exacte et le gain restent côté pilote/jeu.";
        if (f.OsKnown && !f.IsWindows10_2004OrNewer)
            return Row("hags", "HAGS (ordonnancement GPU matériel)", GameFeatureSupport.NotSupported,
                "Windows 10 2004+ ou Windows 11 requis.", limit);

        return f.DisplayGpu.Hags switch
        {
            GpuToggleState.Enabled => Row("hags", "HAGS (ordonnancement GPU matériel)", GameFeatureSupport.Supported,
                "Réglage Windows lu : activé (HwSchMode=2).", limit),
            GpuToggleState.Disabled => Row("hags", "HAGS (ordonnancement GPU matériel)", GameFeatureSupport.Supported,
                "Réglage Windows lu : désactivé (HwSchMode=1).", limit),
            _ => Row("hags", "HAGS (ordonnancement GPU matériel)", GameFeatureSupport.NotVerified,
                f.OsKnown ? "Aucun réglage forcé lu ; le pilote peut utiliser son défaut." : "Version Windows non lue.",
                limit)
        };
    }

    private static GameFeatureEligibility GameMode(GameFeatureEligibilityFacts f)
    {
        const string limit = "La matrice est informative ; le bouton Windows reste la source visuelle du réglage natif.";
        if (f.OsKnown && !f.IsWindows10OrNewer)
            return Row("game-mode", "Game Mode Windows", GameFeatureSupport.NotSupported,
                "Windows 10/11 requis.", limit);

        bool autoOn = Dword(f.GameModeAutoPresent, f.GameModeAutoRaw, "1");
        bool allowOn = Dword(f.GameModeAllowPresent, f.GameModeAllowRaw, "1");
        bool autoOff = Dword(f.GameModeAutoPresent, f.GameModeAutoRaw, "0");
        bool allowOff = Dword(f.GameModeAllowPresent, f.GameModeAllowRaw, "0");
        string evidence =
            autoOn && allowOn ? "Registre lu : Game Mode activé (AutoGameModeEnabled=1, AllowAutoGameMode=1)."
            : autoOff && allowOff ? "Registre lu : Game Mode désactivé par l'utilisateur."
            : f.GameModeAutoPresent || f.GameModeAllowPresent ? "Registre lu : état Game Mode partiel ou personnalisé."
            : "Aucune valeur utilisateur lue ; Windows peut appliquer son défaut.";

        return Row("game-mode", "Game Mode Windows",
            f.OsKnown ? GameFeatureSupport.Supported : GameFeatureSupport.NotVerified,
            evidence, limit);
    }

    private static GameFeatureEligibility AutoHdr(GameFeatureEligibilityFacts f)
    {
        const string limit = "Aurum ne lit pas ici la capacité HDR de l'écran ni la compatibilité par jeu.";
        if (f.OsKnown && !f.IsWindows11OrNewer)
            return Row("auto-hdr", "Auto HDR", GameFeatureSupport.NotSupported,
                "Windows 11 est requis pour l'éligibilité Auto HDR affichée ici.", limit);

        return Row("auto-hdr", "Auto HDR", GameFeatureSupport.NotVerified,
            WindowedOptimizationEvidence(f, "HDR automatique"),
            limit);
    }

    private static GameFeatureEligibility Vrr(GameFeatureEligibilityFacts f)
    {
        const string limit = "Un écran VRR/Adaptive-Sync et un pilote compatible sont nécessaires ; Aurum ne lit pas ce handshake.";
        if (f.OsKnown && !f.IsWindows11OrNewer)
            return Row("vrr", "VRR / taux de rafraîchissement variable", GameFeatureSupport.NotSupported,
                "L'éligibilité fenêtrée moderne est ciblée Windows 11.", limit);

        return Row("vrr", "VRR / taux de rafraîchissement variable", GameFeatureSupport.NotVerified,
            WindowedOptimizationEvidence(f, "VRR"),
            limit);
    }

    private static GameFeatureEligibility DirectStorage(GameFeatureEligibilityFacts f)
    {
        const string limit = "Seuls les jeux qui intègrent DirectStorage en profitent ; Aurum ne teste pas l'API ni la décompression GPU.";
        if (f.OsKnown && !f.IsWindows10_2004OrNewer)
            return Row("directstorage", "DirectStorage", GameFeatureSupport.NotSupported,
                "Windows 10 2004+ ou Windows 11 requis dans cette vérification.", limit);
        if (!f.StorageReadOk)
            return Row("directstorage", "DirectStorage", GameFeatureSupport.NotVerified,
                "La liste des disques n'a pas été lue.", limit);
        if (!f.HasNvmeStorage)
            return Row("directstorage", "DirectStorage", GameFeatureSupport.NotSupported,
                "Aucun SSD NVMe détecté.", limit);

        return Row("directstorage", "DirectStorage", GameFeatureSupport.Supported,
            "Socle local détecté : Windows compatible + SSD NVMe.", limit);
    }

    private static GameFeatureEligibility IntelApo(GameFeatureEligibilityFacts f)
    {
        const string limit = "APO dépend du CPU exact, du BIOS/DTT, de l'application Intel APO et de la liste de jeux Intel ; Aurum ne l'active pas.";
        if (!f.IsIntelCpu)
            return Row("intel-apo", "Intel APO", GameFeatureSupport.NotSupported,
                "CPU Intel non détecté.", limit);

        var evidence = f.CpuFamily is CpuFamily.IntelCore12 or CpuFamily.IntelCore13 or CpuFamily.IntelCore14 or CpuFamily.IntelCoreUltra
            ? $"Plateforme Intel détectée ({CpuLabel(f)}), compatibilité exacte à vérifier dans Intel APO."
            : $"CPU Intel détecté ({CpuLabel(f)}), génération APO non prouvée.";
        return Row("intel-apo", "Intel APO", GameFeatureSupport.NotVerified, evidence, limit);
    }

    private static GameFeatureEligibility Reflex(GameFeatureEligibilityFacts f)
    {
        const string limit = "Reflex s'active dans les jeux compatibles ; Aurum ne peut pas forcer une option par jeu.";
        if (f.GpuVendor != GpuVendor.Nvidia)
            return Row("reflex", "NVIDIA Reflex", GameFeatureSupport.NotSupported,
                "GPU NVIDIA non détecté.", limit);

        return Row("reflex", "NVIDIA Reflex", GameFeatureSupport.Supported,
            $"GPU NVIDIA détecté ({GpuLabel(f)}).", limit);
    }

    private static GameFeatureEligibility SmoothMotion(GameFeatureEligibilityFacts f)
    {
        const string limit = "Fonction NVIDIA App/pilote : Aurum ne lit ni le bascule NVIDIA App ni les profils de jeu.";
        if (f.GpuVendor != GpuVendor.Nvidia)
            return Row("smooth-motion", "NVIDIA Smooth Motion", GameFeatureSupport.NotSupported,
                "GPU NVIDIA non détecté.", limit);

        return Row("smooth-motion", "NVIDIA Smooth Motion", GameFeatureSupport.NotVerified,
            $"GPU NVIDIA détecté ({GpuLabel(f)}), exigence RTX/pilote à vérifier dans NVIDIA App.", limit);
    }

    private static GameFeatureEligibility Afmf(GameFeatureEligibilityFacts f)
    {
        const string limit = "AFMF s'active dans AMD Software/Adrenalin ; Aurum ne lit pas ce bascule pilote.";
        if (f.GpuVendor != GpuVendor.Amd)
            return Row("afmf", "AMD Fluid Motion Frames (AFMF)", GameFeatureSupport.NotSupported,
                "GPU AMD Radeon non détecté.", limit);
        if (!LooksLikeModernRadeonForAfmf(f.GpuName))
            return Row("afmf", "AMD Fluid Motion Frames (AFMF)", GameFeatureSupport.NotVerified,
                $"GPU AMD détecté ({GpuLabel(f)}), génération AFMF non prouvée.", limit);

        return Row("afmf", "AMD Fluid Motion Frames (AFMF)", GameFeatureSupport.Supported,
            $"GPU AMD Radeon récent détecté ({GpuLabel(f)}).", limit);
    }

    private static string WindowedOptimizationEvidence(GameFeatureEligibilityFacts f, string featureName)
    {
        if (f.WindowedOptimizationsPresent && ContainsToken(f.WindowedOptimizationsRaw, "SwapEffectUpgradeEnable=1"))
            return $"Optimisations pour jeux fenêtrés lues comme actives ; {featureName} dépend encore de l'écran/du jeu.";
        if (f.WindowedOptimizationsPresent && ContainsToken(f.WindowedOptimizationsRaw, "SwapEffectUpgradeEnable=0"))
            return $"Optimisations pour jeux fenêtrés lues comme désactivées ; {featureName} reste dépendant de l'écran/du jeu.";
        if (f.WindowedOptimizationsPresent)
            return $"Réglage DirectX global lu mais non reconnu ({f.WindowedOptimizationsRaw}).";
        return $"Aucun réglage DirectX global lu ; {featureName} reste à vérifier dans Windows/le jeu.";
    }

    private static GameFeatureEligibility Row(string id, string name, GameFeatureSupport support, string evidence, string limit) =>
        new(id, name, support, evidence, limit);

    private static bool Dword(bool present, string? raw, string expected) =>
        present && RegistryValue.Matches(raw, expected, RegistryValueType.DWord);

    private static bool ContainsToken(string? raw, string token) =>
        !string.IsNullOrWhiteSpace(raw) && raw.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string CpuLabel(GameFeatureEligibilityFacts f) =>
        string.IsNullOrWhiteSpace(f.CpuName) || f.CpuName == "Unknown" ? f.CpuFamily.ToString() : f.CpuName.Trim();

    private static string GpuLabel(GameFeatureEligibilityFacts f) =>
        string.IsNullOrWhiteSpace(f.GpuName) || f.GpuName == "Unknown" ? f.GpuVendor.ToString() : f.GpuName.Trim();

    private static bool LooksLikeModernRadeonForAfmf(string gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return false;
        var n = gpuName.ToUpperInvariant().Replace("-", " ");
        return n.Contains("RX 6") || n.Contains("RX6")
            || n.Contains("RX 7") || n.Contains("RX7")
            || n.Contains("RX 8") || n.Contains("RX8")
            || n.Contains("RX 9") || n.Contains("RX9")
            || n.Contains("780M") || n.Contains("760M");
    }
}

public sealed record GameTweakReport(
    IReadOnlyList<GameTweakState> Tweaks,
    DisplayGpuState? DisplayGpu = null,
    IReadOnlyList<GameFeatureEligibility>? FeatureEligibility = null)
{
    public int Total => Tweaks.Count;
    public int OptimizedCount => Tweaks.Count(t => t.IsOptimized);
    public int DefaultCount => Tweaks.Count(t => t.IsDefault);
    public int CustomCount => Tweaks.Count(t => t.IsCustomValue);
    public IReadOnlyList<GameFeatureEligibility> FeatureEligibilityRows => FeatureEligibility ?? Array.Empty<GameFeatureEligibility>();

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
    private readonly IHardwareService _hardware;

    public GameOptiService(IRegistryService registry, IHardwareService hardware)
    {
        _registry = registry;
        _hardware = hardware;
    }

    public async Task<GameTweakReport> GetReportAsync()
    {
        var hardware = await _hardware.DetectAsync();
        return await Task.Run(() => GetReport(hardware));
    }

    private GameTweakReport GetReport(HardwareInfo hardware)
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

        bool gameModeAutoPresent = _registry.TryReadValue("HKCU", GameFeatureRegistry.GameBarKey, GameFeatureRegistry.AutoGameModeValueName, out var gameModeAutoRaw);
        bool gameModeAllowPresent = _registry.TryReadValue("HKCU", GameFeatureRegistry.GameBarKey, GameFeatureRegistry.AllowGameModeValueName, out var gameModeAllowRaw);
        bool windowedPresent = _registry.TryReadValue("HKCU", GameFeatureRegistry.DirectXUserGpuPreferencesKey, GameFeatureRegistry.DirectXUserGlobalSettingsValueName, out var windowedRaw);

        bool storageReadOk = hardware.StorageDevices.Count > 0;
        bool hasNvme = hardware.StorageDevices.Any(d => d.BusType.Contains("NVMe", StringComparison.OrdinalIgnoreCase));
        var facts = new GameFeatureEligibilityFacts(
            hardware.OsCaption,
            hardware.OsBuild,
            hardware.IsWindows11,
            hardware.CpuName,
            hardware.DetectedFamily,
            hardware.IsIntel,
            hardware.GpuVendor,
            hardware.GpuPrimary,
            storageReadOk,
            hasNvme,
            gpu,
            gameModeAutoPresent,
            gameModeAutoPresent ? gameModeAutoRaw : null,
            gameModeAllowPresent,
            gameModeAllowPresent ? gameModeAllowRaw : null,
            windowedPresent,
            windowedPresent ? windowedRaw : null);

        return new GameTweakReport(states, gpu, GameFeatureEligibilityPlan.Build(facts));
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
