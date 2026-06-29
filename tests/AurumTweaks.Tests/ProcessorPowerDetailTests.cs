using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="PowerCfgProcessorQuery.ParseCurrentAcDc"/> — the read of a single-setting <c>powercfg /q</c> dump that
/// feeds the Alimentation page's « Détail du plan actif (processeur) » card. The load-bearing honesty points: the (AC, DC)
/// pair rides on the ASCII <c>0x…</c> tokens (code-page-immune, so a mojibaked French console can't corrupt it), powercfg
/// always prints the two current indices LAST so the rule is version-stable, and an unreadable query yields
/// <c>(null, null)</c> — an honest « indisponible », never a fabricated zero. GUIDs (dashed, no <c>0x</c>) are never
/// mistaken for values.
/// </summary>
public class PowerCfgProcessorQueryTests
{
    // A representative English single-setting dump: possible range first, then current AC (100) and DC (50).
    private const string EnglishMaxState = @"
Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
  Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)
    Power Setting GUID: bc5038f7-23e0-4960-96da-33abaf5935ec  (Maximum processor state)
      Minimum Possible Setting: 0x00000000
      Maximum Possible Setting: 0x00000064
      Possible Settings increment: 0x00000001
      Possible Settings units: %
    Current AC Power Setting Index: 0x00000064
    Current DC Power Setting Index: 0x00000032
";

    // The same query as a non-English (French) console would print it — localized labels, identical ASCII hex.
    private const string FrenchMinState = @"
GUID du mode de gestion de l'alimentation : 381b4222-f694-41f0-9685-ff5bb260df2e  (Utilisation normale)
  GUID de sous-groupe d'alimentation : 54533251-82be-4824-96c1-47b60b740d00  (Gestion de l'alimentation du processeur)
    GUID de paramètre d'alimentation : 893dee8e-2bef-41e0-89c6-b55d0929964c  (État minimal du processeur)
      Valeur d'index minimale possible du paramètre d'alimentation : 0x00000000
      Valeur d'index maximale possible du paramètre d'alimentation : 0x00000064
      Incrément des valeurs d'index possibles du paramètre d'alimentation : 0x00000001
      Unités des valeurs d'index possibles du paramètre d'alimentation :
    Index de paramètre d'alimentation actuel d'alimentation CA : 0x00000005
    Index de paramètre d'alimentation actuel d'alimentation CC : 0x00000005
";

    [Fact]
    public void ParseCurrentAcDc_ReadsAcThenDc_FromEnglishDump()
    {
        var (ac, dc) = PowerCfgProcessorQuery.ParseCurrentAcDc(EnglishMaxState);
        Assert.Equal(100, ac);   // AC is the second-to-last 0x token
        Assert.Equal(50, dc);    // DC is the last — never swapped
    }

    [Fact]
    public void ParseCurrentAcDc_SurvivesLocalizedLabels_ReadingHexOnly()
    {
        var (ac, dc) = PowerCfgProcessorQuery.ParseCurrentAcDc(FrenchMinState);
        Assert.Equal(5, ac);
        Assert.Equal(5, dc);
    }

    [Fact]
    public void ParseCurrentAcDc_ParsesUppercaseHex()
    {
        // FF sits in the AC slot (second-to-last token); the trailing 0 is DC.
        var (ac, dc) = PowerCfgProcessorQuery.ParseCurrentAcDc("a: 0x000000FF\nb: 0x00000000");
        Assert.Equal(255, ac);
        Assert.Equal(0, dc);
    }

    [Fact]
    public void ParseCurrentAcDc_GuidsAreNotMistakenForValues()
        // Two dashed GUIDs, zero 0x tokens — the regex requires the 0x prefix, so nothing is read.
        => Assert.Equal((null, null), PowerCfgProcessorQuery.ParseCurrentAcDc(
            "Power Setting GUID: bc5038f7-23e0-4960-96da-33abaf5935ec  (Maximum processor state)\n" +
            "Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00"));

    [Fact]
    public void ParseCurrentAcDc_SingleHexToken_IsTreatedAsUnreadable()
        => Assert.Equal((null, null), PowerCfgProcessorQuery.ParseCurrentAcDc("only one: 0x00000064"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("no hex anywhere in this line")]
    public void ParseCurrentAcDc_EmptyOrHexless_ReturnsNulls(string? raw)
        => Assert.Equal((null, null), PowerCfgProcessorQuery.ParseCurrentAcDc(raw));
}

/// <summary>
/// Pins <see cref="ProcessorPowerDetail"/> — the honest display + interpretation of the active plan's processor knobs.
/// The honesty points: an unread value renders « — » (never 0 %), core parking is named by its real effect (a 100 %
/// unparked floor IS parking disabled), the interpretation is factual frequency behaviour with no invented FPS, and a
/// total read failure says « indisponible » rather than a clean-looking all-zero sheet. A frequency cap (max &lt; 100)
/// outranks the minimum-state line because it is the more decision-relevant anomaly.
/// </summary>
public class ProcessorPowerDetailTests
{
    private static ProcessorPowerDetail Detail(int? min, int? max, int? cores, bool ok = true) =>
        new(min, max, cores, ok);

    [Fact]
    public void StateDisplays_RenderPercent_OrDashWhenNull()
    {
        var d = Detail(100, 100, 100);
        Assert.Equal("100 %", d.MinStateDisplay);
        Assert.Equal("100 %", d.MaxStateDisplay);

        var blank = Detail(null, null, null, ok: false);
        Assert.Equal("—", blank.MinStateDisplay);
        Assert.Equal("—", blank.MaxStateDisplay);
    }

    [Fact]
    public void CoreParking_AtFullFloor_ReadsAsDisabled()
        => Assert.Contains("Désactivé", Detail(100, 100, 100).CoreParkingDisplay);

    [Fact]
    public void CoreParking_BelowFullFloor_NamesTheParkablePercent()
    {
        var display = Detail(100, 100, 90).CoreParkingDisplay;
        Assert.Contains("Actif", display);
        Assert.Contains("10 %", display);   // 100 - 90 cores can be parked
    }

    [Fact]
    public void CoreParking_Unknown_RendersDash()
        => Assert.Equal("—", Detail(100, 100, null).CoreParkingDisplay);

    [Fact]
    public void Interpretation_TotalFailure_SaysUnavailable()
        => Assert.Contains("indisponible", Detail(null, null, null, ok: false).Interpretation);

    [Fact]
    public void Interpretation_FrequencyCap_IsFlagged_AndOutranksMinState()
    {
        // Even with a max-performance minimum state, a max-state cap is the headline anomaly.
        var text = Detail(100, 80, 100).Interpretation;
        Assert.Contains("bridée à 80 %", text);
        Assert.DoesNotContain("ne réduit jamais", text);
    }

    [Fact]
    public void Interpretation_FullMinState_SaysNeverDownclocks()
        => Assert.Contains("ne réduit jamais", Detail(100, 100, 100).Interpretation);

    [Fact]
    public void Interpretation_LowMinState_SaysItDownclocksAtRest()
    {
        var text = Detail(5, 100, 100).Interpretation;
        Assert.Contains("réduit sa fréquence", text);
        Assert.Contains("5 %", text);
    }
}
