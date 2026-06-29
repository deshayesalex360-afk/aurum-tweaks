using System;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="RegistryValue"/> — the pure DWord/QWord parser + comparator extracted from
/// <see cref="RegistryService"/>. The honesty point: a registry DWord is naturally authored in hex ("0x1",
/// the form regedit and every tweak guide show) and big flags as unsigned decimals ("4294967295"). The old
/// <c>int.Parse(.., NumberStyles.Any)</c> accepted neither, so such a tweak would throw inside the writer,
/// return false, and silently do nothing — while still being reported as applied. These accept all three
/// forms and fold them to the same bit pattern, so write and read-back agree and IsApplied stays truthful.
/// </summary>
public class RegistryValueTests
{
    // ---- DWord parsing: decimal-signed, decimal-unsigned, and hex all welcome ----

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("-1", -1)]
    [InlineData("20000", 20000)]
    [InlineData("2147483647", 2147483647)]   // Int32.MaxValue
    [InlineData("0x1", 1)]
    [InlineData("0xFF", 255)]
    [InlineData("0xff", 255)]                 // lowercase hex digits
    [InlineData("0XFF", 255)]                 // uppercase 0X prefix
    [InlineData("0xFFFFFFFF", -1)]            // unsigned 32-bit max folds to -1 (same bit pattern)
    [InlineData("4294967295", -1)]            // unsigned decimal beyond Int32 wraps identically
    [InlineData("  5  ", 5)]                  // surrounding whitespace tolerated
    public void ParseDword_AcceptsDecimalSignedUnsignedAndHex(string input, int expected)
        => Assert.Equal(expected, RegistryValue.ParseDword(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0xZZ")]
    [InlineData(null)]
    public void TryParseDword_RejectsGarbage(string? input)
        => Assert.False(RegistryValue.TryParseDword(input, out _));

    [Fact]
    public void ParseDword_Throws_OnGarbage_SoTheWriterCanFailLoudlyNotSilently()
        => Assert.Throws<FormatException>(() => RegistryValue.ParseDword("nope"));

    // ---- QWord parsing: same rules, full 64-bit range ----

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("-1", -1L)]
    [InlineData("4294967296", 4294967296L)]            // 2^32 — proves it isn't truncated to 32-bit
    [InlineData("0x100000000", 4294967296L)]
    [InlineData("0xFFFFFFFFFFFFFFFF", -1L)]            // unsigned 64-bit max folds to -1
    public void ParseQword_AcceptsDecimalAndHex(string input, long expected)
        => Assert.Equal(expected, RegistryValue.ParseQword(input));

    // ---- Matches: the IsApplied comparison ----

    [Theory]
    [InlineData("1", "0x1", true)]     // headline: hex apply value vs the decimal the registry reads back
    [InlineData("255", "0xFF", true)]
    [InlineData("0xFF", "0xFF", true)] // (the in-memory fake stores the raw hex — must still match)
    [InlineData("1", "2", false)]
    [InlineData("0", "1", false)]
    public void Matches_Dword_ComparesNumerically(string readBack, string expected, bool match)
        => Assert.Equal(match, RegistryValue.Matches(readBack, expected, RegistryValueType.DWord));

    [Theory]
    [InlineData("4294967296", "0x100000000", true)]   // 2^32 across decimal/hex — proves QWord compares full-width, not truncated to 32-bit
    [InlineData("4294967296", "4294967297", false)]
    public void Matches_Qword_ComparesNumerically(string readBack, string expected, bool match)
        => Assert.Equal(match, RegistryValue.Matches(readBack, expected, RegistryValueType.QWord));

    [Theory]
    [InlineData("High", "High", true)]
    [InlineData("High", "high", true)]   // ordinal-ignore-case for strings
    [InlineData("High", "Medium", false)]
    public void Matches_String_ComparesOrdinalIgnoreCase(string readBack, string expected, bool match)
        => Assert.Equal(match, RegistryValue.Matches(readBack, expected, RegistryValueType.String));

    [Fact]
    public void Matches_Dword_WithNonNumericReadBack_IsFalse_NotAThrow()
        => Assert.False(RegistryValue.Matches("garbage", "1", RegistryValueType.DWord));

    [Fact]
    public void Matches_NullHandling_BothNullMatch_OneNullDoesNot()
    {
        Assert.True(RegistryValue.Matches(null, null, RegistryValueType.DWord));
        Assert.False(RegistryValue.Matches(null, "1", RegistryValueType.DWord));
        Assert.False(RegistryValue.Matches("1", null, RegistryValueType.DWord));
    }
}
