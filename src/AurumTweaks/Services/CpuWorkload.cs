using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AurumTweaks.Services;

/// <summary>
/// The pure, deterministic kernel of the CPU stability test. <see cref="Compute"/> mixes integer ALU work
/// (a splitmix64-style hash) with IEEE-754 floating-point work (only +, −, ×, which are correctly rounded
/// and therefore bit-reproducible across conforming x64 hardware) into a single 64-bit checksum.
///
/// <para><see cref="ComputeVector"/> is the AVX2 sibling: it runs four independent lanes at once on the
/// 256-bit FP and integer units — closer to the SIMD load that games, encoders and renderers put on a chip,
/// and therefore better at exposing an undervolt / Curve Optimizer that's stable on scalar code but folds
/// under vector load. It uses <b>only</b> bit-exact operations (packed double +, −, × and packed 64-bit
/// XOR / shift / add — no reciprocal/rsqrt approximations, no FMA contraction), so the AVX2 path and the
/// scalar emulation (<see cref="ComputeVectorScalar"/>) return <b>bit-identical</b> checksums. That keeps the
/// test self-verifying and the unit-test vectors portable across machines with or without AVX2.</para>
///
/// <para>The property the whole test rests on: for a fixed <c>(seed, iterations)</c>, a correctly functioning
/// CPU always returns the <b>same</b> checksum. An unstable core (too-aggressive OC, insufficient vcore, a
/// Curve Optimizer offset that's too negative) occasionally computes a wrong bit and returns a different
/// value — which the service flags as an error. No transcendental functions are used, so results don't
/// depend on the libm implementation and the unit tests are portable.</para>
/// </summary>
public static class CpuWorkload
{
    public const ulong DefaultSeed = 0xC0FFEE0123456789UL;

    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;
    private const ulong Mix1 = 0xBF58476D1CE4E5B9UL;
    private const ulong Mix2 = 0x94D049BB133111EBUL;

    // A factor just above 1 so the FP accumulator drifts every iteration (correctly rounded ⇒ deterministic).
    private const double FpFactor = 1.0000000037;

    /// <summary>True when the running CPU exposes AVX2 — i.e. <see cref="ComputeVector"/> will use the
    /// 256-bit intrinsic path rather than the scalar emulation.</summary>
    public static bool Avx2Available => Avx2.IsSupported;

    /// <summary>
    /// Run <paramref name="iterations"/> rounds of scalar mixing seeded by <paramref name="seed"/> and return
    /// the final checksum. Deterministic and side-effect free — same inputs ⇒ same output on healthy silicon.
    /// </summary>
    public static ulong Compute(ulong seed, int iterations)
    {
        ulong h = seed ^ GoldenGamma;

        // Start the FP accumulator in [1, 2): exactly representable family, fully deterministic.
        double d = 1.0 + (seed & 0xFFFF) * (1.0 / 65536.0);

        for (int i = 0; i < iterations; i++)
        {
            // --- integer mixing: loads the ALUs, bit-exact ---
            h += GoldenGamma;
            ulong z = h;
            z = (z ^ (z >> 30)) * Mix1;
            z = (z ^ (z >> 27)) * Mix2;
            z ^= z >> 31;

            // --- FP mixing: exercises the FPU with correctly-rounded (deterministic) ops only ---
            d = d * FpFactor + 0.25;
            if (d >= 2.0) d -= 1.0;          // keep d bounded in [1, 2); exact in this range

            // fold the FP state back into the integer checksum so an FPU miscalc is also caught
            z ^= (ulong)BitConverter.DoubleToInt64Bits(d);

            h ^= z + (ulong)(uint)i;
        }

        return h;
    }

    /// <summary>
    /// AVX2 sibling of <see cref="Compute"/>: four independent lanes mixed in parallel on the 256-bit units.
    /// Routes to the intrinsic path when AVX2 is present and to a bit-identical scalar emulation otherwise,
    /// so the returned checksum is the same on every machine for a given <c>(seed, iterations)</c>.
    /// </summary>
    public static ulong ComputeVector(ulong seed, int iterations)
        => Avx2.IsSupported ? ComputeVectorAvx2(seed, iterations) : ComputeVectorScalar(seed, iterations);

    /// <summary>
    /// Portable scalar emulation of the four-lane vector kernel. Public so the test-suite can pin the AVX2
    /// path against it (they must agree bit-for-bit) and verify determinism without needing AVX2 hardware.
    /// </summary>
    public static ulong ComputeVectorScalar(ulong seed, int iterations)
    {
        Span<ulong> hInit = stackalloc ulong[4];
        Span<double> dInit = stackalloc double[4];
        InitLanes(seed, hInit, dInit);

        ulong h0 = hInit[0], h1 = hInit[1], h2 = hInit[2], h3 = hInit[3];
        double d0 = dInit[0], d1 = dInit[1], d2 = dInit[2], d3 = dInit[3];

        for (int i = 0; i < iterations; i++)
        {
            ulong iAdd = (ulong)(uint)i;
            Lane(ref h0, ref d0, iAdd);
            Lane(ref h1, ref d1, iAdd);
            Lane(ref h2, ref d2, iAdd);
            Lane(ref h3, ref d3, iAdd);
        }

        return Combine(h0, h1, h2, h3);

        // One lane of the vector kernel — the exact op sequence VPADDQ/VPSLLQ/VPSRLQ/VPXOR + VMULPD/VADDPD/
        // VCMPPD/VANDPD/VSUBPD reduce to. Multiply/shift-only mixing (no 64-bit packed multiply, which is
        // AVX-512) so it maps 1:1 onto AVX2 and stays bit-identical.
        static void Lane(ref ulong h, ref double d, ulong iAdd)
        {
            h += GoldenGamma;
            ulong z = h;
            z ^= z << 13;
            z ^= z >> 7;
            z ^= z << 17;

            d = d * FpFactor + 0.25;
            if (d >= 2.0) d -= 1.0;

            z ^= (ulong)BitConverter.DoubleToInt64Bits(d);
            h ^= z + iAdd;
        }
    }

    private static ulong ComputeVectorAvx2(ulong seed, int iterations)
    {
        Span<ulong> hInit = stackalloc ulong[4];
        Span<double> dInit = stackalloc double[4];
        InitLanes(seed, hInit, dInit);

        Vector256<ulong> h = Vector256.Create(hInit[0], hInit[1], hInit[2], hInit[3]);
        Vector256<double> d = Vector256.Create(dInit[0], dInit[1], dInit[2], dInit[3]);

        Vector256<ulong> gamma = Vector256.Create(GoldenGamma);
        Vector256<double> factor = Vector256.Create(FpFactor);
        Vector256<double> quarter = Vector256.Create(0.25);
        Vector256<double> one = Vector256.Create(1.0);
        Vector256<double> two = Vector256.Create(2.0);

        for (int i = 0; i < iterations; i++)
        {
            // --- integer mixing (xorshift: shifts + XOR only ⇒ pure AVX2, bit-exact) ---
            h = Avx2.Add(h, gamma);
            Vector256<ulong> z = h;
            z = Avx2.Xor(z, Avx2.ShiftLeftLogical(z, (byte)13));
            z = Avx2.Xor(z, Avx2.ShiftRightLogical(z, (byte)7));
            z = Avx2.Xor(z, Avx2.ShiftLeftLogical(z, (byte)17));

            // --- FP mixing (packed +, −, × and a compare-mask: correctly rounded ⇒ deterministic) ---
            d = Avx.Add(Avx.Multiply(d, factor), quarter);
            Vector256<double> ge = Avx.Compare(d, two, FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling);
            d = Avx.Subtract(d, Avx.And(ge, one));   // subtract 1.0 where d >= 2.0, else 0.0

            z = Avx2.Xor(z, d.AsUInt64());
            h = Avx2.Xor(h, Avx2.Add(z, Vector256.Create((ulong)(uint)i)));
        }

        return Combine(h.GetElement(0), h.GetElement(1), h.GetElement(2), h.GetElement(3));
    }

    /// <summary>Per-lane initial state — distinct seed per lane so the four lanes do independent work.</summary>
    private static void InitLanes(ulong seed, Span<ulong> h, Span<double> d)
    {
        for (int k = 0; k < 4; k++)
        {
            ulong s = seed + (ulong)k * GoldenGamma;
            h[k] = s ^ GoldenGamma;
            d[k] = 1.0 + (s & 0xFFFF) * (1.0 / 65536.0);
        }
    }

    /// <summary>Fold the four lane checksums into one, rotating each so lane order matters (catches a single
    /// bad lane). Shared by both paths so they reduce identically.</summary>
    private static ulong Combine(ulong h0, ulong h1, ulong h2, ulong h3)
        => h0
         ^ BitOperations.RotateLeft(h1, 16)
         ^ BitOperations.RotateLeft(h2, 32)
         ^ BitOperations.RotateLeft(h3, 48);
}
