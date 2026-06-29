using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

public class MemorySpeedAssessmentTests
{
    [Fact]
    public void AtRatedSpeed_WhenConfiguredEqualsRated()
        => Assert.Equal(MemoryProfileStatus.AtRatedSpeed, MemorySpeedAssessment.Classify(6000, 6000));

    [Fact]
    public void BelowRatedSpeed_WhenConfiguredWellBelowRated()
        => Assert.Equal(MemoryProfileStatus.BelowRatedSpeed, MemorySpeedAssessment.Classify(4800, 6000));

    [Fact]
    public void Unknown_WhenConfiguredMissing()
        => Assert.Equal(MemoryProfileStatus.Unknown, MemorySpeedAssessment.Classify(0, 6000));

    [Fact]
    public void Unknown_WhenRatedMissing()
        => Assert.Equal(MemoryProfileStatus.Unknown, MemorySpeedAssessment.Classify(6000, 0));

    // The 100 MT/s margin must agree with HardwareInfo.RamRunningBelowRated to the value — there is one
    // "below rated" threshold across the dashboard, the BIOS advisor and this page, not three.
    [Theory]
    [InlineData(5899, 6000, MemoryProfileStatus.BelowRatedSpeed)] // 101 under → real EXPO-off gap
    [InlineData(5900, 6000, MemoryProfileStatus.AtRatedSpeed)]    // exactly 100 under → on the margin, no alarm
    [InlineData(5950, 6000, MemoryProfileStatus.AtRatedSpeed)]    // 50 under → reporting jitter, no alarm
    public void Margin_MirrorsHardwareInfoThreshold(int configured, int rated, MemoryProfileStatus expected)
    {
        Assert.Equal(expected, MemorySpeedAssessment.Classify(configured, rated));
        // Cross-check: the boolean signal the rest of the app uses must agree with our enum verdict.
        var belowPerHardwareInfo = new HardwareInfo { RamConfiguredMhz = configured, RamRatedMhz = rated }.RamRunningBelowRated;
        Assert.Equal(belowPerHardwareInfo, expected == MemoryProfileStatus.BelowRatedSpeed);
    }
}

public class MemoryChannelInferenceTests
{
    [Theory]
    [InlineData(0, MemoryChannelHint.Unknown, "Indéterminé")]
    [InlineData(1, MemoryChannelHint.Single, "Canal simple")]
    [InlineData(2, MemoryChannelHint.DualLikely, "Double canal probable")]
    [InlineData(4, MemoryChannelHint.DualLikely, "Double canal probable")]
    [InlineData(3, MemoryChannelHint.Asymmetric, "Configuration asymétrique (nombre impair de barrettes)")]
    public void Infer_And_Describe(int count, MemoryChannelHint expectedHint, string expectedText)
    {
        var hint = MemoryChannelInference.Infer(count);
        Assert.Equal(expectedHint, hint);
        Assert.Equal(expectedText, MemoryChannelInference.Describe(hint));
    }
}

public class MemoryModuleRowTests
{
    private static MemoryModule Module(
        string slot = "DIMM_A2", string mfr = "Corsair", string part = "CMK32GX5M2B6000Z30",
        long bytes = 17_179_869_184L, int cfg = 6000, int rated = 6000, string type = "DDR5", string bank = "P0 CHANNEL A")
        => new()
        {
            Slot = slot, Manufacturer = mfr, PartNumber = part, CapacityBytes = bytes,
            ConfiguredMhz = cfg, RatedMhz = rated, RamType = type, BankLabel = bank
        };

    [Fact]
    public void Identity_JoinsManufacturerAndPart()
        => Assert.Equal("Corsair CMK32GX5M2B6000Z30", new MemoryModuleRow(Module()).Identity);

    [Fact]
    public void Identity_FallsBack_WhenBlank()
        => Assert.Equal("Fabricant inconnu", new MemoryModuleRow(Module(mfr: "", part: "")).Identity);

    [Fact]
    public void Slot_FallsBack_WhenBlank()
        => Assert.Equal("Slot ?", new MemoryModuleRow(Module(slot: "")).Slot);

    [Fact]
    public void Capacity_IsDash_WhenUnreported_NeverFakeZero()
        => Assert.Equal("—", new MemoryModuleRow(Module(bytes: 0)).Capacity);

    [Fact]
    public void Capacity_Formats_InGo()
    {
        var cap = new MemoryModuleRow(Module()).Capacity;
        Assert.Contains("Go", cap);
        Assert.Contains("16", cap);
    }

    [Fact]
    public void SpeedDisplay_ShowsNominal_WhenItDiffers()
        => Assert.Equal("4800 MT/s · nominal 6000 MT/s", new MemoryModuleRow(Module(cfg: 4800, rated: 6000)).SpeedDisplay);

    [Fact]
    public void SpeedDisplay_OmitsNominal_WhenEqual()
        => Assert.Equal("6000 MT/s", new MemoryModuleRow(Module(cfg: 6000, rated: 6000)).SpeedDisplay);

    [Fact]
    public void SpeedDisplay_IsDash_WhenUnreported()
        => Assert.Equal("—", new MemoryModuleRow(Module(cfg: 0)).SpeedDisplay);

    [Fact]
    public void BelowRated_TrueOnlyBeyondMargin()
    {
        Assert.True(new MemoryModuleRow(Module(cfg: 4800, rated: 6000)).BelowRated);
        Assert.False(new MemoryModuleRow(Module(cfg: 6000, rated: 6000)).BelowRated);
    }
}

public class MemoryModulesReportTests
{
    private static HardwareInfo Info(int modules, long bytesEach, int cfg, int rated, int slots, string type = "DDR5")
    {
        var hw = new HardwareInfo
        {
            RamSlotCount = slots,
            RamType = type,
            RamConfiguredMhz = cfg,
            RamRatedMhz = rated,
            TotalRamBytes = bytesEach * modules
        };
        for (int i = 0; i < modules; i++)
            hw.MemoryModules.Add(new MemoryModule
            {
                Slot = $"DIMM_{i}", Manufacturer = "Corsair", PartNumber = "KIT",
                CapacityBytes = bytesEach, ConfiguredMhz = cfg, RatedMhz = rated, RamType = type
            });
        return hw;
    }

    [Fact]
    public void Empty_ReportsNoModules_Honestly()
    {
        var rep = MemoryModulesReport.From(new HardwareInfo());
        Assert.False(rep.HasModules);
        Assert.Equal(0, rep.ModuleCount);
        Assert.Equal("Aucune barrette détectée", rep.ProfileHeadline);
        Assert.True(rep.ProfileUnknown);
        Assert.False(rep.ProfileOk);
        Assert.False(rep.ProfileWarn);
        Assert.Equal("—", rep.TotalDisplay);
        Assert.Equal("—", rep.SlotsDisplay);
    }

    [Fact]
    public void TwoModulesAtRated_AreHealthy()
    {
        var rep = MemoryModulesReport.From(Info(modules: 2, bytesEach: 17_179_869_184L, cfg: 6000, rated: 6000, slots: 4));
        Assert.True(rep.HasModules);
        Assert.Equal(2, rep.ModuleCount);
        Assert.True(rep.ProfileOk);
        Assert.False(rep.ProfileWarn);
        Assert.Contains("nominale", rep.ProfileHeadline);
        Assert.Equal(MemoryChannelHint.DualLikely, rep.ChannelHint);
        Assert.Contains("Go", rep.TotalDisplay);
        Assert.Contains("32", rep.TotalDisplay);  // 2 × 16 GiB
    }

    [Fact]
    public void BelowRated_RaisesXmpHint()
    {
        var rep = MemoryModulesReport.From(Info(modules: 2, bytesEach: 17_179_869_184L, cfg: 4800, rated: 6000, slots: 4));
        Assert.True(rep.ProfileWarn);
        Assert.False(rep.ProfileOk);
        Assert.Contains("XMP/EXPO", rep.ProfileHeadline);
        // The verdict must agree with the rest of the app's signal for the same inputs.
        Assert.True(new HardwareInfo { RamConfiguredMhz = 4800, RamRatedMhz = 6000 }.RamRunningBelowRated);
    }

    [Fact]
    public void FreeSlots_NeverNegative_WhenBoardUnderreportsSlots()
    {
        // 2 sticks but firmware reports only 1 slot → 0 free, never -1.
        var rep = MemoryModulesReport.From(Info(modules: 2, bytesEach: 8_589_934_592L, cfg: 6000, rated: 6000, slots: 1));
        Assert.Equal(0, rep.FreeSlots);
    }

    [Fact]
    public void SlotsDisplay_PluralisesFreeCount()
    {
        var twoFree = MemoryModulesReport.From(Info(modules: 2, bytesEach: 8_589_934_592L, cfg: 6000, rated: 6000, slots: 4));
        Assert.Contains("2 / 4", twoFree.SlotsDisplay);
        Assert.Contains("2 libres", twoFree.SlotsDisplay);

        var oneFree = MemoryModulesReport.From(Info(modules: 3, bytesEach: 8_589_934_592L, cfg: 6000, rated: 6000, slots: 4));
        Assert.Contains("1 libre", oneFree.SlotsDisplay);
        Assert.DoesNotContain("1 libres", oneFree.SlotsDisplay);
    }
}
