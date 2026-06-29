using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Tests the <see cref="AdaptiveRecommendationService"/> — the "adapté à ton PC" brain behind the
/// dashboard. It cross-references detected hardware against the catalogue to decide what's
/// <i>applicable</i>, what's in the safe one-click <i>default set</i>, how things <i>rank</i>, and what
/// hardware <i>insights</i> to surface. These are honesty-load-bearing: a tweak wrongly shown as "safe
/// for this PC", an anti-cheat-risky tweak leaking into the recommended set, or a "free perf" insight
/// that doesn't actually apply would all mislead the user.
///
/// Everything runs against hand-built fakes (<see cref="FakeHardwareService"/> + <see cref="FakeTweakRepository"/>)
/// so each applicability/scoring axis is controlled precisely and deterministically. The SHIPPED catalog
/// is validated separately by <c>TweakCatalogIntegrityTests</c>.
/// </summary>
public class AdaptiveRecommendationServiceTests
{
    // A throwaway engine for the pure IsApplicable(tweak, hw) tests — that method takes hw as a
    // parameter, so the injected hardware service is irrelevant here.
    private static readonly AdaptiveRecommendationService Engine =
        new(new FakeHardwareService(new HardwareInfo()), new FakeTweakRepository(Array.Empty<Tweak>()));

    private static AdaptiveRecommendationService Svc(HardwareInfo hw, params Tweak[] tweaks)
        => new(new FakeHardwareService(hw), new FakeTweakRepository(tweaks));

    // ---- builders -------------------------------------------------------------

    /// <summary>A minimal, valid tweak. Defaults to the safest shape (Tranquille / no risk).</summary>
    private static Tweak Tw(
        string id,
        TweakTier tier = TweakTier.Tranquille,
        RiskLevel risk = RiskLevel.None,
        int priority = 50,
        TweakApplicability? applicability = null,
        AntiCheatMatrix? antiCheat = null,
        List<string>? windowsVersions = null,
        bool isApplied = false)
    {
        var t = new Tweak
        {
            Id = id,
            Name = new() { ["fr"] = id, ["en"] = id },
            Tier = tier,
            Risk = risk,
            Priority = priority,
            Applicability = applicability,
            IsApplied = isApplied,
        };
        if (antiCheat is not null) t.AntiCheat = antiCheat;
        if (windowsVersions is not null) t.WindowsVersions = windowsVersions;
        return t;
    }

    /// <summary>A machine descriptor. Defaults to a clean AMD X3D + NVIDIA desktop on Win11.</summary>
    private static HardwareInfo Hw(
        bool win11 = true,
        bool laptop = false,
        bool ssd = true,
        double ramGb = 32,
        string cpuVendor = "AuthenticAMD",
        string cpuName = "AMD Ryzen 7 7800X3D",
        CpuFamily family = CpuFamily.Ryzen7000X3D,
        GpuVendor gpu = GpuVendor.Nvidia,
        string ramType = "DDR5",
        int ramConfigured = 6000,
        int ramRated = 6000,
        bool reBar = true,
        bool vbs = false,
        bool ltsc = false,
        bool vanguard = false,
        bool eac = false,
        bool battlEye = false,
        bool faceit = false)
        => new()
        {
            IsWindows11 = win11,
            IsLaptop = laptop,
            SystemDriveIsSsd = ssd,
            TotalRamBytes = (long)(ramGb * 1024 * 1024 * 1024),
            CpuVendor = cpuVendor,
            CpuName = cpuName,
            DetectedFamily = family,
            GpuVendor = gpu,
            RamType = ramType,
            RamConfiguredMhz = ramConfigured,
            RamRatedMhz = ramRated,
            ResizableBarEnabled = reBar,
            VbsRunning = vbs,
            IsLtsc = ltsc,
            VanguardDetected = vanguard,
            EacDetected = eac,
            BattlEyeDetected = battlEye,
            FaceItAcDetected = faceit,
        };

    private static AntiCheatMatrix Banned() => new() { Vanguard = AntiCheatStatus.Banned };

    private static List<string> IdsInDefault(AdaptivePlan p) =>
        p.Recommendations.Where(r => r.InDefaultSet).Select(r => r.Tweak.Id).ToList();

    /// <summary>One installed RAM stick of the given whole-GB capacity (the field the mismatch detector reads).</summary>
    private static MemoryModule Mod(int gb) => new() { CapacityBytes = (long)gb * 1024 * 1024 * 1024 };

    // =====================================================================================
    //  IsApplicable — hardware/OS gating
    // =====================================================================================

    [Fact]
    public void IsApplicable_NullApplicability_AppliesToAnyMachine()
    {
        var t = Tw("any");
        Assert.True(Engine.IsApplicable(t, Hw(win11: true)));
        Assert.True(Engine.IsApplicable(t, Hw(win11: false)));
    }

    [Fact]
    public void IsApplicable_OsVersionGate_HonoursWindowsVersionsList()
    {
        var win11Only = Tw("w11", windowsVersions: new() { "11" });
        Assert.True(Engine.IsApplicable(win11Only, Hw(win11: true)));
        Assert.False(Engine.IsApplicable(win11Only, Hw(win11: false)));

        var win10Only = Tw("w10", windowsVersions: new() { "10" });
        Assert.False(Engine.IsApplicable(win10Only, Hw(win11: true)));
        Assert.True(Engine.IsApplicable(win10Only, Hw(win11: false)));
    }

    [Fact]
    public void IsApplicable_RequiresWin11_ExcludedOnWindows10()
    {
        var t = Tw("needs11", applicability: new() { RequiresWin11 = true });
        Assert.False(Engine.IsApplicable(t, Hw(win11: false)));
        Assert.True(Engine.IsApplicable(t, Hw(win11: true)));
    }

    [Fact]
    public void IsApplicable_DesktopOnly_ExcludedOnLaptop()
    {
        var t = Tw("desktop", applicability: new() { DesktopOnly = true });
        Assert.False(Engine.IsApplicable(t, Hw(laptop: true)));
        Assert.True(Engine.IsApplicable(t, Hw(laptop: false)));
    }

    [Fact]
    public void IsApplicable_SsdOnly_ExcludedOnHdd()
    {
        var t = Tw("ssd", applicability: new() { SsdOnly = true });
        Assert.False(Engine.IsApplicable(t, Hw(ssd: false)));
        Assert.True(Engine.IsApplicable(t, Hw(ssd: true)));
    }

    [Fact]
    public void IsApplicable_MinRamGb_ExcludesUnderMinimum_WithHalfGigTolerance()
    {
        var needs16 = Tw("ram16", applicability: new() { MinRamGb = 16 });

        Assert.False(Engine.IsApplicable(needs16, Hw(ramGb: 8)));   // clearly under
        Assert.True(Engine.IsApplicable(needs16, Hw(ramGb: 32)));   // clearly over

        // Tolerance: a "16 GB" kit usually reports ~15.5 GB after reserved memory; the +0.5 slack keeps
        // it applicable (15.5 + 0.5 = 16.0, which is NOT < 16).
        Assert.True(Engine.IsApplicable(needs16, Hw(ramGb: 15.5)));
    }

    [Fact]
    public void IsApplicable_CpuVendors_MatchAgainstVendorOrName()
    {
        var amdOnly = Tw("amd", applicability: new() { CpuVendors = { "AMD" } });

        // Intel box: neither the WMI vendor nor the name contains "AMD".
        Assert.False(Engine.IsApplicable(amdOnly, Hw(cpuVendor: "GenuineIntel", cpuName: "Intel Core i7-13700K")));
        // AMD box matches on vendor.
        Assert.True(Engine.IsApplicable(amdOnly, Hw(cpuVendor: "AuthenticAMD", cpuName: "AMD Ryzen 7 7800X3D")));
        // Match on NAME alone even when the raw vendor string doesn't say "AMD".
        Assert.True(Engine.IsApplicable(amdOnly, Hw(cpuVendor: "", cpuName: "AMD Ryzen 5 5600")));
    }

    [Fact]
    public void IsApplicable_CpuFamilies_MustContainDetectedFamily()
    {
        var x3dOnly = Tw("x3d", applicability: new() { CpuFamilies = { CpuFamily.Ryzen7000X3D } });
        Assert.False(Engine.IsApplicable(x3dOnly, Hw(family: CpuFamily.IntelCore13)));
        Assert.True(Engine.IsApplicable(x3dOnly, Hw(family: CpuFamily.Ryzen7000X3D)));
    }

    [Fact]
    public void IsApplicable_GpuVendors_MustContainDetectedGpu()
    {
        var nvidiaOnly = Tw("nv", applicability: new() { GpuVendors = { GpuVendor.Nvidia } });
        Assert.False(Engine.IsApplicable(nvidiaOnly, Hw(gpu: GpuVendor.Amd)));
        Assert.True(Engine.IsApplicable(nvidiaOnly, Hw(gpu: GpuVendor.Nvidia)));
    }

    [Fact]
    public void IsApplicable_RamTypes_MatchCaseInsensitively()
    {
        var ddr5Only = Tw("ddr5", applicability: new() { RamTypes = { "DDR5" } });
        Assert.False(Engine.IsApplicable(ddr5Only, Hw(ramType: "DDR4")));
        Assert.True(Engine.IsApplicable(ddr5Only, Hw(ramType: "ddr5")));   // case-insensitive
    }

    // =====================================================================================
    //  Default set — the safe one-click "apply recommended" selection
    // =====================================================================================

    [Fact]
    public async Task DefaultSet_IncludesTranquilleAndSafeAvance_ExcludesRiskyMediumExtreme()
    {
        var svc = Svc(Hw(),
            Tw("tranq", TweakTier.Tranquille, RiskLevel.None),
            Tw("avance-low", TweakTier.Avance, RiskLevel.Low),
            Tw("avance-med", TweakTier.Avance, RiskLevel.Medium),   // Avance but Medium risk → not default
            Tw("extreme", TweakTier.Extreme, RiskLevel.None),       // Extreme → never default
            Tw("tranq-highrisk", TweakTier.Tranquille, RiskLevel.High)); // Risk >= High → never default

        var plan = await svc.BuildPlanAsync(strictCompetitive: false);
        var inDefault = IdsInDefault(plan);

        Assert.Contains("tranq", inDefault);
        Assert.Contains("avance-low", inDefault);
        Assert.DoesNotContain("avance-med", inDefault);
        Assert.DoesNotContain("extreme", inDefault);
        Assert.DoesNotContain("tranq-highrisk", inDefault);

        Assert.Equal(2, plan.RecommendedCount);   // tranq + avance-low
        Assert.Equal(5, plan.TotalApplicable);     // all 5 apply to this machine
    }

    [Fact]
    public async Task TotalApplicable_ExcludesHardwareIncompatibleTweaks()
    {
        // One universal tweak + one that needs Win11 + one desktop-only. On a Win10 laptop only the
        // universal one is applicable.
        var svc = Svc(Hw(win11: false, laptop: true),
            Tw("any"),
            Tw("needs11", applicability: new() { RequiresWin11 = true }),
            Tw("desktop", applicability: new() { DesktopOnly = true }));

        var plan = await svc.BuildPlanAsync(strictCompetitive: false);

        Assert.Equal(1, plan.TotalApplicable);
        Assert.Equal("any", plan.Recommendations.Single().Tweak.Id);
    }

    // =====================================================================================
    //  Anti-cheat masking — the honesty/safety guarantee
    // =====================================================================================

    [Fact]
    public async Task AntiCheatConcern_StrictMode_ExcludedFromDefaultSet_AndScorePenalised()
    {
        // No anti-cheat installed, but the user opted into strict competitive mode.
        var svc = Svc(Hw(vanguard: false),
            Tw("ac", TweakTier.Tranquille, RiskLevel.None, antiCheat: Banned()),
            Tw("clean", TweakTier.Tranquille, RiskLevel.None));

        var plan = await svc.BuildPlanAsync(strictCompetitive: true);

        Assert.DoesNotContain("ac", IdsInDefault(plan));
        Assert.Contains("clean", IdsInDefault(plan));

        // The -60 anti-cheat penalty must drag it below an otherwise-identical clean tweak.
        var ac = plan.Recommendations.Single(r => r.Tweak.Id == "ac").Score;
        var clean = plan.Recommendations.Single(r => r.Tweak.Id == "clean").Score;
        Assert.Equal(clean - 60, ac);
    }

    [Fact]
    public async Task AntiCheatConcern_NonStrict_ReturnsToDefaultSet_WhenNoAntiCheatInstalled()
    {
        var svc = Svc(Hw(vanguard: false),
            Tw("ac", TweakTier.Tranquille, RiskLevel.None, antiCheat: Banned()));

        var plan = await svc.BuildPlanAsync(strictCompetitive: false);

        // No anti-cheat present and not strict → no concern → the (otherwise safe) tweak is recommended.
        Assert.Contains("ac", IdsInDefault(plan));
    }

    [Fact]
    public async Task AntiCheatConcern_NonStrict_StillExcluded_WhenActiveAntiCheatDetected()
    {
        // Vanguard is actually running on this machine: even outside strict mode, don't risk a ban.
        var svc = Svc(Hw(vanguard: true),
            Tw("ac", TweakTier.Tranquille, RiskLevel.None, antiCheat: Banned()));

        var plan = await svc.BuildPlanAsync(strictCompetitive: false);

        Assert.DoesNotContain("ac", IdsInDefault(plan));
    }

    // =====================================================================================
    //  Scoring & ranking
    // =====================================================================================

    [Fact]
    public async Task TunedTweak_IsFlagged_AndRanksAboveGenericPeer()
    {
        var svc = Svc(Hw(gpu: GpuVendor.Nvidia),
            // Deliberately register the generic one first to prove ordering isn't insertion order.
            Tw("generic", TweakTier.Tranquille, RiskLevel.None, priority: 50),
            Tw("tuned", TweakTier.Tranquille, RiskLevel.None, priority: 50,
                applicability: new() { GpuVendors = { GpuVendor.Nvidia } }));

        var plan = await svc.BuildPlanAsync(strictCompetitive: false);

        Assert.True(plan.Recommendations.Single(r => r.Tweak.Id == "tuned").IsTunedForThisPc);
        Assert.False(plan.Recommendations.Single(r => r.Tweak.Id == "generic").IsTunedForThisPc);

        // +18 specificity bonus AND the tuned tie-break both put it first.
        var tuned = plan.Recommendations.Single(r => r.Tweak.Id == "tuned").Score;
        var generic = plan.Recommendations.Single(r => r.Tweak.Id == "generic").Score;
        Assert.Equal(generic + 18, tuned);

        var ids = plan.Recommendations.Select(r => r.Tweak.Id).ToList();
        Assert.True(ids.IndexOf("tuned") < ids.IndexOf("generic"));
    }

    [Fact]
    public async Task AlreadyAppliedTweak_ScoresLower_ThanIdenticalPending()
    {
        var svc = Svc(Hw(),
            Tw("done", TweakTier.Tranquille, RiskLevel.None, priority: 50, isApplied: true),
            Tw("todo", TweakTier.Tranquille, RiskLevel.None, priority: 50));

        var plan = await svc.BuildPlanAsync(strictCompetitive: false);

        var done = plan.Recommendations.Single(r => r.Tweak.Id == "done").Score;
        var todo = plan.Recommendations.Single(r => r.Tweak.Id == "todo").Score;
        Assert.Equal(todo - 25, done);   // -25 "already applied" push-down
    }

    [Fact]
    public async Task Ordering_DefaultSetTweaks_ComeFirst_EvenWithLowerRawScore()
    {
        // Extreme/high-priority would out-score the Tranquille on raw points, but it's not in the default
        // set — so the default-set tweak must still rank first (default-set is the primary sort key).
        var svc = Svc(Hw(),
            Tw("extreme", TweakTier.Extreme, RiskLevel.None, priority: 100),
            Tw("tranq", TweakTier.Tranquille, RiskLevel.None, priority: 10));

        var plan = await svc.BuildPlanAsync(strictCompetitive: false);
        var ids = plan.Recommendations.Select(r => r.Tweak.Id).ToList();

        Assert.True(ids.IndexOf("tranq") < ids.IndexOf("extreme"));
    }

    // =====================================================================================
    //  Insights & potential score
    // =====================================================================================

    [Fact]
    public async Task RamBelowRated_ProducesBiosOpportunityInsight_AndBoostsPotential()
    {
        // DDR5 kit running 4800 vs rated 6000 → EXPO/XMP off → the headline "free win".
        // Intel GPU keeps the GPU/ReBAR opportunity insights out, isolating the RAM contribution.
        var hw = Hw(win11: false, family: CpuFamily.IntelCore13, gpu: GpuVendor.Intel,
                    cpuVendor: "GenuineIntel", cpuName: "Intel Core i5-13600K",
                    ramType: "DDR5", ramConfigured: 4800, ramRated: 6000, ramGb: 16, vbs: false);
        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        var ram = plan.Insights.Single(i => i.ActionPage == "Bios" && i.Severity == InsightSeverity.Opportunity);
        Assert.Contains("4800", ram.TitleFr);
        Assert.Contains("6000", ram.TitleFr);

        // potential = (#pending defaults)*3 + (#opportunity insights)*12 + 15 (RAM below rated), clamped.
        int opp = plan.Insights.Count(i => i.Severity == InsightSeverity.Opportunity);
        Assert.Equal(Math.Clamp(opp * 12 + 15, 0, 100), plan.PotentialScore);
    }

    [Fact]
    public async Task PotentialScore_CleanMachine_CountsOnlyPendingDefaults()
    {
        // Intel + Intel GPU + Win10 + 16 GB + SSD + desktop + RAM at rated speed → zero opportunity
        // insights, so the score reduces to pending defaults × 3.
        var hw = Hw(win11: false, family: CpuFamily.IntelCore13, gpu: GpuVendor.Intel,
                    cpuVendor: "GenuineIntel", cpuName: "Intel Core i5-13600K",
                    ramType: "DDR4", ramConfigured: 3200, ramRated: 3200, ramGb: 16, vbs: false);
        var svc = Svc(hw,
            Tw("a", TweakTier.Tranquille, RiskLevel.None),
            Tw("b", TweakTier.Tranquille, RiskLevel.None));

        var plan = await svc.BuildPlanAsync(strictCompetitive: false);

        Assert.Equal(0, plan.Insights.Count(i => i.Severity == InsightSeverity.Opportunity));
        Assert.Equal(6, plan.PotentialScore);   // 2 pending defaults * 3
    }

    [Fact]
    public async Task Insights_WarnOnLaptop_AndOnHdd()
    {
        var laptop = await Svc(Hw(laptop: true)).BuildPlanAsync(strictCompetitive: false);
        Assert.Contains(laptop.Insights,
            i => i.Severity == InsightSeverity.Warning && i.TitleFr.Contains("portable"));

        var hdd = await Svc(Hw(ssd: false)).BuildPlanAsync(strictCompetitive: false);
        Assert.Contains(hdd.Insights,
            i => i.Severity == InsightSeverity.Warning && i.TitleFr.Contains("HDD"));
    }

    [Fact]
    public async Task VbsInsight_IsOpportunityWhenReclaimable_ButInfoWhenAntiCheatNeedsIt()
    {
        // VBS on, no anti-cheat, not strict → the user can reclaim 5-10% → Opportunity.
        var free = await Svc(Hw(vbs: true)).BuildPlanAsync(strictCompetitive: false);
        Assert.Equal(InsightSeverity.Opportunity,
            free.Insights.Single(i => i.TitleFr.Contains("VBS")).Severity);

        // VBS on, strict competitive → keep it (anti-cheats expect it) → demoted to Info, no temptation.
        var strict = await Svc(Hw(vbs: true)).BuildPlanAsync(strictCompetitive: true);
        Assert.Equal(InsightSeverity.Info,
            strict.Insights.Single(i => i.TitleFr.Contains("VBS")).Severity);
    }

    [Fact]
    public async Task Insights_FlagSmtDisabled_AsBiosOpportunity_WhenSiliconExposesMoreThreads()
    {
        // 8-core part reporting 16 hardware threads but only 8 active → SMT/HT is off in the BIOS.
        var hw = Hw();
        hw.CpuCores = 8;
        hw.CpuThreads = 8;
        hw.CpuMaxThreads = 16;

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        var smt = plan.Insights.Single(i => i.TitleFr.Contains("SMT"));
        Assert.Equal(InsightSeverity.Opportunity, smt.Severity);
        Assert.Equal("Bios", smt.ActionPage);
        Assert.Contains("8", smt.TitleFr);    // active threads
        Assert.Contains("16", smt.TitleFr);   // silicon max
    }

    [Fact]
    public async Task Insights_NoSmtFlag_WhenAllThreadsActive()
    {
        // All 16 threads live → nothing to flag, and never a false "disabled" claim.
        var hw = Hw();
        hw.CpuCores = 8;
        hw.CpuThreads = 16;
        hw.CpuMaxThreads = 16;

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        Assert.DoesNotContain(plan.Insights, i => i.TitleFr.Contains("SMT"));
    }

    [Fact]
    public async Task Insights_FlagSingleChannelRam_AsOpportunity_WhenOneModuleAndAFreeSlot()
    {
        // A single stick in a 2-slot board → single-channel, and a matched stick can still be added.
        var hw = Hw();
        hw.RamModuleCount = 1;
        hw.RamSlotCount = 2;

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        var ch = plan.Insights.Single(i => i.TitleFr.Contains("single-channel"));
        Assert.Equal(InsightSeverity.Opportunity, ch.Severity);
        Assert.Equal("MemoryModules", ch.ActionPage);
    }

    [Fact]
    public async Task Insights_NoSingleChannelFlag_WhenAlreadyDualChannel()
    {
        var hw = Hw();
        hw.RamModuleCount = 2;
        hw.RamSlotCount = 4;

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        Assert.DoesNotContain(plan.Insights, i => i.TitleFr.Contains("single-channel"));
    }

    [Fact]
    public async Task Insights_NoSingleChannelFlag_WhenBoardHasOnlyOneSlot()
    {
        // One physical slot means dual-channel is impossible — never suggest adding a stick that can't fit.
        var hw = Hw();
        hw.RamModuleCount = 1;
        hw.RamSlotCount = 1;

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        Assert.DoesNotContain(plan.Insights, i => i.TitleFr.Contains("single-channel"));
    }

    [Fact]
    public async Task Insights_FlagMismatchedRamCapacity_AsWarning_NamingTheSticks()
    {
        // A 16 GB stick paired with an 8 GB one → flex mode (partial dual-channel) and EXPO/XMP fragility.
        var hw = Hw();
        hw.MemoryModules.Add(Mod(16));
        hw.MemoryModules.Add(Mod(8));

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        var mix = plan.Insights.Single(i => i.TitleFr.Contains("dépareillées"));
        Assert.Equal(InsightSeverity.Warning, mix.Severity);
        Assert.Equal("MemoryModules", mix.ActionPage);
        Assert.Contains("16 + 8 Go", mix.TitleFr);   // real detected sizes, largest first
    }

    [Fact]
    public async Task Insights_NoMismatchFlag_WhenSticksShareCapacity()
    {
        // 2× 16 GB (a matched kit) → nothing to warn about, and never a fabricated "dépareillées" claim.
        var hw = Hw();
        hw.MemoryModules.Add(Mod(16));
        hw.MemoryModules.Add(Mod(16));

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        Assert.DoesNotContain(plan.Insights, i => i.TitleFr.Contains("dépareillées"));
    }

    [Fact]
    public async Task Insights_NoMismatchFlag_WhenSingleStick()
    {
        // One stick can't be "mismatched" — the single-channel tell (1c) owns that case instead.
        var hw = Hw();
        hw.MemoryModules.Add(Mod(16));

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        Assert.DoesNotContain(plan.Insights, i => i.TitleFr.Contains("dépareillées"));
    }

    [Fact]
    public async Task Insights_FlagOutdatedBios_AsOpportunity_OnAmd()
    {
        // AMD box (default Hw) with a 2-year-old firmware → "check for an AGESA update", not a danger.
        var hw = Hw();
        hw.BiosReleaseDate = DateTime.Now.AddMonths(-24);

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        var bios = plan.Insights.Single(i => i.TitleFr.Contains("BIOS daté"));
        Assert.Equal(InsightSeverity.Opportunity, bios.Severity);
        Assert.Equal("Bios", bios.ActionPage);
        Assert.Contains("AGESA", bios.DetailFr);
    }

    [Fact]
    public async Task Insights_FlagOutdatedBios_AsWarning_OnIntelRaptorLake_MentioningTheMicrocodeFix()
    {
        // Intel 13/14th gen + old BIOS → the 0x12B degradation fix may be missing → escalate to Warning.
        var hw = Hw(family: CpuFamily.IntelCore13, gpu: GpuVendor.Nvidia,
                    cpuVendor: "GenuineIntel", cpuName: "Intel Core i9-13900K");
        hw.BiosReleaseDate = DateTime.Now.AddMonths(-24);

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        var bios = plan.Insights.Single(i => i.TitleFr.Contains("BIOS daté"));
        Assert.Equal(InsightSeverity.Warning, bios.Severity);
        Assert.Contains("0x12B", bios.DetailFr);
    }

    [Fact]
    public async Task Insights_NoBiosInsight_WhenFirmwareIsRecent()
    {
        var hw = Hw();
        hw.BiosReleaseDate = DateTime.Now.AddMonths(-3);

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        Assert.DoesNotContain(plan.Insights, i => i.TitleFr.Contains("BIOS daté"));
    }

    [Fact]
    public async Task Insights_NoBiosInsight_WhenReleaseDateUnknown()
    {
        // No firmware date → BiosAgeMonths is -1 → never fabricate an "outdated" claim.
        var plan = await Svc(Hw()).BuildPlanAsync(strictCompetitive: false);

        Assert.DoesNotContain(plan.Insights, i => i.TitleFr.Contains("BIOS daté"));
    }

    [Fact]
    public async Task Insights_X3D_MonoCcd_GivesCacheGuidance_WithoutDualCcdPlacementAdvice()
    {
        // Default Hw is a 7800X3D-class part with cores unset (< 12) → mono-CCD: one all-cache CCD, nothing to park.
        var plan = await Svc(Hw()).BuildPlanAsync(strictCompetitive: false);

        var x3d = plan.Insights.Single(i => i.TitleFr.Contains("X3D"));
        Assert.Contains("mono-CCD", x3d.DetailFr);
        Assert.DoesNotContain("CCD", x3d.TitleFr);        // mono title doesn't advertise CCD management
        Assert.DoesNotContain("Game Bar", x3d.DetailFr);  // the parking advice is dual-CCD only — never shown here
    }

    [Fact]
    public async Task Insights_X3D_DualCcd_LeadsWithCcdPlacementGuidance()
    {
        // 12+ cores on an X3D family → dual-CCD (e.g. 7950X3D): cache on one CCD, frequency on the other.
        var hw = Hw(family: CpuFamily.Ryzen7000X3D, cpuName: "AMD Ryzen 9 7950X3D");
        hw.CpuCores = 16;

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        var x3d = plan.Insights.Single(i => i.TitleFr.Contains("X3D"));
        Assert.Contains("deux CCD", x3d.TitleFr);
        Assert.Contains("Game Bar", x3d.DetailFr);
    }

    [Fact]
    public async Task Insights_FlagWindows10EndOfSupport_AsWarning_WithEsuNuance_AndNoAction()
    {
        // Consumer Win10 lost free security updates on 14 Oct 2025 — an honest security Warning. No ActionPage:
        // no in-app action upgrades the OS, and it must state the ESU prolongation rather than over-claim.
        var plan = await Svc(Hw(win11: false)).BuildPlanAsync(strictCompetitive: false);

        var eol = plan.Insights.Single(i => i.TitleFr.Contains("Windows 10"));
        Assert.Equal(InsightSeverity.Warning, eol.Severity);
        Assert.Null(eol.ActionPage);                       // honest: the app can't upgrade the OS
        Assert.Contains("14 octobre 2025", eol.DetailFr);  // the fixed end-of-support fact
        Assert.Contains("ESU", eol.DetailFr);              // the honest nuance (updates may still arrive via ESU)
    }

    [Fact]
    public async Task Insights_NoWindows10Warning_OnWindows11()
    {
        // Win11 owns the Recall/Copilot insight (#10); the Win10 end-of-support warning must never show there.
        var plan = await Svc(Hw(win11: true)).BuildPlanAsync(strictCompetitive: false);

        Assert.DoesNotContain(plan.Insights, i => i.TitleFr.Contains("Windows 10"));
    }

    [Fact]
    public async Task Insights_NoWindows10Warning_OnLtsc()
    {
        // Win10 LTSC keeps a much longer support lifecycle, so the consumer end-of-support warning must not fire —
        // the LTSC insight (#9) covers it instead.
        var plan = await Svc(Hw(win11: false, ltsc: true)).BuildPlanAsync(strictCompetitive: false);

        Assert.DoesNotContain(plan.Insights, i => i.TitleFr.Contains("Windows 10"));
    }

    // =====================================================================================
    //  Profile summary
    // =====================================================================================

    [Fact]
    public async Task ProfileSummary_IncludesCpuGpuAndRam()
    {
        var hw = Hw(cpuName: "AMD Ryzen 7 7800X3D", ramGb: 32, ramConfigured: 6000);
        hw.GpuPrimary = "NVIDIA GeForce RTX 4080";
        hw.OsCaption = "Microsoft Windows 11 Pro";

        var plan = await Svc(hw).BuildPlanAsync(strictCompetitive: false);

        Assert.Contains("Ryzen 7 7800X3D", plan.ProfileSummaryFr);
        Assert.Contains("RTX 4080", plan.ProfileSummaryFr);
        Assert.Contains("32 Go", plan.ProfileSummaryFr);
        Assert.Contains("6000", plan.ProfileSummaryFr);
        Assert.DoesNotContain("Microsoft", plan.ProfileSummaryFr);   // stripped
    }
}
