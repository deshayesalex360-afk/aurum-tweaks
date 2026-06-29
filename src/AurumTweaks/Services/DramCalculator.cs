using System;
using System.Collections.Generic;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure, deterministic DRAM timing calculator (DRAM-Calc style). Zero hardware access: it only does
/// arithmetic, so it is fully unit-testable and carries no risk. It produces a <b>starting point</b>
/// to type into the BIOS and then validate with a memory tester — never a guaranteed-stable profile.
///
/// <para><b>What is rigorous vs. suggested.</b> The frequency context (tCK = 2000/MT/s, true latency
/// = tCL × tCK) and the cycle conversions are exact physics. The per-IC nanosecond targets are
/// calibrated to widely-published known-good kits (e.g. B-die DDR4-3600 ≈ 16-16-16, Hynix A-die
/// DDR5-6000 ≈ 30-36-36) and scaled by a Safe/Fast/Extreme multiplier; the short secondaries are
/// conventional tuned reference values. All of this is documented and inspectable.</para>
/// </summary>
public static class DramCalculator
{
    // Fast-preset nanosecond targets per IC: (tCL, tRCD, tRP, tRAS, tRFC). DDR5 deliberately has a
    // far looser tRCD/tRP than tCL (the 30-36-36 shape); DDR4 keeps them near-equal.
    private static (double cl, double rcd, double rp, double ras, double rfc) BaseNs(MemoryIc ic) => ic switch
    {
        // ---- DDR4 ----
        MemoryIc.SamsungBDie => (8.6, 8.6, 8.6, 21.0, 260),
        MemoryIc.HynixDJR    => (9.2, 9.8, 9.8, 22.0, 280),
        MemoryIc.HynixCJR    => (10.0, 10.6, 10.6, 23.0, 300),
        MemoryIc.MicronRevB  => (9.0, 10.0, 10.0, 22.0, 290),
        MemoryIc.MicronRevE  => (9.4, 11.0, 11.0, 23.0, 350),
        MemoryIc.SamsungCDie => (10.6, 11.2, 11.2, 24.0, 360),
        MemoryIc.NanyaOther  => (11.0, 12.0, 12.0, 25.0, 380),
        // ---- DDR5 ----
        MemoryIc.HynixADie   => (10.0, 12.0, 12.0, 10.7, 240),
        MemoryIc.HynixMDie   => (11.0, 13.0, 13.0, 11.5, 270),
        MemoryIc.SamsungDdr5 => (11.3, 14.0, 14.0, 12.0, 300),
        MemoryIc.MicronDdr5  => (11.3, 14.7, 14.7, 12.0, 320),
        // Unknown → conservative; works acceptably for either generation.
        _                    => (11.5, 13.5, 13.5, 12.5, 360)
    };

    // (primary multiplier, tRFC multiplier) per preset.
    private static (double prim, double rfc) PresetMul(TimingPreset p) => p switch
    {
        TimingPreset.Safe    => (1.10, 1.08),
        TimingPreset.Extreme => (0.92, 0.93),
        _                    => (1.00, 1.00)
    };

    public static DramTimingSet Compute(DramCalculatorInput input)
    {
        int mtps = Math.Clamp(input.DataRateMtps, 1600, 9200);
        double tck = 2000.0 / mtps;     // nanoseconds per memory clock cycle

        var (clNs, rcdNs, rpNs, rasNs, rfcNs) = BaseNs(input.Ic);
        var (mul, rfcMul) = PresetMul(input.Preset);
        if (input.Rank == MemoryRank.DualRank) rfcNs *= 1.06;   // dual-rank refreshes longer

        // ns → whole cycles, always rounding up (a timing must cover at least the required time).
        int Cyc(double ns) => (int)Math.Ceiling(ns * mtps / 2000.0);

        // ---- Primaries (rigorous ns→cycle, clamped to sane floors) ----
        int tCL  = Math.Max(Cyc(clNs  * mul), 10);
        int tRCD = Math.Max(Cyc(rcdNs * mul), 10);
        int tRP  = Math.Max(Cyc(rpNs  * mul), 10);
        int tRAS = Math.Max(Cyc(rasNs * mul), 16);

        // DDR4 "Safe" assumes Geardown Mode (default on AM4): CAS is rounded up to even.
        bool geardown = input.Generation == RamGeneration.Ddr4 && input.Preset == TimingPreset.Safe;
        if (geardown && tCL % 2 != 0) tCL++;

        if (tRAS < tCL + 1) tRAS = tCL + 1;   // row must stay open long enough to finish a read
        int tRC = tRAS + tRP;                  // hard relationship: row cycle = active + precharge

        int tRFC = Math.Max(Cyc(rfcNs * rfcMul), 1);

        // ---- Secondaries: conventional tuned reference values (labelled as such in the UI) ----
        var s = TypicalSecondaries(input.Generation, input.Preset, tCL);

        int tREFI = input.Preset switch
        {
            TimingPreset.Safe => Cyc(7800.0),   // JEDEC ≈ 7.8 µs between refreshes
            TimingPreset.Fast => 32768,
            _                 => 65535           // Extreme: max, but temperature-sensitive
        };

        string cr = input.Generation == RamGeneration.Ddr5
            ? "2N"
            : (input.Preset == TimingPreset.Safe ? "2T" : "1T");

        double trueLatency = tCL * tck;

        var notes = new List<string>
        {
            "Point de départ : charge ces valeurs en BIOS puis VALIDE (TestMem5 profil Anta777 Extreme, "
              + "Karhu RAM Test, ou y-cruncher). Aucune stabilité n'est garantie automatiquement.",
            "Primaires + tRAS/tRC + tRFC calculés depuis des cibles en nanosecondes par type d'IC ; "
              + "les secondaires courtes sont des valeurs de référence typiques, à affiner."
        };
        if (geardown)
            notes.Add("DDR4 « Safe » suppose Geardown Mode ON (tCL arrondi au pair, Command Rate 2T). "
                    + "En Fast/Extrême : GDM OFF et 1T.");
        if (input.Preset == TimingPreset.Extreme)
            notes.Add("tREFI poussé au maximum (65535) : très sensible à la température — surveille au-delà "
                    + "de ~45 °C et redescends si des erreurs apparaissent.");
        if (input.Rank == MemoryRank.DualRank)
            notes.Add("Dual-rank : tRFC rallongé et fréquence maximale généralement plus basse qu'en single-rank.");
        if (input.Ic == MemoryIc.Unknown)
            notes.Add("IC inconnu : valeurs volontairement prudentes. Identifie tes puces (Thaiphoon Burner) "
                    + "pour viser des timings plus serrés.");

        return new DramTimingSet
        {
            DataRateMtps = mtps,
            IoClockMhz = mtps / 2.0,
            CycleTimeNs = tck,
            TrueLatencyNs = trueLatency,
            CasLatency = tCL,
            Trcd = tRCD,
            Trp = tRP,
            Tras = tRAS,
            Trc = tRC,
            Trfc = tRFC,
            TrfcNs = tRFC * tck,
            TrrdS = s.rrdS,
            TrrdL = s.rrdL,
            Tfaw = s.faw,
            Twr = s.wr,
            Twtrs = s.wtrS,
            Twtrl = s.wtrL,
            Trtp = s.rtp,
            Tcwl = Math.Max(tCL - 2, 9),
            Trefi = tREFI,
            CommandRate = cr,
            PrimarySummary = $"{tCL}-{tRCD}-{tRP}-{tRAS}",
            Notes = notes
        };
    }

    private readonly record struct Secondaries(int rrdS, int rrdL, int faw, int wr, int wtrS, int wtrL, int rtp);

    private static Secondaries TypicalSecondaries(RamGeneration g, TimingPreset p, int tCL)
    {
        if (g == RamGeneration.Ddr4)
        {
            var (rrdS, rrdL, wr, wtrL) = p switch
            {
                TimingPreset.Safe    => (6, 8, 16, 12),
                TimingPreset.Extreme => (4, 6, 10, 8),
                _                    => (4, 6, 12, 10)
            };
            return new Secondaries(rrdS, rrdL, 4 * rrdS, wr, 4, wtrL, Math.Max(wr / 2, 4));
        }

        // DDR5: longer write-recovery / write-to-read in cycles than DDR4.
        var (drrdS, drrdL, dwr, dwtrL) = p switch
        {
            TimingPreset.Safe    => (8, 12, 96, 30),
            TimingPreset.Extreme => (8, 8, 48, 18),
            _                    => (8, 8, 60, 24)
        };
        return new Secondaries(drrdS, drrdL, 4 * drrdS, dwr, 4, dwtrL, Math.Max(dwr / 2, 12));
    }
}
