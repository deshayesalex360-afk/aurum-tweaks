using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// The CPU stability test only works if <see cref="CpuWorkload.Compute"/> is a deterministic pure function:
/// same inputs ⇒ same checksum, every time, on every core. These tests pin exactly that property — it is the
/// foundation the service rests on when it flags a worker whose checksum drifts from the reference.
/// </summary>
public class CpuWorkloadTests
{
    [Fact]
    public void Compute_IsDeterministic_ForSameInputs()
    {
        ulong a = CpuWorkload.Compute(CpuWorkload.DefaultSeed, 100_000);
        ulong b = CpuWorkload.Compute(CpuWorkload.DefaultSeed, 100_000);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_IsSensitive_ToSeed()
    {
        ulong a = CpuWorkload.Compute(1234, 50_000);
        ulong b = CpuWorkload.Compute(1235, 50_000);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_IsSensitive_ToIterationCount()
    {
        ulong a = CpuWorkload.Compute(CpuWorkload.DefaultSeed, 50_000);
        ulong b = CpuWorkload.Compute(CpuWorkload.DefaultSeed, 50_001);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DistinctSeeds_ProduceDistinctChecksums()
    {
        // The service uses DefaultSeed + threadIndex per worker; those must not collide.
        var results = Enumerable.Range(0, 64)
            .Select(t => CpuWorkload.Compute(CpuWorkload.DefaultSeed + (ulong)t, 20_000))
            .ToArray();
        Assert.Equal(results.Length, results.Distinct().Count());
    }

    [Fact]
    public async Task Compute_AgreesAcrossManyParallelCores()
    {
        // This is literally what the service checks: a healthy machine returns the same value on every core.
        ulong reference = CpuWorkload.Compute(CpuWorkload.DefaultSeed + 7, 60_000);

        var bag = new ConcurrentBag<ulong>();
        await Task.WhenAll(Enumerable.Range(0, 256).Select(_ => Task.Run(() =>
            bag.Add(CpuWorkload.Compute(CpuWorkload.DefaultSeed + 7, 60_000)))));

        Assert.All(bag, v => Assert.Equal(reference, v));
    }

    // ---- AVX2 vector kernel: same determinism contract, plus the bit-identical-to-scalar guarantee ----

    [Fact]
    public void ComputeVector_IsDeterministic_ForSameInputs()
    {
        ulong a = CpuWorkload.ComputeVector(CpuWorkload.DefaultSeed, 100_000);
        ulong b = CpuWorkload.ComputeVector(CpuWorkload.DefaultSeed, 100_000);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeVector_IsSensitive_ToSeed()
    {
        ulong a = CpuWorkload.ComputeVector(1234, 50_000);
        ulong b = CpuWorkload.ComputeVector(1235, 50_000);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeVector_IsSensitive_ToIterationCount()
    {
        ulong a = CpuWorkload.ComputeVector(CpuWorkload.DefaultSeed, 50_000);
        ulong b = CpuWorkload.ComputeVector(CpuWorkload.DefaultSeed, 50_001);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeVector_DistinctSeeds_ProduceDistinctChecksums()
    {
        var results = Enumerable.Range(0, 64)
            .Select(t => CpuWorkload.ComputeVector(CpuWorkload.DefaultSeed + (ulong)t, 20_000))
            .ToArray();
        Assert.Equal(results.Length, results.Distinct().Count());
    }

    [Theory]
    [InlineData(CpuWorkload.DefaultSeed, 0)]
    [InlineData(CpuWorkload.DefaultSeed, 1)]
    [InlineData(CpuWorkload.DefaultSeed, 12_345)]
    [InlineData(1UL, 100_000)]
    [InlineData(0xDEADBEEFUL, 77_777)]
    public void ComputeVector_MatchesScalarEmulation_BitForBit(ulong seed, int iterations)
    {
        // The whole portability story: on AVX2 hardware the intrinsic path runs here and must return the EXACT
        // same checksum as the scalar emulation. If they ever diverge, the test vectors stop being portable —
        // so this is the load-bearing assertion for the vector kernel.
        ulong scalar = CpuWorkload.ComputeVectorScalar(seed, iterations);
        ulong dispatched = CpuWorkload.ComputeVector(seed, iterations);
        Assert.Equal(scalar, dispatched);
    }

    [Fact]
    public void ComputeVector_DiffersFrom_ScalarCompute()
    {
        // The AVX2 kernel is a genuinely different (additional) workload, not an alias of the scalar one.
        ulong scalarKernel = CpuWorkload.Compute(CpuWorkload.DefaultSeed, 50_000);
        ulong vectorKernel = CpuWorkload.ComputeVector(CpuWorkload.DefaultSeed, 50_000);
        Assert.NotEqual(scalarKernel, vectorKernel);
    }

    [Fact]
    public async Task ComputeVector_AgreesAcrossManyParallelCores()
    {
        ulong reference = CpuWorkload.ComputeVector(CpuWorkload.DefaultSeed + 7, 60_000);

        var bag = new ConcurrentBag<ulong>();
        await Task.WhenAll(Enumerable.Range(0, 256).Select(_ => Task.Run(() =>
            bag.Add(CpuWorkload.ComputeVector(CpuWorkload.DefaultSeed + 7, 60_000)))));

        Assert.All(bag, v => Assert.Equal(reference, v));
    }
}
