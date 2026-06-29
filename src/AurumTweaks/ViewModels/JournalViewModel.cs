using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AurumTweaks.ViewModels;

/// <summary>
/// The change-journal page: a read-only, newest-first view of every apply/revert batch the app has run, loaded
/// from the persisted <see cref="IApplyJournal"/>. It only displays what the store recorded (the honest tally and
/// the writes verification couldn't confirm) — it never re-derives or re-claims an outcome here. The single
/// destructive action, clearing the trail, is gated behind a two-step confirm so a misclick can't erase history.
/// </summary>
public partial class JournalViewModel : ObservableObject
{
    private readonly IApplyJournal _journal;

    public ObservableCollection<JournalEntry> Entries { get; } = new();

    /// <summary>Completes when the first load from the store has finished — lets the page (and tests) await the
    /// initial fill rather than racing the fire-and-forget load in the constructor.</summary>
    public Task Initialization { get; }

    // EntryCount is the single source of truth for the empty/non-empty UI split; both flags derive from it so
    // there's no second piece of state to keep in sync. It also feeds the "N entrée(s)" header count directly.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntries))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _entryCount;

    [ObservableProperty] private string _status = string.Empty;

    // Read-only synthesis of the whole trail (tallies + the tweaks most often left unconfirmed), recomputed from the
    // pure JournalInsights whenever the list changes. A derived view — it re-applies / re-evaluates nothing, so it
    // honours the page's read-only promise. Seeded to an empty (no-activity) stat so the card never binds to null.
    [ObservableProperty] private JournalStatistics _statistics = new();

    // Two-step clear gate: the first click arms the confirm/cancel pair; only the second click wipes the persisted
    // trail. An audit log is exactly the kind of record a single misclick shouldn't be able to destroy.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingClear))]
    private bool _confirmingClear;

    public bool IsConfirmingClear => ConfirmingClear;
    public bool HasEntries => EntryCount > 0;
    public bool IsEmpty => EntryCount == 0;

    public JournalViewModel(IApplyJournal journal)
    {
        _journal = journal;
        Initialization = LoadAsync();
    }

    /// <summary>Reload from the store. Called when the page is shown (JournalView.Loaded) so a batch just applied
    /// from the Tweaks page appears without an app restart — the VM is a singleton, otherwise filled once.</summary>
    public Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        ConfirmingClear = false;   // a reload supersedes a half-armed clear gate
        Entries.Clear();
        foreach (var e in await _journal.LoadAsync()) Entries.Add(e);
        EntryCount = Entries.Count;
        RefreshStatistics();
    }

    // Recompute the synthesis from the current list. Manual (like EntryCount above) and called at the two points the
    // list changes — load and clear — so the card always reflects exactly what's shown, with no extra observable to
    // keep in sync.
    private void RefreshStatistics() => Statistics = JournalInsights.Compute(Entries.ToList());

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    // Arm the clear gate — honest no-op (with a reason) when there's nothing to clear, so the button never looks
    // like it acted on an already-empty journal.
    [RelayCommand]
    private void BeginClear()
    {
        if (Entries.Count == 0)
        {
            Status = "Le journal est déjà vide.";
            return;
        }
        ConfirmingClear = true;
    }

    // Export the trail to a readable text file the user can keep or share (forum, support). Honest no-op (with a
    // reason) on an empty journal: never write a report that claims to record changes when none exist. The pure
    // JournalTextReport.Render is the tested part; this is the thin save-dialog + write glue.
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Entries.Count == 0)
        {
            Status = "Rien à exporter : le journal est vide.";
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "Exporter le journal",
            FileName = $"aurum-journal-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await File.WriteAllTextAsync(dlg.FileName, JournalTextReport.Render(Entries.ToList(), DateTime.UtcNow));
            Status = $"Journal exporté ({Entries.Count} entrée(s)).";
        }
        catch (IOException ex) { Status = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Status = $"Export impossible : {ex.Message}"; }
    }

    // Copy the same rendered trail to the clipboard — a paste into a forum/Discord thread is how a journal is
    // actually shared, no file step in between. Mirrors the System Report's Export+Copy pair and the Export above:
    // same pure JournalTextReport.Render, honest no-op (with a reason) on an empty journal, and a graceful fallback
    // to the file route when the clipboard is momentarily locked by another process. Copy = consultation, so this
    // respects the page's read-only promise (no line ever re-applies or re-evaluates anything).
    [RelayCommand]
    private void Copy()
    {
        if (Entries.Count == 0)
        {
            Status = "Rien à copier : le journal est vide.";
            return;
        }
        try
        {
            Clipboard.SetText(JournalTextReport.Render(Entries.ToList(), DateTime.UtcNow));
            Status = $"Journal copié ({Entries.Count} entrée(s)). Colle-le où tu veux.";
        }
        catch
        {
            Status = "Copie impossible (presse-papiers occupé). Utilise « Exporter… » à la place.";
        }
    }

    [RelayCommand]
    private void CancelClear() => ConfirmingClear = false;

    [RelayCommand]
    private async Task ConfirmClearAsync()
    {
        ConfirmingClear = false;
        await _journal.ClearAsync();
        Entries.Clear();
        EntryCount = 0;
        RefreshStatistics();
        Status = "Journal effacé.";
    }
}
