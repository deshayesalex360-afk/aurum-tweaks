using System;

namespace AurumTweaks.Services;

/// <summary>
/// Pure, allocation-free pattern primitives for the memory stability test — the part that is
/// fully unit-testable without touching real RAM pressure or threads. Everything here operates on
/// a caller-supplied <see cref="ulong"/>[] buffer so the tests can inject a deliberate bit-flip and
/// assert it is caught at exactly the right word index.
///
/// <para>The algorithm is "moving inversions" (the same idea behind memtest86's classic test): write a
/// pattern, read it back while overwriting with its complement, then read the complement back while
/// restoring the pattern. A cell that can't hold a value, or that flips a neighbour, is exposed by the
/// read-back. We also run an "own-address" pass that stores each cell's global index, which catches
/// stuck address lines (two cells aliasing the same storage).</para>
/// </summary>
public static class MemoryPatterns
{
    /// <summary>The fixed test patterns, walked in order each pass. 64-bit words so one store covers 8 bytes.</summary>
    public static readonly (string Name, ulong Value)[] WordPatterns =
    {
        ("0x00", 0x0000000000000000UL),
        ("0xFF", 0xFFFFFFFFFFFFFFFFUL),
        ("0xAA", 0xAAAAAAAAAAAAAAAAUL),
        ("0x55", 0x5555555555555555UL),
    };

    /// <summary>
    /// Result of a verification sweep. <see cref="FirstBadIndex"/> is -1 when the buffer matched;
    /// otherwise it is the first mismatching word index with the value we read vs. what we expected.
    /// </summary>
    public readonly record struct ScanResult(long FirstBadIndex, ulong Expected, ulong Actual)
    {
        public bool HasError => FirstBadIndex >= 0;
        public static ScanResult Ok => new(-1, 0, 0);
    }

    /// <summary>Write <paramref name="value"/> into every word of the buffer.</summary>
    public static void Fill(ulong[] buffer, ulong value)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = value;
    }

    /// <summary>Read-only check that every word equals <paramref name="expected"/>. Stops at the first mismatch.</summary>
    public static ScanResult VerifyConstant(ulong[] buffer, ulong expected)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != expected)
                return new ScanResult(i, expected, buffer[i]);
        }
        return ScanResult.Ok;
    }

    /// <summary>
    /// The heart of moving-inversions: walk the buffer (ascending or descending), verify each word equals
    /// <paramref name="expected"/>, then immediately overwrite it with <paramref name="replacement"/>.
    /// The very first mismatch is recorded, but the sweep continues so the whole buffer is left holding
    /// <paramref name="replacement"/> ready for the next phase. Walking in both directions matters: it
    /// exposes a cell that only fails when its neighbour was written just before (or after) it.
    /// </summary>
    public static ScanResult CheckThenWrite(ulong[] buffer, ulong expected, ulong replacement, bool ascending)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var result = ScanResult.Ok;

        if (ascending)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                ulong actual = buffer[i];
                if (actual != expected && !result.HasError)
                    result = new ScanResult(i, expected, actual);
                buffer[i] = replacement;
            }
        }
        else
        {
            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                ulong actual = buffer[i];
                if (actual != expected && !result.HasError)
                    result = new ScanResult(i, expected, actual);
                buffer[i] = replacement;
            }
        }

        return result;
    }

    /// <summary>
    /// Own-address test: store each word's <em>global</em> index (<paramref name="baseWordIndex"/> + local i).
    /// Because every cell gets a unique value, a stuck or aliased address line shows up as a cell reading back
    /// a different cell's index.
    /// </summary>
    public static void FillAddressing(ulong[] buffer, long baseWordIndex)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = unchecked((ulong)(baseWordIndex + i));
    }

    /// <summary>Verify the own-address pattern written by <see cref="FillAddressing"/>.</summary>
    public static ScanResult VerifyAddressing(ulong[] buffer, long baseWordIndex)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (int i = 0; i < buffer.Length; i++)
        {
            ulong expected = unchecked((ulong)(baseWordIndex + i));
            if (buffer[i] != expected)
                return new ScanResult(i, expected, buffer[i]);
        }
        return ScanResult.Ok;
    }
}
