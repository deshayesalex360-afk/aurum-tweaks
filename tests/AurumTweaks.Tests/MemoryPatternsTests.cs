using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Exhaustive tests for the pure pattern primitives. The point is to prove the moving-inversions
/// machinery actually <em>catches</em> a deliberately injected bit-flip at exactly the right word —
/// because that is the only thing standing between the UI saying "STABLE" and it being a lie.
/// </summary>
public class MemoryPatternsTests
{
    private const ulong AA = 0xAAAAAAAAAAAAAAAAUL;
    private const ulong Comp = ~AA; // 0x5555...

    private static ulong[] Buffer(int words, ulong fill)
    {
        var b = new ulong[words];
        MemoryPatterns.Fill(b, fill);
        return b;
    }

    [Fact]
    public void Fill_Then_VerifyConstant_RoundTrips()
    {
        var buf = Buffer(64, AA);
        Assert.False(MemoryPatterns.VerifyConstant(buf, AA).HasError);
    }

    [Fact]
    public void VerifyConstant_CatchesInjectedFlip_AtCorrectIndex_WithActualValue()
    {
        var buf = Buffer(64, AA);
        buf[37] = 0xDEADBEEFUL;

        var r = MemoryPatterns.VerifyConstant(buf, AA);

        Assert.True(r.HasError);
        Assert.Equal(37, r.FirstBadIndex);
        Assert.Equal(AA, r.Expected);
        Assert.Equal(0xDEADBEEFUL, r.Actual);
    }

    [Fact]
    public void VerifyConstant_ReportsFirstBadIndex_WhenMultipleErrors()
    {
        var buf = Buffer(64, AA);
        buf[50] = 1;
        buf[10] = 2;

        var r = MemoryPatterns.VerifyConstant(buf, AA);

        Assert.Equal(10, r.FirstBadIndex);   // lowest index wins on a forward scan
        Assert.Equal(2UL, r.Actual);
    }

    [Fact]
    public void CheckThenWrite_Ascending_HappyPath_LeavesBufferAsReplacement()
    {
        var buf = Buffer(64, AA);

        var r = MemoryPatterns.CheckThenWrite(buf, AA, Comp, ascending: true);

        Assert.False(r.HasError);
        Assert.False(MemoryPatterns.VerifyConstant(buf, Comp).HasError); // whole buffer is now the complement
    }

    [Fact]
    public void CheckThenWrite_Descending_HappyPath_LeavesBufferAsReplacement()
    {
        var buf = Buffer(64, Comp);

        var r = MemoryPatterns.CheckThenWrite(buf, Comp, AA, ascending: false);

        Assert.False(r.HasError);
        Assert.False(MemoryPatterns.VerifyConstant(buf, AA).HasError);
    }

    [Fact]
    public void CheckThenWrite_CatchesFlip_AndStillCompletesTheOverwrite()
    {
        var buf = Buffer(64, AA);
        buf[12] = 0; // a cell that didn't hold the pattern

        var r = MemoryPatterns.CheckThenWrite(buf, AA, Comp, ascending: true);

        Assert.True(r.HasError);
        Assert.Equal(12, r.FirstBadIndex);
        Assert.Equal(AA, r.Expected);
        Assert.Equal(0UL, r.Actual);
        // Crucially the sweep continued: the buffer is uniformly the replacement, ready for the next phase.
        Assert.False(MemoryPatterns.VerifyConstant(buf, Comp).HasError);
    }

    [Fact]
    public void CheckThenWrite_Ascending_ReportsLowestBadIndex()
    {
        var buf = Buffer(64, AA);
        buf[40] = 7;
        buf[5] = 9;

        var r = MemoryPatterns.CheckThenWrite(buf, AA, Comp, ascending: true);

        Assert.Equal(5, r.FirstBadIndex);
        Assert.Equal(9UL, r.Actual);
    }

    [Fact]
    public void CheckThenWrite_Descending_ReportsHighestBadIndex()
    {
        var buf = Buffer(64, AA);
        buf[40] = 7;
        buf[5] = 9;

        var r = MemoryPatterns.CheckThenWrite(buf, AA, Comp, ascending: false);

        Assert.Equal(40, r.FirstBadIndex);   // first encountered when walking high→low
        Assert.Equal(7UL, r.Actual);
    }

    [Fact]
    public void Addressing_RoundTrips_WithNonZeroBase()
    {
        var buf = new ulong[128];
        const long baseIdx = 1_000_000;

        MemoryPatterns.FillAddressing(buf, baseIdx);

        Assert.Equal((ulong)(baseIdx + 0), buf[0]);
        Assert.Equal((ulong)(baseIdx + 127), buf[127]);
        Assert.False(MemoryPatterns.VerifyAddressing(buf, baseIdx).HasError);
    }

    [Fact]
    public void VerifyAddressing_DetectsAliasing_WhenTwoCellsSwap()
    {
        var buf = new ulong[128];
        const long baseIdx = 0;
        MemoryPatterns.FillAddressing(buf, baseIdx);

        // Simulate a stuck address line: cell 20 ends up holding cell 84's value.
        buf[20] = buf[84];

        var r = MemoryPatterns.VerifyAddressing(buf, baseIdx);

        Assert.True(r.HasError);
        Assert.Equal(20, r.FirstBadIndex);
        Assert.Equal(20UL, r.Expected);
        Assert.Equal(84UL, r.Actual);
    }
}
