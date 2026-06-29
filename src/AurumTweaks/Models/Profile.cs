using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AurumTweaks.Models;

/// <summary>
/// A named bundle of tweaks. Built-in presets and user profiles share this model.
/// </summary>
public class Profile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New profile";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("isBuiltIn")]
    public bool IsBuiltIn { get; set; }

    [JsonPropertyName("isCompetitiveSafe")]
    public bool IsCompetitiveSafe { get; set; }

    [JsonPropertyName("tweakIds")]
    public List<string> TweakIds { get; set; } = new();

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastAppliedUtc")]
    public DateTime? LastAppliedUtc { get; set; }

    /// <summary>Transient, never persisted: the compact « N tweak(s) · X tranquille / Y avancé / Z extrême » line the
    /// Profiles page shows on each card so the user sees what a profile contains BEFORE loading it. The VM sets it
    /// after resolving the profile against the live catalogue (which the model itself has no access to); it stays
    /// null on any profile not run through ProfileComposition.Summarize.</summary>
    [JsonIgnore]
    public string? CompositionLabel { get; set; }

    /// <summary>Transient, never persisted: the « Attention : … » pre-apply caution the card shows when the resolved
    /// set carries hardware / anti-cheat / Extreme-tier risk — the same disclosure the confirmation gate gives after
    /// « Charger », surfaced earlier. Empty/null when nothing is risky (the card hides the line). Set by the VM from
    /// ProfileApplyRisk.Assess against the live catalogue.</summary>
    [JsonIgnore]
    public string? RiskHint { get; set; }

    /// <summary>Transient, never persisted: the « ⚠ N réglage(s) en conflit » caution the card shows when the resolved
    /// set contains two or more tweaks that write the SAME registry value / service startup to DIFFERENT values — the
    /// invisible footgun a union/merge can introduce, where apply order alone decides the winner. Empty/null when the
    /// set is internally consistent (the card hides the line). Set by the VM from TweakConflictDetector.Detect against
    /// the live catalogue, so it can never claim a conflict the apply path wouldn't actually hit.</summary>
    [JsonIgnore]
    public string? ConflictHint { get; set; }
}
