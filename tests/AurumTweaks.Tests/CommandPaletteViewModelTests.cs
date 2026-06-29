using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.ViewModels;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Drives the palette state holder against the real <see cref="NavigationCatalog"/> plus fake tweaks. Verifies the
/// open/close lifecycle, that the query filters live, that wrap-around selection behaves, and — the honesty
/// invariant — that Activate raises navigation for the highlighted row and does nothing on an empty result set.
/// </summary>
public class CommandPaletteViewModelTests
{
    private static Tweak Tw(string id, string frName, string frDesc = "")
        => new()
        {
            Id = id,
            Name = new() { ["fr"] = frName },
            Description = new() { ["fr"] = frDesc },
            Category = TweakCategory.Gaming
        };

    private static CommandPaletteViewModel Make(params Tweak[] tweaks)
        => Make(new NavigationHistory(), tweaks);

    private static CommandPaletteViewModel Make(INavigationHistory history, params Tweak[] tweaks)
        => new(new FakeTweakRepository(tweaks), new FakeLocalizationService(), history);

    [Fact]
    public async Task Open_WithEmptyQuery_ShowsEveryPageActionAndTweak()
    {
        var vm = Make(Tw("t1", "Tweak un"), Tw("t2", "Tweak deux"));
        await vm.Initialization;

        vm.Open();

        Assert.True(vm.IsOpen);
        Assert.Equal(NavigationCatalog.Pages.Count + PaletteActionCatalog.Actions.Count + 2, vm.Results.Count);
        Assert.Equal(0, vm.SelectedIndex);
    }

    [Fact]
    public void Open_WithRecentPages_BubblesThemToTheTop_NewestFirst()
    {
        // The MRU launcher behaviour: on an empty query the pages you just visited sit at the top, most-recent
        // first — without dropping or duplicating any other row.
        var history = new NavigationHistory();
        history.Record("Audio");
        history.Record("Dns");          // most recently visited
        var vm = Make(history);

        vm.Open();                      // empty query → full list, recents floated up

        Assert.Equal("Dns", vm.Results[0].Id);
        Assert.Equal("Audio", vm.Results[1].Id);
        Assert.Equal(0, vm.SelectedIndex);
        Assert.Equal(NavigationCatalog.Pages.Count + PaletteActionCatalog.Actions.Count, vm.Results.Count);
    }

    [Fact]
    public void Close_HidesAndClearsQuery()
    {
        var vm = Make();
        vm.Open();
        vm.Query = "abc";

        vm.Close();

        Assert.False(vm.IsOpen);
        Assert.Equal(string.Empty, vm.Query);
    }

    [Fact]
    public void Query_FiltersToMatchingEntries()
    {
        var vm = Make();
        vm.Open();

        vm.Query = "dns";

        Assert.Contains(vm.Results, e => e.Id == "Dns");
        Assert.True(vm.Results.Count < NavigationCatalog.Pages.Count);
    }

    [Fact]
    public async Task TweakRows_AreSearchable_AfterInitialization()
    {
        var vm = Make(Tw("disable-cortana", "Désactiver Cortana"));
        await vm.Initialization;
        vm.Open();

        vm.Query = "cortana";

        Assert.Contains(vm.Results,
            e => e.Kind == PaletteEntryKind.Tweak && e.Title == "Désactiver Cortana");
    }

    [Fact]
    public async Task TweakRows_AreSearchable_ByWhatTheyTouch()
    {
        // The power-user path: someone who knows the registry value but not the tweak's French name should still
        // find it. The operation target is folded into the hidden keywords, never shown on the row.
        var t = new Tweak
        {
            Id = "net-throttle",
            Name = new() { ["fr"] = "Désactiver le throttling réseau" },
            Category = TweakCategory.NetworkLatency,
            Operations =
            {
                new TweakOperation
                {
                    Type = OperationType.Registry,
                    Hive = "HKLM",
                    Key = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\System Profile",
                    Name = "NetworkThrottlingIndex",
                    Apply = "ffffffff",
                    Revert = "10"
                }
            }
        };
        var vm = Make(t);
        await vm.Initialization;
        vm.Open();

        vm.Query = "NetworkThrottlingIndex";

        Assert.Contains(vm.Results, e => e.Kind == PaletteEntryKind.Tweak && e.Id == "net-throttle");
    }

    [Fact]
    public void ActionRows_AreSearchable_FromTheFirstOpen()
    {
        // Actions are a static catalog, so they're searchable immediately — no Initialization await (which gates
        // only the tweak rows). "rapport" hits the « Exporter le rapport système » title.
        var vm = Make();
        vm.Open();

        vm.Query = "rapport";

        Assert.Contains(vm.Results,
            e => e.Kind == PaletteEntryKind.Action && e.Id == "ExportSystemReport");
    }

    [Fact]
    public void MoveSelection_WrapsAtBothEnds()
    {
        var vm = Make();
        vm.Open();                       // empty query → full list
        var n = vm.Results.Count;
        Assert.True(n > 1);

        vm.SelectedIndex = 0;
        vm.MoveSelection(-1);
        Assert.Equal(n - 1, vm.SelectedIndex);   // Up from the top wraps to the bottom

        vm.MoveSelection(1);
        Assert.Equal(0, vm.SelectedIndex);       // Down from the bottom wraps to the top
    }

    [Fact]
    public void Activate_RaisesNavigation_ForHighlightedRow_AndCloses()
    {
        var vm = Make();
        PaletteEntry? navigated = null;
        vm.NavigationRequested += (_, e) => navigated = e;

        vm.Open();
        vm.Query = "dns";   // the DNS page is the unambiguous top-ranked row
        vm.Activate();

        Assert.NotNull(navigated);
        Assert.Equal("Dns", navigated!.Id);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void Activate_WithNoResults_IsHonestNoOp()
    {
        var vm = Make();
        PaletteEntry? navigated = null;
        vm.NavigationRequested += (_, e) => navigated = e;

        vm.Open();
        vm.Query = "zzzzzzzz";   // matches nothing
        Assert.Empty(vm.Results);

        vm.Activate();

        Assert.Null(navigated);
    }
}
