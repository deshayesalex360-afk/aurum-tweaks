using System;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the SHAPE of <see cref="SnapshotReport.Render"/> — the plain-text drift report the user copies or exports.
/// Faithful by construction: every case feeds a REAL <see cref="SnapshotDiff.Compare"/> result, so the test proves
/// the report lays out exactly what the diff classified (same buckets, same honest wording, same order) and never
/// re-derives or embellishes an outcome. A section heading appears ONLY when its bucket has rows — an empty heading
/// would imply a category of change that didn't happen. Pure (no I/O); the file write / clipboard copy is untested glue.
/// </summary>
public class SnapshotReportTests
{
    private static SnapshotEntry E(string id, TweakAppliedState state, string? name = null)
        => new(id, name ?? id, state);

    private static SystemSnapshot Snap(params SnapshotEntry[] entries)
        => new() { Entries = entries.ToList() };

    private static readonly DateTime FixedUtc = new(2026, 1, 2, 3, 4, 0, DateTimeKind.Utc);

    private static string Render(SystemSnapshot baseline, SystemSnapshot current, string label = "réf", string target = "maintenant")
        => SnapshotReport.Render(SnapshotDiff.Compare(baseline, current), label, target, FixedUtc);

    [Fact]
    public void Header_CarriesTitle_BaselineLabel_NowArrow_AndTheSummary()
    {
        // No change → the report still has the full header and the honest "no change" summary line.
        var text = Render(
            Snap(E("t", TweakAppliedState.Applied)),
            Snap(E("t", TweakAppliedState.Applied)),
            "avant MAJ Windows");

        Assert.Contains("Aurum Tweaks — Comparaison d'instantané", text);
        Assert.Contains("Généré le ", text);
        Assert.Contains("Référence : avant MAJ Windows → maintenant", text);
        Assert.Contains("Aucun changement depuis l'instantané de référence.", text);
    }

    [Fact]
    public void Header_HonoursACustomTargetLabel_NotJustMaintenant()
    {
        // When two SAVED snapshots are compared (historical A → B), the report must name B, not pretend it's "now".
        var text = Render(
            Snap(E("t", TweakAppliedState.Applied)),
            Snap(E("t", TweakAppliedState.NotApplied)),
            label: "avant MAJ", target: "après MAJ");

        Assert.Contains("Référence : avant MAJ → après MAJ", text);
        Assert.DoesNotContain("→ maintenant", text);
    }

    [Fact]
    public void CrossVersionCaveat_AppearsForDifferingVersions_NamingBothInArrowOrder()
    {
        // Two sides captured by different builds → the report warns a difference may be a version change, not a real
        // drift. The version arrow mirrors the « Référence : A → B » order.
        var text = SnapshotReport.Render(
            SnapshotDiff.Compare(Snap(E("t", TweakAppliedState.Applied)), Snap(E("t", TweakAppliedState.NotApplied))),
            "avant", "après", FixedUtc, baselineVersion: "0.1.0", targetVersion: "0.2.0");

        Assert.Contains("versions différentes (0.1.0 → 0.2.0)", text);
        Assert.Contains("pas d'une dérive réelle", text);
    }

    [Fact]
    public void NoCaveat_WhenVersionsMatch_OrAreUnknown()
    {
        var diff = SnapshotDiff.Compare(Snap(E("t", TweakAppliedState.Applied)), Snap(E("t", TweakAppliedState.NotApplied)));

        // Same build on both sides → no version warning.
        Assert.DoesNotContain("versions différentes",
            SnapshotReport.Render(diff, "a", "b", FixedUtc, baselineVersion: "0.1.0", targetVersion: "0.1.0"));
        // The default call (no versions supplied — e.g. older snapshots) stays exactly as before: no caveat.
        Assert.DoesNotContain("versions différentes", Render(
            Snap(E("t", TweakAppliedState.Applied)), Snap(E("t", TweakAppliedState.NotApplied))));
    }

    [Fact]
    public void RegressionRow_RendersName_BracketedId_AndTheTransition()
    {
        var text = Render(
            Snap(E("t", TweakAppliedState.Applied, "Mon Tweak")),
            Snap(E("t", TweakAppliedState.NotApplied, "Mon Tweak")));

        Assert.Contains("RÉGRESSIONS (étaient appliqués, ne le sont plus) :", text);
        Assert.Contains("  - Mon Tweak [t] : Appliqué → Non appliqué", text);
    }

    [Fact]
    public void NoChange_HasSummaryButNoSectionHeadings()
    {
        // A clean comparison must not print any bucket heading — that would imply a change that didn't occur.
        var text = Render(
            Snap(E("t", TweakAppliedState.Applied)),
            Snap(E("t", TweakAppliedState.Applied)));

        Assert.DoesNotContain("RÉGRESSIONS", text);
        Assert.DoesNotContain("INCERTAINS", text);
        Assert.DoesNotContain("AMÉLIORATIONS", text);
        Assert.DoesNotContain("AJOUTÉS", text);
        Assert.DoesNotContain("RETIRÉS", text);
    }

    [Fact]
    public void OnlyRegression_OmitsEveryOtherBucketHeading()
    {
        var text = Render(
            Snap(E("t", TweakAppliedState.Applied)),
            Snap(E("t", TweakAppliedState.NotApplied)));

        Assert.Contains("RÉGRESSIONS", text);          // the one bucket with a row
        Assert.DoesNotContain("INCERTAINS", text);
        Assert.DoesNotContain("AMÉLIORATIONS", text);
        Assert.DoesNotContain("AJOUTÉS", text);
        Assert.DoesNotContain("RETIRÉS", text);
    }

    [Fact]
    public void UncertainRow_RendersTheIndeterminateTransition_NotAsARegression()
    {
        // We knew it was on, can't read it back now → "uncertain", never folded into the regression section.
        var text = Render(
            Snap(E("u", TweakAppliedState.Applied)),
            Snap(E("u", TweakAppliedState.Indeterminate)));

        Assert.Contains("INCERTAINS (un état n'a pas pu être relu — non comptés comme régression) :", text);
        Assert.Contains("  - u [u] : Appliqué → Indéterminé", text);
        Assert.DoesNotContain("RÉGRESSIONS", text);
    }

    [Fact]
    public void AddedAndRemoved_RenderAbsentTransitions_AddedBeforeRemoved()
    {
        var text = Render(
            Snap(E("gone", TweakAppliedState.Applied)),
            Snap(E("added", TweakAppliedState.Applied)));

        Assert.Contains("AJOUTÉS AU CATALOGUE depuis la référence :", text);
        Assert.Contains("  - added [added] : Absent → Appliqué", text);
        Assert.Contains("RETIRÉS DU CATALOGUE depuis la référence :", text);
        Assert.Contains("  - gone [gone] : Appliqué → Absent", text);
        Assert.True(text.IndexOf("AJOUTÉS", StringComparison.Ordinal) < text.IndexOf("RETIRÉS", StringComparison.Ordinal));
    }

    [Fact]
    public void Sections_AppearInFixedOrder_RegressionsThenUncertainThenImprovements()
    {
        // The report's order mirrors the page so the file reads like the panel. One row in each of the three
        // state buckets lets us pin the relative order deterministically.
        var text = Render(
            Snap(E("reg", TweakAppliedState.Applied),
                 E("unc", TweakAppliedState.Applied),
                 E("imp", TweakAppliedState.NotApplied)),
            Snap(E("reg", TweakAppliedState.NotApplied),
                 E("unc", TweakAppliedState.Indeterminate),
                 E("imp", TweakAppliedState.Applied)));

        var reg = text.IndexOf("RÉGRESSIONS", StringComparison.Ordinal);
        var unc = text.IndexOf("INCERTAINS", StringComparison.Ordinal);
        var imp = text.IndexOf("AMÉLIORATIONS", StringComparison.Ordinal);
        Assert.True(reg >= 0 && unc >= 0 && imp >= 0);
        Assert.True(reg < unc, "Regressions must precede Uncertain");
        Assert.True(unc < imp, "Uncertain must precede Improvements");
    }
}
