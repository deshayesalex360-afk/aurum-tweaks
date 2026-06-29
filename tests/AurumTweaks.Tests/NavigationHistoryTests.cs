using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the session navigation trail behind the palette's "recent first" ordering: newest-first order,
/// de-duplication (re-visiting promotes rather than duplicates), a small bound that drops the oldest, and
/// blank keys ignored. Pure in-memory list behaviour — no I/O.
/// </summary>
public class NavigationHistoryTests
{
    [Fact]
    public void Record_PutsNewestFirst()
    {
        var h = new NavigationHistory();
        h.Record("A");
        h.Record("B");
        h.Record("C");

        Assert.Equal(new[] { "C", "B", "A" }, h.Recent);
    }

    [Fact]
    public void Record_Revisiting_PromotesWithoutDuplicating()
    {
        var h = new NavigationHistory();
        h.Record("A");
        h.Record("B");
        h.Record("A");   // re-visit the older page

        Assert.Equal(new[] { "A", "B" }, h.Recent);   // A floated to front, not duplicated
    }

    [Fact]
    public void Record_IsBounded_DroppingTheOldest()
    {
        var h = new NavigationHistory();
        for (var i = 0; i < 12; i++) h.Record($"P{i}");   // cap is 8

        Assert.Equal(8, h.Recent.Count);
        Assert.Equal("P11", h.Recent[0]);        // newest kept
        Assert.Equal("P4", h.Recent[7]);         // oldest still inside the window
        Assert.DoesNotContain("P3", h.Recent);   // evicted past the cap
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_IgnoresBlankKeys(string? key)
    {
        var h = new NavigationHistory();
        h.Record(key!);

        Assert.Empty(h.Recent);
    }
}
