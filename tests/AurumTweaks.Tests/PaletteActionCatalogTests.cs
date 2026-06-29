using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Guards the command palette's global-action catalog and — critically — the contract that every action row
/// actually runs something. An action id that drifts from <c>MainViewModel.RunPaletteAction</c> would leave a row
/// that looks runnable but does nothing: a dead button, which the honesty mandate forbids. The action half of the
/// palette; mirrors <see cref="NavigationCatalogTests"/> for pages.
/// </summary>
public class PaletteActionCatalogTests
{
    [Fact]
    public void Actions_AreNonEmpty_Unique_AndWellFormed()
    {
        var actions = PaletteActionCatalog.Actions;
        Assert.NotEmpty(actions);
        Assert.All(actions, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Id), "action id is blank");
            Assert.False(string.IsNullOrWhiteSpace(a.Title), $"{a.Id} has a blank title");
            Assert.False(string.IsNullOrWhiteSpace(a.Group), $"{a.Id} has a blank group");
            Assert.Equal(PaletteEntryKind.Action, a.Kind);
        });

        var ids = actions.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // Actions and pages share one palette universe and are dispatched by Kind; a shared id would be ambiguous (which
    // row did the user mean?). Keep the two id spaces disjoint so the dispatch stays unambiguous.
    [Fact]
    public void ActionIds_DoNotCollideWithPageKeys()
    {
        var actionIds = PaletteActionCatalog.Actions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var pageKeys = NavigationCatalog.Pages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        var collisions = actionIds.Intersect(pageKeys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(collisions.Count == 0, $"action id(s) collide with page keys: {string.Join(", ", collisions)}");
    }

    // The anti-dead-action guard, mirroring NavigationCatalogTests. RunPaletteAction dispatches through the static
    // App.Services locator a unit test can't stand up, so we read its string-literal case labels straight from source
    // and assert set-equality with the catalog: no catalog action without a dispatch arm (dead action), and no
    // dispatch arm without a catalog row (an action you could run but never find in the palette).
    [Fact]
    public void ActionIds_AndDispatchArms_StayInSync()
    {
        var source = File.ReadAllText(FindRepoFile("src", "AurumTweaks", "ViewModels", "MainViewModel.cs"));
        var dispatchIds = Regex.Matches(source, "case\\s+\"([A-Za-z]+)\"\\s*:")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var catalogIds = PaletteActionCatalog.Actions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        var deadActions = catalogIds.Except(dispatchIds).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var unreachable = dispatchIds.Except(catalogIds).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(deadActions.Count == 0 && unreachable.Count == 0,
            "PaletteActionCatalog and MainViewModel.RunPaletteAction drifted.\n" +
            $"  dead actions (catalog id with no dispatch arm): {string.Join(", ", deadActions)}\n" +
            $"  dispatch arms missing from the catalog:         {string.Join(", ", unreachable)}");
    }

    // Walk up from the test's output directory until the source tree is found — robust to bin depth and the absence
    // of a working-dir convention. Throws (a real failure) if the repo layout can't be located at all.
    private static string FindRepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not locate {string.Join('/', parts)} from {AppContext.BaseDirectory}");
    }
}
