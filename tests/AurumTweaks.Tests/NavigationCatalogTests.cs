using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Guards the command palette's page catalog and — critically — the contract that every palette row actually
/// navigates somewhere. A renamed page key that drifts from <c>MainViewModel.Navigate</c> would otherwise leave a
/// row that looks like it jumps but goes nowhere: a dead button, which the honesty mandate forbids.
/// </summary>
public class NavigationCatalogTests
{
    [Fact]
    public void Pages_AreNonEmpty_Unique_AndWellFormed()
    {
        var pages = NavigationCatalog.Pages;
        Assert.NotEmpty(pages);
        Assert.All(pages, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Id), "page key is blank");
            Assert.False(string.IsNullOrWhiteSpace(p.Title), $"{p.Id} has a blank title");
            Assert.False(string.IsNullOrWhiteSpace(p.Group), $"{p.Id} has a blank group");
            Assert.Equal(PaletteEntryKind.Page, p.Kind);
        });

        var ids = pages.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // The anti-dead-button guard. Navigate resolves view-models through the static App.Services locator, which a
    // unit test can't stand up, so we read its switch arms straight from source and assert set-equality with the
    // catalog: no catalog row without a Navigate arm (dead row), and no navigable page missing from the palette.
    [Fact]
    public void CatalogKeys_AndNavigateKeys_StayInSync()
    {
        var source = File.ReadAllText(FindRepoFile("src", "AurumTweaks", "ViewModels", "MainViewModel.cs"));
        var navKeys = Regex.Matches(source, "\"([A-Za-z]+)\"\\s*=>")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var catalogKeys = NavigationCatalog.Pages.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        var deadRows = catalogKeys.Except(navKeys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var unreachable = navKeys.Except(catalogKeys).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(deadRows.Count == 0 && unreachable.Count == 0,
            "NavigationCatalog and MainViewModel.Navigate drifted.\n" +
            $"  dead palette rows (catalog key with no Navigate arm): {string.Join(", ", deadRows)}\n" +
            $"  pages missing from the palette catalog:               {string.Join(", ", unreachable)}");
    }

    // Walk up from the test's output directory until the source tree is found — robust to bin depth and the
    // absence of a working-dir convention. Throws (a real failure) if the repo layout can't be located at all.
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
