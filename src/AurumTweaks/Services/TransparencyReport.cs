using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Whether the running executable carries an embedded Authenticode signature. Deliberately a THREE-state answer, not a
/// bool: the probe distinguishes « aucune signature » from « impossible à déterminer », and it NEVER promotes a present
/// signature to « vérifiée » — its cryptographic validity isn't re-checked here, so the SHA-256 fingerprint stays the
/// reference. Fail-safe: anything the probe can't establish is <see cref="Indeterminate"/>, never assumed signed.
/// </summary>
public enum ExecutableSignatureState
{
    /// <summary>The probe could not determine the state (a read error, a missing file). Disclosed as such.</summary>
    Indeterminate,

    /// <summary>No embedded Authenticode signature — the honest state for an unsigned build.</summary>
    Absent,

    /// <summary>An embedded signature is present. Its validity is NOT re-verified here; the disclosure says « présente »,
    /// never « vérifiée ».</summary>
    Present
}

/// <summary>
/// The running build's own identity — the facts a sceptical downloader needs to confirm the binary is the published one:
/// its version, the SHA-256 of the executable on disk (to compare against the official download page), and whether it
/// carries a digital signature. Plain values gathered by the I/O probe (<see cref="BuildIdentity"/>) and rendered by the
/// pure <see cref="TransparencyReport"/>; a null hash is an honest « indisponible », never a placeholder.
/// </summary>
public sealed record BuildIdentityFacts(
    string AppVersion,
    string? ExecutableSha256,
    ExecutableSignatureState ExecutableSignature,
    string? ExecutablePath = null);

/// <summary>
/// The handful of REAL, locally-derived facts the transparency disclosure reports about THIS install — never invented
/// marketing numbers. Each field is read from a genuine source: the loaded catalog (counts per tier), the integrity
/// gate's own verdict (<see cref="ITweakRepository.RejectedFiles"/>), the user's restore-point setting, and the data
/// directory. Kept as plain values so the disclosure (<see cref="TransparencyReport"/>) is rendered — and pinned by
/// tests — without touching the registry, the disk, or the settings store: the project's « test the decision, not the
/// world » pattern.
/// </summary>
public sealed record TransparencyFacts(
    string DataDirectory,
    int TweakCount,
    int TranquilleCount,
    int AvanceCount,
    int ExtremeCount,
    int RejectedCatalogFileCount,
    bool RestorePointPolicyOn,
    BuildIdentityFacts? BuildIdentity = null)
{
    /// <summary>The integrity gate refused nothing ⇒ every tweak file on disk matched the SHA-256 manifest baked into
    /// this build. Drives the « intègre » vs « X fichier(s) refusé(s) » line — a tamper is disclosed, never masked.</summary>
    public bool CatalogIntegrityIntact => RejectedCatalogFileCount == 0;

    /// <summary>Count the loaded catalog by tier and fold in the live integrity / restore-point signals. Pure: the
    /// caller supplies the already-loaded catalog and the gate's rejected list, so this stays unit-testable.</summary>
    public static TransparencyFacts Derive(
        IReadOnlyList<Tweak> catalog,
        IReadOnlyList<string> rejectedCatalogFiles,
        bool restorePointPolicyOn,
        string dataDirectory,
        BuildIdentityFacts? buildIdentity = null)
        => new(
            dataDirectory,
            catalog.Count,
            catalog.Count(t => t.Tier == TweakTier.Tranquille),
            catalog.Count(t => t.Tier == TweakTier.Avance),
            catalog.Count(t => t.Tier == TweakTier.Extreme),
            rejectedCatalogFiles.Count,
            restorePointPolicyOn,
            buildIdentity);
}

/// <summary>
/// Pure renderer for the « Transparence &amp; confiance » disclosure — the plain-text answer to the only honest reaction
/// to a PC optimizer downloaded from the internet: « pourquoi te ferais-je confiance ? ». It folds the app's
/// load-bearing promises (no telemetry, full reversibility, a fail-closed catalog integrity gate, an explicit list of
/// what it refuses to do) together with the REAL <see cref="TransparencyFacts"/> of this install, into one paste the
/// user can read on the page, export, or hand to a sceptical reviewer.
/// <list type="bullet">
/// <item>Every promise here mirrors a genuine boundary in the code — « aucun serveur » because there is no HttpClient,
/// « entièrement réversible » because apply/revert are tested inverses, « fichier refusé » because the loader hashes
/// each tweak against a baked-in manifest. If one line were false it would be a bug to fix, not a slogan.</item>
/// <item>The restore-point line reflects the user's CURRENT setting — it never claims a safety net they opted out of,
/// the same honesty as the pre-flight check.</item>
/// <item>A tampered catalog is disclosed (« X fichier(s) refusé(s) »), never papered over with a green « intègre ».</item>
/// </list>
/// House style matches <see cref="EvidenceReport"/> / <see cref="SystemReport"/> (header, '='/'-' rules, the « généré
/// localement et jamais envoyé » footer); the clipboard / file write is thin glue in the page's ViewModel.
/// </summary>
public static class TransparencyReport
{
    private const int Width = 48;

    public static string Render(TransparencyFacts facts, DateTime generatedUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Transparence & confiance");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(new string('=', Width));
        sb.AppendLine();
        sb.AppendLine("Un optimiseur PC téléchargé sur Internet : tu as raison de te méfier. Voici, sans détour, quelle");
        sb.AppendLine("version tu exécutes et comment vérifier le binaire toi-même, ce qu'Aurum Tweaks fait de tes");
        sb.AppendLine("données, ce que tu peux annuler, et ce qu'il refuse de faire.");

        // Identity first — « qui es-tu, et comment je vérifie que c'est bien toi » — only when the probe supplied it
        // (a pure render without build facts omits the section rather than inventing a version or hash).
        if (facts.BuildIdentity is not null) AppendBuildIdentity(sb, facts.BuildIdentity);
        AppendPrivacy(sb, facts);
        AppendReversibility(sb, facts);
        AppendIntegrity(sb, facts);
        AppendNonCapabilities(sb);

        sb.AppendLine();
        sb.AppendLine(new string('-', Width));
        sb.AppendLine("Document généré localement et jamais envoyé — Aurum Tweaks n'a pas de serveur, il n'y a");
        sb.AppendLine("personne à qui transmettre quoi que ce soit. Chaque ligne ci-dessus correspond à une limite");
        sb.AppendLine("réelle du code, vérifiable : si une seule était fausse, ce serait un bug à corriger.");
        return sb.ToString();
    }

    // The binary's own identity. The catalog-integrity gate (below) only means something if the BINARY itself is
    // authentic — a user who can swap the assembly swaps the baked manifest too. So this section hands the user the one
    // primitive that needs no trust in us: the SHA-256 of the executable they actually launched, to match against the
    // hash published on the official download page. A missing hash is « indisponible », never a placeholder; a present
    // signature is « présente » (validity not re-asserted here), an absent one is disclosed plainly rather than implied.
    private static void AppendBuildIdentity(StringBuilder sb, BuildIdentityFacts id)
    {
        sb.AppendLine();
        sb.AppendLine("IDENTITÉ DE L'APPLICATION — quelle version, et comment vérifier le binaire");
        sb.AppendLine($"  - Version : {id.AppVersion}");
        if (!string.IsNullOrWhiteSpace(id.ExecutableSha256))
        {
            sb.AppendLine($"  - Empreinte SHA-256 de l'exécutable : {id.ExecutableSha256}");
            sb.AppendLine("    Compare-la à celle publiée sur la page de téléchargement officielle : si elles");
            sb.AppendLine("    correspondent, le fichier que tu as lancé est exactement celui qui a été publié, octet");
            sb.AppendLine("    pour octet — aucune confiance en nous requise, juste une comparaison que tu fais toi-même.");
        }
        else
        {
            sb.AppendLine("  - Empreinte SHA-256 de l'exécutable : indisponible (le binaire n'a pas pu être lu).");
        }

        // The crux of download trust: don't take OUR word for the fingerprint above (a tampered build could print
        // anything) — recompute it yourself with a tool we don't control, then compare to the official page. We hand
        // over the exact command, pointed at the real binary, so there's nothing to look up.
        if (!string.IsNullOrWhiteSpace(id.ExecutablePath))
        {
            sb.AppendLine("    Tu n'as pas à nous croire sur parole : recalcule-la toi-même — dans PowerShell :");
            sb.AppendLine($"      Get-FileHash \"{id.ExecutablePath}\" -Algorithm SHA256");
        }
        sb.AppendLine(id.ExecutableSignature switch
        {
            ExecutableSignatureState.Present
                => "  - Signature numérique : présente. Sa validité n'est pas revérifiée ici — l'empreinte ci-dessus reste la référence.",
            ExecutableSignatureState.Absent
                => "  - Signature numérique : absente. Cette version n'est pas signée — vérifie l'authenticité par l'empreinte ci-dessus.",
            _
                => "  - Signature numérique : statut indéterminé — réfère-toi à l'empreinte ci-dessus.",
        });
    }

    // No phone-home: there is no HttpClient anywhere in the app. The only sockets opened are user-launched diagnostics
    // (ping / traceroute / DNS resolver test) toward a target the user picks — so the honest claim is « rien vers nous »,
    // never a blanket « zéro réseau » that the DNS/latency pages would quietly contradict.
    private static void AppendPrivacy(StringBuilder sb, TransparencyFacts facts)
    {
        sb.AppendLine();
        sb.AppendLine("CONFIDENTIALITÉ — aucune donnée ne quitte ta machine");
        sb.AppendLine("  - Aucune télémétrie, aucun compte, aucun serveur, aucun mouchard : rien n'est transmis, jamais.");
        sb.AppendLine("  - Les seules communications réseau sont des diagnostics que TU lances (ping, traceroute, test");
        sb.AppendLine("    de résolveur DNS) vers la cible de TON choix — jamais vers nous, et seulement à ta demande.");
        if (!string.IsNullOrWhiteSpace(facts.DataDirectory))
            sb.AppendLine($"  - Réglages, journaux et profils restent en local, sous : {facts.DataDirectory}");
    }

    private static void AppendReversibility(StringBuilder sb, TransparencyFacts facts)
    {
        sb.AppendLine();
        sb.AppendLine("RÉVERSIBILITÉ — rien n'est gravé dans le marbre");
        sb.AppendLine("  - Chaque tweak a son inverse exact : « Appliquer » et « Rétablir » sont symétriques, un par");
        sb.AppendLine("    un ou via « Tout rétablir » sur la page Tweaks.");
        sb.AppendLine(facts.RestorePointPolicyOn
            ? "  - Un point de restauration Windows est créé avant une application groupée — option actuellement"
              + " ACTIVE (sauf si Windows en a déjà créé un de moins de 24 h)."
            : "  - Point de restauration avant application : actuellement DÉSACTIVÉ dans les Paramètres — aucun ne"
              + " sera créé, c'est dit clairement plutôt que sous-entendu.");
        sb.AppendLine("  - Le journal des modifications garde la trace de chaque changement, exportable à tout moment.");
    }

    // The actual security control, stated plainly: because every operation runs AS ADMIN, a writable catalog would be
    // an escalation path — so the loader trusts only an exact SHA-256 match and refuses anything else. The counts and
    // the intact/refused line come straight from the gate, so the page can't claim a clean catalog it didn't verify.
    private static void AppendIntegrity(StringBuilder sb, TransparencyFacts facts)
    {
        sb.AppendLine();
        sb.AppendLine("INTÉGRITÉ DU CATALOGUE — chaque tweak est vérifié avant de s'exécuter");
        sb.AppendLine("  - Les tweaks agissent sur le Registre, les services, les tâches planifiées et des réglages");
        sb.AppendLine("    système, appliqués en tant qu'administrateur — donc l'origine de chaque fichier compte.");
        sb.AppendLine("  - Avant d'être chargé, chaque fichier de tweak est comparé à une empreinte SHA-256 figée dans");
        sb.AppendLine("    l'application. Un fichier ajouté ou modifié est REFUSÉ : ses opérations ne s'exécutent jamais.");
        sb.AppendLine($"  - Catalogue chargé : {facts.TweakCount} tweak(s) — "
            + $"{facts.TranquilleCount} Tranquille, {facts.AvanceCount} Avancé, {facts.ExtremeCount} Extrême.");
        sb.AppendLine(facts.CatalogIntegrityIntact
            ? "  - État : intègre — tous les fichiers correspondent à l'empreinte de référence."
            : $"  - État : {facts.RejectedCatalogFileCount} fichier(s) REFUSÉ(s) (empreinte non conforme) — "
              + "non chargé(s), donc sans effet.");
    }

    // The honesty mandate, made auditable: the things the app deliberately will NOT do, so « ce n'est pas dans le
    // produit » is a written promise rather than a gap a user has to discover.
    private static void AppendNonCapabilities(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("CE QU'AURUM TWEAKS NE FAIT PAS (par conception)");
        sb.AppendLine("  - Aucun pilote noyau (ring-0), aucun module signé installé en arrière-plan.");
        sb.AppendLine("  - Aucune écriture firmware : ni flash de BIOS, ni vBIOS, ni NVRAM.");
        sb.AppendLine("  - Aucune écriture matérielle directe (tensions, VRM) ; les réglages GPU passent par les API");
        sb.AppendLine("    du fabricant (NVAPI/ADL) quand elles existent, jamais par un accès matériel brut.");
        sb.AppendLine("  - Aucune mesure inventée, aucun badge « vérifié » ou « sûr » factice, aucun bouton mort.");
        sb.AppendLine("  - Aucun logiciel tiers embarqué, aucune publicité.");
    }
}
