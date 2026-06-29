using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Guards the « Transparence &amp; confiance » disclosure — the download-trust artefact whose whole value is that every
/// line is TRUE. The load-bearing rules, each pinned here:
/// <list type="bullet">
/// <item>the privacy section states there is no server / no telemetry AND discloses where data actually lives, and it
/// frames the only network traffic as user-launched diagnostics (never a false « zéro réseau »);</item>
/// <item>the restore-point line reflects the user's CURRENT setting — when they opted out it says so, never implying a
/// safety net that won't be created (the same honesty as the pre-flight check);</item>
/// <item>the integrity section reports the real gate verdict — « intègre » only when nothing was refused, and a tamper
/// is disclosed as « X fichier(s) refusé(s) », never masked by a green all-clear.</item>
/// </list>
/// Pure arithmetic + fixed promises over <see cref="TransparencyFacts"/>, so no I/O — same precedent as
/// <see cref="EvidenceReport"/> / <see cref="BenchmarkTextReport"/>.
/// </summary>
public class TransparencyReportTests
{
    private static readonly DateTime Generated = new(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);

    // A real lowercase-hex SHA-256 (the digest of the empty input) — a recognisable, well-formed fixture, never a
    // placeholder, so the « shown verbatim » assertions exercise a genuine 64-hex string.
    private const string SampleHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static TransparencyFacts Facts(
        string dataDir = @"C:\Users\x\AppData\Local\AurumTweaks",
        int tranquille = 40, int avance = 30, int extreme = 20,
        int rejected = 0, bool restoreOn = true, BuildIdentityFacts? build = null)
        => new(dataDir, tranquille + avance + extreme, tranquille, avance, extreme, rejected, restoreOn, build);

    private static BuildIdentityFacts Build(
        string version = "1.4.2", string? sha256 = SampleHash,
        ExecutableSignatureState sig = ExecutableSignatureState.Absent,
        string? path = @"C:\Program Files\AurumTweaks\AurumTweaks.exe")
        => new(version, sha256, sig, path);

    private static Tweak OfTier(TweakTier tier) => new() { Tier = tier };

    [Fact]
    public void Render_IsWellFormed_HeaderPlusEverySection()
    {
        var text = TransparencyReport.Render(Facts(), Generated);

        Assert.Contains("Aurum Tweaks — Transparence & confiance", text);
        Assert.Contains("CONFIDENTIALITÉ", text);
        Assert.Contains("RÉVERSIBILITÉ", text);
        Assert.Contains("INTÉGRITÉ DU CATALOGUE", text);
        Assert.Contains("CE QU'AURUM TWEAKS NE FAIT PAS", text);
    }

    // ---- Build identity: which build is this, and how to verify the binary independently ----

    [Fact]
    public void Render_BuildIdentity_ShowsVersionAndFingerprint_AndHowToVerifyAgainstTheOfficialPage()
    {
        var text = TransparencyReport.Render(
            Facts(build: Build(version: "1.4.2", sha256: SampleHash)), Generated);

        Assert.Contains("IDENTITÉ DE L'APPLICATION", text);
        Assert.Contains("Version : 1.4.2", text);
        Assert.Contains(SampleHash, text);                            // the real fingerprint, verbatim
        Assert.Contains("page de téléchargement officielle", text);   // the verify-it-yourself guidance
    }

    [Fact]
    public void Render_BuildIdentity_OffersIndependentVerification_WithTheRealGetFileHashCommand()
    {
        // Download trust hinges on NOT having to take our fingerprint on faith — the disclosure hands over the exact
        // command, aimed at the real binary, so the user recomputes the hash with a tool we don't control.
        const string path = @"C:\Program Files\AurumTweaks\AurumTweaks.exe";
        var text = TransparencyReport.Render(Facts(build: Build(path: path)), Generated);

        Assert.Contains($"Get-FileHash \"{path}\" -Algorithm SHA256", text);   // the real path, ready to copy-run
        Assert.Contains("nous croire sur parole", text);   // the « don't take our word for it » framing
    }

    [Fact]
    public void Render_BuildIdentity_WithoutAPath_OmitsTheVerificationCommand_NeverAGuessedPath()
    {
        var text = TransparencyReport.Render(Facts(build: Build(path: null)), Generated);

        Assert.DoesNotContain("Get-FileHash", text);   // no command pointed at a path we don't actually have
    }

    [Fact]
    public void Render_BuildIdentity_Unsigned_DisclosesItPlainly_NeverImpliesSigned()
    {
        // An unsigned build is the honest current state — say so, and point at the fingerprint. Never let the absence
        // read as a signature the binary doesn't carry.
        var text = TransparencyReport.Render(
            Facts(build: Build(sig: ExecutableSignatureState.Absent)), Generated);

        Assert.Contains("Signature numérique : absente", text);
        Assert.Contains("n'est pas signée", text);
        Assert.DoesNotContain("Signature numérique : présente", text);
    }

    [Fact]
    public void Render_BuildIdentity_SignaturePresent_StatesPresence_ButNeverClaimsValidity()
    {
        // A present signature is reported as present — its cryptographic validity is NOT re-asserted here, so the proof
        // never upgrades to a fabricated « signée et vérifiée ».
        var text = TransparencyReport.Render(
            Facts(build: Build(sig: ExecutableSignatureState.Present)), Generated);

        Assert.Contains("Signature numérique : présente", text);
        Assert.Contains("validité n'est pas revérifiée", text);
        Assert.DoesNotContain("signée et vérifiée", text);
    }

    [Fact]
    public void Render_BuildIdentity_FingerprintUnavailable_SaysIndisponible_FabricatesNoHashNorVerifyGuidance()
    {
        var text = TransparencyReport.Render(
            Facts(build: Build(sha256: null, sig: ExecutableSignatureState.Indeterminate)), Generated);

        Assert.Contains("Empreinte SHA-256 de l'exécutable : indisponible", text);
        Assert.Contains("statut indéterminé", text);
        Assert.DoesNotContain("page de téléchargement officielle", text);   // the verify guidance only rides a real hash
    }

    [Fact]
    public void Render_WithoutBuildIdentity_OmitsTheSection_FabricatesNoVersionOrHash()
    {
        var text = TransparencyReport.Render(Facts(), Generated);   // build defaults to null

        Assert.DoesNotContain("IDENTITÉ DE L'APPLICATION", text);
        Assert.DoesNotContain("Empreinte SHA-256 de l'exécutable", text);
    }

    [Fact]
    public void Render_Privacy_StatesNoServerNoTelemetry_AndDisclosesTheDataDirectory()
    {
        var dir = @"D:\somewhere\AurumTweaks";
        var text = TransparencyReport.Render(Facts(dataDir: dir), Generated);

        Assert.Contains("Aucune télémétrie", text);
        Assert.Contains("aucun serveur", text);
        Assert.Contains(dir, text);   // the real path, not a placeholder
    }

    [Fact]
    public void Render_Privacy_FramesNetworkAsUserLaunchedDiagnostics_NotAFalseZeroNetwork()
    {
        // The DNS/latency pages DO open sockets, so an honest disclosure can't claim « zéro réseau » — it names the
        // user-launched diagnostics and says plainly they go to the user's target, never to us.
        var text = TransparencyReport.Render(Facts(), Generated);

        Assert.Contains("ping", text);
        Assert.Contains("DNS", text);
        Assert.Contains("jamais vers nous", text);
    }

    [Fact]
    public void Render_Privacy_OmitsTheDataDirLine_WhenPathUnknown()
    {
        var text = TransparencyReport.Render(Facts(dataDir: ""), Generated);

        Assert.DoesNotContain("restent en local, sous", text);   // no empty « sous :  » dangling line
    }

    [Fact]
    public void Render_Reversibility_RestorePointOn_StatesItIsActive()
    {
        var text = TransparencyReport.Render(Facts(restoreOn: true), Generated);

        Assert.Contains("point de restauration", text);
        Assert.Contains("ACTIVE", text);
        Assert.Contains("Tout rétablir", text);
    }

    [Fact]
    public void Render_Reversibility_RestorePointOff_SaysSoHonestly_NeverImpliesANet()
    {
        // Honesty: a user who turned the option off must read that NO point will be created — never a vague green.
        var text = TransparencyReport.Render(Facts(restoreOn: false), Generated);

        Assert.Contains("DÉSACTIVÉ", text);
        Assert.Contains("aucun ne", text);     // « aucun ne sera créé »
        Assert.DoesNotContain("actuellement ACTIVE", text);
    }

    [Fact]
    public void Render_Integrity_DescribesTheSha256Gate_AndCountsThisCatalog()
    {
        var text = TransparencyReport.Render(Facts(tranquille: 40, avance: 30, extreme: 20), Generated);

        Assert.Contains("SHA-256", text);
        Assert.Contains("REFUSÉ", text);                 // the gate's behaviour is described
        Assert.Contains("90 tweak(s)", text);            // 40 + 30 + 20, the real total
        Assert.Contains("40 Tranquille", text);
        Assert.Contains("30 Avancé", text);
        Assert.Contains("20 Extrême", text);
    }

    [Fact]
    public void Render_Integrity_Intact_SaysIntegre()
    {
        var text = TransparencyReport.Render(Facts(rejected: 0), Generated);

        Assert.Contains("État : intègre", text);
    }

    [Fact]
    public void Render_Integrity_Tampered_DisclosesTheRefusedCount_NotAGreenIntegre()
    {
        // A dropped/edited file was refused — the disclosure must SAY so, never claim the catalog is « intègre ».
        var text = TransparencyReport.Render(Facts(rejected: 2), Generated);

        Assert.Contains("2 fichier(s) REFUSÉ(s)", text);
        Assert.DoesNotContain("État : intègre", text);
    }

    [Fact]
    public void Render_NonCapabilities_PinsTheHonestyRefusals()
    {
        var text = TransparencyReport.Render(Facts(), Generated);

        Assert.Contains("Aucun pilote noyau", text);
        Assert.Contains("vBIOS", text);
        Assert.Contains("NVRAM", text);
        Assert.Contains("Aucune mesure inventée", text);
    }

    [Fact]
    public void Render_FooterAlwaysStates_LocalOnly_AndNoServer()
    {
        foreach (var restoreOn in new[] { true, false })
        {
            var text = TransparencyReport.Render(Facts(restoreOn: restoreOn), Generated);
            Assert.Contains("jamais envoyé", text);
            Assert.Contains("n'a pas de serveur", text);
        }
    }

    [Fact]
    public void Derive_CountsTheCatalogByTier_AndTotals()
    {
        var catalog = new List<Tweak>
        {
            OfTier(TweakTier.Tranquille), OfTier(TweakTier.Tranquille),
            OfTier(TweakTier.Avance),
            OfTier(TweakTier.Extreme), OfTier(TweakTier.Extreme), OfTier(TweakTier.Extreme),
        };

        var facts = TransparencyFacts.Derive(catalog, Array.Empty<string>(), restorePointPolicyOn: true, "dir");

        Assert.Equal(6, facts.TweakCount);
        Assert.Equal(2, facts.TranquilleCount);
        Assert.Equal(1, facts.AvanceCount);
        Assert.Equal(3, facts.ExtremeCount);
        Assert.True(facts.CatalogIntegrityIntact);
    }

    [Fact]
    public void Derive_WithRejectedFiles_FlipsTheIntegrityFlag_AndCarriesTheCount()
    {
        var rejected = new[] { "extreme/evil.json [Unknown]", "advanced/good.json [Tampered]" };

        var facts = TransparencyFacts.Derive(
            new[] { OfTier(TweakTier.Tranquille) }, rejected, restorePointPolicyOn: false, "dir");

        Assert.Equal(2, facts.RejectedCatalogFileCount);
        Assert.False(facts.CatalogIntegrityIntact);
        Assert.False(facts.RestorePointPolicyOn);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(5, false)]
    public void CatalogIntegrityIntact_IsTrue_OnlyWhenNothingWasRefused(int rejected, bool expected)
        => Assert.Equal(expected, Facts(rejected: rejected).CatalogIntegrityIntact);
}
