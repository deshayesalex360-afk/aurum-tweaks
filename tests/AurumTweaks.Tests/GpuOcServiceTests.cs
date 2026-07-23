using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.Services.Interop;
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

    // The real backends: preserve the existing tests' behavior exactly — the AMD GetStatus branch still
    // performs a real read-only ADLX probe, and the out-of-range Apply tests still reject at validation
    // before any native call, so the dev machine's GPU is never written.
    private static GpuOcService ServiceFor(GpuVendor vendor, string gpuName)
        => new(new FakeHw(new HardwareInfo { GpuVendor = vendor, GpuPrimary = gpuName }),
               new NvApiBackend(), new AdlxBackend());

    // Fully fake-backed service — no real GPU touched, so the multi-axis orchestration (partial-failure
    // honesty, read-back decisions, vendor routing) can be driven deterministically.
    private static GpuOcService FakeService(GpuVendor vendor, INvApi nv, IAdlxApi adlx, string name = "Test GPU")
        => new(new FakeHw(new HardwareInfo { GpuVendor = vendor, GpuPrimary = name }), nv, adlx);

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
        // The backend is available if ANY ADLX axis (power limit / GPU max freq / memory max freq) verified.
        if (status.BackendAvailable)
        {
            Assert.True(status.PowerLimitNative || status.GfxTuningNative || status.VramTuningNative);
            Assert.Contains("ADLX", status.Message!);
            Assert.Contains("relecture", status.Message!);
            if (status.PowerLimitNative)
            {
                Assert.Equal(GpuPowerBackendKind.AdlxDocumented, status.PowerBackend);
                Assert.True(status.PowerLimitMinPct < status.PowerLimitMaxPct);
            }
            if (status.GfxTuningNative)
                Assert.True(status.GfxMaxFreqMinMhz < status.GfxMaxFreqMaxMhz);
            if (status.VramTuningNative)
                Assert.True(status.VramMaxFreqMinMhz < status.VramMaxFreqMaxMhz);
        }
        else
        {
            Assert.False(status.PowerLimitNative);
            Assert.False(status.GfxTuningNative);
            Assert.False(status.VramTuningNative);
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

    // ---- Fake-backed orchestration: partial-failure honesty, read decisions, vendor routing ----
    // (These are the honesty-critical paths the review flagged as structurally untestable before the
    //  INvApi/IAdlxApi seams existed — a regression flipping a partial failure to Success, or writing to
    //  the wrong GPU on a hybrid rig, would ship green without them.)

    private static AdlxGpuInfo AmdInfo(bool power = false, bool gfx = false, bool vram = false)
        => new("Radeon Test", false, power, gfx, vram);

    [Fact]
    public async Task Apply_AmdPowerAppliedThenGfxFails_ReturnsFailure_NamingTheAppliedPowerAxis()
    {
        // The single most valuable missing test (per the review): power writes OK, gfx write fails →
        // the result must be a FAILURE that still NAMES the power axis that already took effect.
        var adlx = new FakeAdlxApi
        {
            Info = AmdInfo(power: true, gfx: true),
            PowerReadable = true, PowerMin = -10, PowerMax = 15, PowerCur = 0,
            GfxReadable = true, GfxMin = 500, GfxMax = 3000, GfxCur = 2500,
            PowerWriteOk = true,
            GfxWriteOk = false, GfxError = "driver refusé",
        };
        var svc = FakeService(GpuVendor.Amd, new FakeNvApi(), adlx);

        // temp 83 is within the backend-agnostic ValidateFrequencies window (it isn't applied on AMD, but
        // the profile must still be valid); the VM always sends a real temp default.
        var result = await svc.ApplyAsync(new GpuOcProfile(0, 0, 10, 83, 0) { AmdMaxFreqMhz = 2700 });

        Assert.False(result.Success);
        Assert.Contains("Appliqué partiellement", result.Error);
        Assert.Contains("power limit", result.Error);          // the axis that DID apply is named
        Assert.Contains("fréquence GPU max NON appliquée", result.Error);
        Assert.Equal(new[] { 10 }, adlx.PowerWrites);          // power really was written before the failure
    }

    [Fact]
    public async Task Apply_NvidiaOffsetsAppliedThenPowerFails_ReturnsFailure_SayingOffsetsApplied()
    {
        var nv = new FakeNvApi
        {
            Present = true,
            OffsetWriteOk = true,
            PowerReadable = true, PMin = 90_000, PDef = 100_000, PMax = 120_000, PCur = 100_000,
            PowerWriteOk = false, PowerError = "nv power refusé",
        };
        var svc = FakeService(GpuVendor.Nvidia, nv, new FakeAdlxApi());

        var result = await svc.ApplyAsync(new GpuOcProfile(150, 1200, 110, 83, 0));

        Assert.False(result.Success);
        Assert.Contains("Offsets appliqués", result.Error);    // the offsets DID apply
        Assert.Contains("power limit NON appliqué", result.Error);
        Assert.Equal(new[] { (150, 1200) }, nv.OffsetWrites);  // offsets really written before the failure
    }

    [Fact]
    public async Task ReadCurrent_Amd_OneVerifiedAxisFailsToRead_ReturnsNull_NotAHalfPopulatedProfile()
    {
        // The invariant the VM relies on to fall back to the captured default instead of a stray 0
        // (which a blind apply would clamp to the window minimum = a silent downclock).
        var adlx = new FakeAdlxApi
        {
            Info = AmdInfo(power: true, gfx: true),
            PowerReadable = true, PowerMin = -10, PowerMax = 15, PowerCur = 5,
            GfxReadable = true, GfxMin = 500, GfxMax = 3000, GfxCur = 2500,
        };
        var svc = FakeService(GpuVendor.Amd, new FakeNvApi(), adlx);
        await svc.GetStatusAsync();          // primes verification for both axes (cached)

        adlx.GfxReadable = false;            // now the gfx READ fails at read-time
        var current = await svc.ReadCurrentAsync();

        Assert.Null(current);               // any verified-axis read failure → whole profile null
    }

    [Fact]
    public async Task ReadCurrent_Amd_AllVerifiedAxesReadCleanly_ReturnsThePopulatedProfile()
    {
        var adlx = new FakeAdlxApi
        {
            Info = AmdInfo(power: true, gfx: true, vram: true),
            PowerReadable = true, PowerMin = -10, PowerMax = 15, PowerCur = 5,
            GfxReadable = true, GfxMin = 500, GfxMax = 3000, GfxCur = 2650,
            VramReadable = true, VramMin = 1000, VramMax = 2600, VramCur = 2400,
        };
        var svc = FakeService(GpuVendor.Amd, new FakeNvApi(), adlx);

        var current = await svc.ReadCurrentAsync();

        Assert.NotNull(current);
        Assert.Equal(5, current!.PowerLimitPct);
        Assert.Equal(2650, current.AmdMaxFreqMhz);
        Assert.Equal(2400, current.AmdMaxVramFreqMhz);
    }

    [Fact]
    public async Task Apply_AmdPrimaryWithNvidiaAlsoPresent_WritesToAmd_NeverTheNvidiaCard()
    {
        // Hybrid rig: hardware reports AMD primary, but an NVAPI-drivable NVIDIA card also exists.
        // The vendor gate must route to ADLX and NEVER write the undisclosed NVIDIA GPU.
        var nv = new FakeNvApi
        {
            Present = true, OffsetWriteOk = true, PowerWriteOk = true,
            PowerReadable = true, PMin = 90_000, PDef = 100_000, PMax = 120_000, PCur = 100_000,
        };
        var adlx = new FakeAdlxApi
        {
            Info = AmdInfo(power: true),
            PowerReadable = true, PowerMin = -10, PowerMax = 15, PowerCur = 0, PowerWriteOk = true,
        };
        var svc = FakeService(GpuVendor.Amd, nv, adlx);

        var result = await svc.ApplyAsync(new GpuOcProfile(150, 1200, 10, 83, 0));

        Assert.True(result.Success);
        Assert.Empty(nv.OffsetWrites);                 // the NVIDIA card was NEVER touched
        Assert.Empty(nv.PowerWrites);
        Assert.Equal(new[] { 10 }, adlx.PowerWrites);  // the AMD axis WAS written
        Assert.Contains("offsets core/mémoire (style NVIDIA) non appliqués sur AMD", result.Applied);
    }

    [Fact]
    public async Task Reset_AmdPowerRestoreFails_ReturnsFailure_NotAFabricatedSuccess()
    {
        var adlx = new FakeAdlxApi
        {
            Info = AmdInfo(power: true),
            PowerReadable = true, PowerMin = -10, PowerMax = 15, PowerCur = 5,
            PowerWriteOk = false, PowerError = "restore refusé",
        };
        var svc = FakeService(GpuVendor.Amd, new FakeNvApi(), adlx);

        var result = await svc.ResetAsync();

        Assert.False(result.Success);
        Assert.Contains("NON restauré", result.Error);
    }
}
