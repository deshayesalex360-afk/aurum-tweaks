using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// The built-in preset catalogue, kept pure (no filesystem) so its safety-bearing flags can be pinned by
/// tests — same extraction pattern as <c>HardwareClassification</c> / <c>InputTuningLogic</c>. The
/// load-bearing promise: exactly one preset, <c>preset-gaming-safe</c>, carries
/// <see cref="Profile.IsCompetitiveSafe"/>, because the competitive filter trusts that flag to tell a player
/// a preset is safe to run under Vanguard/FACEIT/EAC. A second preset wrongly carrying it — or this one
/// losing it — would be an anti-cheat honesty regression, exactly the kind a test must catch.
/// </summary>
public static class ProfilePresets
{
    /// <summary>A fresh copy of the six built-in presets (new instances each call — never a shared mutable list).</summary>
    public static IReadOnlyList<Profile> BuiltIn() => new List<Profile>
    {
        new() { Id = "preset-stock", Name = "Stock", Description = "Windows par défaut, aucun tweak appliqué.", IsBuiltIn = true },
        new() { Id = "preset-tranquille", Name = "Tranquille", Description = "Tweaks safe pour tout le monde.", IsBuiltIn = true },
        new() { Id = "preset-gaming-safe", Name = "Compétitif sécurisé", Description = "Tweaks gaming compatibles Vanguard/FACEIT/EAC.", IsBuiltIn = true, IsCompetitiveSafe = true },
        new() { Id = "preset-gaming", Name = "Gaming compétitif", Description = "Tweaks gaming complets, certains incompatibles avec anti-cheat strict.", IsBuiltIn = true },
        new() { Id = "preset-streaming", Name = "Streaming", Description = "Tweaks orientés OBS/streaming.", IsBuiltIn = true },
        new() { Id = "preset-extreme", Name = "Extrême", Description = "Tous les tweaks. Pour utilisateurs avertis.", IsBuiltIn = true }
    };
}

/// <summary>
/// Resolves the concrete catalogue tweaks a profile actually applies — pure (no I/O), so the membership rules
/// and the load-bearing competitive-safety invariant are unit-testable, the same extraction pattern as
/// <see cref="ProfilePresets"/>. <see cref="ProfilePresets.BuiltIn"/> ships the six presets with EMPTY
/// <see cref="Profile.TweakIds"/> (it has no catalogue to read); their real membership is derived here at
/// apply-time from live tweak metadata (tier / risk / category / anti-cheat matrix). A user profile instead
/// carries explicit ids, which we resolve against the catalogue — silently skipping ids that no longer exist
/// so a stale id can neither crash an apply nor be counted as applied.
///
/// The promise a test must guard: <c>preset-gaming-safe</c> resolves to ZERO tweaks carrying any anti-cheat
/// concern. That preset is what tells a competitive player "safe under Vanguard/FACEIT/EAC", so a single
/// risky tweak leaking in would be a ban-risk honesty regression.
/// </summary>
public static class ProfileComposition
{
    public static IReadOnlyList<Tweak> Resolve(Profile profile, IReadOnlyList<Tweak> catalog)
    {
        if (profile.IsBuiltIn)
        {
            var predicate = PresetPredicate(profile.Id);
            return predicate is null
                ? Array.Empty<Tweak>()                          // unknown preset → apply nothing, never a guess
                : catalog.Where(predicate).ToList();
        }

        // User profile: honour its explicit id list, in catalogue order, skipping ids that no longer resolve.
        var wanted = new HashSet<string>(profile.TweakIds, StringComparer.OrdinalIgnoreCase);
        return catalog.Where(t => wanted.Contains(t.Id)).ToList();
    }

    /// <summary>Count what a profile resolves to, split by tier — the honest "what's inside" the Profiles page shows
    /// on each card BEFORE « Charger », so loading a set is never a blind click. Reuses <see cref="Resolve"/>, so it
    /// inherits exactly the membership the apply uses (preset predicate / user id list, stale ids skipped).</summary>
    public static ProfileCompositionSummary Summarize(Profile profile, IReadOnlyList<Tweak> catalog)
        => Summarize(Resolve(profile, catalog));

    /// <summary>Same tier tally from an already-resolved member list, so a caller that needs BOTH the composition and
    /// the pre-apply risk (the Profiles page card) can <see cref="Resolve"/> once and feed the one list to this and
    /// to <see cref="ProfileApplyRisk.Assess"/> — no double resolve, and the two lines can never describe
    /// different sets.</summary>
    public static ProfileCompositionSummary Summarize(IReadOnlyList<Tweak> members)
        => new(
            members.Count,
            members.Count(t => t.Tier == TweakTier.Tranquille),
            members.Count(t => t.Tier == TweakTier.Avance),
            members.Count(t => t.Tier == TweakTier.Extreme));

    // Membership rule per built-in preset, expressed against real tweak metadata. null = an id we don't know,
    // which Resolve turns into the empty set (honest: we never fabricate a preset we can't define).
    private static Func<Tweak, bool>? PresetPredicate(string presetId) => presetId switch
    {
        "preset-stock"       => static _ => false,                              // Stock = Windows left as-is
        "preset-tranquille"  => static t => t.Tier == TweakTier.Tranquille,     // the whole "safe for everyone" tier
        // Competitive-safe: nothing that touches an anti-cheat, nothing past medium risk, nothing Extreme-tier.
        "preset-gaming-safe" => static t => !t.AntiCheat.HasAnyConcern
                                            && t.Risk <= RiskLevel.Medium
                                            && t.Tier <= TweakTier.Avance,
        // Full gaming set: the performance/latency-bearing categories, up to high risk, anti-cheat-risky INCLUDED
        // (the preset's own description warns about that) but never hardware-damaging.
        "preset-gaming"      => static t => t.Risk <= RiskLevel.High
                                            && t.Category is TweakCategory.Gaming
                                                          or TweakCategory.NetworkLatency
                                                          or TweakCategory.PerformanceMultimedia
                                                          or TweakCategory.PowerBoot,
        // Streaming: quiet the background (debloat / services / telemetry) and keep the uplink clean, while
        // staying safe (≤ medium risk, no anti-cheat concern, ≤ Avance) so the encoder never hitches.
        "preset-streaming"   => static t => !t.AntiCheat.HasAnyConcern
                                            && t.Risk <= RiskLevel.Medium
                                            && t.Tier <= TweakTier.Avance
                                            && t.Category is TweakCategory.Debloat
                                                          or TweakCategory.Services
                                                          or TweakCategory.PrivacyTelemetry
                                                          or TweakCategory.NetworkLatency,
        "preset-extreme"     => static _ => true,                               // literally every tweak in the catalogue
        _ => null
    };
}

/// <summary>The per-tier breakdown of what a profile resolves to (<see cref="ProfileComposition.Summarize"/>).
/// <see cref="Label"/> is the compact, honest French line the profile card shows; it names only the tiers that
/// actually occur, so an all-tranquille preset never reads as carrying risk it doesn't, and an empty profile
/// (Stock) says « Aucun tweak » rather than implying a phantom batch.</summary>
public sealed record ProfileCompositionSummary(int Total, int Tranquille, int Avance, int Extreme)
{
    public bool IsEmpty => Total == 0;

    public string Label
    {
        get
        {
            if (Total == 0) return "Aucun tweak";
            var parts = new List<string>(3);
            if (Tranquille > 0) parts.Add($"{Tranquille} tranquille");
            if (Avance > 0) parts.Add($"{Avance} avancé");
            if (Extreme > 0) parts.Add($"{Extreme} extrême");
            return $"{Total} tweak(s) · {string.Join(" / ", parts)}";
        }
    }
}

/// <summary>
/// Resolves the on-disk path for a profile id — pure (no I/O) so the path-containment rule is unit-testable,
/// same extraction pattern as <see cref="ProfilePresets"/>. Builds from the file name ONLY
/// (<see cref="Path.GetFileName(string)"/>): ids are app-generated today, but a future user-influenced id
/// carrying ".." or an absolute/rooted path must never escape <paramref name="profilesDir"/> — plain
/// <see cref="Path.Combine(string, string)"/> would honour an absolute second argument and write anywhere.
/// </summary>
public static class ProfilePath
{
    public static string For(string profilesDir, string id)
        => Path.Combine(profilesDir, Path.GetFileName($"{id}.json"));
}

/// <summary>
/// Portable export/import of a user profile — pure (no I/O, no dialog) so the trust boundary is unit-testable,
/// same extraction pattern as <see cref="ProfilePresets"/>. The format is deliberately minimal (name +
/// description + tweak ids); everything else is local bookkeeping.
///
/// <para><b>Load-bearing honesty on import.</b> A profile file is untrusted input — it may come from another
/// machine, a forum, or a hand-edited file — so <see cref="Parse"/>:
/// <list type="bullet">
/// <item>NEVER trusts a payload's <c>isBuiltIn</c>/<c>isCompetitiveSafe</c> flags: an imported profile is always
///   a plain user bundle (false/false), so a crafted file can't masquerade as a trusted preset nor wear a fake
///   "AC SAFE" badge it didn't earn;</item>
/// <item>mints a fresh local id (never reuses the payload's), so importing can't silently overwrite an existing
///   local profile that happens to share an id;</item>
/// <item>reconciles the tweak ids against the LIVE catalogue, keeping only those that exist here and reporting
///   how many were dropped — we never store (or later "apply") an id this build doesn't know. With zero
///   recognized ids the import is refused outright rather than saving an empty profile that would apply nothing.</item>
/// </list></para>
/// </summary>
public static class ProfileTransfer
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>A portable copy of the profile: only its name, description and tweak ids (no id, flags or
    /// timestamps — those are local bookkeeping the importer re-derives).</summary>
    public static string Serialize(Profile profile) => JsonSerializer.Serialize(new
    {
        name = profile.Name,
        description = profile.Description,
        tweakIds = profile.TweakIds
    }, Opts);

    public static ProfileImport Parse(string json, IReadOnlyList<Tweak> catalog)
    {
        Profile? raw;
        try { raw = JsonSerializer.Deserialize<Profile>(json, Opts); }
        catch (JsonException) { raw = null; }
        return Reconcile(raw, catalog);
    }

    /// <summary>Reconcile one deserialized profile against the live catalogue: keep only ids the catalogue knows,
    /// report the unknown ones, and emit a sanitized profile (fresh id, default flags — never trusting the payload).
    /// Shared by single-file <see cref="Parse"/> and the multi-profile <see cref="ProfileBundle"/> importer so both
    /// trust the catalogue identically. Internal: exercised through both public entry points.</summary>
    internal static ProfileImport Reconcile(Profile? raw, IReadOnlyList<Tweak> catalog)
    {
        if (raw is null || string.IsNullOrWhiteSpace(raw.Name))
            return new ProfileImport { Ok = false, Summary = "Fichier de profil invalide ou illisible." };

        var name = raw.Name.Trim();
        var known = new HashSet<string>(catalog.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        var ids = raw.TweakIds ?? new List<string>();
        var recognized = ids.Where(known.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var unknown = ids.Where(id => !known.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (recognized.Count == 0)
            return new ProfileImport
            {
                Ok = false,
                UnknownIds = unknown,
                Summary = unknown.Count > 0
                    ? $"« {name} » : aucun des {unknown.Count} tweak(s) n'existe dans ce catalogue — rien à importer."
                    : $"« {name} » ne contient aucun tweak — rien à importer."
            };

        // Fresh id + false flags by model default — an imported profile is always a plain, local user bundle.
        var profile = new Profile
        {
            Name = name,
            Description = raw.Description?.Trim() ?? string.Empty,
            TweakIds = recognized
        };

        var summary = $"Profil « {name} » importé : {recognized.Count} tweak(s)";
        if (unknown.Count > 0) summary += $", {unknown.Count} ignoré(s) (absent(s) de ce catalogue)";
        summary += ".";

        return new ProfileImport
        {
            Ok = true,
            Profile = profile,
            RecognizedCount = recognized.Count,
            UnknownIds = unknown,
            Summary = summary
        };
    }
}

/// <summary>Outcome of parsing an imported profile file. <see cref="Ok"/> is true only when the file yielded a
/// usable, locally-applicable profile (at least one recognized tweak). <see cref="Summary"/> is the honest,
/// user-facing (French) result. <see cref="Profile"/> is the sanitized profile to store when Ok.</summary>
public sealed class ProfileImport
{
    public bool Ok { get; init; }
    public string Summary { get; init; } = string.Empty;
    public Profile? Profile { get; init; }
    public int RecognizedCount { get; init; }
    public IReadOnlyList<string> UnknownIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A portable bundle of MANY user profiles — a one-file backup / migration / full-setup share. Serialization writes
/// the same portable per-profile shape as <see cref="ProfileTransfer"/> (name, description, tweak ids only) under a
/// tagged envelope; import reconciles every profile against the live catalogue through <see cref="ProfileTransfer"/>,
/// keeping the ones that yield at least one recognized tweak and honestly tallying what was skipped. Pure (no I/O),
/// so the trust/skip behaviour is unit-testable; the file dialogs are the only untested glue on the page.
/// </summary>
public static class ProfileBundle
{
    public const string FormatTag = "aurum-profiles-bundle";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(IEnumerable<Profile> profiles) => JsonSerializer.Serialize(new
    {
        format = FormatTag,
        version = 1,
        profiles = profiles.Select(p => new { name = p.Name, description = p.Description, tweakIds = p.TweakIds })
    }, Opts);

    public static ProfileBundleImport Parse(string json, IReadOnlyList<Tweak> catalog)
    {
        BundleEnvelope? dto;
        try { dto = JsonSerializer.Deserialize<BundleEnvelope>(json, Opts); }
        catch (JsonException) { dto = null; }

        if (dto?.Profiles is null || dto.Profiles.Count == 0)
            return new ProfileBundleImport { Summary = "Lot de profils invalide ou vide." };

        var accepted = new List<Profile>();
        int skipped = 0;
        foreach (var raw in dto.Profiles)
        {
            var result = ProfileTransfer.Reconcile(raw, catalog);
            if (result.Ok && result.Profile is not null) accepted.Add(result.Profile);
            else skipped++;
        }

        string summary;
        if (accepted.Count == 0)
            summary = $"Aucun profil du lot n'a pu être importé ({skipped} sans tweak reconnu).";
        else if (skipped == 0)
            summary = $"Lot importé : {accepted.Count} profil(s).";
        else
            summary = $"Lot importé : {accepted.Count} profil(s) ({skipped} ignoré(s), aucun tweak reconnu).";

        return new ProfileBundleImport { Profiles = accepted, SkippedCount = skipped, Summary = summary };
    }

    // Case-insensitive matching (Opts) binds the lowercase "profiles" key; each element deserializes as a Profile
    // with only name/description/tweakIds set — Reconcile rebuilds a clean profile, so absent flags never matter.
    private sealed class BundleEnvelope
    {
        public List<Profile>? Profiles { get; set; }
    }
}

/// <summary>Outcome of importing a profile bundle. <see cref="Profiles"/> are the reconciled, ready-to-store
/// profiles (each yielded ≥1 recognized tweak); <see cref="SkippedCount"/> is how many were dropped; <see cref="Ok"/>
/// is true only when at least one profile survived. <see cref="Summary"/> is the honest, user-facing (French) tally.</summary>
public sealed class ProfileBundleImport
{
    public IReadOnlyList<Profile> Profiles { get; init; } = Array.Empty<Profile>();
    public int SkippedCount { get; init; }
    public string Summary { get; init; } = string.Empty;
    public bool Ok => Profiles.Count > 0;
}

/// <summary>
/// Assesses what's genuinely risky in the set a profile is about to apply — pure (no I/O), so the
/// confirmation-gate rule is unit-testable. The product applies presets in one click, and the heaviest ones
/// (preset-extreme, preset-gaming) carry anti-cheat-risky, hardware-risk and Extreme-tier tweaks; surfacing
/// exactly those before execution (rather than after, behind a restore point) is an honesty/safety improvement,
/// not a fake "are you sure" speed bump. Only these three real concern axes trip the gate.
/// </summary>
public static class ProfileApplyRisk
{
    public static ProfileRisk Assess(IReadOnlyList<Tweak> tweaks)
    {
        int hardware = tweaks.Count(t => t.Risk == RiskLevel.HardwareDamage);
        int antiCheat = tweaks.Count(t => t.AntiCheat.HasAnyConcern);
        int extreme = tweaks.Count(t => t.Tier == TweakTier.Extreme);
        bool requires = hardware > 0 || antiCheat > 0 || extreme > 0;

        var parts = new List<string>();
        if (hardware > 0) parts.Add($"{hardware} à risque matériel");
        if (antiCheat > 0) parts.Add($"{antiCheat} à risque anti-cheat");
        if (extreme > 0) parts.Add($"{extreme} de niveau Extrême");
        var joined = string.Join(", ", parts);

        return new ProfileRisk
        {
            HardwareCount = hardware,
            AntiCheatCount = antiCheat,
            ExtremeCount = extreme,
            RequiresConfirmation = requires,
            Summary = requires
                ? $"Ce profil contient {joined}. Confirme pour appliquer."
                : string.Empty,
            // Compact, CTA-free variant of the same enumeration for the profile card's pre-apply caution line: it
            // moves the gate's disclosure earlier (onto the card, before the click) without re-deriving what counts
            // as risky, so the two can never name different risks. Empty exactly when nothing is risky.
            ShortLabel = requires ? $"Attention : {joined}" : string.Empty
        };
    }
}

/// <summary>The risk breakdown of a to-apply set. <see cref="RequiresConfirmation"/> gates a one-click apply;
/// <see cref="Summary"/> is the honest, user-facing (French) reason (empty when nothing is risky).</summary>
public sealed class ProfileRisk
{
    public int HardwareCount { get; init; }
    public int AntiCheatCount { get; init; }
    public int ExtremeCount { get; init; }
    public bool RequiresConfirmation { get; init; }
    public string Summary { get; init; } = string.Empty;

    /// <summary>The compact « Attention : … » caution the profile card shows before « Charger » (empty when nothing
    /// is risky) — the same risk enumeration as <see cref="Summary"/> without the confirmation call-to-action.</summary>
    public string ShortLabel { get; init; } = string.Empty;
}

/// <summary>
/// Forks a profile into a fresh, editable USER profile. The membership is passed in already-resolved, so the same
/// helper serves both cases the page needs: a user profile is copied by handing over its stored id list verbatim
/// (stale ids preserved — a faithful copy), and a built-in preset is forked by handing over its currently-resolved
/// membership (a concrete snapshot, which is how an otherwise-immutable preset becomes editable). Keeping resolution
/// out of here means no re-derivation and a trivially testable core.
/// </summary>
public static class ProfileDuplicate
{
    /// <summary>Clone <paramref name="source"/> into a new user profile carrying <paramref name="memberIds"/>.
    /// Honesty-bearing choices: a fresh id (never overwrites the source), <c>IsBuiltIn = false</c> (a fork is always
    /// editable, even from a preset), the ids copied into a NEW list (mutating the copy can't touch the original),
    /// and NO <c>LastAppliedUtc</c> — the copy has never been applied, so carrying the stamp would claim an apply
    /// that never happened. The name is de-duplicated against <paramref name="existingNames"/> so two forks of one
    /// source don't present as identical cards.</summary>
    public static Profile Clone(Profile source, IReadOnlyList<string> memberIds, IEnumerable<string> existingNames)
        => new()
        {
            Name = UniqueName(source.Name, existingNames),
            Description = source.Description,
            IsBuiltIn = false,
            IsCompetitiveSafe = source.IsCompetitiveSafe,   // identical id set ⇒ identical safety property
            TweakIds = memberIds.ToList()
            // Id defaults to a fresh Guid; CreatedUtc defaults to now; LastAppliedUtc stays null.
        };

    /// <summary>« {base} (copie) », or « {base} (copie N) » for the smallest N ≥ 2 not already taken — so duplicating
    /// the same profile repeatedly yields distinct, ordered names. Case-insensitive to match the file-per-name feel.</summary>
    public static string UniqueName(string baseName, IEnumerable<string> existingNames)
    {
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var first = $"{baseName} (copie)";
        if (!taken.Contains(first)) return first;
        for (int n = 2; ; n++)
        {
            var candidate = $"{baseName} (copie {n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}

/// <summary>Set-difference of two profiles' resolved tweak-id memberships, so a user juggling several setups can see
/// at a glance what one profile adds, drops, or shares versus another. Pure: the caller resolves each profile (preset
/// predicate or user id-list) to its id set first, so presets and user profiles compare on equal footing and stale
/// ids are handled by the same Resolve the apply path uses. Order is the left/right input order; ids are de-duplicated
/// case-insensitively (matching the catalogue's identity rule).</summary>
public static class ProfileDiff
{
    public static ProfileComparison Compare(IEnumerable<string> leftIds, IEnumerable<string> rightIds)
    {
        var left = Dedupe(leftIds, out var leftSet);
        var right = Dedupe(rightIds, out var rightSet);
        return new ProfileComparison
        {
            OnlyInLeft = left.Where(id => !rightSet.Contains(id)).ToList(),
            OnlyInRight = right.Where(id => !leftSet.Contains(id)).ToList(),
            Shared = left.Where(rightSet.Contains).ToList(),
        };
    }

    /// <summary>One honest French line: "identical" when neither side has anything the other lacks, otherwise the
    /// shared / left-only / right-only tally naming both profiles.</summary>
    public static string Summarize(ProfileComparison c, string leftName, string rightName)
        => c.Identical
            ? $"« {leftName} » et « {rightName} » contiennent exactement les mêmes tweaks ({c.Shared.Count})."
            : $"{c.Shared.Count} en commun · {c.OnlyInLeft.Count} propre(s) à « {leftName} » · {c.OnlyInRight.Count} propre(s) à « {rightName} ».";

    private static List<string> Dedupe(IEnumerable<string> ids, out HashSet<string> set)
    {
        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var id in ids)
            if (!string.IsNullOrWhiteSpace(id) && set.Add(id)) ordered.Add(id);
        return ordered;
    }
}

public sealed class ProfileComparison
{
    public IReadOnlyList<string> OnlyInLeft { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OnlyInRight { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Shared { get; init; } = Array.Empty<string>();

    /// <summary>True when each side contains exactly what the other does — the two profiles would apply the same set.</summary>
    public bool Identical => OnlyInLeft.Count == 0 && OnlyInRight.Count == 0;
}

/// <summary>Renders a <see cref="ProfileComparison"/> as a shareable French text block — the "Copier le comparatif"
/// half of the Profiles comparison tool, mirroring <c>SnapshotReport</c> / <c>JournalTextReport</c>. Pure (no
/// clipboard): the VM does the one-line Clipboard.SetText, this builds the text, so the layout is unit-testable.
/// Each id list is emitted ONLY when it has rows — an empty heading would imply a bucket that isn't there (the
/// JournalTextReport rule) — so an identical comparison reads as just the summary plus the shared list.</summary>
public static class ProfileDiffReport
{
    public static string Render(ProfileComparison comparison, string leftName, string rightName, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Comparaison de profils");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine($"« {leftName} » (A) vs « {rightName} » (B)");
        sb.AppendLine(ProfileDiff.Summarize(comparison, leftName, rightName));
        sb.AppendLine(new string('-', 48));

        AppendSection(sb, $"PROPRE À « {leftName} » (A)", comparison.OnlyInLeft);
        AppendSection(sb, "EN COMMUN", comparison.Shared);
        AppendSection(sb, $"PROPRE À « {rightName} » (B)", comparison.OnlyInRight);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string heading, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"{heading} ({ids.Count}) :");
        foreach (var id in ids)
            sb.AppendLine($"  - {id}");
    }
}

/// <summary>Union of two profiles' resolved memberships into one — "I want everything from A AND from B". The
/// companion to <see cref="ProfileDiff"/>: compare to see what differs, merge to combine. Pure: the caller resolves
/// each profile to its id set first, so a preset's predicate set and a user id-list merge on equal footing.</summary>
public static class ProfileMerge
{
    /// <summary>Left's ids (de-duplicated case-insensitively) followed by the ids only the right side adds — so the
    /// merge reads as "A, plus what B brings", with no duplicate when both name the same tweak.</summary>
    public static IReadOnlyList<string> Union(IEnumerable<string> leftIds, IEnumerable<string> rightIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var id in leftIds.Concat(rightIds))
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) ordered.Add(id);
        return ordered;
    }

    /// <summary>« {A} + {B} », disambiguated with a numbered suffix when that name is already on a card
    /// (see <see cref="ProfileNaming.Disambiguate"/>) — so merging the same pair twice yields distinct, ordered cards.</summary>
    public static string UniqueMergedName(string leftName, string rightName, IEnumerable<string> existingNames)
        => ProfileNaming.Disambiguate($"{leftName} + {rightName}", existingNames);
}

/// <summary>Shared "pick a name no card already uses" rule for the profile-producing tools (merge « A + B »,
/// difference « A sans B »): returns <paramref name="baseName"/> when free, else « {baseName} (N) » for the
/// smallest N ≥ 2 not taken. Case-insensitive, matching the one-file-per-name store — a differently-cased clash
/// still forces a suffix instead of one profile silently overwriting another's file.</summary>
public static class ProfileNaming
{
    public static string Disambiguate(string baseName, IEnumerable<string> existingNames)
    {
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (int n = 2; ; n++)
        {
            var candidate = $"{baseName} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}

/// <summary>Turns the raw <see cref="TweakConflictDetector"/> findings into the one honest line a profile card shows
/// when its resolved set is internally contradictory — two or more tweaks writing the SAME target to DIFFERENT values,
/// the footgun a union/merge silently introduces and where apply order alone decides the winner. Empty when the set is
/// consistent (the card hides the line, claiming nothing). Pure phrasing so the count and caveat are unit-testable; the
/// detector does the actual analysis.</summary>
public static class ProfileConflicts
{
    public static string Summarize(IReadOnlyList<TweakConflict> conflicts)
        => conflicts.Count == 0
            ? string.Empty
            : $"⚠ {conflicts.Count} réglage(s) en conflit dans ce profil : à l'application, l'ordre décide du gagnant.";
}

/// <summary>Honest tally of a profile's LIVE applied state — how many of its resolved tweaks the system currently
/// reports as <see cref="TweakAppliedState.Applied"/>, vs not, vs unverifiable. The applied count is Applied ONLY:
/// an <see cref="TweakAppliedState.Indeterminate"/> op (shell-only, nothing to read back) is disclosed separately,
/// never folded into "appliqué" — the same rule <see cref="ITweakService.DetectStatesAsync"/> enforces, so the
/// Profiles page's « Vérifier » can never paint a ✓ it didn't read. Pure, so the phrasing is unit-testable without
/// probing the registry; the VM does the actual probe and feeds the states here.</summary>
public sealed record ProfileLiveState(int Applied, int NotApplied, int Indeterminate, int Total)
{
    public static ProfileLiveState Summarize(IReadOnlyList<TweakAppliedState> states)
        => new(
            states.Count(s => s == TweakAppliedState.Applied),
            states.Count(s => s == TweakAppliedState.NotApplied),
            states.Count(s => s == TweakAppliedState.Indeterminate),
            states.Count);

    /// <summary>True only when every resolved tweak read back as Applied — the one case that earns the ✓.</summary>
    public bool FullyApplied => Total > 0 && Applied == Total;

    /// <summary>One honest French line: the applied/total ratio, then the not-applied and indeterminate counts
    /// disclosed explicitly (so the unverifiable ones are never silently implied to be missing), and the ✓ only
    /// when the whole profile is genuinely live.</summary>
    public string Label(string profileName)
    {
        if (Total == 0) return $"« {profileName} » ne contient aucun tweak à vérifier.";
        var sb = new StringBuilder($"« {profileName} » : {Applied}/{Total} tweak(s) appliqué(s)");
        if (NotApplied > 0) sb.Append($", {NotApplied} non appliqué(s)");
        if (Indeterminate > 0) sb.Append($", {Indeterminate} indéterminé(s)");
        sb.Append(FullyApplied ? " ✓" : ".");
        return sb.ToString();
    }
}

public sealed class ProfileService : IProfileService
{
    private readonly string _profilesDir;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ProfileService()
    {
        _profilesDir = Environment.ExpandEnvironmentVariables(
            "%LOCALAPPDATA%\\AurumTweaks\\Profiles");
        Directory.CreateDirectory(_profilesDir);
    }

    public async Task<IReadOnlyList<Profile>> LoadProfilesAsync()
    {
        var list = new List<Profile>();
        foreach (var file in Directory.EnumerateFiles(_profilesDir, "*.json"))
        {
            try
            {
                await using var s = File.OpenRead(file);
                var p = await JsonSerializer.DeserializeAsync<Profile>(s, JsonOpts);
                if (p is not null) list.Add(p);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to load profile {File}", file);
            }
        }
        return list;
    }

    public async Task SaveAsync(Profile profile)
    {
        await using var s = File.Create(ProfilePath.For(_profilesDir, profile.Id));
        await JsonSerializer.SerializeAsync(s, profile, JsonOpts);
    }

    public Task DeleteAsync(string profileId)
    {
        var path = ProfilePath.For(_profilesDir, profileId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Profile>> GetBuiltInPresetsAsync()
        => Task.FromResult(ProfilePresets.BuiltIn());
}
