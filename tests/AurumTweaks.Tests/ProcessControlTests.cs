using System.Collections.Generic;
using System.Diagnostics;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Priority-level mapping. The load-bearing honesty point: Realtime (and Idle/BelowNormal) round-trip on the READ
/// side so an already-Realtime process is labelled truthfully, but they are NOT in <see cref="PriorityLevels.Offered"/>,
/// so no button can ever push them through — Realtime would outrank the OS threads servicing keyboard/mouse/audio.
/// </summary>
public class PriorityLevelsTests
{
    [Theory]
    [InlineData(ProcessPriorityClass.Idle, ProcessPriorityLevel.Idle)]
    [InlineData(ProcessPriorityClass.BelowNormal, ProcessPriorityLevel.BelowNormal)]
    [InlineData(ProcessPriorityClass.Normal, ProcessPriorityLevel.Normal)]
    [InlineData(ProcessPriorityClass.AboveNormal, ProcessPriorityLevel.AboveNormal)]
    [InlineData(ProcessPriorityClass.High, ProcessPriorityLevel.High)]
    [InlineData(ProcessPriorityClass.RealTime, ProcessPriorityLevel.Realtime)]
    public void FromClass_MapsEveryWindowsClass(ProcessPriorityClass cls, ProcessPriorityLevel expected)
        => Assert.Equal(expected, PriorityLevels.FromClass(cls));

    [Theory]
    [InlineData(ProcessPriorityLevel.Idle, ProcessPriorityClass.Idle)]
    [InlineData(ProcessPriorityLevel.Normal, ProcessPriorityClass.Normal)]
    [InlineData(ProcessPriorityLevel.AboveNormal, ProcessPriorityClass.AboveNormal)]
    [InlineData(ProcessPriorityLevel.High, ProcessPriorityClass.High)]
    [InlineData(ProcessPriorityLevel.Realtime, ProcessPriorityClass.RealTime)]
    public void ToClass_MapsBackToWindowsClass(ProcessPriorityLevel level, ProcessPriorityClass expected)
        => Assert.Equal(expected, PriorityLevels.ToClass(level));

    [Fact]
    public void ToClass_Unknown_NeverWritesUnknown_FallsBackToNormal()
        => Assert.Equal(ProcessPriorityClass.Normal, PriorityLevels.ToClass(ProcessPriorityLevel.Unknown));

    [Fact]
    public void Offered_IsExactlyNormalAboveHigh()
        => Assert.Equal(
            new[] { ProcessPriorityLevel.Normal, ProcessPriorityLevel.AboveNormal, ProcessPriorityLevel.High },
            PriorityLevels.Offered);

    [Theory]
    [InlineData(ProcessPriorityLevel.Normal, true)]
    [InlineData(ProcessPriorityLevel.AboveNormal, true)]
    [InlineData(ProcessPriorityLevel.High, true)]
    [InlineData(ProcessPriorityLevel.Realtime, false)]   // the whole point — never offerable
    [InlineData(ProcessPriorityLevel.Idle, false)]
    [InlineData(ProcessPriorityLevel.BelowNormal, false)]
    [InlineData(ProcessPriorityLevel.Unknown, false)]
    public void IsOffered_OnlyTheSafeBoostLevels(ProcessPriorityLevel level, bool expected)
        => Assert.Equal(expected, PriorityLevels.IsOffered(level));

    [Fact]
    public void Realtime_ParsesButIsNotOffered()
    {
        // The honesty invariant in one assertion: we can READ Realtime, but we may never SET it.
        Assert.Equal(ProcessPriorityLevel.Realtime, PriorityLevels.FromClass(ProcessPriorityClass.RealTime));
        Assert.False(PriorityLevels.IsOffered(ProcessPriorityLevel.Realtime));
    }

    [Theory]
    [InlineData(ProcessPriorityLevel.Normal, "Normale")]
    [InlineData(ProcessPriorityLevel.High, "Haute")]
    [InlineData(ProcessPriorityLevel.Realtime, "Temps réel")]
    [InlineData(ProcessPriorityLevel.Unknown, "Inconnue")]
    public void Label_IsFrench(ProcessPriorityLevel level, string expected)
        => Assert.Equal(expected, PriorityLevels.Label(level));
}

/// <summary>
/// CPU-layout derived facts. A layout is only « hybrid » when there is a strict, non-empty subset of performance
/// cores — an empty probe result OR every core sharing one efficiency class both honestly mean "no P/E choice".
/// </summary>
public class CpuLayoutTests
{
    [Fact]
    public void Hybrid_StrictNonEmptySubsetIsHybrid()
    {
        var layout = new CpuLayout(16, new[] { 0, 1, 2, 3, 4, 5, 6, 7 });
        Assert.True(layout.IsHybrid);
        Assert.Equal(8, layout.PerformanceCoreCount);
        Assert.Equal(8, layout.EfficiencyCoreCount);
    }

    [Fact]
    public void Flat_NoPerformanceCores_IsNotHybrid()
    {
        var layout = CpuLayout.Flat(8);
        Assert.False(layout.IsHybrid);
        Assert.Equal(0, layout.PerformanceCoreCount);
        Assert.Equal(0, layout.EfficiencyCoreCount);
    }

    [Fact]
    public void AllCoresSameClass_IsNotHybrid()
    {
        // PerformanceCoreIndices.Count == LogicalCount ⇒ the "< LogicalCount" guard makes this non-hybrid.
        var layout = new CpuLayout(8, new[] { 0, 1, 2, 3, 4, 5, 6, 7 });
        Assert.False(layout.IsHybrid);
        Assert.Equal(0, layout.EfficiencyCoreCount);
    }

    [Fact]
    public void Flat_NegativeCount_ClampsToZero()
        => Assert.Equal(0, CpuLayout.Flat(-4).LogicalCount);
}

/// <summary>
/// Affinity-mask math. Every preset must yield a non-empty mask (Windows rejects an all-zero affinity), the
/// « performance cores » preset is only valid on a hybrid CPU, and the 64-logical boundary must not trip the
/// <c>1 &lt;&lt; 64</c> shift trap (in C# the shift count is masked to 63, so 1&lt;&lt;64 == 1 — the guard is essential).
/// </summary>
public class AffinityPlanTests
{
    [Theory]
    [InlineData(1, 0x1UL)]
    [InlineData(4, 0xFUL)]
    [InlineData(8, 0xFFUL)]
    [InlineData(63, (1UL << 63) - 1UL)]
    public void AllMask_BelowBoundary_IsLowNBits(int logical, ulong expected)
        => Assert.Equal(expected, AffinityPlan.AllMask(CpuLayout.Flat(logical)));

    [Theory]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(128)]
    public void AllMask_AtOrAboveSixtyFour_IsAllOnes_NoShiftTrap(int logical)
        => Assert.Equal(ulong.MaxValue, AffinityPlan.AllMask(CpuLayout.Flat(logical)));

    [Fact]
    public void Build_PerformanceCores_OnHybrid_IsExactlyThePCoreBits()
    {
        var layout = new CpuLayout(16, new[] { 0, 1, 2, 3, 4, 5, 6, 7 });
        Assert.Equal(0xFFUL, AffinityPlan.Build(AffinityStrategy.PerformanceCores, layout));
    }

    [Fact]
    public void Build_AllCores_IsEveryLogicalBit()
    {
        var layout = new CpuLayout(16, new[] { 0, 1, 2, 3 });
        Assert.Equal(0xFFFFUL, AffinityPlan.Build(AffinityStrategy.AllCores, layout));
    }

    [Fact]
    public void Build_PerformanceCores_OnFlatCpu_FallsBackToAllCores()
    {
        // No P/E distinction ⇒ asking for "performance cores" honestly gives all cores, never an empty mask.
        var layout = CpuLayout.Flat(8);
        Assert.Equal(0xFFUL, AffinityPlan.Build(AffinityStrategy.PerformanceCores, layout));
    }

    [Fact]
    public void Build_NeverReturnsEmptyMask_WhenPCoreIndicesAreOutOfReach()
    {
        // Pathological: a hybrid layout whose P-core indices all sit at/above bit 64 — the mask would be empty,
        // so the builder must fall back to all-cores rather than hand Windows an invalid 0 mask.
        var layout = new CpuLayout(128, new[] { 64, 65, 66, 67 });
        Assert.True(layout.IsHybrid);
        Assert.Equal(ulong.MaxValue, AffinityPlan.Build(AffinityStrategy.PerformanceCores, layout));
    }

    [Fact]
    public void Offered_Hybrid_OffersBothStrategies()
    {
        var layout = new CpuLayout(16, new[] { 0, 1, 2, 3, 4, 5, 6, 7 });
        Assert.Equal(new[] { AffinityStrategy.AllCores, AffinityStrategy.PerformanceCores }, AffinityPlan.Offered(layout));
    }

    [Fact]
    public void Offered_Flat_OffersOnlyAllCores()
        => Assert.Equal(new[] { AffinityStrategy.AllCores }, AffinityPlan.Offered(CpuLayout.Flat(8)));

    [Fact]
    public void IsOffered_PerformanceCores_RejectedOnFlatCpu()
    {
        Assert.False(AffinityPlan.IsOffered(AffinityStrategy.PerformanceCores, CpuLayout.Flat(8)));
        Assert.True(AffinityPlan.IsOffered(AffinityStrategy.AllCores, CpuLayout.Flat(8)));
    }
}

/// <summary>Pure, culture-free affinity formatting: core counting, "all cores" detection, and range collapsing.</summary>
public class AffinityFormatTests
{
    [Theory]
    [InlineData(0xFFUL, 8)]
    [InlineData(0x0UL, 0)]
    [InlineData(0x34FUL, 7)]   // bits {0,1,2,3,6,8,9}
    public void CoreCount_IsPopCount(ulong mask, int expected)
        => Assert.Equal(expected, AffinityFormat.CoreCount(mask));

    [Fact]
    public void Describe_AllCores_SaysAllWithCount()
        => Assert.Equal("Tous les cœurs (8)", AffinityFormat.Describe(0xFFUL, CpuLayout.Flat(8)));

    [Fact]
    public void Describe_Subset_ListsTheCores()
        => Assert.Equal("8 cœur(s) : 0-7", AffinityFormat.Describe(0xFFUL, CpuLayout.Flat(16)));

    [Fact]
    public void Describe_GappedSubset_CollapsesRanges()
        => Assert.Equal("7 cœur(s) : 0-3, 6, 8-9", AffinityFormat.Describe(0x34FUL, CpuLayout.Flat(16)));

    [Fact]
    public void Describe_OutOfRangeBits_AreClampedToLayout()
        // mask has bits beyond the 4 logical cores; effective is masked down to all-4 ⇒ "all cores".
        => Assert.Equal("Tous les cœurs (4)", AffinityFormat.Describe(0xFFUL, CpuLayout.Flat(4)));

    [Fact]
    public void Describe_EmptyMask_IsDash()
        => Assert.Equal("—", AffinityFormat.Describe(0x0UL, CpuLayout.Flat(8)));

    [Theory]
    [InlineData(new[] { 0, 1, 2, 3, 6, 8, 9 }, "0-3, 6, 8-9")]
    [InlineData(new[] { 5 }, "5")]
    [InlineData(new[] { 0, 2, 4 }, "0, 2, 4")]
    [InlineData(new[] { 10, 11, 12 }, "10-12")]
    public void Ranges_CollapseConsecutiveRuns(int[] sorted, string expected)
        => Assert.Equal(expected, AffinityFormat.Ranges(sorted));

    [Fact]
    public void Ranges_Empty_IsEmptyString()
        => Assert.Equal(string.Empty, AffinityFormat.Ranges(new int[0]));

    [Fact]
    public void SetBits_ReturnsAscendingIndicesBelowLimit()
        => Assert.Equal(new[] { 0, 1, 6 }, AffinityFormat.SetBits(0x43UL, 8));   // bits 0,1,6
}

/// <summary>
/// Running-process ↔ installed-game cross-reference. Matching is install-directory containment, drive- and
/// case-insensitive, with a trailing separator so « …\Valorant » can't spuriously match « …\ValorantTool ».
/// </summary>
public class ProcessGameMatchTests
{
    private static DetectedGame Game(string dir, bool ac = false, string acName = "", string platform = "Steam")
        => new() { Name = "Test", Platform = platform, InstallDirectory = dir, HasAntiCheat = ac, AntiCheatName = acName };

    [Fact]
    public void Match_ExeUnderInstallDir_MatchesTheGame()
    {
        var game = Game(@"C:\Games\Valorant", ac: true, acName: "Vanguard", platform: "Riot");
        var hit = ProcessGameMatch.Match(@"C:\Games\Valorant\live\VALORANT.exe", new[] { game });
        Assert.Same(game, hit);
        Assert.True(hit!.HasAntiCheat);
        Assert.Equal("Vanguard", hit.AntiCheatName);
    }

    [Fact]
    public void Match_SiblingDirWithSharedPrefix_DoesNotMatch()
    {
        // The trailing-separator guard: "Valorant" must not match a process living in "ValorantTool".
        var game = Game(@"C:\Games\Valorant");
        Assert.Null(ProcessGameMatch.Match(@"C:\Games\ValorantTool\x.exe", new[] { game }));
    }

    [Fact]
    public void Match_IsCaseAndSlashInsensitive()
    {
        var game = Game(@"C:\Games\Valorant");
        Assert.Same(game, ProcessGameMatch.Match(@"c:/games/valorant/live/x.exe", new[] { game }));
    }

    [Fact]
    public void Match_ExePathEqualsInstallDir_Matches()
    {
        var game = Game(@"C:\Games\Valorant");
        Assert.Same(game, ProcessGameMatch.Match(@"C:\Games\Valorant", new[] { game }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Match_NoExePath_IsNull(string? exe)
        => Assert.Null(ProcessGameMatch.Match(exe, new[] { Game(@"C:\Games\Valorant") }));

    [Fact]
    public void Match_GameWithoutInstallDir_IsSkipped()
        => Assert.Null(ProcessGameMatch.Match(@"C:\Games\Valorant\x.exe", new[] { Game("") }));

    [Fact]
    public void Match_NoGames_IsNull()
        => Assert.Null(ProcessGameMatch.Match(@"C:\Games\Valorant\x.exe", new DetectedGame[0]));
}

/// <summary>
/// The recommendation core. A game gets High priority and — on a hybrid CPU — the performance cores; a normal
/// process is left to Windows. An anti-cheat-protected game carries the tampering warning so the user acts knowingly.
/// </summary>
public class PriorityAffinityAdviceTests
{
    private static readonly CpuLayout Hybrid = new(16, new[] { 0, 1, 2, 3, 4, 5, 6, 7 });
    private static readonly CpuLayout Flat = CpuLayout.Flat(8);

    [Fact]
    public void Game_OnHybrid_HighPriority_PerformanceCores_NoWarning()
    {
        var a = PriorityAffinityAdvice.For(isGame: true, hasAntiCheat: false, Hybrid);
        Assert.Equal(ProcessPriorityLevel.High, a.RecommendedPriority);
        Assert.Equal(AffinityStrategy.PerformanceCores, a.RecommendedAffinity);
        Assert.Null(a.Warning);
        Assert.False(string.IsNullOrWhiteSpace(a.Rationale));
    }

    [Fact]
    public void Game_OnFlatCpu_HighPriority_AllCores()
    {
        var a = PriorityAffinityAdvice.For(isGame: true, hasAntiCheat: false, Flat);
        Assert.Equal(ProcessPriorityLevel.High, a.RecommendedPriority);
        Assert.Equal(AffinityStrategy.AllCores, a.RecommendedAffinity);
    }

    [Fact]
    public void Game_WithAntiCheat_CarriesTheTamperingWarning()
    {
        var a = PriorityAffinityAdvice.For(isGame: true, hasAntiCheat: true, Flat);
        Assert.NotNull(a.Warning);
        Assert.Contains("anticheat", a.Warning!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonGame_NormalPriority_AllCores_EvenOnHybrid_NoWarning()
    {
        var a = PriorityAffinityAdvice.For(isGame: false, hasAntiCheat: false, Hybrid);
        Assert.Equal(ProcessPriorityLevel.Normal, a.RecommendedPriority);
        Assert.Equal(AffinityStrategy.AllCores, a.RecommendedAffinity);   // never strands a non-game on P-cores
        Assert.Null(a.Warning);
    }
}

/// <summary>Display-state of a process row, including the honesty rules: no action buttons on an inaccessible process.</summary>
public class RunningProcessInfoTests
{
    private static readonly CpuLayout Hybrid = new(16, new[] { 0, 1, 2, 3, 4, 5, 6, 7 });

    private static RunningProcessInfo Row(
        bool accessible = true, bool isGame = false, bool hasAntiCheat = false,
        string name = "game", ulong affinity = 0xFFFFUL, CpuLayout? layout = null)
    {
        var lay = layout ?? Hybrid;
        var advice = PriorityAffinityAdvice.For(isGame, hasAntiCheat, lay);
        return new RunningProcessInfo(
            Pid: 1234, Name: name, ExecutablePath: @"C:\g\x.exe", Accessible: accessible,
            Priority: ProcessPriorityLevel.Normal, AffinityMask: affinity, IsGame: isGame,
            Platform: isGame ? "Riot" : "", HasAntiCheat: hasAntiCheat,
            AntiCheatName: hasAntiCheat ? "Vanguard" : "", WorkingSetBytes: 1024L * 1024,
            Layout: lay, Advice: advice);
    }

    [Fact]
    public void AccessibleGame_ShowsActions_AndOptimize()
    {
        var r = Row(accessible: true, isGame: true);
        Assert.True(r.ShowActions);
        Assert.False(r.ShowInaccessible);
        Assert.True(r.ShowOptimize);
        Assert.True(r.ShowGameBadge);
        Assert.True(r.ShowPerformanceCores);   // accessible + hybrid
    }

    [Fact]
    public void InaccessibleProcess_HidesActions_ShowsExplanation_AffinityDash()
    {
        var r = Row(accessible: false, isGame: true);
        Assert.False(r.ShowActions);
        Assert.True(r.ShowInaccessible);
        Assert.False(r.ShowOptimize);            // optimize needs accessibility
        Assert.False(r.ShowPerformanceCores);    // hidden when inaccessible even on hybrid
        Assert.Equal("—", r.AffinityDisplay);
    }

    [Fact]
    public void NonGame_DoesNotShowOptimizeOrGameBadge()
    {
        var r = Row(accessible: true, isGame: false);
        Assert.False(r.ShowOptimize);
        Assert.False(r.ShowGameBadge);
        Assert.True(r.ShowActions);              // a normal app is still tunable
    }

    [Fact]
    public void AntiCheatBadge_ShownOnlyForProtectedGame()
    {
        Assert.True(Row(isGame: true, hasAntiCheat: true).ShowAntiCheat);
        Assert.False(Row(isGame: true, hasAntiCheat: false).ShowAntiCheat);
        Assert.False(Row(isGame: false, hasAntiCheat: true).ShowAntiCheat);   // not a game ⇒ no badge
    }

    [Fact]
    public void PerformanceCoresButton_HiddenOnFlatCpu()
        => Assert.False(Row(accessible: true, isGame: true, layout: CpuLayout.Flat(8)).ShowPerformanceCores);

    [Fact]
    public void DisplayName_FallsBackToPid_WhenNameBlank()
        => Assert.Equal("PID 1234", Row(name: "  ").DisplayName);

    [Fact]
    public void StateDisplay_ShowsPriorityAndAffinity()
    {
        var r = Row(accessible: true, affinity: 0xFFFFUL);
        Assert.Contains("Normale", r.StateDisplay);
        Assert.Contains("Tous les cœurs (16)", r.StateDisplay);
    }
}

/// <summary>The report-level summary: human CPU description and the game/listed counts.</summary>
public class ProcessControlReportTests
{
    private static RunningProcessInfo Proc(bool isGame) => new(
        Pid: 1, Name: "p", ExecutablePath: null, Accessible: true, Priority: ProcessPriorityLevel.Normal,
        AffinityMask: 0xFFUL, IsGame: isGame, Platform: "", HasAntiCheat: false, AntiCheatName: "",
        WorkingSetBytes: 0, Layout: CpuLayout.Flat(8),
        Advice: PriorityAffinityAdvice.For(isGame, false, CpuLayout.Flat(8)));

    [Fact]
    public void CpuSummary_Hybrid_SpellsOutPerformanceAndEfficiency()
    {
        var report = new ProcessControlReport(new List<RunningProcessInfo>(), new CpuLayout(16, new[] { 0, 1, 2, 3, 4, 5, 6, 7 }), QueryOk: true);
        Assert.Equal("16 cœurs logiques · hybride (8 performance / 8 efficients)", report.CpuSummary);
    }

    [Fact]
    public void CpuSummary_Flat_IsJustTheCount()
    {
        var report = new ProcessControlReport(new List<RunningProcessInfo>(), CpuLayout.Flat(8), QueryOk: true);
        Assert.Equal("8 cœurs logiques", report.CpuSummary);
    }

    [Fact]
    public void Counts_CountAllAndGamesSeparately()
    {
        var report = new ProcessControlReport(
            new[] { Proc(isGame: true), Proc(isGame: false), Proc(isGame: false) },
            CpuLayout.Flat(8), QueryOk: true);
        Assert.Equal(3, report.Count);
        Assert.Equal(1, report.GameCount);
    }
}

public class ProcessPersistencePlanTests
{
    private static readonly CpuLayout Hybrid = new(16, new[] { 0, 1, 2, 3, 4, 5, 6, 7 });

    private static RunningProcessInfo GameRow(bool accessible = true) => new(
        Pid: 1234,
        Name: "Game",
        ExecutablePath: @"C:\Games\Game\Game.exe",
        Accessible: accessible,
        Priority: ProcessPriorityLevel.Normal,
        AffinityMask: 0xFFFFUL,
        IsGame: true,
        Platform: "Steam",
        HasAntiCheat: false,
        AntiCheatName: "",
        WorkingSetBytes: 0,
        Layout: Hybrid,
        Advice: PriorityAffinityAdvice.For(isGame: true, hasAntiCheat: false, Hybrid));

    [Fact]
    public void BuildRule_UsesRecommendedPriorityAffinity_AndOptionalPowerPlan()
    {
        var activePlan = PowerSchemeCatalog.Balanced;
        var rule = ProcessPersistencePlan.BuildRule(
            GameRow(),
            includeHighPerformancePlan: true,
            currentPowerPlan: activePlan,
            createdUtc: new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Game", rule.ProcessName);
        Assert.Equal(ProcessPriorityLevel.High, rule.Priority);
        Assert.Equal((long)0xFF, rule.AffinityMask);
        Assert.Equal(PowerSchemeCatalog.HighPerformance, rule.PowerPlanWhileRunning);
        Assert.Equal(activePlan, rule.PowerPlanWhenIdle);
    }

    [Fact]
    public void Upsert_ReplacesByProcessName_CaseInsensitive()
    {
        var oldRule = ProcessPersistencePlan.BuildRule(GameRow(), false, null, DateTime.UnixEpoch);
        var updated = oldRule with { ProcessName = "game", AffinityMask = (long)0xF };

        var rules = ProcessPersistencePlan.Upsert(new[] { oldRule }, updated);

        var only = Assert.Single(rules);
        Assert.Equal((long)0xF, only.AffinityMask);
    }

    [Fact]
    public void Remove_DropsOnlyMatchingProcessName()
    {
        var game = ProcessPersistencePlan.BuildRule(GameRow(), false, null, DateTime.UnixEpoch);
        var other = game with { ProcessName = "Other", DisplayName = "Other" };

        var rules = ProcessPersistencePlan.Remove(new[] { game, other }, "GAME");

        Assert.Same(other, Assert.Single(rules));
    }

    [Fact]
    public void RenderScript_IsInspectableAndLimitedToProcessPowerCfgWork()
    {
        var script = ProcessPersistencePlan.RenderScript();

        Assert.Contains(ProcessPersistencePlan.RulesFileName, script);
        Assert.Contains("Get-Process -Name", script);
        Assert.Contains("PriorityClass", script);
        Assert.Contains("ProcessorAffinity", script);
        Assert.Contains("powercfg.exe /setactive", script);
        Assert.Contains("tâche planifiée", script);
        Assert.DoesNotContain("New-Service", script, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sc.exe create", script, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-ItemProperty", script, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Set-MpPreference", script, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaskCommands_TargetVisibleAurumTaskName()
    {
        var create = ProcessPersistencePlan.BuildCreateTaskArgs(@"C:\Aurum\ApplyProcessRules.ps1");
        var delete = ProcessPersistencePlan.BuildDeleteTaskArgs();
        var query = ProcessPersistencePlan.BuildQueryTaskArgs();

        Assert.Contains(ProcessPersistencePlan.TaskName, create);
        Assert.Contains("/SC MINUTE", create);
        Assert.Contains("/RL HIGHEST", create);
        Assert.Contains("powershell.exe -NoProfile", create);
        Assert.Contains(ProcessPersistencePlan.TaskName, delete);
        Assert.Contains(ProcessPersistencePlan.TaskName, query);
        Assert.DoesNotContain("/SVC", create, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PersistenceReport_StatusDisclosesTaskPresence()
    {
        var rule = ProcessPersistencePlan.BuildRule(GameRow(), false, null, DateTime.UnixEpoch);

        var missing = new ProcessPersistenceReport(new[] { rule }, TaskInstalled: false, "rules", "script");
        var active = new ProcessPersistenceReport(new[] { rule }, TaskInstalled: true, "rules", "script");

        Assert.Contains("absente", missing.StateDisplay);
        Assert.Contains("active", active.StateDisplay);
        Assert.Contains("1", active.StateDisplay);
    }
}
