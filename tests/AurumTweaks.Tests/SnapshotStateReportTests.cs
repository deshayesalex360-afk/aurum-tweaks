using System;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the SHAPE of <see cref="SnapshotStateReport.Render"/> — the human-readable single-snapshot state report a user
/// copies or exports to show « voici l'état exact de mes tweaks » (the readable counterpart to the machine-readable
/// JSON export). Honesty contract: the per-state counts ride in the header; tweaks are grouped under the SAME tri-state
/// the page detected; an <see cref="TweakAppliedState.Indeterminate"/> tweak lands under its OWN heading and is NEVER
/// folded into « appliqué » or « non appliqué »; an empty snapshot says so rather than printing bare headings; and the
/// load-bearing footer keeps « état au moment de la capture » + « indéterminé ≠ désactivé ». Pure (no I/O); the
/// clipboard copy / file write is untested glue. Counts are small integers, so the asserted text is locale-independent.
/// </summary>
public class SnapshotStateReportTests
{
    private static SnapshotEntry E(string id, TweakAppliedState state, string? name = null)
        => new(id, name ?? id, state);

    private static SystemSnapshot Snap(params SnapshotEntry[] entries)
        => new() { Entries = entries.ToList() };

    private static readonly DateTime FixedUtc = new(2026, 1, 2, 3, 4, 0, DateTimeKind.Utc);

    private static string Render(SystemSnapshot snapshot) => SnapshotStateReport.Render(snapshot, FixedUtc);

    // --- Header ---

    [Fact]
    public void Header_CarriesTitle_Label_CaptureTime_AndTheStateCounts()
    {
        var s = Snap(
            E("a", TweakAppliedState.Applied),
            E("b", TweakAppliedState.Applied),
            E("c", TweakAppliedState.NotApplied));
        s.Label = "avant MAJ Windows";

        var text = Render(s);
        Assert.Contains("Aurum Tweaks — État de l'instantané", text);
        Assert.Contains("Généré le ", text);
        Assert.Contains("Instantané : avant MAJ Windows", text);
        Assert.Contains("Capturé le ", text);
        // The header's own StateSummaryLabel — proves the totals travel even where a 0-row section is omitted.
        Assert.Contains("2 appliqué(s) · 1 non · 0 indéterminé(s)", text);
    }

    [Fact]
    public void Header_StampsCaptureVersion_OnlyWhenTheSnapshotRecordedIt()
    {
        // A snapshot captured by a known build shows « Version à la capture » — the honest provenance for a shared or
        // imported state. A record with no version (older snapshot / foreign file) omits the line rather than guessing
        // a build (e.g. the one reading it back, which would be wrong for an imported snapshot).
        var withVersion = Snap(E("a", TweakAppliedState.Applied));
        withVersion.AppVersion = "9.9.9-test";
        Assert.Contains("Version à la capture : 9.9.9-test", Render(withVersion));

        Assert.DoesNotContain("Version à la capture", Render(Snap(E("a", TweakAppliedState.Applied))));
    }

    // --- Empty ---

    [Fact]
    public void NoEntries_SaysSo_WithoutFabricatingSections()
    {
        var text = Render(Snap());   // a record with no probes at all
        Assert.Contains("Aucun tweak enregistré dans cet instantané.", text);
        Assert.DoesNotContain("APPLIQUÉS", text);     // also covers « NON APPLIQUÉS » (it contains the substring)
        Assert.DoesNotContain("INDÉTERMINÉS", text);
    }

    // --- Grouping by state ---

    [Fact]
    public void AppliedTweaks_ListedUnderApplied_WithNameAndBracketedId()
    {
        var text = Render(Snap(E("hpet.off", TweakAppliedState.Applied, "Désactiver HPET")));
        Assert.Contains("APPLIQUÉS (1) :", text);
        Assert.Contains("  - Désactiver HPET [hpet.off]", text);
    }

    [Fact]
    public void NotAppliedTweaks_ListedUnderNonAppliqués()
    {
        var text = Render(Snap(E("x", TweakAppliedState.NotApplied, "Truc")));
        Assert.Contains("NON APPLIQUÉS (1) :", text);
        Assert.Contains("  - Truc [x]", text);
    }

    [Fact]
    public void Indeterminate_GetsItsOwnHeading_NeverFoldedIntoAppliedOrNotApplied()
    {
        // One genuine Applied + one Indeterminate. The Indeterminate row must sit under INDÉTERMINÉS (rendered last),
        // and the Applied row must sit ABOVE that heading (i.e. in the Applied section) — never merged into one bucket.
        var text = Render(Snap(
            E("appA", TweakAppliedState.Applied, "Aaa"),
            E("indZ", TweakAppliedState.Indeterminate, "Zzz")));

        Assert.Contains("INDÉTERMINÉS (1) :", text);
        Assert.Contains("  - Zzz [indZ]", text);

        var appliedHeading = text.IndexOf("APPLIQUÉS (1) :", StringComparison.Ordinal);
        var indetHeading = text.IndexOf("INDÉTERMINÉS (1) :", StringComparison.Ordinal);
        var appliedRow = text.IndexOf("  - Aaa [appA]", StringComparison.Ordinal);
        var indetRow = text.IndexOf("  - Zzz [indZ]", StringComparison.Ordinal);

        Assert.True(appliedHeading >= 0 && indetHeading >= 0 && appliedRow >= 0 && indetRow >= 0);
        Assert.True(appliedRow > appliedHeading && appliedRow < indetHeading, "Applied row belongs to the Applied section");
        Assert.True(indetRow > indetHeading, "Indeterminate row belongs to the Indeterminate section");
    }

    // --- Section omission + counts ---

    [Fact]
    public void EmptySection_IsOmitted_NoZeroRowHeading()
    {
        // Only Applied entries → neither the NotApplied nor the Indeterminate heading may appear (the footer's
        // mixed-case « Indéterminé » is not the all-caps heading token, so the assertions stay precise).
        var text = Render(Snap(E("a", TweakAppliedState.Applied)));
        Assert.Contains("APPLIQUÉS (1) :", text);
        Assert.DoesNotContain("NON APPLIQUÉS", text);
        Assert.DoesNotContain("INDÉTERMINÉS", text);
    }

    [Fact]
    public void Counts_InHeadings_ReflectEachBucketSize()
    {
        // Distinct counts per bucket so each heading token is unambiguous (« NON APPLIQUÉS (3) » never contains
        // « APPLIQUÉS (2) »).
        var text = Render(Snap(
            E("a1", TweakAppliedState.Applied), E("a2", TweakAppliedState.Applied),
            E("n1", TweakAppliedState.NotApplied), E("n2", TweakAppliedState.NotApplied), E("n3", TweakAppliedState.NotApplied)));
        Assert.Contains("APPLIQUÉS (2) :", text);
        Assert.Contains("NON APPLIQUÉS (3) :", text);
    }

    [Fact]
    public void Rows_AreOrdered_ByNameThenId()
    {
        // Names out of order and an id that would sort the other way prove name-first ordering; the two « Même »
        // rows prove the id tiebreak.
        var text = Render(Snap(
            E("z", TweakAppliedState.Applied, "Alpha"),
            E("a", TweakAppliedState.Applied, "Beta"),
            E("b", TweakAppliedState.Applied, "Même"),
            E("a2", TweakAppliedState.Applied, "Même")));

        Assert.True(text.IndexOf("Alpha [z]", StringComparison.Ordinal)
                  < text.IndexOf("Beta [a]", StringComparison.Ordinal), "ordered by name, not id");
        Assert.True(text.IndexOf("Même [a2]", StringComparison.Ordinal)
                  < text.IndexOf("Même [b]", StringComparison.Ordinal), "same name → id tiebreak");
    }

    // --- Footer caveat (load-bearing) ---

    [Fact]
    public void Footer_KeepsTheLoadBearingCaveat_CaptureMoment_AndIndeterminateIsNotOff()
    {
        var text = Render(Snap(E("t", TweakAppliedState.Applied)));
        Assert.Contains("AU MOMENT DE LA CAPTURE", text);   // the state is historical, may have drifted
        Assert.Contains("a pu changer depuis", text);
        Assert.Contains("PAS « désactivé »", text);          // indeterminate ≠ off — never read as a disabled tweak
    }
}
