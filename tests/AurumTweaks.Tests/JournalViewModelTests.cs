using System;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the change-journal page: it loads the persisted trail newest-first, derives its empty/non-empty UI from a
/// single count, and — the honesty-bearing part — guards the destructive clear behind a two-step confirm so a lone
/// click can't wipe the audit history. Driven through the in-memory <see cref="RecordingApplyJournal"/>; no file.
/// </summary>
public class JournalViewModelTests
{
    private static JournalEntry Entry(string action = "Application", params string[] ids)
        => new(DateTime.UtcNow, action, ids.Length, 0, ids, Array.Empty<string>());

    private static async Task<JournalViewModel> NewVm(RecordingApplyJournal journal)
    {
        var vm = new JournalViewModel(journal);
        await vm.Initialization;   // first load from the store completes before the test acts
        return vm;
    }

    [Fact]
    public async Task Load_FillsEntriesFromTheStore_AndReflectsTheCount()
    {
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(Entry("Application", "a"));
        await journal.RecordAsync(Entry("Restauration", "a"));   // newest

        var vm = await NewVm(journal);

        Assert.Equal(2, vm.EntryCount);
        Assert.True(vm.HasEntries);
        Assert.False(vm.IsEmpty);
        Assert.Equal("Restauration", vm.Entries[0].Action);      // store order (newest-first) preserved
        Assert.Equal("Application", vm.Entries[1].Action);
    }

    [Fact]
    public async Task Load_OnEmptyStore_IsEmpty()
    {
        var vm = await NewVm(new RecordingApplyJournal());

        Assert.Equal(0, vm.EntryCount);
        Assert.False(vm.HasEntries);
        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task Refresh_PicksUpEntriesAddedAfterConstruction()
    {
        // The VM is a singleton built once; a batch applied later from the Tweaks page must appear when the page is
        // re-shown (Loaded → RefreshAsync), not only after a restart.
        var journal = new RecordingApplyJournal();
        var vm = await NewVm(journal);
        Assert.True(vm.IsEmpty);

        await journal.RecordAsync(Entry("Application", "later"));
        await vm.RefreshAsync();

        Assert.Equal(1, vm.EntryCount);
        Assert.Equal("later", vm.Entries[0].TweakIds.Single());
    }

    [Fact]
    public async Task BeginClear_ArmsTheConfirmGate_WithoutWiping()
    {
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(Entry("Application", "a"));
        var vm = await NewVm(journal);

        vm.BeginClearCommand.Execute(null);

        Assert.True(vm.IsConfirmingClear);                       // gate armed
        Assert.Single(vm.Entries);                               // but nothing wiped yet — two clicks from gone
        Assert.Single(journal.Entries);
    }

    [Fact]
    public async Task BeginClear_OnAnEmptyJournal_IsHonestNoOp_AndNeverArms()
    {
        var vm = await NewVm(new RecordingApplyJournal());

        vm.BeginClearCommand.Execute(null);

        Assert.False(vm.IsConfirmingClear);                      // nothing to clear → the gate never arms
        Assert.Equal("Le journal est déjà vide.", vm.Status);
    }

    [Fact]
    public async Task ConfirmClear_WipesBothTheStoreAndTheList()
    {
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(Entry("Application", "a"));
        var vm = await NewVm(journal);
        vm.BeginClearCommand.Execute(null);

        await vm.ConfirmClearCommand.ExecuteAsync(null);

        Assert.False(vm.IsConfirmingClear);                      // gate closes
        Assert.Empty(vm.Entries);                               // list emptied
        Assert.Empty(journal.Entries);                          // and the persisted trail genuinely cleared
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task CancelClear_DisarmsTheGate_AndKeepsEveryEntry()
    {
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(Entry("Application", "a"));
        var vm = await NewVm(journal);
        vm.BeginClearCommand.Execute(null);

        vm.CancelClearCommand.Execute(null);

        Assert.False(vm.IsConfirmingClear);                      // gate disarmed
        Assert.Single(vm.Entries);                              // and nothing was lost
        Assert.Single(journal.Entries);
    }

    [Fact]
    public async Task Export_OnAnEmptyJournal_IsHonestNoOp()
    {
        // The dialog-driven export path is untested glue, but its honest guard isn't: an empty journal must refuse
        // to write a report (which would claim to record changes that don't exist) and say why.
        var vm = await NewVm(new RecordingApplyJournal());

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Rien à exporter : le journal est vide.", vm.Status);
    }

    [Fact]
    public async Task Copy_OnAnEmptyJournal_IsHonestNoOp()
    {
        // Same honest-guard as Export: the Clipboard.SetText is untested glue, but the empty branch returns before
        // it — copying an empty journal must refuse (with a reason) rather than silently put a blank report on the
        // clipboard. A non-empty copy can't be unit-tested here (the clipboard needs an STA host), so the guard is
        // the meaningful assertable half.
        var vm = await NewVm(new RecordingApplyJournal());

        vm.CopyCommand.Execute(null);

        Assert.Equal("Rien à copier : le journal est vide.", vm.Status);
    }

    [Fact]
    public async Task Load_ComputesTheSynthesis_FromTheStoredTrail()
    {
        // The page's "Synthèse" card binds Statistics; it must reflect the loaded trail, including the diagnostic
        // ranking of the writes verification couldn't confirm — without re-deriving anything from the live machine.
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(new JournalEntry(DateTime.UtcNow, "Application", 3, 0, new[] { "a", "b", "c" }, new[] { "b" }));
        await journal.RecordAsync(Entry("Restauration", "a"));

        var vm = await NewVm(journal);

        Assert.True(vm.Statistics.HasActivity);
        Assert.Equal(1, vm.Statistics.ApplyBatches);
        Assert.Equal(1, vm.Statistics.RevertBatches);
        Assert.Equal(1, vm.Statistics.TotalUnconfirmed);
        Assert.Equal("b", Assert.Single(vm.Statistics.MostUnconfirmed).TweakId);
    }

    [Fact]
    public async Task ConfirmClear_ResetsTheSynthesis_ToNoActivity()
    {
        // Wiping the trail must wipe the synthesis with it — a stale card would otherwise still claim activity the
        // user just erased.
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(Entry("Application", "a"));
        var vm = await NewVm(journal);
        Assert.True(vm.Statistics.HasActivity);

        vm.BeginClearCommand.Execute(null);
        await vm.ConfirmClearCommand.ExecuteAsync(null);

        Assert.False(vm.Statistics.HasActivity);
        Assert.Equal("Aucune activité enregistrée.", vm.Statistics.Summary);
    }

    [Fact]
    public async Task Refresh_SupersedesAHalfArmedClearGate()
    {
        // Reloading the page mid-confirm must not leave a stale "confirm clear?" armed over freshly loaded rows.
        var journal = new RecordingApplyJournal();
        await journal.RecordAsync(Entry("Application", "a"));
        var vm = await NewVm(journal);
        vm.BeginClearCommand.Execute(null);
        Assert.True(vm.IsConfirmingClear);

        await vm.RefreshAsync();

        Assert.False(vm.IsConfirmingClear);
    }
}
