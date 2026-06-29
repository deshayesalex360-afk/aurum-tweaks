using System;
using System.Linq;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="PowerSchemeParser"/> — the read of <c>powercfg /list</c> stdout behind the Alimentation page.
/// The load-bearing honesty point: scheme identity rides on the GUID (pure ASCII, code-page-immune), the active
/// plan is exactly the one powercfg flags with a trailing <c>*</c>, and lines without a GUID (the header, the
/// separator dashes) contribute no scheme — we never invent a plan that powercfg didn't list.
/// </summary>
public class PowerSchemeParserTests
{
    // A representative `powercfg /list` block: header, separator, three stock schemes, the first one active.
    private const string SampleList = @"
Existing Power Schemes (* Active)
-----------------------------------
Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *
Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)
Power Scheme GUID: a1841308-3541-4fab-bc81-f71556f20b4a  (Power saver)
";

    [Fact]
    public void ParseList_ReadsEverySchemeWithGuidAndName()
    {
        var schemes = PowerSchemeParser.ParseList(SampleList);

        Assert.Equal(3, schemes.Count);
        Assert.Equal(PowerSchemeCatalog.Balanced, schemes[0].Id);
        Assert.Equal("Balanced", schemes[0].Name);
        Assert.Equal("High performance", schemes[1].Name);
        Assert.Equal("Power saver", schemes[2].Name);
    }

    [Fact]
    public void ParseList_MarksOnlyTheStarredSchemeActive()
    {
        var schemes = PowerSchemeParser.ParseList(SampleList);

        Assert.Single(schemes, s => s.IsActive);
        Assert.True(schemes.Single(s => s.Id == PowerSchemeCatalog.Balanced).IsActive);
        Assert.False(schemes.Single(s => s.Id == PowerSchemeCatalog.HighPerformance).IsActive);
    }

    [Fact]
    public void ParseList_SkipsHeaderAndSeparatorLines()
        // Only the GUID-bearing lines become schemes — the title and the dashes are not mistaken for plans.
        => Assert.Empty(PowerSchemeParser.ParseList("Existing Power Schemes (* Active)\n-----------------------------------"));

    [Fact]
    public void ParseList_ActiveMarker_SurvivesTrailingWhitespace()
    {
        var schemes = PowerSchemeParser.ParseList(
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *   ");
        Assert.True(Assert.Single(schemes).IsActive);
    }

    [Fact]
    public void ParseList_HandlesCrlfLineEndings()
    {
        var crlf = string.Join("\r\n",
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *",
            "Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)");
        var schemes = PowerSchemeParser.ParseList(crlf);

        Assert.Equal(2, schemes.Count);
        Assert.Equal("Balanced", schemes[0].Name);   // the trailing '\r' is trimmed, name is clean
    }

    [Fact]
    public void ParseList_GuidLineWithoutName_KeepsGuidNameEmpty()
    {
        var scheme = Assert.Single(PowerSchemeParser.ParseList(
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e"));
        Assert.Equal(PowerSchemeCatalog.Balanced, scheme.Id);
        Assert.Equal("", scheme.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseList_EmptyOrNull_ReturnsEmpty(string? raw)
        => Assert.Empty(PowerSchemeParser.ParseList(raw));

    [Fact]
    public void FirstGuid_ReturnsTheCreatedScheme_FromDuplicateOutput()
    {
        var created = PowerSchemeParser.FirstGuid(
            "Power Scheme GUID: 11111111-2222-3333-4444-555555555555  (Performances ultimes)");
        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), created);
    }

    [Theory]
    [InlineData("no guid here")]
    [InlineData("")]
    [InlineData(null)]
    public void FirstGuid_WithoutAGuid_ReturnsNull(string? raw)
        => Assert.Null(PowerSchemeParser.FirstGuid(raw));
}
