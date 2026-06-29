using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure hardware-string classifiers (<see cref="HardwareClassification"/>) extracted from
/// <see cref="HardwareService"/>. These turn raw Win32_* strings into the family/vendor enums that gate
/// EVERY downstream recommendation: <c>DetectedFamily</c> drives both the adaptive engine's CpuFamilies
/// filter and the BIOS advisor's per-CPU ranking, so a misclassification silently mis-targets advice —
/// most dangerously across platforms (AM4 vs AM5). That makes this an honesty surface, not just a parser.
/// </summary>
public class HardwareClassificationTests
{
    // ---- ClassifyCpu: Ryzen X3D, keyed off GENERATION not marketing tier ----------
    // The load-bearing correctness rule. X3D part numbers cross tiers: the 5800X3D is a
    // "Ryzen 7" name on an AM4/Zen3 die; the 7950X3D/7900X3D are "Ryzen 9" names on 7000-series
    // dies. A tier-based rule ("Ryzen 9" → 9000) would put them on the wrong platform and feed,
    // e.g., AM5 EXPO advice to an AM4 board. We key off the 4-digit model's first (generation) digit.

    [Theory]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", CpuFamily.Ryzen9000X3D)]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor", CpuFamily.Ryzen9000X3D)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", CpuFamily.Ryzen9000X3D)]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", CpuFamily.Ryzen7000X3D)] // "Ryzen 9" tier, 7000 gen
    [InlineData("AMD Ryzen 9 7900X3D 12-Core Processor", CpuFamily.Ryzen7000X3D)] // "Ryzen 9" tier, 7000 gen
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", CpuFamily.Ryzen7000X3D)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", CpuFamily.Ryzen5000X3D)] // "Ryzen 7" tier, AM4/Zen3
    [InlineData("AMD Ryzen 5 5600X3D 6-Core Processor", CpuFamily.Ryzen5000X3D)]
    public void ClassifyCpu_X3D_UsesGenerationDigit_NotTier(string name, CpuFamily expected)
        => Assert.Equal(expected, HardwareClassification.ClassifyCpu(name));

    // ---- ClassifyCpu: Ryzen non-X3D, keyed off the full model number ---------------

    [Theory]
    [InlineData("AMD Ryzen 9 9950X 16-Core Processor", CpuFamily.Ryzen9000)]
    [InlineData("AMD Ryzen 5 9600X 6-Core Processor", CpuFamily.Ryzen9000)]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", CpuFamily.Ryzen7000)] // "Ryzen 9" tier, 7000 gen
    [InlineData("AMD Ryzen 7 7700X 8-Core Processor", CpuFamily.Ryzen7000)]
    [InlineData("AMD Ryzen 9 5950X 16-Core Processor", CpuFamily.Ryzen5000)]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", CpuFamily.Ryzen5000)]
    [InlineData("AMD Ryzen 5 5600X 6-Core Processor", CpuFamily.Ryzen5000)]
    [InlineData("AMD Ryzen 7 3700X 8-Core Processor", CpuFamily.Ryzen3000)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", CpuFamily.Ryzen3000)]
    public void ClassifyCpu_RyzenNonX3D_UsesModelNumber(string name, CpuFamily expected)
        => Assert.Equal(expected, HardwareClassification.ClassifyCpu(name));

    // ---- ClassifyCpu: Intel Core 12/13/14 ------------------------------------------

    [Theory]
    [InlineData("Intel(R) Core(TM) i9-14900K", CpuFamily.IntelCore14)]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", CpuFamily.IntelCore13)]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12600K", CpuFamily.IntelCore12)]
    public void ClassifyCpu_Intel_MapsSupportedGenerations(string name, CpuFamily expected)
        => Assert.Equal(expected, HardwareClassification.ClassifyCpu(name));

    // ---- ClassifyCpu: "Core Ultra" is ONE honest bucket (Meteor/Arrow/Lunar) -------
    // The Win32_Processor string only reliably surfaces the "Core Ultra" brand, not the underlying die,
    // and every downstream rule (hybrid P/E-core guidance, generic Intel BIOS cards, the "Intel Core Ultra"
    // label) treats them identically — so we fold all three into IntelCoreUltra rather than overclaim a die
    // we can't read. Crucially the "ULTRA" check runs BEFORE the 12/13/14 digit checks, so a mobile part
    // like "Core Ultra 5 125H" is NOT mis-read as a 12th-gen chip via the "12" substring.

    [Theory]
    [InlineData("Intel(R) Core(TM) Ultra 9 285K")]    // Arrow Lake  (series 2, desktop)
    [InlineData("Intel(R) Core(TM) Ultra 7 265K")]    // Arrow Lake  (series 2, desktop)
    [InlineData("Intel(R) Core(TM) Ultra 7 155H")]    // Meteor Lake (series 1, mobile)
    [InlineData("Intel(R) Core(TM) Ultra 5 125H")]    // Meteor Lake — "125" must NOT trip the "12" rule
    [InlineData("Intel(R) Core(TM) Ultra 7 258V")]    // Lunar Lake  (series 2, mobile)
    public void ClassifyCpu_CoreUltra_FoldsEveryDieIntoOneHonestBucket(string name)
        => Assert.Equal(CpuFamily.IntelCoreUltra, HardwareClassification.ClassifyCpu(name));

    // ---- ClassifyCpu: honest Unknown for anything we don't model -------------------

    [Theory]
    [InlineData("AMD Ryzen Threadripper 3970X 32-Core Processor")] // HEDT, not a desktop family
    [InlineData("AMD Ryzen 7 2700X 8-Core Processor")]              // Zen+ (2000), unsupported
    [InlineData("AMD Ryzen 5 1600 Six-Core Processor")]            // Zen (1000), unsupported
    [InlineData("Intel(R) Core(TM) i7-11900K")]                    // 11th gen, unsupported
    [InlineData("Intel(R) Core(TM) i7-7700K")]                     // Kaby Lake, unsupported
    [InlineData("Some Unknown CPU")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ClassifyCpu_Unmodelled_IsUnknown_NotAWrongGuess(string? name)
        => Assert.Equal(CpuFamily.Unknown, HardwareClassification.ClassifyCpu(name!));

    // ---- ClassifyBios: manufacturer string → vendor menu paths ---------------------

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", BiosVendor.Asus)]
    [InlineData("Micro-Star International Co., Ltd.", BiosVendor.Msi)] // matched via "MICRO-STAR"
    [InlineData("MSI", BiosVendor.Msi)]
    [InlineData("Gigabyte Technology Co., Ltd.", BiosVendor.Gigabyte)]
    [InlineData("ASRock", BiosVendor.Asrock)]
    [InlineData("Biostar Group", BiosVendor.Biostar)]
    [InlineData("Dell Inc.", BiosVendor.Dell)]
    [InlineData("HP", BiosVendor.Hp)]
    [InlineData("Hewlett-Packard", BiosVendor.Hp)] // matched via "HEWLETT"
    [InlineData("LENOVO", BiosVendor.Lenovo)]
    [InlineData("Supermicro", BiosVendor.Unknown)]
    [InlineData("", BiosVendor.Unknown)]
    [InlineData(null, BiosVendor.Unknown)]
    public void ClassifyBios_MapsKnownVendors(string? manufacturer, BiosVendor expected)
        => Assert.Equal(expected, HardwareClassification.ClassifyBios(manufacturer!));

    // ---- ClassifyGpu: product string → GPU vendor (gates GPU-OC applicability) ------

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", GpuVendor.Nvidia)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", GpuVendor.Nvidia)]
    [InlineData("AMD Radeon RX 7900 XTX", GpuVendor.Amd)]       // "RX" must not trip the "RTX" rule
    [InlineData("Radeon RX 6800 XT", GpuVendor.Amd)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", GpuVendor.Intel)]
    [InlineData("Intel(R) UHD Graphics 770", GpuVendor.Intel)]
    [InlineData("Microsoft Basic Display Adapter", GpuVendor.Unknown)]
    [InlineData("", GpuVendor.Unknown)]
    [InlineData(null, GpuVendor.Unknown)]
    public void ClassifyGpu_MapsKnownVendors(string? name, GpuVendor expected)
        => Assert.Equal(expected, HardwareClassification.ClassifyGpu(name!));
}
