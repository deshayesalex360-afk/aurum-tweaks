using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the change-journal domain: the honest one-line <see cref="JournalEntry.Summary"/> (no failure/unconfirmed
/// clause unless real), the bounded newest-first <see cref="JournalLog.Prepend"/> (an audit log must never grow
/// without limit, and order is what the page relies on), and the <see cref="JournalReport"/> mapping (an apply
/// records what verification couldn't confirm; a revert records none). All pure — no file, no clock injection
/// beyond a recency sanity check.
/// </summary>
public class JournalTests
{
    private static JournalEntry Entry(string action = "Application", int ok = 1, int failed = 0,
                                      string[]? ids = null, string[]? unconfirmed = null)
        => new(DateTime.UtcNow, action, ok, failed,
               ids ?? new[] { "a" }, unconfirmed ?? Array.Empty<string>());

    // ---- JournalEntry honest summary + labels ----

    [Fact]
    public void Summary_CleanApply_IsActionAndSuccessCountOnly()
        => Assert.Equal("Application · 3 réussi(s)", Entry(ok: 3).Summary);

    [Fact]
    public void Summary_WithFailures_AdmitsThem()
        => Assert.Equal("Application · 2 réussi(s), 1 échec(s)", Entry(ok: 2, failed: 1).Summary);

    [Fact]
    public void Summary_WithUnconfirmed_FlagsThem()
        => Assert.Equal("Application · 2 réussi(s) · 1 non confirmé(s)",
            Entry(ok: 2, unconfirmed: new[] { "x" }).Summary);

    [Fact]
    public void Summary_WithFailuresAndUnconfirmed_ShowsBothClauses()
        => Assert.Equal("Application · 1 réussi(s), 2 échec(s) · 1 non confirmé(s)",
            Entry(ok: 1, failed: 2, unconfirmed: new[] { "x" }).Summary);

    [Fact]
    public void Flags_And_Labels_ReflectTheLists()
    {
        var e = Entry(ok: 1, failed: 1, ids: new[] { "a", "b" }, unconfirmed: new[] { "b" });

        Assert.True(e.HasFailures);
        Assert.True(e.HasUnconfirmed);
        Assert.Equal("a, b", e.TweakIdsLabel);
        Assert.Equal("b", e.UnconfirmedLabel);
    }

    [Fact]
    public void CleanEntry_HasNoFailureOrUnconfirmedFlags()
    {
        var e = Entry();

        Assert.False(e.HasFailures);
        Assert.False(e.HasUnconfirmed);
    }

    // ---- JournalLog: bounded, newest-first ----

    [Fact]
    public void Prepend_PutsTheNewEntryFirst()
    {
        var old = Entry(ids: new[] { "old" });
        var fresh = Entry(ids: new[] { "new" });

        var result = JournalLog.Prepend(new[] { old }, fresh);

        Assert.Same(fresh, result[0]);
        Assert.Same(old, result[1]);
    }

    [Fact]
    public void Prepend_CapsToTheLimit_DroppingTheOldest()
    {
        var existing = Enumerable.Range(0, 5).Select(i => Entry(ids: new[] { $"e{i}" })).ToList();
        var fresh = Entry(ids: new[] { "fresh" });

        var result = JournalLog.Prepend(existing, fresh, cap: 3);

        Assert.Equal(3, result.Count);
        Assert.Same(fresh, result[0]);                       // newest survives at the front
        Assert.Equal(new[] { "fresh", "e0", "e1" },          // oldest (e3, e4) dropped from the tail
            result.Select(e => e.TweakIds[0]));
    }

    [Fact]
    public void Prepend_BelowCap_KeepsEverything()
    {
        var existing = new[] { Entry(ids: new[] { "a" }), Entry(ids: new[] { "b" }) };

        var result = JournalLog.Prepend(existing, Entry(ids: new[] { "c" }), cap: 10);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Prepend_DoesNotMutateTheInputList()
    {
        var existing = new List<JournalEntry> { Entry(ids: new[] { "a" }) };

        JournalLog.Prepend(existing, Entry(ids: new[] { "b" }), cap: 1);

        Assert.Single(existing);                             // caller's list is untouched
    }

    // ---- JournalReport: batch outcome → entry ----

    [Fact]
    public void ForApply_MapsTally_Ids_AndPullsUnconfirmedFromVerification()
    {
        var verification = new VerificationReport(
            Confirmed: new[] { "ok" }, Unconfirmed: new[] { "stuck" }, Unverifiable: Array.Empty<string>());

        var e = JournalReport.ForApply(new BatchTweakResult(2, 1), new[] { "ok", "stuck" }, verification);

        Assert.Equal("Application", e.Action);
        Assert.Equal(2, e.Succeeded);
        Assert.Equal(1, e.Failed);
        Assert.Equal(new[] { "ok", "stuck" }, e.TweakIds);
        Assert.Equal(new[] { "stuck" }, e.Unconfirmed);
        Assert.InRange(e.TimestampUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void ForApply_WithoutVerification_RecordsNoUnconfirmed()
    {
        var e = JournalReport.ForApply(new BatchTweakResult(1, 0), new[] { "a" }, verification: null);

        Assert.False(e.HasUnconfirmed);
        Assert.Empty(e.Unconfirmed);
    }

    [Fact]
    public void ForRevert_IsLabelledRestauration_AndPullsStillActiveFromVerification()
    {
        // The revert twin of ForApply: the Unconfirmed list carries the revert-side meaning — tweaks the re-probe
        // found STILL ACTIVE despite the engine reporting the revert ran (from RevertVerifier). So a revert that
        // didn't take is recorded in the durable trail exactly as an apply that didn't stick — no longer always empty.
        var verification = new VerificationReport(
            Confirmed: new[] { "a", "c" }, Unconfirmed: new[] { "b" }, Unverifiable: Array.Empty<string>());

        var e = JournalReport.ForRevert(new BatchTweakResult(3, 0), new[] { "a", "b", "c" }, verification);

        Assert.Equal("Restauration", e.Action);
        Assert.Equal(3, e.Succeeded);
        Assert.Equal(new[] { "b" }, e.Unconfirmed);
    }

    [Fact]
    public void ForRevert_WithoutVerification_RecordsNoUnconfirmed()
    {
        // A surface that runs no post-revert re-probe (e.g. the snapshot path) passes null → makes no claim either way.
        var e = JournalReport.ForRevert(new BatchTweakResult(3, 0), new[] { "a", "b", "c" }, verification: null);

        Assert.False(e.HasUnconfirmed);
        Assert.Empty(e.Unconfirmed);
    }
}
