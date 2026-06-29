using System;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the SHAPE of <see cref="BiosTextReport.Render"/> — the human-readable « est-ce que mon BIOS est bien réglé ? »
/// report a user copies or exports to a forum/Discord (the readable counterpart to the on-page advisor cards). Honesty
/// contract: the per-state counts ride in the header; recommendations are grouped under the SAME state the advisor
/// detected; a <see cref="BiosCheckState.Verify"/> setting lands under its OWN « À VÉRIFIER » heading and is NEVER
/// folded into « À CHANGER » or « DÉJÀ OK »; an empty group prints no heading (no « (0) » phantom work); empty optional
/// rows are dropped rather than printed as bare labels; a null report (scan unfinished) says so; and the load-bearing
/// footer keeps « À VÉRIFIER » ≠ confirmed, « les chemins sont indicatifs », « Aurum ne modifie aucun réglage BIOS à ta
/// place », and « il a pu changer depuis ». Pure (no I/O); the clipboard copy / file write is untested glue. Counts are
/// small integers, so the asserted text is locale-independent.
/// </summary>
public class BiosTextReportTests
{
    private static readonly DateTime FixedUtc = new(2026, 1, 2, 3, 4, 0, DateTimeKind.Utc);

    private static string Render(BiosAdvisorReport? report, TweakTier tier = TweakTier.Tranquille)
        => BiosTextReport.Render(report, tier, FixedUtc);

    private static BiosRecommendation Rec(
        string name,
        BiosCheckState state,
        string category = "Platform",
        string detected = "",
        string conseil = "Active-le.",
        string vendorPath = "Advanced > X",
        string vendorAlias = "",
        string gain = "",
        string validation = "",
        string notes = "",
        RiskLevel risk = RiskLevel.None)
        => new()
        {
            Setting = new BiosSetting
            {
                Name = name,
                Category = category,
                ExpectedGain = gain,
                ValidationTool = validation,
                Notes = notes,
                Risk = risk
            },
            State = state,
            DetectedStateText = detected,
            VendorPath = vendorPath,
            VendorAlias = vendorAlias,
            TierRecommendation = conseil
        };

    // Counts mirror the recs so the report is internally honest (the same totals the advisor computes).
    private static BiosAdvisorReport Report(string platform, params BiosRecommendation[] recs)
        => new()
        {
            PlatformSummary = platform,
            Recommendations = recs.ToList(),
            ActionNeededCount = recs.Count(r => r.State == BiosCheckState.ActionNeeded),
            VerifyCount = recs.Count(r => r.State == BiosCheckState.Verify),
            OptimalCount = recs.Count(r => r.State == BiosCheckState.Optimal)
        };

    // --- Header ---

    [Fact]
    public void Header_CarriesTitle_GeneratedTime_Platform_AndCounts()
    {
        var text = Render(Report("Ryzen 7 7800X3D  ·  X670E  ·  32 GB DDR5-6000",
            Rec("a", BiosCheckState.ActionNeeded),
            Rec("v", BiosCheckState.Verify),
            Rec("o", BiosCheckState.Optimal)));

        Assert.Contains("Aurum Tweaks — Configuration & conseils BIOS", text);
        Assert.Contains("Généré le ", text);
        Assert.Contains("Profil détecté : Ryzen 7 7800X3D  ·  X670E  ·  32 GB DDR5-6000", text);
        // Header carries the totals — proves they travel even where a 0-row group is omitted elsewhere.
        Assert.Contains("1 à changer · 1 à vérifier · 1 déjà OK", text);
    }

    [Theory]
    [InlineData(TweakTier.Tranquille, "Tranquille")]
    [InlineData(TweakTier.Avance, "Avancé")]
    [InlineData(TweakTier.Extreme, "Extrême")]
    public void Header_ShowsSelectedTierLabel(TweakTier tier, string label)
    {
        var text = Render(Report("PC", Rec("x", BiosCheckState.Verify)), tier);
        Assert.Contains($"Niveau de recommandation : {label}", text);
    }

    [Fact]
    public void EmptyPlatformSummary_SaysMaterielNonIdentifie()
    {
        var text = Render(Report("", Rec("x", BiosCheckState.Verify)));
        Assert.Contains("Profil détecté : matériel non identifié", text);
    }

    // --- Null / empty ---

    [Fact]
    public void NullReport_SaysScanNotFinished_WithoutFabricatingHeadings()
    {
        var text = Render(null);
        Assert.Contains("Détection matérielle pas encore terminée", text);
        Assert.DoesNotContain("À CHANGER (", text);
        Assert.DoesNotContain("À VÉRIFIER (", text);
    }

    [Fact]
    public void NoRecommendations_SaysSo_WithoutFabricatingSections()
    {
        var text = Render(Report("PC détecté"));
        Assert.Contains("Aucune recommandation BIOS applicable au matériel détecté.", text);
        Assert.DoesNotContain("À CHANGER (", text);
        Assert.DoesNotContain("À VÉRIFIER (", text);   // also guards the footer token (returns before the footer)
    }

    // --- Grouping by state ---

    [Fact]
    public void ActionNeeded_ListedUnderAChanger_WithDetected_Conseil_AndWhere()
    {
        var text = Render(Report("PC", Rec("Resizable BAR", BiosCheckState.ActionNeeded,
            category: "Platform",
            detected: "Resizable BAR est DÉSACTIVÉ.",
            conseil: "Active Above 4G + ReBAR.",
            vendorPath: "Advanced > PCI > Above 4G Decoding")));

        Assert.Contains("À CHANGER (1) :", text);
        Assert.Contains("  • Resizable BAR — Platform", text);
        Assert.Contains("Détecté", text);
        Assert.Contains("Resizable BAR est DÉSACTIVÉ.", text);
        Assert.Contains("Conseil", text);
        Assert.Contains("Active Above 4G + ReBAR.", text);
        Assert.Contains("Où", text);
        Assert.Contains("Advanced > PCI > Above 4G Decoding", text);
    }

    [Fact]
    public void Verify_GetsItsOwnHeading_NeverFoldedIntoActionNeededOrOptimal()
    {
        // One ActionNeeded + one Verify. The Verify row must sit under À VÉRIFIER (rendered after À CHANGER), and the
        // ActionNeeded row must sit in the À CHANGER section ABOVE that heading — never merged into one bucket.
        var text = Render(Report("PC",
            Rec("Aaa", BiosCheckState.ActionNeeded),
            Rec("Zzz", BiosCheckState.Verify)));

        Assert.Contains("À VÉRIFIER (1) :", text);

        var actionHeading = text.IndexOf("À CHANGER (1) :", StringComparison.Ordinal);
        var verifyHeading = text.IndexOf("À VÉRIFIER (1) :", StringComparison.Ordinal);
        var actionRow = text.IndexOf("  • Aaa", StringComparison.Ordinal);
        var verifyRow = text.IndexOf("  • Zzz", StringComparison.Ordinal);

        Assert.True(actionHeading >= 0 && verifyHeading >= 0 && actionRow >= 0 && verifyRow >= 0);
        Assert.True(actionRow > actionHeading && actionRow < verifyHeading, "ActionNeeded row belongs to the À CHANGER section");
        Assert.True(verifyRow > verifyHeading, "Verify row belongs to the À VÉRIFIER section");
    }

    [Fact]
    public void Optimal_ListedUnderDejaOk()
    {
        var text = Render(Report("PC", Rec("Secure Boot", BiosCheckState.Optimal)));
        Assert.Contains("DÉJÀ OK (1) :", text);
        Assert.Contains("  • Secure Boot — Platform", text);
    }

    [Fact]
    public void UnknownState_ListedUnderGuide()
    {
        // The advisor doesn't currently emit Unknown, but the model allows it — it must still have a home (its own
        // GUIDE heading), never be silently dropped.
        var text = Render(Report("PC", Rec("Fan curve", BiosCheckState.Unknown, category: "Fan")));
        Assert.Contains("GUIDE (1) :", text);
        Assert.Contains("  • Fan curve — Fan", text);
    }

    // --- Section omission + counts ---

    [Fact]
    public void EmptyGroup_IsOmitted_NoZeroRowHeading()
    {
        // Only ActionNeeded entries → no other heading may appear. The footer quotes « À VÉRIFIER », so the absence
        // assertion targets the heading-with-count token "À VÉRIFIER (" which the footer never contains.
        var text = Render(Report("PC", Rec("a", BiosCheckState.ActionNeeded)));
        Assert.Contains("À CHANGER (1) :", text);
        Assert.DoesNotContain("À VÉRIFIER (", text);
        Assert.DoesNotContain("DÉJÀ OK (", text);
        Assert.DoesNotContain("GUIDE (", text);
    }

    [Fact]
    public void Counts_InHeadings_ReflectEachGroupSize()
    {
        // Distinct counts per bucket so each heading token is unambiguous.
        var text = Render(Report("PC",
            Rec("a1", BiosCheckState.ActionNeeded), Rec("a2", BiosCheckState.ActionNeeded),
            Rec("v1", BiosCheckState.Verify), Rec("v2", BiosCheckState.Verify), Rec("v3", BiosCheckState.Verify),
            Rec("o1", BiosCheckState.Optimal)));

        Assert.Contains("À CHANGER (2) :", text);
        Assert.Contains("À VÉRIFIER (3) :", text);
        Assert.Contains("DÉJÀ OK (1) :", text);
        Assert.Contains("2 à changer · 3 à vérifier · 1 déjà OK", text);
    }

    // --- Per-row honesty ---

    [Fact]
    public void CriticalSetting_GetsHardwareSafetyFlag_NonCriticalDoesNot()
    {
        var critical = Render(Report("PC", Rec("VSOC", BiosCheckState.Verify, risk: RiskLevel.HardwareDamage)));
        Assert.Contains("⚠ SÉCURITÉ HARDWARE", critical);

        var ordinary = Render(Report("PC", Rec("EXPO", BiosCheckState.ActionNeeded, risk: RiskLevel.Low)));
        Assert.DoesNotContain("⚠ SÉCURITÉ HARDWARE", ordinary);
    }

    [Fact]
    public void EmptyOptionalRows_AreOmitted_NotPrintedAsBareLabels()
    {
        // Only Conseil + Où are set; detected/alias/gain/validation/notes stay empty and must not print their labels.
        var text = Render(Report("PC", Rec("Secure Boot", BiosCheckState.ActionNeeded)));
        Assert.Contains("Conseil", text);
        Assert.Contains("Où", text);
        // Case-sensitive: the row label is « Détecté » (capital D); the header's « Profil détecté » is lower-case, so a
        // genuinely-absent detected row is proven without colliding with the header.
        Assert.DoesNotContain("Détecté", text);
        Assert.DoesNotContain("Alias", text);
        Assert.DoesNotContain("Gain", text);
        Assert.DoesNotContain("Validation", text);
        Assert.DoesNotContain("Note", text);
    }

    // --- Footer caveat (load-bearing) ---

    [Fact]
    public void Footer_KeepsTheLoadBearingCaveats()
    {
        var text = Render(Report("PC", Rec("x", BiosCheckState.Verify)));
        Assert.Contains("PAS un état confirmé", text);                       // « À VÉRIFIER » is not a confirmed state
        Assert.Contains("les chemins sont indicatifs", text);               // menu names vary by vendor/version
        Assert.Contains("Aurum ne modifie aucun réglage BIOS à ta place", text); // advisory only — never applied for you
        Assert.Contains("il a pu changer depuis", text);                    // state is historical, may have drifted
    }
}
