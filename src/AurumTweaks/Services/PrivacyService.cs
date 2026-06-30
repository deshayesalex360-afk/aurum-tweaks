using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>The broad area a privacy setting belongs to — drives the FR section label shown on the page.</summary>
public enum PrivacyCategory { Telemetry, Advertising, ActivityHistory, Suggestions, Search, Input, Ai }

/// <summary>
/// One curated privacy/telemetry consent setting this page can toggle. Each maps to a single, individually-named,
/// well-documented registry value — never an opaque blob. <see cref="HardenedValue"/> is the privacy-protective
/// value; <see cref="DefaultValue"/> is the Windows default the key holds (or would hold) when nothing is configured,
/// so an absent key honestly reads as the setting's default state, never as a fabricated « protégé ». Some Windows
/// policy values are truly "not configured" only when the value is absent; those set <see cref="RestoreDeletesValue"/>
/// so revert deletes the value instead of writing a pretend-default. <see cref="Note"/> carries a per-setting caveat
/// (e.g. the telemetry floor on consumer SKUs) shown verbatim in the UI.
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
    string? Note = null,
    string HardenedStateDisplay = "Protégé (collecte réduite)",
    string DefaultStateDisplay = "Défaut Windows (collecte active)",
    bool RestoreDeletesValue = false);

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
    public const string WindowsAiPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI";
    public const string WindowsAiUserPolicy = @"Software\Policies\Microsoft\Windows\WindowsAI";
    public const string WindowsCopilotPolicy = @"Software\Policies\Microsoft\Windows\WindowsCopilot";
    public const string FeatureUpdateCaveat =
        "Une mise à jour de fonctionnalité Windows peut réactiver, réinstaller ou ignorer certains composants selon l'édition et la build ; relisez l'état après mise à jour.";
    public const string AiPolicyDefaultDisplay = "choix Windows/utilisateur non forcé";
    public const string AiPolicyHardenedDisplay = "Désactivé par politique";

    public static IReadOnlyList<PrivacySetting> Settings { get; } = new[]
    {
        new PrivacySetting("allow-telemetry", "Données de diagnostic (télémétrie)",
            "Réduit au minimum les données de diagnostic envoyées à Microsoft. Politique système (HKLM).",
            PrivacyCategory.Telemetry, "HKLM", DataCollectionPolicy, "AllowTelemetry", RegistryValueType.DWord, "0", "3",
            "Sur Windows Famille/Pro, le niveau « Sécurité » (0) est plafonné à « Requis » (1) par Windows ; il n'est pleinement honoré que sur Entreprise/Éducation. Cela réduit la collecte, ne l'élimine pas."),

        new PrivacySetting("recall-snapshots-device", "Recall Windows — snapshots (machine)",
            "Force la politique machine qui empêche Recall d'enregistrer des instantanés quand cette fonction est présente et honorée par Windows.",
            PrivacyCategory.Ai, "HKLM", WindowsAiPolicy, "DisableAIDataAnalysis", RegistryValueType.DWord, "1", "0",
            "Microsoft indique que l'activation de cette politique peut supprimer les instantanés Recall existants ; la clé est réversible, pas les données déjà effacées par Windows. " + FeatureUpdateCaveat,
            AiPolicyHardenedDisplay, AiPolicyDefaultDisplay, RestoreDeletesValue: true),

        new PrivacySetting("recall-snapshots-user", "Recall Windows — snapshots (utilisateur)",
            "Force la politique du profil courant qui empêche Recall d'enregistrer des instantanés quand cette fonction est présente et honorée par Windows.",
            PrivacyCategory.Ai, "HKCU", WindowsAiUserPolicy, "DisableAIDataAnalysis", RegistryValueType.DWord, "1", "0",
            "Microsoft indique que l'activation de cette politique peut supprimer les instantanés Recall existants ; la clé est réversible, pas les données déjà effacées par Windows. " + FeatureUpdateCaveat,
            AiPolicyHardenedDisplay, AiPolicyDefaultDisplay, RestoreDeletesValue: true),

        new PrivacySetting("copilot-windows-policy", "Copilot Windows (politique)",
            "Désactive l'expérience Copilot Windows sur les builds qui honorent cette ancienne politique ; ne désinstalle pas Microsoft 365 Copilot ni les autres applications IA.",
            PrivacyCategory.Ai, "HKCU", WindowsCopilotPolicy, "TurnOffWindowsCopilot", RegistryValueType.DWord, "1", "0",
            "Cette politique ne couvre pas tous les produits Copilot récents et peut être ignorée ou remplacée par Windows/Microsoft 365. " + FeatureUpdateCaveat,
            AiPolicyHardenedDisplay, AiPolicyDefaultDisplay, RestoreDeletesValue: true),

        new PrivacySetting("click-to-do-policy", "Click to Do (IA Windows)",
            "Désactive la disponibilité de Click to Do sur les builds Windows qui exposent cette politique Windows AI.",
            PrivacyCategory.Ai, "HKLM", WindowsAiPolicy, "DisableClickToDo", RegistryValueType.DWord, "1", "0",
            "Microsoft marque cette politique comme récente et liée aux builds qui la prennent en charge ; sur une build stable non compatible, elle peut n'avoir aucun effet visible. " + FeatureUpdateCaveat,
            AiPolicyHardenedDisplay, AiPolicyDefaultDisplay, RestoreDeletesValue: true),

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
        PrivacyCategory.Ai              => "IA Windows",
        _                               => "Confidentialité",
    };

    public static PrivacySetting? Find(string? id) =>
        Settings.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The live state of one privacy setting, derived from a registry read. The honesty rules live here: an
/// <b>absent</b> key means Windows is using its default/not-configured behavior, so absence reads from the setting's
/// single default wording, never as a fabricated « protégé ». Comparison goes through
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
    public bool IsDefault =>
        !IsPresent || (!Setting.RestoreDeletesValue && RegistryValue.Matches(LiveValue, Setting.DefaultValue, Setting.Kind));
    public bool IsCustomValue => IsPresent && !IsHardened && !IsDefault;

    // Harden is offered unless already hardened; Restore only when a key is actually present AND not already at the
    // Windows default (writing the default onto an absent key would be a semantic no-op → button stays disabled).
    public bool CanHarden => !IsHardened;
    public bool CanRestore => IsPresent && !IsDefault;

    public bool ShowHardenedBadge => IsHardened;

    public string StateDisplay =>
        !IsPresent   ? $"Non configuré · {Setting.DefaultStateDisplay}"
        : IsHardened ? Setting.HardenedStateDisplay
        : IsDefault  ? Setting.DefaultStateDisplay
        : $"Valeur personnalisée : {LiveValue}";
}

/// <summary>
/// One concrete registry change a bulk action wants to make: either set <see cref="Setting"/>'s value to
/// <see cref="Value"/>, or delete it when <see cref="DeletesValue"/> is true (policy "not configured" restore).
/// </summary>
public readonly record struct PrivacyWrite(PrivacySetting Setting, string? Value)
{
    public bool DeletesValue => Value is null;
}

public static class PrivacyPlan
{
    /// <summary>« Tout renforcer » — every setting to its privacy-protective value.</summary>
    public static IReadOnlyList<PrivacyWrite> HardenAll(IReadOnlyList<PrivacySetting> settings) =>
        settings.Select(s => new PrivacyWrite(s, s.HardenedValue)).ToList();

    /// <summary>« Tout rétablir » — every setting back to its Windows default value.</summary>
    public static IReadOnlyList<PrivacyWrite> RestoreAll(IReadOnlyList<PrivacySetting> settings) =>
        settings.Select(Restore).ToList();

    public static PrivacyWrite Restore(PrivacySetting setting) =>
        new(setting, setting.RestoreDeletesValue ? null : setting.DefaultValue);
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

public sealed record PrivacyFirewallRule(
    string Name,
    string DisplayName,
    string ProgramPath,
    string Description);

public sealed record PrivacyFirewallRuleState(PrivacyFirewallRule Rule, bool Present, bool Enabled)
{
    public bool BlocksTraffic => Present && Enabled;
}

public sealed record PrivacyFirewallReport(IReadOnlyList<PrivacyFirewallRuleState> Rules, bool QueryOk)
{
    public int Total => Rules.Count;
    public int PresentCount => Rules.Count(r => r.Present);
    public int BlockingCount => Rules.Count(r => r.BlocksTraffic);
    public bool AnyPresent => QueryOk && Rules.Any(r => r.Present);
    public bool AllBlocking => QueryOk && Rules.Count > 0 && Rules.All(r => r.BlocksTraffic);
    public bool CanBlock => QueryOk && !AllBlocking;
    public bool CanRemove => QueryOk && AnyPresent;

    public string StateDisplay =>
        !QueryOk     ? "Pare-feu non lu · PowerShell/Windows Firewall a refusé la requête."
        : AllBlocking ? $"Actif · {BlockingCount}/{Total} règle(s) Aurum bloquante(s)."
        : AnyPresent  ? $"Partiel · {BlockingCount}/{Total} règle(s) bloquante(s), {PresentCount} présente(s)."
        : "Non configuré · aucune règle pare-feu Aurum.";
}

/// <summary>
/// Pure definition and rendering for the optional telemetry firewall block. The honesty boundary is here: only exact,
/// named, removable outbound rules are produced, scoped to known Windows telemetry executables. There is no hosts file,
/// DNS hijack, blanket Microsoft block, or claim that this stops all telemetry.
/// </summary>
public static class PrivacyFirewallPlan
{
    public const string RuleGroup = "Aurum Tweaks - Confidentialité";
    public const string UiLimit =
        "Règles Windows Firewall nommées et retirables. Elles ciblent uniquement CompatTelRunner, DeviceCensus et wsqmcons quand ces exécutables existent ; aucun hosts/DNS, aucun blocage total de Microsoft. Une mise à jour de fonctionnalité Windows peut recréer ou remplacer certains composants.";

    public static IReadOnlyList<PrivacyFirewallRule> TelemetryRules { get; } = new[]
    {
        new PrivacyFirewallRule(
            "AurumTweaks.Privacy.Telemetry.CompatTelRunner",
            "Aurum Tweaks - Bloquer CompatTelRunner",
            @"%SystemRoot%\System32\CompatTelRunner.exe",
            "Bloque uniquement les sorties réseau de CompatTelRunner.exe."),
        new PrivacyFirewallRule(
            "AurumTweaks.Privacy.Telemetry.DeviceCensus",
            "Aurum Tweaks - Bloquer DeviceCensus",
            @"%SystemRoot%\System32\DeviceCensus.exe",
            "Bloque uniquement les sorties réseau de DeviceCensus.exe."),
        new PrivacyFirewallRule(
            "AurumTweaks.Privacy.Telemetry.WsqmCons",
            "Aurum Tweaks - Bloquer wsqmcons",
            @"%SystemRoot%\System32\wsqmcons.exe",
            "Bloque uniquement les sorties réseau de wsqmcons.exe."),
    };

    public static PrivacyFirewallReport BuildReport(IReadOnlyDictionary<string, bool> enabledByName, bool queryOk)
    {
        var states = TelemetryRules
            .Select(r => enabledByName.TryGetValue(r.Name, out var enabled)
                ? new PrivacyFirewallRuleState(r, Present: true, Enabled: enabled)
                : new PrivacyFirewallRuleState(r, Present: false, Enabled: false))
            .ToList();
        return new PrivacyFirewallReport(states, queryOk);
    }

    public static IReadOnlyDictionary<string, bool> ParseRuleEnabledLines(string stdout)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('|', 2);
            if (parts.Length != 2) continue;
            var name = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            map[name] = ParseEnabled(parts[1]);
        }
        return map;
    }

    public static string BuildQueryCommand()
        => "$names=@(" + RuleNameList() + ");" +
           "Get-NetFirewallRule -Name $names -ErrorAction SilentlyContinue | " +
           "ForEach-Object { $_.Name + '|' + $_.Enabled }";

    public static string BuildEnsureCommand()
    {
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference='Stop';");
        sb.Append("$names=@(").Append(RuleNameList()).Append(");");
        sb.Append("Remove-NetFirewallRule -Name $names -ErrorAction SilentlyContinue;");
        foreach (var rule in TelemetryRules)
        {
            sb.Append("$program=[Environment]::ExpandEnvironmentVariables('").Append(Ps(rule.ProgramPath)).Append("');");
            sb.Append("New-NetFirewallRule ");
            sb.Append("-Name '").Append(Ps(rule.Name)).Append("' ");
            sb.Append("-DisplayName '").Append(Ps(rule.DisplayName)).Append("' ");
            sb.Append("-Group '").Append(Ps(RuleGroup)).Append("' ");
            sb.Append("-Direction Outbound -Action Block -Program $program -Profile Any ");
            sb.Append("-Description '").Append(Ps(rule.Description)).Append("' ");
            sb.Append("-Enabled True -ErrorAction Stop | Out-Null;");
        }
        return sb.ToString();
    }

    public static string BuildRemoveCommand()
        => "$names=@(" + RuleNameList() + ");Remove-NetFirewallRule -Name $names -ErrorAction SilentlyContinue";

    private static bool ParseEnabled(string value)
    {
        var v = value.Trim();
        return v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("1", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || v.Equals("enabled", StringComparison.OrdinalIgnoreCase);
    }

    private static string RuleNameList() =>
        string.Join(",", TelemetryRules.Select(r => "'" + Ps(r.Name) + "'"));

    private static string Ps(string value) => value.Replace("'", "''");
}

/// <summary>
/// « Confidentialité » — a curated front-end over the consent/telemetry registry switches. Every control performs a
/// real, reversible registry write (or exact policy delete) that the page RE-READS, so a write Windows rejects comes
/// back as the unchanged truth, never a fabricated « done ». Scope is stated honestly: this reduces collection — it
/// does not make Windows private, and the telemetry floor / AI-policy caveats are disclosed per-setting. The optional
/// firewall block uses named, removable rules only; no hosts/DNS hijack.
/// </summary>
public sealed class PrivacyService : IPrivacyService
{
    private readonly IRegistryService _registry;

    public PrivacyService(IRegistryService registry) => _registry = registry;

    public Task<PrivacyReport> GetReportAsync() => Task.Run(GetReport);

    public Task<PrivacyFirewallReport> GetTelemetryFirewallReportAsync() => Task.Run(GetTelemetryFirewallReport);

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

    private static PrivacyFirewallReport GetTelemetryFirewallReport()
    {
        var (exit, stdout) = ProcessRunner.Capture("powershell.exe",
            "-NoProfile -NonInteractive -Command \"" + PrivacyFirewallPlan.BuildQueryCommand() + "\"");
        var live = PrivacyFirewallPlan.ParseRuleEnabledLines(stdout);
        return PrivacyFirewallPlan.BuildReport(live, queryOk: exit == 0);
    }

    public Task<bool> SetHardenedAsync(string settingId, bool harden) => Task.Run(() => SetHardened(settingId, harden));

    private bool SetHardened(string settingId, bool harden)
    {
        var s = PrivacyCatalog.Find(settingId);
        if (s is null) return false;
        return ApplyWrite(harden ? new PrivacyWrite(s, s.HardenedValue) : PrivacyPlan.Restore(s));
    }

    public Task<bool> ApplyAllAsync(bool harden) => Task.Run(() => ApplyAll(harden));

    public Task<bool> SetTelemetryFirewallBlockedAsync(bool block) => Task.Run(() => SetTelemetryFirewallBlocked(block));

    private bool ApplyAll(bool harden)
    {
        var plan = harden
            ? PrivacyPlan.HardenAll(PrivacyCatalog.Settings)
            : PrivacyPlan.RestoreAll(PrivacyCatalog.Settings);

        bool allOk = true;
        foreach (var w in plan)
            allOk &= ApplyWrite(w);
        return allOk;
    }

    private bool ApplyWrite(PrivacyWrite write) =>
        write.DeletesValue
            ? _registry.DeleteValue(write.Setting.Hive, write.Setting.Key, write.Setting.ValueName)
            : _registry.WriteValue(write.Setting.Hive, write.Setting.Key, write.Setting.ValueName, write.Value!, write.Setting.Kind);

    private static bool SetTelemetryFirewallBlocked(bool block)
    {
        var command = block ? PrivacyFirewallPlan.BuildEnsureCommand() : PrivacyFirewallPlan.BuildRemoveCommand();
        var (exit, _) = ProcessRunner.Capture("powershell.exe",
            "-NoProfile -NonInteractive -Command \"" + command + "\"",
            timeoutMs: 30_000);
        return exit == 0;
    }
}
