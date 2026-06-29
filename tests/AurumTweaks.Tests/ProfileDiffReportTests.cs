using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="ProfileDiffReport"/> — the shareable text behind the Profiles page's "Copier le comparatif".
/// It must name both profiles, carry the same honest summary line as the on-screen panel, and list the ids of each
/// non-empty bucket. The load-bearing rule (shared with SnapshotReport / JournalTextReport): a bucket heading is
/// emitted ONLY when it has rows — an empty heading would imply a difference that isn't there — so an identical
/// comparison reads as the summary plus the shared list, with no phantom "propre à" sections.
/// </summary>
public class ProfileDiffReportTests
{
    private static readonly DateTime FixedUtc = new(2026, 6, 19, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Render_Different_NamesBothProfiles_AndListsEveryBucket()
    {
        var c = ProfileDiff.Compare(new[] { "a", "b" }, new[] { "b", "c" });

        var text = ProfileDiffReport.Render(c, "Setup A", "Setup B", FixedUtc);

        Assert.Contains("Aurum Tweaks — Comparaison de profils", text);
        Assert.Contains("Généré le", text);
        Assert.Contains("« Setup A » (A) vs « Setup B » (B)", text);
        Assert.Contains("1 en commun", text);                       // the same summary line the panel shows
        Assert.Contains("PROPRE À « Setup A » (A) (1) :", text);
        Assert.Contains("  - a", text);
        Assert.Contains("EN COMMUN (1) :", text);
        Assert.Contains("  - b", text);
        Assert.Contains("PROPRE À « Setup B » (B) (1) :", text);
        Assert.Contains("  - c", text);
    }

    [Fact]
    public void Render_Identical_ShowsOnlyTheSharedBucket_NoPhantomPropreSections()
    {
        var c = ProfileDiff.Compare(new[] { "a", "b" }, new[] { "a", "b" });

        var text = ProfileDiffReport.Render(c, "A", "B", FixedUtc);

        Assert.Contains("exactement les mêmes", text);
        Assert.Contains("EN COMMUN (2) :", text);
        Assert.DoesNotContain("PROPRE À", text);   // both left-only and right-only are empty → omitted, not blank headings
    }

    [Fact]
    public void Render_FullyDisjoint_OmitsTheSharedHeading()
    {
        var c = ProfileDiff.Compare(new[] { "a" }, new[] { "b" });

        var text = ProfileDiffReport.Render(c, "A", "B", FixedUtc);

        Assert.DoesNotContain("EN COMMUN", text);
        Assert.Contains("PROPRE À « A » (A) (1) :", text);
        Assert.Contains("PROPRE À « B » (B) (1) :", text);
    }

    [Fact]
    public void Render_ListsIdsInInputOrder_OneBulletEach()
    {
        var c = ProfileDiff.Compare(new[] { "x1", "x2", "x3" }, Array.Empty<string>());

        var text = ProfileDiffReport.Render(c, "A", "B", FixedUtc);

        Assert.Contains("PROPRE À « A » (A) (3) :", text);
        var i1 = text.IndexOf("  - x1", StringComparison.Ordinal);
        var i2 = text.IndexOf("  - x2", StringComparison.Ordinal);
        var i3 = text.IndexOf("  - x3", StringComparison.Ordinal);
        Assert.True(i1 >= 0 && i2 > i1 && i3 > i2);   // emitted in the left input order
    }
}
