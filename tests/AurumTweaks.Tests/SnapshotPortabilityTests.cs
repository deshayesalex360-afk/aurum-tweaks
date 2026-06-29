using System;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the honesty surface of <see cref="SnapshotPortability"/> — the pure (de)serialization behind exporting a
/// snapshot to a portable file and importing one back (a baseline carried across a reinstall, or shared by someone
/// else). A foreign / hand-edited file is VALIDATED, not trusted: unreadable JSON and an entry-less snapshot are
/// refused with a French reason (so the list never holds a silent, uncomparable record), malformed rows are dropped,
/// and every import is given a FRESH id so it can never overwrite a stored snapshot. An Indeterminate state survives
/// the round-trip unchanged — an import must never coerce "couldn't read it" into a confident ✓ that a later diff
/// would then treat as a real state. Pure (no disk); the file read/write is untested glue in SnapshotService.
/// </summary>
public class SnapshotPortabilityTests
{
    private static readonly DateTime FixedNow = new(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc);

    private static SnapshotEntry E(string id, TweakAppliedState state, string? name = null)
        => new(id, name ?? id, state);

    private static SystemSnapshot Snap(string label, params SnapshotEntry[] entries)
        => new() { Label = label, Entries = entries.ToList() };

    [Fact]
    public void Serialize_RoundTripsThroughImport_PreservingLabelAndEntries()
    {
        var original = Snap("avant MAJ",
            E("t1", TweakAppliedState.Applied, "Tweak 1"),
            E("t2", TweakAppliedState.NotApplied, "Tweak 2"));

        var ok = SnapshotPortability.TryImport(SnapshotPortability.Serialize(original), FixedNow, out var imported, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("avant MAJ", imported!.Label);
        Assert.Equal(2, imported.Entries.Count);
        var e1 = imported.Entries.Single(e => e.TweakId == "t1");
        Assert.Equal("Tweak 1", e1.TweakName);
        Assert.Equal(TweakAppliedState.Applied, e1.State);
        Assert.Equal(TweakAppliedState.NotApplied, imported.Entries.Single(e => e.TweakId == "t2").State);
    }

    [Fact]
    public void TryImport_AssignsAFreshId_SoItNeverOverwritesTheOriginal()
    {
        var original = Snap("ref", E("t", TweakAppliedState.Applied));
        var originalId = original.Id;

        var ok = SnapshotPortability.TryImport(SnapshotPortability.Serialize(original), FixedNow, out var imported, out _);

        Assert.True(ok);
        Assert.NotEqual(originalId, imported!.Id);
        Assert.True(Guid.TryParse(imported.Id, out _));     // a real fresh guid, not blank
    }

    [Fact]
    public void TryImport_KeepsAnIndeterminateStateUnchanged_NeverCoercedToApplied()
    {
        // "Couldn't read it back" must survive the file round-trip as Indeterminate — importing must not upgrade an
        // unknown into a confident ✓ (which a later diff would then read as a real, comparable state).
        var original = Snap("ref", E("u", TweakAppliedState.Indeterminate));

        var ok = SnapshotPortability.TryImport(SnapshotPortability.Serialize(original), FixedNow, out var imported, out _);

        Assert.True(ok);
        Assert.Equal(TweakAppliedState.Indeterminate, Assert.Single(imported!.Entries).State);
    }

    [Fact]
    public void TryImport_RejectsInvalidJson_WithAFrenchReason()
    {
        var ok = SnapshotPortability.TryImport("{ this is not valid json", FixedNow, out var imported, out var error);

        Assert.False(ok);
        Assert.Null(imported);
        Assert.Contains("illisible", error!);
    }

    [Fact]
    public void TryImport_RejectsAnEntrylessSnapshot_RatherThanImportAnUncomparableRecord()
    {
        var ok = SnapshotPortability.TryImport(SnapshotPortability.Serialize(Snap("vide")), FixedNow, out var imported, out var error);

        Assert.False(ok);
        Assert.Null(imported);
        Assert.Contains("aucun tweak", error!);
    }

    [Fact]
    public void TryImport_DropsRowsWithNoId_ButKeepsTheWellFormedOnes()
    {
        // A hand-edited / foreign file can carry a row with no id; it can't be matched to a tweak, so it's dropped —
        // the good rows still import.
        const string json = """
            {
              "label": "mixte",
              "capturedUtc": "2026-01-01T00:00:00Z",
              "entries": [
                { "tweakId": "good", "tweakName": "Bon", "state": "Applied" },
                { "tweakId": "", "tweakName": "Sans id", "state": "Applied" }
              ]
            }
            """;

        var ok = SnapshotPortability.TryImport(json, FixedNow, out var imported, out _);

        Assert.True(ok);
        Assert.Equal("good", Assert.Single(imported!.Entries).TweakId);
    }

    [Fact]
    public void TryImport_AllRowsMalformed_IsRejectedLikeAnEmptyFile()
    {
        const string json = """
            {
              "label": "tout cassé",
              "entries": [ { "tweakId": "", "tweakName": "x", "state": "Applied" } ]
            }
            """;

        var ok = SnapshotPortability.TryImport(json, FixedNow, out var imported, out var error);

        Assert.False(ok);
        Assert.Null(imported);
        Assert.Contains("aucun tweak", error!);
    }

    [Fact]
    public void TryImport_PreservesTheOriginalCaptureTime_NotTheImportTime()
    {
        var captured = new DateTime(2025, 3, 4, 5, 6, 0, DateTimeKind.Utc);
        var original = new SystemSnapshot { Label = "ref", CapturedUtc = captured, Entries = { E("t", TweakAppliedState.Applied) } };

        var ok = SnapshotPortability.TryImport(SnapshotPortability.Serialize(original), FixedNow, out var imported, out _);

        Assert.True(ok);
        Assert.Equal(captured, imported!.CapturedUtc);       // a historical record keeps its own time, not "now"
    }

    [Fact]
    public void TryImport_SubstitutesNow_WhenTheFileCarriesTheZeroDate()
    {
        // A file with no real capture time (the zero date) must not import as year 0001 — it takes the import time so
        // it sorts and displays sensibly alongside genuine captures.
        const string json = """
            {
              "label": "sans date",
              "capturedUtc": "0001-01-01T00:00:00",
              "entries": [ { "tweakId": "t", "tweakName": "T", "state": "Applied" } ]
            }
            """;

        var ok = SnapshotPortability.TryImport(json, FixedNow, out var imported, out _);

        Assert.True(ok);
        Assert.Equal(FixedNow, imported!.CapturedUtc);
    }

    [Fact]
    public void TryImport_TrimsTheLabel()
    {
        const string json = """
            {
              "label": "   ref   ",
              "capturedUtc": "2026-01-01T00:00:00Z",
              "entries": [ { "tweakId": "t", "tweakName": "T", "state": "Applied" } ]
            }
            """;

        var ok = SnapshotPortability.TryImport(json, FixedNow, out var imported, out _);

        Assert.True(ok);
        Assert.Equal("ref", imported!.Label);
    }
}
