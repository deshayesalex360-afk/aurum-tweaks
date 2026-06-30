using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the shareable « rapport système ». The honesty-bearing properties: a platform flag Windows couldn't read
/// back stays « indéterminé » (never a fabricated « non »), the report only lists tweaks actually detected as
/// applied, the RAM-below-rated (EXPO/XMP off) tell appears only when true, and the report always states it is
/// local-only. Pure (clock passed in); timezone-dependent timestamp text is not asserted, only structure/content.
/// </summary>
public class SystemReportTests
{
    private static HardwareInfo Hw() => new()
    {
        CpuName = "AMD Ryzen 7 7800X3D",
        CpuCores = 8,
        CpuThreads = 16,
        GpuPrimary = "NVIDIA GeForce RTX 4070",
        MotherboardManufacturer = "ASUS",
        MotherboardModel = "ROG STRIX B650-A",
        BiosVersion = "2024",
        TotalRamBytes = 32L * 1024 * 1024 * 1024,
        RamType = "DDR5",
        RamConfiguredMhz = 6000,
        RamRatedMhz = 6000,
        RamModuleCount = 2,
        OsCaption = "Windows 11 Pro",
        OsBuild = "22631",
        TpmStatus = TriState.Yes,
        SecureBootStatus = TriState.Yes,
        ReBarStatus = TriState.Yes,
        VirtualizationStatus = TriState.Yes
    };

    private static string Render(HardwareInfo hw, IReadOnlyList<string>? applied = null,
                                 IReadOnlyList<JournalEntry>? journal = null,
                                 bool restore = true, bool strict = false,
                                 string? activePowerPlan = null,
                                 ProcessorPowerDetail? processorDetail = null,
                                 TimerResolutionReading? timerResolution = null,
                                 PendingRebootStatus? pendingReboot = null,
                                 DriveHealthReport? driveHealth = null,
                                 OptimizationScorecard? scorecard = null,
                                 ScoreProgress? scoreProgress = null)
        => SystemReport.Render(hw, applied ?? Array.Empty<string>(), journal ?? Array.Empty<JournalEntry>(),
                               restore, strict, DateTime.UtcNow,
                               activePowerPlan, processorDetail, timerResolution, pendingReboot, driveHealth,
                               scorecard, scoreProgress);

    // The build that produced the paste is what a bug report / forum reply asks for first. It rides in the header only
    // when supplied (production passes BuildIdentity.CurrentVersion); a null keeps the older shape intact.
    [Fact]
    public void Header_StampsAppVersion_OnlyWhenProvided()
    {
        var with = SystemReport.Render(Hw(), Array.Empty<string>(), Array.Empty<JournalEntry>(),
                                       true, false, DateTime.UtcNow, appVersion: "9.9.9-test");
        Assert.Contains("Version : 9.9.9-test", with);
        Assert.DoesNotContain("Version :", Render(Hw()));
    }

    // Build a scorecard through the REAL pure core (never a hand-pinned record), so the section tests exercise the
    // same maths the dashboard ring uses — and the « hors score » / 100-stays-reachable honesty is the genuine one.
    private static OptimizationScorecard Score(params (TweakCategory Cat, int Weight, TweakAppliedState State)[] inputs)
        => OptimizationScore.Compute(inputs.Select(i => new ScoreInput(i.Cat, i.Weight, i.State)));

    // A trend built through the REAL ScoreHistory.Summarize (never a hand-pinned ScoreProgress), so the report test
    // exercises the same delta/direction wording the dashboard ring shows.
    private static ScoreProgress Trend(int previous, int current)
        => ScoreHistory.Summarize(new[]
        {
            new ScoreSnapshot(DateTime.UtcNow.AddDays(-2), previous),
            new ScoreSnapshot(DateTime.UtcNow, current)
        });

    private static ProcessorPowerDetail Cpu(int? min, int? max, int? cores, bool ok = true) => new(min, max, cores, ok);

    // A drive whose verdict is DERIVED by the real evaluator (never a pinned label) from the health/wear/error inputs,
    // so the section tests exercise the same honesty core the page uses. Capacity/counters the section doesn't print
    // are left at harmless defaults.
    private static DriveHealthInfo Drive(string name, DriveHealth health = DriveHealth.Healthy,
                                         string bus = "NVMe", int? wear = null, long? uncorrected = null)
        => new(name, DriveMedia.Ssd, health, bus, 1_000_204_886_016L, null, wear, null, uncorrected);

    private static DriveHealthReport DriveReport(params DriveHealthInfo[] drives) => new(drives, true);
    private static DriveHealthReport FailedDriveReport() => new(Array.Empty<DriveHealthInfo>(), false);

    // Build a real verdict via the same evaluator the app uses, so the section tests assert the true wording.
    private static PendingRebootStatus Reboot(bool cbs = false, bool wu = false,
                                              bool fileRename = false, bool computerRename = false)
        => PendingRebootEvaluator.Evaluate(new PendingRebootSignals(cbs, wu, fileRename, computerRename));

    // (ok, current, default-coarse, best-precise) in 100-ns units; defaults to an honest 0.5 ms current reading.
    private static TimerResolutionReading Timer(bool ok = true, uint current = 5000, uint min = 156250, uint max = 5000)
        => new(ok, current, min, max);

    // The first line containing the needle — lets the tests assert a row's value without pinning column padding.
    private static string LineWith(string report, string needle)
    {
        foreach (var line in report.Split('\n'))
            if (line.Contains(needle)) return line;
        return string.Empty;
    }

    [Fact]
    public void Render_CarriesTheTitle_AndIsLabelledLocalOnly()
    {
        var report = Render(Hw());

        Assert.Contains("Aurum Tweaks — Rapport système", report);
        Assert.Contains("jamais envoyé", report);   // the export claim is honest: nothing leaves the machine
    }

    [Fact]
    public void Render_IncludesTheDetectedHardware()
    {
        var report = Render(Hw());

        Assert.Contains("AMD Ryzen 7 7800X3D (8 cœurs / 16 threads)", report);
        Assert.Contains("NVIDIA GeForce RTX 4070", report);
        Assert.Contains("ASUS ROG STRIX B650-A", report);
        Assert.Contains("32 Go DDR5 @ 6000 MT/s", report);
        Assert.Contains("Windows 11 Pro (build 22631)", report);
    }

    [Fact]
    public void Render_UnreadablePlatformFlag_StaysIndeterminate_NeverFakesOff()
    {
        var hw = Hw();
        hw.TpmStatus = TriState.Unknown;   // we genuinely couldn't read it back

        var report = Render(hw);

        // The honesty line: an unknown TPM reads as « indéterminé », never the confirmed-off « non ».
        Assert.Contains("indéterminé", LineWith(report, "TPM"));
        Assert.DoesNotContain("non", LineWith(report, "TPM"));
    }

    [Fact]
    public void Render_ConfirmedOffFlag_SaysNon()
    {
        var hw = Hw();
        hw.SecureBootStatus = TriState.No;

        Assert.Contains("non", LineWith(Render(hw), "Secure Boot"));
    }

    [Fact]
    public void Render_RamBelowRated_FlagsExpoLikelyOff()
    {
        var hw = Hw();
        hw.RamConfiguredMhz = 4800;
        hw.RamRatedMhz = 6000;

        var report = Render(hw);

        Assert.Contains("EXPO/XMP probablement désactivé", report);
    }

    [Fact]
    public void Render_RamAtRated_OmitsTheExpoNote()
    {
        // Hw() runs at its rated 6000 — the note must not appear, or it'd be a fabricated warning.
        var report = Render(Hw());

        Assert.DoesNotContain("EXPO/XMP", report);
    }

    [Fact]
    public void Render_MismatchedRamCapacities_FlagsFlexMode_NamingTheSticks()
    {
        // A 16 GB stick next to an 8 GB one → flex mode (partial dual-channel). The paste must call it out by its
        // real sizes, mirroring the dashboard insight — the same honest tell as EXPO-off, not a fabricated warning.
        var hw = Hw();
        hw.MemoryModules.Add(new MemoryModule { CapacityBytes = 16L * 1024 * 1024 * 1024 });
        hw.MemoryModules.Add(new MemoryModule { CapacityBytes = 8L * 1024 * 1024 * 1024 });

        var report = Render(hw);

        Assert.Contains("dépareillées", report);
        Assert.Contains("16 + 8 Go", report);   // real detected sizes, largest first
        Assert.Contains("flex mode", report);
    }

    [Fact]
    public void Render_MatchedRamKit_OmitsTheMismatchNote()
    {
        // 2× 16 GB (a matched kit) → no mismatch, so the note must not appear or it'd be a fabricated warning.
        var hw = Hw();
        hw.MemoryModules.Add(new MemoryModule { CapacityBytes = 16L * 1024 * 1024 * 1024 });
        hw.MemoryModules.Add(new MemoryModule { CapacityBytes = 16L * 1024 * 1024 * 1024 });

        Assert.DoesNotContain("dépareillées", Render(hw));
    }

    // --- Detected-but-previously-dropped diagnostic signals (the forum-paste essentials) ---

    [Fact]
    public void Render_Gpu_IncludesDriverVersionAndDate_WhenDetected()
    {
        var hw = Hw();
        hw.GpuDriverVersion = "31.0.15.4633";
        hw.GpuDriverDate = new DateTime(2024, 6, 12);

        var gpuLine = LineWith(Render(hw), "GPU");

        // The driver version is the #1 forum ask; the year confirms the date rendered (separator is culture-dependent).
        Assert.Contains("pilote 31.0.15.4633", gpuLine);
        Assert.Contains("2024", gpuLine);
    }

    [Fact]
    public void Render_Gpu_OmitsDriverInfo_WhenNotDetected_NoEmptyHusk()
    {
        // Hw() carries no driver version/date → the GPU line is the bare name, never a fabricated « (pilote ) ».
        var gpuLine = LineWith(Render(Hw()), "GPU");

        Assert.Contains("NVIDIA GeForce RTX 4070", gpuLine);
        Assert.DoesNotContain("pilote", gpuLine);
        Assert.DoesNotContain("(", gpuLine);
    }

    [Fact]
    public void Render_Cpu_FlagsSmtDisabled_WhenSiliconSupportsMoreThreads()
    {
        var hw = Hw();
        hw.CpuMaxThreads = 16;   // silicon max
        hw.CpuThreads = 8;       // only 8 active → SMT/HT switched off in BIOS

        var cpuLine = LineWith(Render(hw), "CPU");

        Assert.Contains("SMT/HT désactivé", cpuLine);
        Assert.Contains("16 threads possibles", cpuLine);
    }

    [Fact]
    public void Render_Cpu_NoSmtFlag_WhenSmtActive()
    {
        var hw = Hw();
        hw.CpuMaxThreads = 16;
        hw.CpuThreads = 16;   // active == max → SMT on, no fabricated « désactivé » note

        Assert.DoesNotContain("SMT/HT désactivé", Render(hw));
    }

    [Fact]
    public void Render_Ram_IncludesChannelVerdict_WhenDetected()
    {
        var hw = Hw();
        hw.MemoryChannelSummary = "2 × 16 GB (dual-channel probable) — 2/4 slots";

        // Single- vs dual-channel is a major, commonly-missed perf tell — it must reach the paste.
        Assert.Contains("dual-channel probable", LineWith(Render(hw), "Canaux"));
    }

    [Fact]
    public void Render_Ram_OmitsChannelRow_WhenNotDetected()
    {
        // Hw() has no channel summary → no bare « Canaux » label dangling without a value.
        Assert.DoesNotContain("Canaux", Render(Hw()));
    }

    [Fact]
    public void Render_NoAppliedTweaks_SaysNoneDetected()
    {
        var report = Render(Hw(), applied: Array.Empty<string>());

        Assert.Contains("TWEAKS APPLIQUÉS (0)", report);
        Assert.Contains("(aucun détecté comme appliqué)", report);
    }

    [Fact]
    public void Render_ListsEachAppliedTweak_WithItsCount()
    {
        var report = Render(Hw(), applied: new[] { "Désactiver la télémétrie", "Plan d'alimentation hautes perfs" });

        Assert.Contains("TWEAKS APPLIQUÉS (2)", report);
        Assert.Contains("- Désactiver la télémétrie", report);
        Assert.Contains("- Plan d'alimentation hautes perfs", report);
    }

    [Fact]
    public void Render_NoAntiCheat_SaysNone_AndOneDetected_IsNamed()
    {
        Assert.Contains("(aucun)", Render(Hw()));

        var hw = Hw();
        hw.VanguardDetected = true;
        Assert.Contains("Riot Vanguard", Render(hw));
    }

    [Fact]
    public void Render_SafetyToggles_ReflectTheRealSettings()
    {
        var report = Render(Hw(), restore: true, strict: false);

        Assert.Contains("activé", LineWith(report, "Point de restauration avant tweaks"));
        Assert.Contains("désactivé", LineWith(report, "Mode compétitif strict"));
    }

    [Fact]
    public void Render_EmptyJournal_SaysNone_AndEntriesShowTheirHonestSummary()
    {
        Assert.Contains("(aucune)", Render(Hw()));

        var entry = new JournalEntry(DateTime.UtcNow, "Application", 3, 0,
                                     new[] { "t1", "t2", "t3" }, Array.Empty<string>());
        var report = Render(Hw(), journal: new[] { entry });

        Assert.Contains("Application · 3 réussi(s)", report);   // the entry's own honest summary, verbatim
        Assert.Contains("Tweaks : t1, t2, t3", report);
    }

    [Fact]
    public void Render_UnconfirmedJournalEntry_CarriesTheFlag()
    {
        var entry = new JournalEntry(DateTime.UtcNow, "Application", 1, 0,
                                     new[] { "stuck" }, new[] { "stuck" });

        var report = Render(Hw(), journal: new[] { entry });

        Assert.Contains("Non confirmé(s) : stuck", report);
    }

    [Fact]
    public void Render_JournalSection_LeadsWithAWholeTrailSynthesis()
    {
        // The journal section opens with the same honest big picture the page's card shows, before the per-entry detail.
        var report = Render(Hw(), journal: new[]
        {
            new JournalEntry(DateTime.UtcNow, "Application", 2, 0, new[] { "a", "b" }, Array.Empty<string>()),
            new JournalEntry(DateTime.UtcNow, "Restauration", 1, 0, new[] { "a" }, Array.Empty<string>())
        });

        Assert.Contains("Synthèse : 2 lot(s) · 1 application(s), 1 restauration(s)", report);
    }

    [Fact]
    public void Render_JournalSynthesis_RanksTheMostOftenUnconfirmed()
    {
        // "stuck" left unconfirmed across two batches surfaces in the section lead as a 2× diagnostic row.
        var report = Render(Hw(), journal: new[]
        {
            new JournalEntry(DateTime.UtcNow, "Application", 1, 0, new[] { "stuck" }, new[] { "stuck" }),
            new JournalEntry(DateTime.UtcNow, "Application", 1, 0, new[] { "stuck" }, new[] { "stuck" })
        });

        Assert.Contains("Tweaks le plus souvent non confirmés :", report);
        Assert.Contains("stuck — 2×", report);
    }

    [Fact]
    public void Render_EmptyJournal_HasNoSynthesis()
    {
        // An empty trail must not sprout a synthesis (which would imply a history that isn't there); "(aucune)" is
        // already pinned above — this guards the new lead specifically.
        Assert.DoesNotContain("Synthèse", Render(Hw()));
    }

    [Fact]
    public void Render_LongJournal_SummarisesEverything_ButLimitsDetailToTheRecentWindow()
    {
        // 12 batches: the synthesis counts ALL of them, but the per-entry detail is capped to the newest 10, so the
        // two oldest (id10, id11) are summarised-but-not-listed. The "sur 12" note keeps that windowing honest — a
        // shared report stays readable without implying the listed rows are the whole history.
        var entries = new List<JournalEntry>();
        for (var i = 0; i < 12; i++)
            entries.Add(new JournalEntry(DateTime.UtcNow.AddMinutes(-i), "Application", 1, 0,
                                         new[] { $"id{i:00}" }, Array.Empty<string>()));

        var report = Render(Hw(), journal: entries);

        Assert.Contains("Synthèse : 12 lot(s)", report);   // every batch counted
        Assert.Contains("récente(s) sur 12", report);       // windowing made explicit, not silent
        Assert.Contains("Tweaks : id00", report);           // the newest entry is listed in detail
        Assert.DoesNotContain("id10", report);              // the two oldest are counted, never listed
        Assert.DoesNotContain("id11", report);
    }

    [Fact]
    public void Render_BlankHardwareField_ShowsADash_NotAnEmptyLabel()
    {
        var hw = Hw();
        hw.GpuPrimary = "";

        // A blank value is rendered as an em dash on its own row, never a dangling label.
        Assert.Contains("—", LineWith(Render(hw), "GPU"));
    }

    [Fact]
    public void Render_NoPowerOrTimerData_OmitsBothSections()
    {
        // Backward-compatible default: a caller that didn't probe powercfg/ntdll must not sprout empty machine-state
        // sections (which would read as « unknown but present »).
        var report = Render(Hw());

        Assert.DoesNotContain("ALIMENTATION", report);
        Assert.DoesNotContain("MINUTEUR SYSTÈME", report);
    }

    [Fact]
    public void Render_PowerSection_ShowsActivePlanAndProcessorDetail()
    {
        var report = Render(Hw(), activePowerPlan: "Performances élevées", processorDetail: Cpu(100, 100, 100));

        Assert.Contains("ALIMENTATION", report);
        Assert.Contains("Performances élevées", LineWith(report, "Plan actif"));
        Assert.Contains("100 %", LineWith(report, "CPU maximal"));
        Assert.Contains("Désactivé", LineWith(report, "Parcage cœurs"));   // 100 % unparked floor = parking off
    }

    [Fact]
    public void Render_PowerSection_UnreadableProcessorDetail_StaysIndeterminate()
    {
        // The plan name read but the per-knob query failed: the detail says « indéterminé », never a fabricated 0 %.
        var report = Render(Hw(), activePowerPlan: "Plan personnalisé", processorDetail: Cpu(null, null, null, ok: false));

        Assert.Contains("ALIMENTATION", report);
        Assert.Contains("indéterminé", LineWith(report, "Détail CPU"));
        Assert.DoesNotContain("0 %", report);
    }

    [Fact]
    public void Render_TimerSection_ShowsResolutionWhenRead()
    {
        var report = Render(Hw(), timerResolution: Timer());

        Assert.Contains("MINUTEUR SYSTÈME", report);
        Assert.Contains("0,5 ms", LineWith(report, "Résolution"));   // fr-FR, deterministic
    }

    [Fact]
    public void Render_TimerSection_FailedRead_StaysIndeterminate()
    {
        var report = Render(Hw(), timerResolution: Timer(ok: false));

        Assert.Contains("MINUTEUR SYSTÈME", report);
        Assert.Contains("indéterminé", LineWith(report, "Résolution"));
    }

    [Fact]
    public void Render_NoPendingRebootData_OmitsTheSection()
    {
        // Backward-compatible default: a caller that didn't probe the reboot signals must not sprout the section.
        Assert.DoesNotContain("REDÉMARRAGE EN ATTENTE", Render(Hw()));
    }

    [Fact]
    public void Render_PendingReboot_ListsTheDetectedSignalsAsReasons()
    {
        var report = Render(Hw(), pendingReboot: Reboot(cbs: true, wu: true));

        Assert.Contains("REDÉMARRAGE EN ATTENTE", report);
        Assert.Contains("redémarrage requis", LineWith(report, "État"));
        Assert.Contains("CBS", report);
        Assert.Contains("Windows Update", report);
    }

    [Fact]
    public void Render_NoPendingReboot_SaysNone_WithoutFabricatingASignal()
    {
        var report = Render(Hw(), pendingReboot: Reboot());   // all-clear signals

        Assert.Contains("REDÉMARRAGE EN ATTENTE", report);
        Assert.Contains("aucun (signaux standards)", LineWith(report, "État"));
        Assert.DoesNotContain("CBS", report);   // no fabricated signal bullet when nothing is pending
    }

    // --- Drive health (the « paste my whole config » must surface a dying disk, not just its capacity) ---

    [Fact]
    public void Render_NoDriveHealthData_OmitsTheSection()
    {
        // Backward-compatible default: a caller that didn't probe drive health must not sprout the section.
        Assert.DoesNotContain("SANTÉ DISQUES", Render(Hw()));
    }

    [Fact]
    public void Render_DriveHealth_FailedQuery_SaysSo_WithoutFabricatingAnAllClear()
    {
        var report = Render(Hw(), driveHealth: FailedDriveReport());

        Assert.Contains("SANTÉ DISQUES", report);
        Assert.Contains("Lecture impossible", report);   // distinct from a genuine « all drives healthy »
    }

    [Fact]
    public void Render_DriveHealth_NoDrivesListed_SaysSo()
    {
        var report = Render(Hw(), driveHealth: DriveReport());

        Assert.Contains("SANTÉ DISQUES", report);
        Assert.Contains("Aucun disque physique listé par Windows.", report);
    }

    [Fact]
    public void Render_DriveHealth_HealthyDrive_TaggedSain_NoAlarm_NoUsbCaveat()
    {
        var report = Render(Hw(), driveHealth: DriveReport(Drive("Samsung 990 Pro")));

        Assert.Contains("Tous les disques sont sains.", report);          // honest headline
        Assert.Contains("• Samsung 990 Pro [Sain]", report);
        // A clean drive gets NO data-loss spell-out (that'd be a fabricated alarm) and NO USB caveat (it's internal).
        Assert.DoesNotContain("aucun problème signalé", report);
        Assert.DoesNotContain("USB", report);
    }

    [Fact]
    public void Render_DriveHealth_DyingDrive_IsFlaggedWithVerdictAndActionableMessage()
    {
        // The core reason this section exists: a drive Windows calls « Unhealthy » must be impossible to miss in the paste.
        var report = Render(Hw(), driveHealth: DriveReport(Drive("Vieux SSD", DriveHealth.Unhealthy)));

        Assert.Contains("Alerte : une défaillance de disque est signalée", report);   // headline, never cheerier
        Assert.Contains("• Vieux SSD [Défaillant]", report);
        Assert.Contains("Défaillance signalée par Windows", report);                  // the actionable « back up now »
    }

    [Fact]
    public void Render_DriveHealth_WarningDrive_TaggedWatch_AndExplained()
    {
        var report = Render(Hw(), driveHealth: DriveReport(Drive("SSD système", DriveHealth.Warning)));

        Assert.Contains("À surveiller : un disque mérite ton attention.", report);
        Assert.Contains("[À surveiller]", report);
        Assert.Contains("Windows signale un avertissement", report);
    }

    [Fact]
    public void Render_DriveHealth_HighWearButWindowsHealthy_EscalatesToWatch()
    {
        // Windows reports the drive « Healthy », yet 85 % wear escalates the verdict to « à surveiller » via the same
        // evaluator the page uses — the section reflects the real verdict, not just the raw Windows health.
        var report = Render(Hw(), driveHealth: DriveReport(Drive("SSD bien usé", DriveHealth.Healthy, wear: 85)));

        Assert.Contains("[À surveiller]", report);
        Assert.Contains("Usure élevée", report);
    }

    [Fact]
    public void Render_DriveHealth_UsbDrive_CarriesTheMaskedSmartCaveat()
    {
        // A USB-bridged drive's empty SMART fields must never read as « healthy » — the caveat says so.
        var report = Render(Hw(), driveHealth: DriveReport(Drive("Disque externe", bus: "USB")));

        Assert.Contains("compteurs SMART souvent masqués", report);
    }

    [Fact]
    public void Render_DriveHealth_MixedFleet_HeadlineReflectsWorst_AndListsEveryDrive()
    {
        // One healthy + one failing: the headline is never softened by the healthy drive, and BOTH are listed.
        var report = Render(Hw(), driveHealth: DriveReport(
            Drive("SSD système", DriveHealth.Healthy),
            Drive("HDD données", DriveHealth.Unhealthy)));

        Assert.Contains("Alerte : une défaillance de disque est signalée", report);
        Assert.Contains("• SSD système [Sain]", report);
        Assert.Contains("• HDD données [Défaillant]", report);
    }

    // --- Optimization score (the shareable headline of how much of the SAFE recommended set is actually active) ---

    [Fact]
    public void Render_NoScorecard_OmitsTheOptimizationSection()
    {
        // Backward-compatible default: a caller that didn't compute the score must not sprout the section.
        Assert.DoesNotContain("OPTIMISATION", Render(Hw()));
    }

    [Fact]
    public void Render_AllUnverifiableScorecard_OmitsTheSection_NeverAFabricatedZero()
    {
        // A scorecard with nothing readable (every recommended tweak shell-only) has no data → the section stays
        // hidden rather than printing a dishonest « 0 / 100 ». Mirrors the HasScore gate on the dashboard ring.
        var blind = Score((TweakCategory.PrivacyTelemetry, 50, TweakAppliedState.Indeterminate));

        Assert.DoesNotContain("OPTIMISATION", Render(Hw(), scorecard: blind));
    }

    [Fact]
    public void Render_Scorecard_ShowsScoreGradeAndVerifiableCount()
    {
        // 1 of 2 equal-weight readable tweaks live → 50/100, grade « Partiel », 1/2 active.
        var card = Score(
            (TweakCategory.PrivacyTelemetry, 50, TweakAppliedState.Applied),
            (TweakCategory.NetworkLatency, 50, TweakAppliedState.NotApplied));

        var report = Render(Hw(), scorecard: card);

        Assert.Contains("OPTIMISATION (état réel du système)", report);
        Assert.Contains("50 / 100 — Partiel", LineWith(report, "Score"));
        Assert.Contains("1 / 2", LineWith(report, "Recommandé actif"));
    }

    [Fact]
    public void Render_Scorecard_DisclosesUnverifiableTweaksAsHorsScore_So100StaysHonest()
    {
        // 1 applied + 1 unverifiable → an honest 100 (the blind tweak is excluded from the maths), and the unread one
        // is disclosed as « hors score », never silently folded into the number as a penalty for an unreadable value.
        var card = Score(
            (TweakCategory.PrivacyTelemetry, 50, TweakAppliedState.Applied),
            (TweakCategory.Gaming, 50, TweakAppliedState.Indeterminate));

        var report = Render(Hw(), scorecard: card);

        Assert.Contains("100 / 100 — Optimisé", LineWith(report, "Score"));
        Assert.Contains("hors score", LineWith(report, "Non vérifiable"));
    }

    [Fact]
    public void Render_Scorecard_AllReadable_OmitsTheHorsScoreRow()
    {
        // Nothing unverifiable → no « Non vérifiable » row (printing one would fabricate a disclosure of an empty set).
        var card = Score((TweakCategory.PrivacyTelemetry, 50, TweakAppliedState.Applied));

        Assert.DoesNotContain("Non vérifiable", Render(Hw(), scorecard: card));
    }

    [Fact]
    public void Render_Scorecard_BreaksDownByCategory_UsingTheSharedFrenchLabels()
    {
        // The per-category lines must read with the same French labels the dashboard bars use (one shared source), so
        // a forum reader sees « Confidentialité » / « Réseau & latence », never the raw English enum names.
        var card = Score(
            (TweakCategory.PrivacyTelemetry, 50, TweakAppliedState.Applied),
            (TweakCategory.NetworkLatency, 50, TweakAppliedState.NotApplied));

        var report = Render(Hw(), scorecard: card);

        Assert.Contains("100 % (1/1)", LineWith(report, "Confidentialité"));
        Assert.Contains("0 % (0/1)", LineWith(report, "Réseau & latence"));
        Assert.DoesNotContain("PrivacyTelemetry", report);   // never the raw enum name in the FR paste
    }

    [Fact]
    public void Render_Scorecard_ShowsTheTrend_WhenTheScoreMovedSinceThePreviousMeasure()
    {
        // The shared paste shows momentum, not just a static number — the SAME relative line the dashboard ring uses
        // (direction + signed delta + anchor date), built through the real Summarize so the wording can't drift. A
        // relative line only: never a second absolute score that could contradict the headline.
        var card = Score((TweakCategory.PrivacyTelemetry, 50, TweakAppliedState.Applied));

        var line = LineWith(Render(Hw(), scorecard: card, scoreProgress: Trend(previous: 62, current: 75)), "Tendance");

        Assert.Contains("En hausse", line);
        Assert.Contains("+13", line);
    }

    [Fact]
    public void Render_Scorecard_OmitsTheTrend_WhenThereIsNoPriorMeasure()
    {
        // A first-ever score has no honest trend to report — the « Tendance » row must be absent, never a fabricated
        // "+0" or a "stable" claim with no anchor date.
        var card = Score((TweakCategory.PrivacyTelemetry, 50, TweakAppliedState.Applied));

        Assert.DoesNotContain("Tendance", Render(Hw(), scorecard: card, scoreProgress: ScoreProgress.None));
    }
}
