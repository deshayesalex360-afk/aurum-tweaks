using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// The Ctrl+K command palette — Linear's signature jump-to-anything, fitted to a 37-page app. Holds the open
/// state, the live query, the ranked results, and the highlighted row; the ranking itself lives in the pure
/// <see cref="CommandPaletteSearch"/> core so this stays a thin state holder. It never touches the page graph:
/// activating a row raises <see cref="NavigationRequested"/> and <c>MainViewModel</c> performs the jump, which
/// keeps the dependency one-way (no Main↔Palette cycle) and the whole VM unit-testable without a window.
/// </summary>
public partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ITweakRepository _repo;
    private readonly ILocalizationService _localization;
    private readonly INavigationHistory _history;

    // The full searchable universe: the static page catalog, the global-action catalog, and one row per tweak
    // (folded in once the catalog finishes loading). Pages and actions are searchable immediately; only tweak rows
    // wait on the load. Rebuilt when the language changes, since that's the only thing that alters tweak titles.
    private IReadOnlyList<PaletteEntry> _entries;
    private IReadOnlyList<Tweak> _loadedTweaks = Array.Empty<Tweak>();

    public ObservableCollection<PaletteEntry> Results { get; } = new();

    /// <summary>Completes once tweak rows have been folded in. Lets tests await the full universe instead of
    /// racing the fire-and-forget load kicked off in the constructor.</summary>
    public Task Initialization { get; }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private int _selectedIndex;

    /// <summary>Raised when a row is activated (Enter or click). <c>MainViewModel</c> listens and navigates —
    /// the palette deliberately knows nothing about how navigation happens.</summary>
    public event EventHandler<PaletteEntry>? NavigationRequested;

    public CommandPaletteViewModel(ITweakRepository repo, ILocalizationService localization, INavigationHistory history)
    {
        _repo = repo;
        _localization = localization;
        _history = history;
        _entries = PagesAndActions();                 // pages + actions are searchable immediately; tweaks fold in below
        Recompute();
        _localization.LanguageChanged += (_, _) => RebuildEntries();
        Initialization = LoadTweakEntriesAsync();
    }

    private async Task LoadTweakEntriesAsync()
    {
        _loadedTweaks = await _repo.LoadAllAsync();    // cached after the splash; an empty list if none loaded
        RebuildEntries();
    }

    // Pages first (stable, curated order), then global actions, then a row per tweak. Tweak titles come from the
    // localization service, so this also runs on a language switch to keep palette labels in the active language.
    private void RebuildEntries()
    {
        var entries = new List<PaletteEntry>(PagesAndActions());
        foreach (var t in _loadedTweaks)
            entries.Add(TweakEntry(t));
        _entries = entries;
        if (IsOpen) Recompute();
    }

    // The language-independent prefix of the universe: curated pages then global actions. Both are static catalogs,
    // so this is the set searchable from the very first open, before the async tweak load completes.
    private static IReadOnlyList<PaletteEntry> PagesAndActions()
    {
        var entries = new List<PaletteEntry>(NavigationCatalog.Pages);
        entries.AddRange(PaletteActionCatalog.Actions);
        return entries;
    }

    private PaletteEntry TweakEntry(Tweak t)
    {
        var title = _localization.GetLocalizedFrom(t.Name);
        if (string.IsNullOrWhiteSpace(title)) title = t.Id;
        // Keywords widen recall without crowding the visible row (which shows only the title + a "Tweak" tag): the
        // id, the description text, the category enum name, and — for power users — the concrete things the tweak
        // touches (registry paths, service names…), so "NetworkThrottlingIndex" or "DiagTrack" finds it by target.
        var keywords = $"{t.Id} {_localization.GetLocalizedFrom(t.Description)} {t.Category} {TweakOperationSummary.SearchTargets(t)}";
        return new PaletteEntry(t.Id, title, "Tweak", PaletteEntryKind.Tweak, keywords);
    }

    [RelayCommand]
    private void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        Query = string.Empty;     // OnQueryChanged recomputes; force it too in case the query was already empty
        Recompute();
        SelectedIndex = 0;
        IsOpen = true;
    }

    // Public (not a RelayCommand) because the window shell drives it directly — backdrop click and Esc — alongside
    // Open/MoveSelection/Activate. Only Toggle is bound in XAML (Ctrl+K), so only it needs to be a command.
    public void Close()
    {
        IsOpen = false;
        Query = string.Empty;
    }

    /// <summary>Move the highlight by <paramref name="delta"/> rows, wrapping at both ends so Down past the last
    /// result returns to the first (standard palette feel). A no-op when there are no results.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) { SelectedIndex = 0; return; }
        var i = (SelectedIndex + delta) % Results.Count;
        if (i < 0) i += Results.Count;
        SelectedIndex = i;
    }

    /// <summary>Fire navigation for the highlighted row and close. Honest no-op when nothing matches the query —
    /// pressing Enter on an empty result set does nothing rather than jumping somewhere arbitrary.</summary>
    public void Activate()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        var entry = Results[SelectedIndex];
        Close();
        NavigationRequested?.Invoke(this, entry);
    }

    partial void OnQueryChanged(string value) => Recompute();

    private void Recompute()
    {
        var ranked = CommandPaletteSearch.Rank(_entries, Query);
        // Empty query is the "launcher" view (full list, catalog order) — bubble the pages the user most recently
        // visited to the top there, the way Ctrl+P does. A typed query is left to pure relevance ranking, so
        // recency never outranks a better textual match.
        if (string.IsNullOrWhiteSpace(Query))
            ranked = PaletteRecency.PrioritizeRecent(ranked, _history.Recent);
        Results.Clear();
        foreach (var e in ranked) Results.Add(e);
        SelectedIndex = 0;   // always re-highlight the best match (top row) as the query changes
    }
}
