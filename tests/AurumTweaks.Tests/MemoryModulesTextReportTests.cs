using System;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="MemoryModulesTextReport"/> — the shareable « ma RAM tourne-t-elle en double canal à la bonne
/// vitesse ? » paste. Honesty contract: it renders only the REAL state the page already read (Win32_PhysicalMemory),
/// leads with the shared <see cref="MemoryModulesReport.ProfileHeadline"/> so the paste matches the screen, prints « — »
/// for a capacity Windows didn't expose (never a fabricated 0 Go), keeps the XMP/EXPO verdict INDICATIVE (Windows'
/// « nominale » is often the JEDEC base) and the channel mode a PROBABILITY, distinguishes an empty read from a populated
/// one, and keeps the read-only / never-sent / no-FPS / profile-lives-in-BIOS footer lines.
/// </summary>
public class MemoryModulesTextReportTests
{
    private static readonly DateTime When = new(2026, 6, 21, 14, 30, 0, DateTimeKind.Utc);

    private static MemoryModule Mod(
        string slot = "DIMM_A2", string mfr = "Corsair", string part = "CMK32GX5M2B6000Z30",
        long bytes = 17_179_869_184L, int cfg = 6000, int rated = 6000, string type = "DDR5", string bank = "P0 CHANNEL A")
        => new()
        {
            Slot = slot, Manufacturer = mfr, PartNumber = part, CapacityBytes = bytes,
            ConfiguredMhz = cfg, RatedMhz = rated, RamType = type, BankLabel = bank
        };

    private static MemoryModulesReport Report(int slots, string ramType, int cfg, int rated, params MemoryModule[] modules)
    {
        var hw = new HardwareInfo
        {
            RamSlotCount = slots,
            RamType = ramType,
            RamConfiguredMhz = cfg,
            RamRatedMhz = rated,
            TotalRamBytes = modules.Sum(m => m.CapacityBytes)
        };
        foreach (var m in modules) hw.MemoryModules.Add(m);
        return MemoryModulesReport.From(hw);
    }

    private static MemoryModulesReport AtRatedPair() =>
        Report(4, "DDR5", 6000, 6000, Mod(slot: "DIMM_A1"), Mod(slot: "DIMM_A2"));

    [Fact]
    public void Header_AlwaysCarriesTitle()
    {
        var text = MemoryModulesTextReport.Render(AtRatedPair(), When);
        Assert.Contains("Aurum Tweaks — Barrettes mémoire", text);
    }

    [Fact]
    public void Headline_AtRated_IsRendered()
    {
        var text = MemoryModulesTextReport.Render(AtRatedPair(), When);
        Assert.Contains("Mémoire à sa vitesse nominale rapportée", text);
    }

    [Fact]
    public void Synthesis_ListsTypeTotalSlotsAndTheChannelHint()
    {
        var text = MemoryModulesTextReport.Render(AtRatedPair(), When);
        Assert.Contains("SYNTHÈSE", text);
        Assert.Contains("Type", text);
        Assert.Contains("DDR5", text);
        Assert.Contains("Total installé", text);
        Assert.Contains("Canaux (indicatif)", text);          // channel mode framed as a hint, never asserted
        Assert.Contains("Double canal probable", text);       // two sticks → « probable », not « actif »
    }

    [Fact]
    public void Modules_ListIdentityAndSlot_WithCountHeader()
    {
        var text = MemoryModulesTextReport.Render(AtRatedPair(), When);
        Assert.Contains("BARRETTES (2)", text);
        Assert.Contains("Corsair CMK32GX5M2B6000Z30", text);
        Assert.Contains("[DIMM_A1]", text);
        Assert.Contains("[DIMM_A2]", text);
    }

    [Fact]
    public void BelowRated_HeadlineFlagsXmpInactive_AndModuleIsMarked()
    {
        var text = MemoryModulesTextReport.Render(
            Report(4, "DDR5", 4800, 6000, Mod(slot: "DIMM_A1", cfg: 4800), Mod(slot: "DIMM_A2", cfg: 4800)), When);
        Assert.Contains("XMP/EXPO probablement inactif", text);                 // indicative language, never « inactif » as fact
        Assert.Contains("Sous la vitesse nominale", text);                      // the per-module marker
    }

    [Fact]
    public void Empty_SaysNoModules_AndCarriesElevationCaveat_NotAFabricatedStick()
    {
        var text = MemoryModulesTextReport.Render(MemoryModulesReport.From(new HardwareInfo()), When);
        Assert.Contains("Aucune barrette détectée", text);
        Assert.Contains("BARRETTES (0)", text);
        Assert.Contains("Aucune barrette listée par Windows", text);
        Assert.Contains("élevée (administrateur)", text);                       // honest why-empty caveat from ProfileDetail
        Assert.DoesNotContain("  • ", text);                                    // no fabricated module bullet
    }

    [Fact]
    public void AbsentCapacity_RendersDash_NeverAFabricatedZero()
    {
        // A stick whose capacity Windows didn't report: capacity must be « — », never « 0 Go ».
        var text = MemoryModulesTextReport.Render(
            Report(2, "DDR5", 6000, 6000, Mod(bytes: 0)), When);
        Assert.Contains("Capacité", text);
        Assert.DoesNotContain("0 Go", text);     // no fabricated capacity
    }

    [Fact]
    public void BankLabel_AppearsWhenPresent()
    {
        var text = MemoryModulesTextReport.Render(
            Report(2, "DDR5", 6000, 6000, Mod(bank: "P0 CHANNEL A")), When);
        Assert.Contains("Banque", text);
        Assert.Contains("P0 CHANNEL A", text);
    }

    [Fact]
    public void SingleModule_ChannelHintIsCanalSimple()
    {
        var text = MemoryModulesTextReport.Render(
            Report(2, "DDR5", 6000, 6000, Mod(slot: "DIMM_A1")), When);
        Assert.Contains("Canal simple", text);
    }

    [Fact]
    public void Footer_KeepsReadOnlyNeverSentNoFpsAndBiosHonestyLines()
    {
        var text = MemoryModulesTextReport.Render(AtRatedPair(), When);
        Assert.Contains("jamais envoyé", text);
        Assert.Contains("aucun gain de FPS", text);
        Assert.Contains("BIOS", text);
        Assert.Contains("sous-estimer", text);    // the « nominale » caveat: Windows can under-report the kit's real rating
    }
}
