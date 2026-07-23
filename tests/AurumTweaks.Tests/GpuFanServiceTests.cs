using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using AurumTweaks.Services.Interop;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the fan service against a fake NVAPI seam (no real card). The honesty/safety points: a manual set
/// is ALWAYS floored by GpuFanSafety before reaching the driver; success is read-back-confirmed by the
/// fake's contract; a non-NVIDIA GPU gets an honest Adrenalin referral, never a silent no-op.
/// </summary>
public class GpuFanServiceTests
{
    private sealed class FakeHw : IHardwareService
    {
        private readonly HardwareInfo _hw;
        public FakeHw(GpuVendor v) => _hw = new HardwareInfo { GpuVendor = v, GpuPrimary = v.ToString() };
        public Task<HardwareInfo> DetectAsync() => Task.FromResult(_hw);
    }

    private static GpuFanService Svc(GpuVendor vendor, FakeNvApi nv) => new(new FakeHw(vendor), nv);

    [Fact]
    public async Task GetStatus_Nvidia_ReadsLevelAndRpm_AndIsAvailable()
    {
        var nv = new FakeNvApi { Present = true, FanReadable = true, FanLevelPct = 45, FanRpm = 1500 };
        var status = await Svc(GpuVendor.Nvidia, nv).GetStatusAsync();

        Assert.True(status.Available);
        Assert.Equal(45, status.CurrentPercent);
        Assert.Equal(1500, status.Rpm);
        Assert.Contains("relecture", status.Message);
    }

    [Fact]
    public async Task GetStatus_Amd_IsUnavailable_AndRefersToAdrenalin()
    {
        var status = await Svc(GpuVendor.Amd, new FakeNvApi()).GetStatusAsync();
        Assert.False(status.Available);
        Assert.Contains("Adrenalin", status.Message);
    }

    [Fact]
    public async Task SetManual_Nvidia_PassesTheRequestedValueToTheDriver()
    {
        var nv = new FakeNvApi { Present = true, FanReadable = true, FanWriteOk = true };
        var r = await Svc(GpuVendor.Nvidia, nv).SetManualAsync(60);

        Assert.True(r.Success);
        Assert.Equal(new[] { 60 }, nv.FanManualWrites);
    }

    [Fact]
    public async Task SetManual_BelowTheSafetyFloor_IsFlooredBeforeTheDriverSeesIt()
    {
        var nv = new FakeNvApi { Present = true, FanReadable = true, FanWriteOk = true };
        // The service must clamp to the hard floor (20) — a request of 5 % must never reach the card as 5.
        var r = await Svc(GpuVendor.Nvidia, nv).SetManualAsync(5);

        Assert.True(r.Success);
        Assert.Equal(GpuFanSafety.HardFloorPercent, nv.FanManualWrites[0]);
    }

    [Fact]
    public async Task SetAuto_Nvidia_CallsTheDriverAutoPath()
    {
        var nv = new FakeNvApi { Present = true, FanReadable = true, FanWriteOk = true };
        var r = await Svc(GpuVendor.Nvidia, nv).SetAutoAsync();

        Assert.True(r.Success);
        Assert.Equal(1, nv.FanAutoCount);
    }

    [Fact]
    public async Task SetManual_Amd_RefusesNatively_NeverTouchesNvApi()
    {
        var nv = new FakeNvApi { Present = true };
        var r = await Svc(GpuVendor.Amd, nv).SetManualAsync(60);

        Assert.False(r.Success);
        Assert.Empty(nv.FanManualWrites);
    }
}
