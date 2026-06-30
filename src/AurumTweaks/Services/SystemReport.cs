using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure renderer for the shareable « rapport système » — the plain-text snapshot a user pastes on a forum or a
/// support thread when asking for help or showing off a build. No I/O: it takes the already-detected
/// <see cref="HardwareInfo"/>, the names of the tweaks currently detected as applied, the full change journal, and
/// the two safety toggles, and lays them out. The journal section opens with a whole-trail synthesis (the same pure
/// <see cref="JournalInsights"/> the Journal page's card shows) and then lists only the newest few entries, so a long
/// history is summarised honestly instead of dumping all 200 rows. Honesty-bearing and therefore unit-tested: every
/// platform flag Windows couldn't read back stays « indéterminé » (never a fabricated « inactif »), it only lists
/// tweaks actually detected as applied, the synthesis counts every recorded batch (so the truncated detail never
/// reads as the entire trail), and it never invents a metric the hardware didn't expose — it is faithful layout, not
/// embellishment. The optional optimization score, when supplied, carries the pure core's honesty intact: it shows
/// only when there's verifiable data, and discloses any unverifiable tweaks as « hors score » rather than counting
/// them as missing. Mirrors <see cref="JournalTextReport"/>; the file write is thin glue in the VM.
/// </summary>
public static class SystemReport
{
    // Labels in the aligned sections never exceed this; long settings labels are written un-padded instead.
    private const int LabelWidth = 16;

    // The journal section summarises the WHOLE trail but lists only the newest few in detail — a shared report
    // shouldn't dump all 200 entries. The synthesis above the list is what keeps that truncation honest.
    private const int MaxJournalEntriesShown = 10;

    public static string Render(
        HardwareInfo hw,
        IReadOnlyList<string> appliedTweakNames,
        IReadOnlyList<JournalEntry> journal,
        bool createRestorePointBeforeTweaks,
        bool strictCompetitiveAntiCheat,
        DateTime generatedUtc,
        string? activePowerPlan = null,
        ProcessorPowerDetail? processorDetail = null,
        TimerResolutionReading? timerResolution = null,
        PendingRebootStatus? pendingReboot = null,
        DriveHealthReport? driveHealth = null,
        OptimizationScorecard? scorecard = null,
        ScoreProgress? scoreProgress = null,
        string? appVersion = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aurum Tweaks — Rapport système");
        sb.AppendLine($"Généré le {generatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}");
        // The build that produced this paste — the first thing a bug triage or forum reply asks (« sur quelle
        // version ? »). Same resolver as the Transparence disclosure (BuildIdentity), so the two can't disagree.
        if (!string.IsNullOrWhiteSpace(appVersion))
            sb.AppendLine($"Version : {appVersion.Trim()}");
        sb.AppendLine(new string('=', 48));

        sb.AppendLine();
        sb.AppendLine("MATÉRIEL");
        sb.AppendLine(Row("CPU", CpuLine(hw)));
        sb.AppendLine(Row("GPU", GpuLine(hw)));
        sb.AppendLine(Row("Carte mère", Val($"{hw.MotherboardManufacturer} {hw.MotherboardModel}")));
        sb.AppendLine(Row("BIOS", BiosLine(hw)));
        sb.AppendLine(Row("RAM", RamLine(hw)));
        foreach (var m in hw.MemoryModules)
            sb.AppendLine(Continuation(m.Summary));
        // Single- vs dual-channel is a top « pourquoi mon PC rame » tell people forget to check — surface the honest
        // verdict Windows' module layout already gave us (reused verbatim, never re-derived) when it's available.
        if (!string.IsNullOrWhiteSpace(hw.MemoryChannelSummary))
            sb.AppendLine(Row("Canaux", hw.MemoryChannelSummary));
        AppendList(sb, "Stockage", hw.StorageDevices.Select(d => d.Summary).ToList());
        AppendList(sb, "Affichage", hw.Displays.Select(d => d.Summary).ToList());
        sb.AppendLine(Row("OS", OsLine(hw)));

        sb.AppendLine();
        sb.AppendLine("PLATEFORME (sécurité / virtualisation)");
        sb.AppendLine(Row("VBS", Bool(hw.VbsRunning)));
        sb.AppendLine(Row("HVCI", Bool(hw.HvciRunning)));
        sb.AppendLine(Row("TPM", Tri(hw.TpmStatus) + TpmSpec(hw)));
        sb.AppendLine(Row("Secure Boot", Tri(hw.SecureBootStatus)));
        sb.AppendLine(Row("Resizable BAR", Tri(hw.ReBarStatus)));
        sb.AppendLine(Row("Virtualisation", Tri(hw.VirtualizationStatus)));

        // Drive health — included only when the caller probed it (optional). The flagship « paste my whole config »
        // report must surface a dying drive, not merely list its capacity in MATÉRIEL above: data loss is the highest
        // stake here. A failed query says so plainly; otherwise we lead with the shared honest headline and tag each
        // drive with Windows' verdict, spelling out the actionable message for anything that isn't a clean « sain ».
        if (driveHealth is { } dh)
        {
            sb.AppendLine();
            sb.AppendLine("SANTÉ DISQUES");
            if (!dh.QueryOk)
                sb.AppendLine("  Lecture impossible — module de stockage Windows indisponible ou accès refusé.");
            else if (dh.Count == 0)
                sb.AppendLine("  Aucun disque physique listé par Windows.");
            else
            {
                sb.AppendLine($"  {dh.Headline}");
                foreach (var d in dh.Drives)
                {
                    sb.AppendLine($"  • {d.Name} [{d.VerdictLabel}]");
                    if (d.Verdict != DriveVerdict.Healthy)
                        sb.AppendLine($"    {d.VerdictMessage}");
                    if (d.IsUsb)
                        sb.AppendLine("    Disque externe (USB) : compteurs SMART souvent masqués — une mesure absente n'est pas « saine ».");
                }
            }
        }

        // The live power/timer readouts — included only when the caller gathered them (optional), so the report stays
        // honest in contexts that don't probe powercfg/ntdll, and « indéterminé » when the probe itself came back empty.
        if (activePowerPlan is not null || processorDetail is not null)
        {
            sb.AppendLine();
            sb.AppendLine("ALIMENTATION");
            sb.AppendLine(Row("Plan actif", Val(activePowerPlan)));
            if (processorDetail is { QueryOk: true } pd)
            {
                sb.AppendLine(Row("CPU minimal", pd.MinStateDisplay));
                sb.AppendLine(Row("CPU maximal", pd.MaxStateDisplay));
                sb.AppendLine(Row("Parcage cœurs", pd.CoreParkingDisplay));
            }
            else
            {
                sb.AppendLine(Row("Détail CPU", "indéterminé (powercfg illisible)"));
            }
        }

        if (timerResolution is { } tr)
        {
            sb.AppendLine();
            sb.AppendLine("MINUTEUR SYSTÈME");
            sb.AppendLine(Row("Résolution", tr.QueryOk ? tr.CurrentDisplay : "indéterminé"));
            if (tr.QueryOk)
                sb.AppendLine(Row("Maximum", tr.BestDisplay));
        }

        // The pending-reboot verdict — included only when the caller probed it (optional), so the report stays honest
        // in contexts that don't read the registry signals. A "not pending" result is worded as « aucun (signaux
        // standards) », never a guarantee, and each detected signal becomes a plain-language bullet.
        if (pendingReboot is { } pr)
        {
            sb.AppendLine();
            sb.AppendLine("REDÉMARRAGE EN ATTENTE");
            sb.AppendLine(Row("État", pr.IsPending ? "redémarrage requis" : "aucun (signaux standards)"));
            foreach (var reason in pr.Reasons)
                sb.AppendLine($"  - {reason}");
        }

        sb.AppendLine();
        sb.AppendLine("ANTI-CHEAT DÉTECTÉS");
        var antiCheats = AntiCheats(hw);
        if (antiCheats.Count == 0)
            sb.AppendLine("  (aucun)");
        else
            foreach (var a in antiCheats) sb.AppendLine($"  - {a}");

        // The optimization score — included only when the caller computed it from a live probe (optional, so a
        // context that didn't detect stays silent rather than printing a fabricated 0/100). It's the shareable
        // headline of how much of the SAFE recommended set is actually active right now; the per-category lines tell
        // a forum reader where the machine is strong or weak at a glance. Honest by construction — the pure core it
        // comes from drops unverifiable tweaks from the maths, so a genuine 100 stays reachable and any tweak Windows
        // can't read back is disclosed as « hors score », never silently counted as missing.
        if (scorecard is { HasData: true } sc)
        {
            sb.AppendLine();
            sb.AppendLine("OPTIMISATION (état réel du système)");
            sb.AppendLine(Row("Score", $"{sc.Score} / 100 — {sc.GradeLabel}"));
            // The trend the dashboard shows, in the shared paste too — a relative line (direction + delta + anchor
            // date) only, never a second absolute number that could contradict the headline score above. Shown
            // only on a real trend (≥ 2 distinct measures); composed by the SAME ScoreProgress.TrendLine the ring
            // uses, so the report and the dashboard can't word the same movement differently.
            if (scoreProgress is { HasTrend: true } sp)
                sb.AppendLine(Row("Tendance", sp.TrendLine));
            sb.AppendLine(Row("Recommandé actif", $"{sc.AppliedCount} / {sc.VerifiableCount} optimisation(s) vérifiable(s)"));
            if (sc.IndeterminateCount > 0)
                sb.AppendLine(Row("Non vérifiable", $"{sc.IndeterminateCount} tweak(s) — hors score (Windows ne les relit pas)"));
            foreach (var c in sc.Categories)
                sb.AppendLine($"    {TweakCategoryLabels.French(c.Category).PadRight(24)}: {c.Percent,3} % ({c.AppliedCount}/{c.VerifiableCount})");
        }

        sb.AppendLine();
        sb.AppendLine($"TWEAKS APPLIQUÉS ({appliedTweakNames.Count})");
        if (appliedTweakNames.Count == 0)
            sb.AppendLine("  (aucun détecté comme appliqué)");
        else
            foreach (var n in appliedTweakNames) sb.AppendLine($"  - {n}");

        sb.AppendLine();
        sb.AppendLine("RÉGLAGES DE SÉCURITÉ");
        sb.AppendLine($"  Point de restauration avant tweaks  : {OnOff(createRestorePointBeforeTweaks)}");
        sb.AppendLine($"  Mode compétitif strict (anti-cheat) : {OnOff(strictCompetitiveAntiCheat)}");

        sb.AppendLine();
        sb.AppendLine("ACTIVITÉ (journal)");
        if (journal.Count == 0)
        {
            sb.AppendLine("  (aucune)");
        }
        else
        {
            // Lead with the whole-trail synthesis (the same pure aggregate the Journal page's card shows), then
            // the newest few in detail. Stats count EVERY recorded batch, so the truncated list below can't read
            // as the entire history — the "sur N" note makes the windowing explicit rather than silent.
            var stats = JournalInsights.Compute(journal);
            sb.AppendLine($"  Synthèse : {stats.Summary}");
            if (stats.FirstActivityLabel is not null && stats.LastActivityLabel is not null)
                sb.AppendLine($"  Activité du {stats.FirstActivityLabel} au {stats.LastActivityLabel}");
            if (stats.HasUnconfirmed)
            {
                sb.AppendLine("  Tweaks le plus souvent non confirmés :");
                foreach (var f in stats.MostUnconfirmed)
                    sb.AppendLine($"    - {f.Label}");
            }

            var shown = journal.Take(MaxJournalEntriesShown).ToList();
            sb.AppendLine(journal.Count > shown.Count
                ? $"  {shown.Count} entrée(s) récente(s) sur {journal.Count} :"
                : "  Détail :");
            foreach (var e in shown)
            {
                sb.AppendLine($"  [{e.LocalTimestampLabel}] {e.Summary}");
                sb.AppendLine($"    Tweaks : {e.TweakIdsLabel}");
                if (e.HasUnconfirmed)
                    sb.AppendLine($"    Non confirmé(s) : {e.UnconfirmedLabel}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 48));
        sb.AppendLine("Rapport informatif, généré localement et jamais envoyé. Les valeurs reflètent ce qu'Aurum a pu");
        sb.AppendLine("lire de Windows au moment de la génération ; « indéterminé » = non lisible de façon fiable.");
        return sb.ToString();
    }

    private static string Row(string label, string value) => $"  {label.PadRight(LabelWidth)}: {value}";

    // Aligns a continuation line under a Row's value column (2 leading + label + ": ").
    private static string Continuation(string value) => new string(' ', 2 + LabelWidth + 2) + value;

    // A labeled field that may carry several values: the first sits on the label row, the rest align beneath it.
    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            sb.AppendLine(Row(label, "—"));
            return;
        }
        sb.AppendLine(Row(label, items[0]));
        for (var i = 1; i < items.Count; i++)
            sb.AppendLine(Continuation(items[i]));
    }

    private static string Val(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();

    private static string Bool(bool b) => b ? "oui" : "non";

    private static string OnOff(bool b) => b ? "activé" : "désactivé";

    // Honest mapping of the tri-state platform flags: "couldn't read it back" must never collapse into "non".
    private static string Tri(TriState t) => t switch
    {
        TriState.Yes => "oui",
        TriState.No => "non",
        _ => "indéterminé"
    };

    // CPU/GPU/RAM/OS value lines are internal (not private) so the EvidenceReport's « MACHINE » block renders the rig
    // from the EXACT same source as this system report — the proof paste and the system report can never disagree on the
    // hardware, and the honest tells (SMT off, RAM below rated) carry into the proof verbatim. Anti-drift, single source.
    internal static string CpuLine(HardwareInfo hw)
    {
        var name = Val(hw.CpuName);
        if (hw.CpuCores <= 0 || hw.CpuThreads <= 0) return name;
        var line = $"{name} ({hw.CpuCores} cœurs / {hw.CpuThreads} threads)";
        // SMT/HT off is deliberate for some, a forgotten BIOS toggle for others — state it factually (the detector
        // only flags it when the silicon genuinely supports more threads) so a « multi-cœur faible » paste shows it,
        // without judging the choice. Mirrors the RAM-below-rated tell already in RamLine.
        if (hw.SmtCapableButOff)
            line += $" — SMT/HT désactivé ({hw.CpuMaxThreads} threads possibles)";
        return line;
    }

    internal static string GpuLine(HardwareInfo hw)
    {
        var name = Val(hw.GpuPrimary);
        // The driver version is the first thing a forum/Discord thread asks for; the date dates it. Append only what
        // was actually detected — no « (pilote ) » husk when WMI returned nothing.
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(hw.GpuDriverVersion)) parts.Add($"pilote {hw.GpuDriverVersion.Trim()}");
        if (hw.GpuDriverDate is { } d) parts.Add($"{d:dd/MM/yyyy}");
        return parts.Count > 0 ? $"{name} ({string.Join(", ", parts)})" : name;
    }

    private static string BiosLine(HardwareInfo hw)
    {
        var v = Val(hw.BiosVersion);
        return hw.BiosReleaseDate is { } d ? $"{v} ({d:dd/MM/yyyy})" : v;
    }

    internal static string RamLine(HardwareInfo hw)
    {
        var parts = new List<string>();
        if (hw.TotalRamBytes > 0) parts.Add($"{hw.TotalRamGb:0} Go");
        if (!string.IsNullOrWhiteSpace(hw.RamType)) parts.Add(hw.RamType);
        if (hw.RamConfiguredMhz > 0) parts.Add($"@ {hw.RamConfiguredMhz} MT/s");
        var line = parts.Count > 0 ? string.Join(" ", parts) : "—";
        if (hw.RamModuleCount > 0) line += $" — {hw.RamModuleCount} module(s)";
        // The classic "why is my PC slow" tell: RAM left at JEDEC because EXPO/XMP was never switched on.
        if (hw.RamRunningBelowRated)
            line += $" — sous sa fréquence nominale ({hw.RamRatedMhz} MT/s) : EXPO/XMP probablement désactivé";
        // Mixed stick capacities run in "flex mode": only the matched portion is dual-channel and two different kits
        // often won't POST at the rated EXPO/XMP profile. Name the real sizes (the same GiB projection the dashboard
        // insight and the per-module list above use) so a « pourquoi ça rame » paste shows the dépareillé kit plainly.
        if (hw.RamCapacityMismatched)
            line += $" — barrettes dépareillées ({string.Join(" + ", hw.RamModuleCapacitiesGb)} Go) : « flex mode », dual-channel partiel";
        return line;
    }

    internal static string OsLine(HardwareInfo hw)
    {
        var c = Val(hw.OsCaption);
        return string.IsNullOrWhiteSpace(hw.OsBuild) ? c : $"{c} (build {hw.OsBuild})";
    }

    private static string TpmSpec(HardwareInfo hw)
        => string.IsNullOrWhiteSpace(hw.TpmSpecVersion) ? string.Empty : $" (spec {hw.TpmSpecVersion})";

    private static IReadOnlyList<string> AntiCheats(HardwareInfo hw)
    {
        var list = new List<string>();
        if (hw.VanguardDetected) list.Add("Riot Vanguard");
        if (hw.FaceItAcDetected) list.Add("FACEIT Anti-Cheat");
        if (hw.EacDetected) list.Add("Easy Anti-Cheat");
        if (hw.BattlEyeDetected) list.Add("BattlEye");
        return list;
    }
}
