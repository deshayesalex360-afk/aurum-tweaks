using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Tests for the GPU overclocking service. These cover ONLY surfaces that can never WRITE to a live
/// GPU: the pure <see cref="GpuOcValidation"/> logic, the vendor status branches, and an out-of-range
/// <see cref="GpuOcService.ApplyAsync"/> (rejected at validation BEFORE any native call).
///
/// <para><b>Why so constrained:</b> the dev/CI machine has a real NVIDIA GPU. An in-range
/// <c>ApplyAsync</c>/<c>ResetAsync</c>, or the NVIDIA <c>GetStatusAsync</c> branch, would actually
/// drive NVAPI and change the live card's state during <c>dotnet test</c>. Those paths are
/// intentionally never exercised here. The AMD status branch DOES perform a real, strictly
/// <b>read-only</b> ADLX probe (loads amdadlx64.dll when an AMD driver is present, reads support
/// flags, writes nothing) — so its assertions pin the honesty invariant for whichever outcome the
/// machine genuinely reports, rather than assuming one.</para>
/// </summary>
public class GpuOcServiceTests
{
    /// <summary>Returns a fixed HardwareInfo; no real WMI/sensor probing.</summary>
    private sealed class FakeHw : IHardwareService
    {
        private readonly HardwareInfo _hw;
        public FakeHw(HardwareInfo hw) => _hw = hw;
        public Task<HardwareInfo> DetectAsync() => Task.FromResult(_hw);
    }

    private static GpuOcService ServiceFor(GpuVendor vendor, string gpuName)
        => new(new FakeHw(new HardwareInfo { GpuVendor = vendor, GpuPrimary = gpuName }));

    private static GpuOcProfile Profile(int core = 0, int mem = 0, int power = 100, int temp = 83, int mv = 900)
        => new(core, mem, power, temp, mv);

    // ---- Pure validation: in-range / boundaries / each axis out-of-range ----

    [Fact]
    public void Validate_InRangeProfile_ReturnsNull()
        => Assert.Null(GpuOcValidation.Validate(Profile(core: 150, mem: 1200)));

    [Theory]
    [InlineData(GpuOcValidation.CoreMin, GpuOcValidation.MemMin, GpuOcValidation.PowerMin, GpuOcValidation.TempMin)]
    [InlineData(GpuOcValidation.CoreMax, GpuOcValidation.MemMax, GpuOcValidation.PowerMax, GpuOcValidation.TempMax)]
    public void Validate_InclusiveBoundaries_AreValid(int core, int mem, int power, int temp)
        => Assert.Null(GpuOcValidation.Validate(Profile(core, mem, power, temp)));

    [Theory]
    [InlineData(GpuOcValidation.CoreMax + 1, 0, 100, 83)]   // core too high
    [InlineData(GpuOcValidation.CoreMin - 1, 0, 100, 83)]   // core too low
    [InlineData(0, GpuOcValidation.MemMax + 1, 100, 83)]    // mem too high
    [InlineData(0, GpuOcValidation.MemMin - 1, 100, 83)]    // mem too low
    [InlineData(0, 0, GpuOcValidation.PowerMax + 1, 83)]    // power too high
    [InlineData(0, 0, GpuOcValidation.PowerMin - 1, 83)]    // power too low
    [InlineData(0, 0, 100, GpuOcValidation.TempMax + 1)]    // temp too high
    [InlineData(0, 0, 100, GpuOcValidation.TempMin - 1)]    // temp too low
    public void Validate_OutOfRange_ReturnsError(int core, int mem, int power, int temp)
        => Assert.NotNull(GpuOcValidation.Validate(Profile(core, mem, power, temp)));

    [Fact]
    public void ValidateFrequencies_IgnoresThePowerAxis_ItIsBackendSpecific()
        // An AMD Adrenalin-scale power value (possibly negative) must not be rejected by the
        // backend-agnostic gate — each vendor path validates power against its own window.
        => Assert.Null(GpuOcValidation.ValidateFrequencies(Profile(power: -10)));

    // ---- Pure clamp: pulls into range, no-op when already inside ----------

    [Fact]
    public void Clamp_HighSide_PullsDownToMaximums()
    {
        var c = GpuOcValidation.Clamp(Profile(core: 5000, mem: 9000, power: 999, temp: 200, mv: 1500));

        Assert.Equal(GpuOcValidation.CoreMax, c.CoreOffsetMhz);
        Assert.Equal(GpuOcValidation.MemMax, c.MemoryOffsetMhz);
        Assert.Equal(GpuOcValidation.PowerMax, c.PowerLimitPct);
        Assert.Equal(GpuOcValidation.TempMax, c.TempLimitC);
        Assert.Equal(1500, c.TargetVoltageMv);   // voltage is not applied natively → passes through untouched
    }

    [Fact]
    public void Clamp_LowSide_PullsUpToMinimums()
    {
        var c = GpuOcValidation.Clamp(Profile(core: -9000, mem: -9000, power: 0, temp: 0));

        Assert.Equal(GpuOcValidation.CoreMin, c.CoreOffsetMhz);
        Assert.Equal(GpuOcValidation.MemMin, c.MemoryOffsetMhz);
        Assert.Equal(GpuOcValidation.PowerMin, c.PowerLimitPct);
        Assert.Equal(GpuOcValidation.TempMin, c.TempLimitC);
    }

    [Fact]
    public void Clamp_InRange_IsNoOp()
    {
        var p = Profile(core: 150, mem: 1200);
        Assert.Equal(p, GpuOcValidation.Clamp(p));   // records → value equality
    }

    // ---- Vendor decision: AMD / Unknown never probe NVAPI -----------------

    [Fact]
    public async Task GetStatus_Amd_IsHonest_EitherVerifiedAdlxPower_OrAdrenalinReferral()
    {
        // Machine-agnostic honesty invariant: whichever way the read-only ADLX probe resolves on the
        // machine running the tests, the status must carry the matching proof obligations — a native
        // claim must name ADLX + read-back, an unavailable state must refer to Adrenalin.
        var status = await ServiceFor(GpuVendor.Amd, "AMD Radeon RX 7900 XTX").GetStatusAsync();

        Assert.Equal(GpuVendor.Amd, status.Vendor);
        Assert.NotNull(status.Message);
        if (status.PowerLimitNative)
        {
            Assert.True(status.BackendAvailable);
            Assert.Equal(GpuPowerBackendKind.AdlxDocumented, status.PowerBackend);
            Assert.Contains("ADLX", status.Message!);
            Assert.Contains("relecture", status.Message!);
            Assert.True(status.PowerLimitMinPct < status.PowerLimitMaxPct);
        }
        else
        {
            Assert.False(status.BackendAvailable);
            Assert.Contains("Adrenalin", status.Message!);
        }
    }

    [Fact]
    public async Task GetStatus_Unknown_ReportsUnavailable()
    {
        var status = await ServiceFor(GpuVendor.Unknown, "Microsoft Basic Render Driver").GetStatusAsync();

        Assert.Equal(GpuVendor.Unknown, status.Vendor);
        Assert.False(status.BackendAvailable);
    }

    // ---- ApplyAsync gate: out-of-range is rejected BEFORE any native call --

    [Fact]
    public async Task Apply_OutOfRangeCore_IsRejectedBeforeNative()
    {
        // Validation runs first and returns immediately, so NVAPI is never reached even though the
        // detected vendor is NVIDIA — this is the property that keeps the test machine's GPU safe.
        var svc = ServiceFor(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4080 SUPER");

        var result = await svc.ApplyAsync(Profile(core: GpuOcValidation.CoreMax + 5000));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Null(result.Applied);
    }

    [Fact]
    public async Task Apply_OutOfRangeMemory_IsRejectedBeforeNative()
    {
        var svc = ServiceFor(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4080 SUPER");

        var result = await svc.ApplyAsync(Profile(mem: GpuOcValidation.MemMax + 5000));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
