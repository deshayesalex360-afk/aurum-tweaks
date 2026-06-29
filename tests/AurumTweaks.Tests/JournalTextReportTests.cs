using System;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the shareable text report: it carries the real header/count, lists each entry's honest summary and ids,
/// and shows the "non confirmé(s)" clause ONLY for entries that actually have one — the report must never imply a
/// cleaner (or dirtier) run than the journal recorded. Pure (clock passed in); timezone-dependent timestamp text
/// is deliberately not asserted, only structure and content.
/// </summary>
public class JournalTextReportTests
{
    private static JournalEntry Entry(string action = "Application", int ok = 1, int failed = 0,
                                      string[]? ids = null, string[]? unconfirmed = null)
        => new(DateTime.UtcNow, action, ok, failed,
               ids ?? new[] { "a" }, unconfirmed ?? Array.Empty<string>());

    [Fact]
    public void Render_Empty_StatesNoneRecorded_WithAZeroCount()
    {
        var report = JournalTextReport.Render(Array.Empty<JournalEntry>(), DateTime.UtcNow);

        Assert.Contains("0 entrée(s)", report);
        Assert.Contains("(aucune modification enregistrée)", report);
    }

    [Fact]
    public void Render_CarriesTheTitleAndEntryCount()
    {
        var report = JournalTextReport.Render(new[] { Entry(), Entry() }, DateTime.UtcNow);

        Assert.Contains("Aurum Tweaks — Journal des modifications", report);
        Assert.Contains("2 entrée(s)", report);
    }

    [Fact]
    public void Render_EachEntry_ShowsItsHonestSummaryAndIds()
    {
        var report = JournalTextReport.Render(
            new[] { Entry(action: "Restauration", ok: 2, ids: new[] { "x", "y" }) }, DateTime.UtcNow);

        Assert.Contains("Restauration · 2 réussi(s)", report);   // the entry's own honest summary, verbatim
        Assert.Contains("Tweaks : x, y", report);
    }

    [Fact]
    public void Render_ShowsUnconfirmedClause_OnlyForEntriesThatHaveOne()
    {
        var clean = Entry(ids: new[] { "ok" });
        var flagged = Entry(ok: 1, ids: new[] { "stuck" }, unconfirmed: new[] { "stuck" });

        var report = JournalTextReport.Render(new[] { clean, flagged }, DateTime.UtcNow);

        Assert.Contains("Non confirmé(s) : stuck", report);
        // Exactly one such clause — the clean entry must not contribute a (fabricated) one. (The synthesis lead uses a
        // lowercase "non confirmé(s)" / "non confirmés", so it never inflates this capital-N per-entry-clause count.)
        var occurrences = report.Split("Non confirmé(s)").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Render_LeadsWithASynthesis_OfTheWholeTrail()
    {
        // The shared report opens with the same honest big picture the page's card shows — before the per-entry detail.
        var report = JournalTextReport.Render(
            new[] { Entry(action: "Application", ok: 2), Entry(action: "Restauration", ok: 1) }, DateTime.UtcNow);

        Assert.Contains("Synthèse : 2 lot(s) · 1 application(s), 1 restauration(s)", report);
    }

    [Fact]
    public void Render_Synthesis_RanksTheMostOftenUnconfirmed()
    {
        // "stuck" left unconfirmed across two batches surfaces in the lead as a 2× diagnostic row — the report's
        // most actionable line for someone sharing "which tweak won't stick on my machine".
        var report = JournalTextReport.Render(new[]
        {
            Entry(ids: new[] { "stuck" }, unconfirmed: new[] { "stuck" }),
            Entry(ids: new[] { "stuck" }, unconfirmed: new[] { "stuck" })
        }, DateTime.UtcNow);

        Assert.Contains("Tweaks le plus souvent non confirmés :", report);
        Assert.Contains("stuck — 2×", report);
    }

    [Fact]
    public void Render_Empty_HasNoSynthesisLead()
    {
        // An empty journal must not sprout a synthesis (which would imply a history that isn't there).
        var report = JournalTextReport.Render(Array.Empty<JournalEntry>(), DateTime.UtcNow);

        Assert.DoesNotContain("Synthèse", report);
    }
}
