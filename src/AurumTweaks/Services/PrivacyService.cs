using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>The broad area a privacy setting belongs to — drives the FR section label shown on the page.</summary>
public enum PrivacyCategory { Telemetry, Advertising, ActivityHistory, Suggestions, Search, Input }

/// <summary>
/// One curated privacy/telemetry consent setting this page can toggle. Each maps to a single, individually-named,
/// well-documented registry value — never an opaque blob. <see cref="HardenedValue"/> is the privacy-protective
/// value; <see cref="DefaultValue"/> is the Windows default the key holds (or would hold) when nothing is configured,
/// so an absent key honestly reads as « collecte active », never as a fabricated « protégé ». <see cref="Note"/>
/// carries a per-setting caveat (e.g. the telemetry floor on consumer SKUs) shown verbatim in the UI.
/// </summary>
public sealed record PrivacySetting(
    string Id,
    string Label,
    string Advice,
    PrivacyCategory Category,
    string Hive,
    string Key,
    string ValueName,
    RegistryValueType Kind,
    string HardenedValue,
    string DefaultValue,
    string? Note = null);

/// <summary>
/// The curated set of consent/telemetry settings. Deliberately scoped to HKCU and HKLM\SOFTWARE\Policies values
/// with stable, public semantics — pinned by the load-bearing <c>AllSettings_TargetHkcuOrHklmPolicies</c> guard.
/// This page covers the user-facing CONSENT switches; the transport behind some of them (the DiagTrack service, the
/// telemetry scheduled tasks) is handled by the « Services Windows » and « Tâches planifiées » pages — they
/// complement, they don't duplicate.
/// </summary>
public static class PrivacyCatalog
{
    public const string DataCollectionPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    public const string SystemPolicy = @"SOFTWARE\Policies\Microsoft\Windows\System";
    public const string AdvertisingInfo = @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo";
    public const string PrivacyKey = @"Software\Microsoft\Windows\CurrentVersion\Privacy";
    public const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    public const string OnlineSpeech = @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy";
    public const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    public const string Personalization = @"Software\Microsoft\Personalization\Settings";

    public static IReadOnlyList<PrivacySetting> Settings { get; } = new[]
    {
        new PrivacySetting("allow-telemetry", "Données de diagnostic (télémétrie)",
            "Réduit au minimum les données de diagnostic envoyées à Microsoft. Politique système (HKLM).",
            PrivacyCategory.Telemetry, "HKLM", DataCollectionPolicy, "AllowTelemetry", RegistryValueType.DWord, "0", "3",
            "Sur Windows Famille/Pro, le niveau « Sécurité » (0) est plafonné à « Requis » (1) par Windows ; il n'est pleinement honoré que sur Entreprise/Éducation. Cela réduit la collecte, ne l'élimine pas."),

        new PrivacySetting("advertising-id", "Identifiant de publicité",
            "Empêche les applications d'utiliser un identifiant unique pour vous cibler avec des publicités personnalisées.",
            PrivacyCategory.Advertising, "HKCU", AdvertisingInfo, "Enabled", RegistryValueType.DWord, "0", "1"),

        new PrivacySetting("tailored-experiences", "Expériences personnalisées",
            "Désactive les astuces, suggestions et publicités adaptées à partir de vos données de diagnostic.",
            PrivacyCategory.Advertising, "HKCU", PrivacyKey, "TailoredExperiencesWithDiagnosticDataEnabled", RegistryValueType.DWord, "0", "1"),

        new PrivacySetting("start-track-progs", "Suivi des applications lancées",
            "Windows cesse de pister les applications ouvertes pour classer le menu Démarrer et la recherche.",
            PrivacyCategory.ActivityHistory, "HKCU", ExplorerAdvanced, "Start_TrackProgs", RegistryValueType.DWord, "0", "1"),

        new PrivacySetting("activity-feed", "Historique d'activité (Timeline)",
            "Désactive la collecte de l'historique d'activité. Politique système (HKLM).",
            PrivacyCategory.ActivityHistory, "HKLM", SystemPolicy, "EnableActivityFeed", RegistryValueType.DWord, "0", "1"),

        new PrivacySetting("start-suggestions", "Suggestions dans le menu Démarrer",
            "Supprime les applications et contenus « suggérés » affichés dans le menu Démarrer.",
            PrivacyCategory.Suggestions, "HKCU", ContentDelivery, "SystemPaneSuggestionsEnabled", RegistryValueType.DWord, "0", "1"),

        new PrivacySetting("silent-installed-apps", "Installation silencieuse d'applications",
            "Empêche Windows d'installer automatiquement des applications promues (jeux, partenaires) en arrière-plan.",
            PrivacyCategory.Suggestions, "HKCU", ContentDelivery, "SilentInstalledAppsEnabled", RegistryValueType.DWord, "0", "1"),

        new PrivacySetting("online-speech", "Reconnaissance vocale en ligne",
            "Désactive l'envoi de votre voix aux services cloud Microsoft. La reconnaissance hors-ligne reste disponible.",
            PrivacyCategory.Input, "HKCU", OnlineSpeech, "HasAccepted", RegistryValueType.DWord, "0", "1"),

        new PrivacySetting("bing-search", "Recherche web (Bing) dans Démarrer",
            "La recherche du menu Démarrer cesse d'interroger Bing et de remonter des résultats web.",
            PrivacyCategory.Search, "HKCU", SearchKey, "BingSearchEnabled", RegistryValueType.DWord, "0", "1"),

        new PrivacySetting("inking-typing", "Personnalisation de la saisie (frappe & stylet)",
            "Désactive l'analyse de votre frappe et de votre écriture manuscrite pour le dictionnaire personnalisé.",
            PrivacyCategory.Input, "HKCU", Personalization, "AcceptedPrivacyPolicy", RegistryValueType.DWord, "0", "1"),
    };

    public static string CategoryLabel(PrivacyCategory category) => category switch
    {
        PrivacyCategory.Telemetry       => "Télémétrie & diagnostics",
        PrivacyCategory.Advertising     => "Publicité & personnalisation",
        PrivacyCategory.ActivityHistory => "Historique d'activité",
        PrivacyCategory.Suggestions     => "Suggestions & contenu",
        PrivacyCategory.Search          => "Recherche",
        PrivacyCategory.Input           => "Saisie vocale & texte",
        _                               => "Confidentialité",
    };

    public static PrivacySetting? Find(string? id) =>
        Settings.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The live state of one privacy setting, derived from a registry read. The honesty rules live here: an
/// <b>absent</b> key means Windows is using its default — which for every catalog entry is the collection-active
/// value — so absence reads as « collecte active », never as a fabricated « protégé ». Comparison goes through
/// <see cref="RegistryValue.Matches"/> so a DWord read back as "0x0" matches an expected "0" numerically. The
/// <c>Can*</c> gates refuse a write that wouldn't change anything (re-hardening, or "restoring" an absent key),
/// so no button is ever a no-op.
/// </summary>
public sealed record PrivacySettingState(PrivacySetting Setting, string? LiveValue, bool IsPresent)
{
    public string Id => Setting.Id;
    public string Label => Setting.Label;
    public string Advice => Setting.Advice;
    public string ValueName => Setting.ValueName;
    public string CategoryLabel => PrivacyCatalog.CategoryLabel(Setting.Category);
    public string? Note => Setting.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Setting.Note);

    public bool IsHardened => IsPresent && RegistryValue.Matches(LiveValue, Setting.HardenedValue, Setting.Kind);
    public bool IsDefault => !IsPresent || RegistryValue.Matches(LiveValue, Setting.DefaultValue, Setting.Kind);
    public bool IsCustomValue => IsPresent && !IsHardened && !IsDefault;

    // Harden is offered unless already hardened; Restore only when a key is actually present AND not already at the
    // Windows default (writing the default onto an absent key would be a semantic no-op → button stays disabled).
    public bool CanHarden => !IsHardened;
    public bool CanRestore => IsPresent && !IsDefault;

    public bool ShowHardenedBadge => IsHardened;

    public string StateDisplay =>
        !IsPresent   ? "Non configuré · défaut Windows (collecte active)"
        : IsHardened ? "Protégé (collecte réduite)"
        : IsDefault  ? "Défaut Windows (collecte active)"
        : $"Valeur personnalisée : {LiveValue}";
}

/// <summary>One concrete write a bulk action wants to make: set <see cref="Setting"/>'s value to <see cref="Value"/>.</summary>
public readonly record struct PrivacyWrite(PrivacySetting Setting, string Value);

public static class PrivacyPlan
{
    /// <summary>« Tout renforcer » — every setting to its privacy-protective value.</summary>
    public static IReadOnlyList<PrivacyWrite> HardenAll(IReadOnlyList<PrivacySetting> settings) =>
        settings.Select(s => new PrivacyWrite(s, s.HardenedValue)).ToList();

    /// <summary>« Tout rétablir » — every setting back to its Windows default value.</summary>
    public static IReadOnlyList<PrivacyWrite> RestoreAll(IReadOnlyList<PrivacySetting> settings) =>
        settings.Select(s => new PrivacyWrite(s, s.DefaultValue)).ToList();
}

public sealed record PrivacyReport(IReadOnlyList<PrivacySettingState> Settings)
{
    public int Total => Settings.Count;
    public int HardenedCount => Settings.Count(s => s.IsHardened);
    public int DefaultCount => Settings.Count(s => s.IsDefault);
    public int CustomCount => Settings.Count(s => s.IsCustomValue);

    public bool AllHardened => Settings.Count > 0 && Settings.All(s => s.IsHardened);
    public bool NoneHardened => Settings.All(s => !s.IsHardened);
}

/// <summary>
/// « Confidentialité » — a curated front-end over the consent/telemetry registry switches. Every control performs a
/// real, reversible registry write that the page RE-READS, so a write Windows rejects comes back as the unchanged
/// truth, never a fabricated « done ». Scope is stated honestly: this reduces collection — it does not make Windows
/// private, and the telemetry floor on consumer SKUs is disclosed per-setting. The DiagTrack service and the
/// telemetry scheduled tasks (the transport) live on their own pages.
/// </summary>
public sealed class PrivacyService : IPrivacyService
{
    private readonly IRegistryService _registry;

    public PrivacyService(IRegistryService registry) => _registry = registry;

    public Task<PrivacyReport> GetReportAsync() => Task.Run(GetReport);

    private PrivacyReport GetReport()
    {
        var states = new List<PrivacySettingState>(PrivacyCatalog.Settings.Count);
        foreach (var s in PrivacyCatalog.Settings)
        {
            bool present = _registry.TryReadValue(s.Hive, s.Key, s.ValueName, out var live);
            states.Add(new PrivacySettingState(s, present ? live : null, present));
        }
        return new PrivacyReport(states);
    }

    public Task<bool> SetHardenedAsync(string settingId, bool harden) => Task.Run(() => SetHardened(settingId, harden));

    private bool SetHardened(string settingId, bool harden)
    {
        var s = PrivacyCatalog.Find(settingId);
        if (s is null) return false;
        var value = harden ? s.HardenedValue : s.DefaultValue;
        return _registry.WriteValue(s.Hive, s.Key, s.ValueName, value, s.Kind);
    }

    public Task<bool> ApplyAllAsync(bool harden) => Task.Run(() => ApplyAll(harden));

    private bool ApplyAll(bool harden)
    {
        var plan = harden
            ? PrivacyPlan.HardenAll(PrivacyCatalog.Settings)
            : PrivacyPlan.RestoreAll(PrivacyCatalog.Settings);

        bool allOk = true;
        foreach (var w in plan)
            allOk &= _registry.WriteValue(w.Setting.Hive, w.Setting.Key, w.Setting.ValueName, w.Value, w.Setting.Kind);
        return allOk;
    }
}
