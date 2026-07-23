using System;
using System.Collections.Generic;
using System.IO;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure per-game OC matching + the binding→profile materialisation. The decision that will drive
/// a real GPU write must be exact and deterministic: case/whitespace-insensitive name match, disabled
/// bindings never fire, and an all-stock binding is recognised as a no-op so the watcher never applies
/// nothing. Voltage never appears in a binding's profile (Aurum never writes it).
/// </summary>
public class GameOcMatchingTests
{
    private static GameOcBinding B(string name, bool enabled = true, int core = 0, int power = 100)
        => new() { GameName = name, Enabled = enabled, CoreOffsetMhz = core, PowerLimitPct = power };

    [Fact]
    public void ForActiveGame_ExactNameMatch_ReturnsTheBinding()
    {
        var bindings = new[] { B("Cyberpunk 2077", core: 150), B("Valorant", core: 50) };
        var hit = GameOcMatching.ForActiveGame(bindings, "Cyberpunk 2077");
        Assert.NotNull(hit);
        Assert.Equal(150, hit!.CoreOffsetMhz);
    }

    [Theory]
    [InlineData("cyberpunk 2077")]      // case-insensitive
    [InlineData("  Cyberpunk 2077  ")]  // whitespace-insensitive
    public void ForActiveGame_IsCaseAndWhitespaceInsensitive(string active)
        => Assert.NotNull(GameOcMatching.ForActiveGame(new[] { B("Cyberpunk 2077", core: 150) }, active));

    [Fact]
    public void ForActiveGame_DisabledBinding_NeverMatches()
        => Assert.Null(GameOcMatching.ForActiveGame(new[] { B("Valorant", enabled: false, core: 50) }, "Valorant"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unbound Game")]
    public void ForActiveGame_NoMatchOrEmpty_ReturnsNull(string? active)
        => Assert.Null(GameOcMatching.ForActiveGame(new[] { B("Valorant", core: 50) }, active));

    [Fact]
    public void ForActiveGame_FirstEnabledMatchWins()
    {
        var bindings = new[] { B("Doom", core: 100), B("Doom", core: 200) };
        Assert.Equal(100, GameOcMatching.ForActiveGame(bindings, "Doom")!.CoreOffsetMhz);
    }

    [Fact]
    public void IsNonStock_TrueForAnyRealOc_FalseForAllNeutral()
    {
        Assert.False(GameOcMatching.IsNonStock(B("g")));                 // all neutral → no-op
        Assert.True(GameOcMatching.IsNonStock(B("g", core: 100)));
        Assert.True(GameOcMatching.IsNonStock(B("g", power: 120)));
        Assert.True(GameOcMatching.IsNonStock(new GameOcBinding { GameName = "g", AmdMaxFreqMhz = 2800 }));
    }

    [Fact]
    public void ToProfile_MaterialisesTheBoundAxes_AndNeverStoresVoltage()
    {
        var b = new GameOcBinding
        {
            GameName = "g", CoreOffsetMhz = 150, MemoryOffsetMhz = 1200, PowerLimitPct = 110, TempLimitC = 84,
            AmdMaxFreqMhz = 2800, AmdMaxVramFreqMhz = 2500,
        };
        var p = b.ToProfile();
        Assert.Equal(150, p.CoreOffsetMhz);
        Assert.Equal(1200, p.MemoryOffsetMhz);
        Assert.Equal(110, p.PowerLimitPct);
        Assert.Equal(84, p.TempLimitC);
        Assert.Equal(2800, p.AmdMaxFreqMhz);
        Assert.Equal(2500, p.AmdMaxVramFreqMhz);
        Assert.Equal(900, p.TargetVoltageMv);   // neutral placeholder — voltage is never applied
    }
}

/// <summary>Round-trips the binding store through a real temp file — persistence must survive the JSON
/// cycle and degrade to empty (never throw) on a missing or corrupt file.</summary>
public sealed class GameOcBindingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aurum-goc-" + Guid.NewGuid().ToString("N"));
    private string Path_ => System.IO.Path.Combine(_dir, "game-oc-bindings.json");

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void SaveThenLoad_PreservesEveryBoundAxis()
    {
        var store = new GameOcBindingStore(Path_);
        var bindings = new List<GameOcBinding>
        {
            new() { GameName = "Cyberpunk 2077", Platform = "Steam", CoreOffsetMhz = 150, MemoryOffsetMhz = 1200,
                    PowerLimitPct = 112, TempLimitC = 84, AmdMaxFreqMhz = 2850, AmdMaxVramFreqMhz = 2600, Enabled = true },
        };
        store.Save(bindings);

        var loaded = store.Load();
        Assert.Single(loaded);
        Assert.Equal("Cyberpunk 2077", loaded[0].GameName);
        Assert.Equal(150, loaded[0].CoreOffsetMhz);
        Assert.Equal(112, loaded[0].PowerLimitPct);
        Assert.Equal(2850, loaded[0].AmdMaxFreqMhz);
        Assert.True(loaded[0].Enabled);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty_NeverThrows()
        => Assert.Empty(new GameOcBindingStore(Path_).Load());

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty_NeverThrows()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not valid json ]");
        Assert.Empty(new GameOcBindingStore(Path_).Load());
    }
}
