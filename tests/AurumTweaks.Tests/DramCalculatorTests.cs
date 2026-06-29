using System;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Tests for the pure DRAM timings calculator. Zero hardware access — it is just arithmetic, so we
/// can assert hard physics (tCK, true latency, tRC=tRAS+tRP), monotonicity, preset/IC ordering, and a
/// few golden-range anchors taken from widely-published known-good kits (B-die DDR4-3600 ≈ 16-16-16,
/// Hynix A-die DDR5-6000 ≈ 30-36-36). Nothing here claims a profile is stable — only that the numbers
/// the calculator produces are internally consistent and land in sane bands.
/// </summary>
public class DramCalculatorTests
{
    private static DramTimingSet Compute(
        RamGeneration gen = RamGeneration.Ddr5,
        MemoryIc ic = MemoryIc.HynixADie,
        int mtps = 6000,
        MemoryRank rank = MemoryRank.SingleRank,
        TimingPreset preset = TimingPreset.Fast)
        => DramCalculator.Compute(new DramCalculatorInput(gen, ic, mtps, rank, preset));

    // ---- Frequency physics (exact, not a guess) ----

    [Theory]
    [InlineData(3600)]
    [InlineData(6000)]
    [InlineData(8000)]
    public void CycleTime_Is2000OverDataRate(int mtps)
    {
        var r = Compute(mtps: mtps);
        Assert.Equal(2000.0 / mtps, r.CycleTimeNs, 6);
        Assert.Equal(mtps / 2.0, r.IoClockMhz, 6);
    }

    [Fact]
    public void TrueLatency_IsCasTimesCycleTime()
    {
        var r = Compute(mtps: 6000);
        Assert.Equal(r.CasLatency * r.CycleTimeNs, r.TrueLatencyNs, 6);
    }

    [Theory]
    [InlineData(100, 1600)]      // below floor → clamped up
    [InlineData(99999, 9200)]    // above ceiling → clamped down
    [InlineData(6000, 6000)]     // in range → unchanged
    public void DataRate_IsClampedToSupportedWindow(int requested, int expected)
        => Assert.Equal(expected, Compute(mtps: requested).DataRateMtps);

    // ---- Hard structural relationships ----

    [Theory]
    [InlineData(RamGeneration.Ddr4, MemoryIc.SamsungBDie, 3600)]
    [InlineData(RamGeneration.Ddr5, MemoryIc.HynixADie, 6000)]
    [InlineData(RamGeneration.Ddr5, MemoryIc.MicronDdr5, 8000)]
    public void RowCycle_EqualsActivePlusPrecharge(RamGeneration gen, MemoryIc ic, int mtps)
    {
        var r = Compute(gen, ic, mtps);
        Assert.Equal(r.Tras + r.Trp, r.Trc);
    }

    [Fact]
    public void Tras_IsAtLeastCasPlusOne()
    {
        foreach (var preset in new[] { TimingPreset.Safe, TimingPreset.Fast, TimingPreset.Extreme })
        {
            var r = Compute(preset: preset);
            Assert.True(r.Tras >= r.CasLatency + 1, $"tRAS {r.Tras} < tCL+1 {r.CasLatency + 1} ({preset})");
        }
    }

    [Fact]
    public void Tcwl_NeverDropsBelowNine()
        => Assert.True(Compute(mtps: 4000).Tcwl >= 9);

    [Fact]
    public void PrimarySummary_MatchesPrimaries()
    {
        var r = Compute();
        Assert.Equal($"{r.CasLatency}-{r.Trcd}-{r.Trp}-{r.Tras}", r.PrimarySummary);
    }

    // ---- Monotonicity: more MT/s over the same ns target ⇒ at least as many cycles ----

    [Fact]
    public void HigherFrequency_NeverLowersCasCycles()
    {
        int lo = Compute(mtps: 6000).CasLatency;
        int hi = Compute(mtps: 8000).CasLatency;
        Assert.True(hi >= lo, $"tCL dropped from {lo}@6000 to {hi}@8000");
    }

    // ---- Preset tightness: Extreme ≤ Fast ≤ Safe on the primaries ----

    [Fact]
    public void TighterPreset_NeverLoosensCas()
    {
        int safe    = Compute(preset: TimingPreset.Safe).CasLatency;
        int fast    = Compute(preset: TimingPreset.Fast).CasLatency;
        int extreme = Compute(preset: TimingPreset.Extreme).CasLatency;
        Assert.True(extreme <= fast, $"Extreme tCL {extreme} > Fast {fast}");
        Assert.True(fast <= safe, $"Fast tCL {fast} > Safe {safe}");
    }

    [Fact]
    public void TighterPreset_NeverLoosensTrueLatency()
    {
        double safe    = Compute(preset: TimingPreset.Safe).TrueLatencyNs;
        double fast    = Compute(preset: TimingPreset.Fast).TrueLatencyNs;
        double extreme = Compute(preset: TimingPreset.Extreme).TrueLatencyNs;
        Assert.True(extreme <= fast + 1e-9);
        Assert.True(fast <= safe + 1e-9);
    }

    // ---- IC quality ordering (better IC ⇒ tighter tCL, same freq/preset) ----

    [Fact]
    public void BetterIc_NeverHasLooserCas_Ddr4()
    {
        int bdie = Compute(RamGeneration.Ddr4, MemoryIc.SamsungBDie, 3600).CasLatency;
        int cjr  = Compute(RamGeneration.Ddr4, MemoryIc.HynixCJR, 3600).CasLatency;
        int nanya = Compute(RamGeneration.Ddr4, MemoryIc.NanyaOther, 3600).CasLatency;
        Assert.True(bdie <= cjr, $"B-die tCL {bdie} > CJR {cjr}");
        Assert.True(cjr <= nanya, $"CJR tCL {cjr} > Nanya {nanya}");
    }

    // ---- DDR5 "30-36-36" shape: tRCD/tRP looser than tCL for A-die ----

    [Fact]
    public void Ddr5_ADie_HasLooserRcdThanCas()
    {
        var r = Compute(RamGeneration.Ddr5, MemoryIc.HynixADie, 6000);
        Assert.True(r.Trcd > r.CasLatency, $"expected tRCD>{r.CasLatency}, got {r.Trcd}");
        Assert.Equal(r.Trcd, r.Trp); // symmetric tRCD/tRP on the published shape
    }

    // ---- Command rate per generation/preset ----

    [Theory]
    [InlineData(RamGeneration.Ddr4, TimingPreset.Safe, "2T")]
    [InlineData(RamGeneration.Ddr4, TimingPreset.Fast, "1T")]
    [InlineData(RamGeneration.Ddr4, TimingPreset.Extreme, "1T")]
    [InlineData(RamGeneration.Ddr5, TimingPreset.Safe, "2N")]
    [InlineData(RamGeneration.Ddr5, TimingPreset.Fast, "2N")]
    public void CommandRate_FollowsGenerationAndPreset(RamGeneration gen, TimingPreset preset, string expected)
    {
        var ic = gen == RamGeneration.Ddr4 ? MemoryIc.SamsungBDie : MemoryIc.HynixADie;
        int mtps = gen == RamGeneration.Ddr4 ? 3600 : 6000;
        Assert.Equal(expected, Compute(gen, ic, mtps, preset: preset).CommandRate);
    }

    // ---- DDR4 "Safe" assumes Geardown Mode → even tCL ----

    [Theory]
    [InlineData(MemoryIc.SamsungBDie)]
    [InlineData(MemoryIc.HynixCJR)]
    [InlineData(MemoryIc.MicronRevE)]
    public void Ddr4_Safe_RoundsCasToEven(MemoryIc ic)
        => Assert.Equal(0, Compute(RamGeneration.Ddr4, ic, 3600, preset: TimingPreset.Safe).CasLatency % 2);

    // ---- tREFI ordering: Extreme (max) ≥ Fast ≥ Safe ----

    [Fact]
    public void Trefi_LoosensWithAggressiveness()
    {
        int safe    = Compute(preset: TimingPreset.Safe).Trefi;
        int fast    = Compute(preset: TimingPreset.Fast).Trefi;
        int extreme = Compute(preset: TimingPreset.Extreme).Trefi;
        Assert.True(extreme >= fast, $"Extreme tREFI {extreme} < Fast {fast}");
        Assert.True(fast >= safe, $"Fast tREFI {fast} < Safe {safe}");
        Assert.Equal(65535, extreme); // Extreme is pinned to the maximum
    }

    // ---- Dual-rank refreshes longer (higher tRFC) than single-rank ----

    [Fact]
    public void DualRank_HasAtLeastAsHighTrfc()
    {
        int single = Compute(rank: MemoryRank.SingleRank).Trfc;
        int dual   = Compute(rank: MemoryRank.DualRank).Trfc;
        Assert.True(dual >= single, $"dual-rank tRFC {dual} < single {single}");
    }

    // ---- Determinism (compare scalars; Notes is a fresh list each call so whole-record equality won't hold) ----

    [Fact]
    public void Compute_IsDeterministic()
    {
        var a = Compute(RamGeneration.Ddr4, MemoryIc.SamsungBDie, 3600, MemoryRank.DualRank, TimingPreset.Fast);
        var b = Compute(RamGeneration.Ddr4, MemoryIc.SamsungBDie, 3600, MemoryRank.DualRank, TimingPreset.Fast);
        Assert.Equal(a.PrimarySummary, b.PrimarySummary);
        Assert.Equal(a.CasLatency, b.CasLatency);
        Assert.Equal(a.Trfc, b.Trfc);
        Assert.Equal(a.Trefi, b.Trefi);
        Assert.Equal(a.CommandRate, b.CommandRate);
    }

    // ---- Golden-range anchors (published known-good kits) ----

    [Fact]
    public void Anchor_Ddr4_3600_Bdie_Fast_IsAround_16_16_16()
    {
        var r = Compute(RamGeneration.Ddr4, MemoryIc.SamsungBDie, 3600, preset: TimingPreset.Fast);
        Assert.InRange(r.CasLatency, 14, 17);
        Assert.InRange(r.Trcd, 14, 18);
        Assert.InRange(r.Trp, 14, 18);
        Assert.Equal("1T", r.CommandRate);
        Assert.InRange(r.TrueLatencyNs, 7.5, 9.5); // ~8.9 ns at 16-3600
    }

    [Fact]
    public void Anchor_Ddr5_6000_ADie_Fast_IsAround_30_36_36()
    {
        var r = Compute(RamGeneration.Ddr5, MemoryIc.HynixADie, 6000, preset: TimingPreset.Fast);
        Assert.InRange(r.CasLatency, 28, 32);
        Assert.InRange(r.Trcd, 34, 40);
        Assert.InRange(r.Trp, 34, 40);
        Assert.Equal("2N", r.CommandRate);
        Assert.InRange(r.TrueLatencyNs, 9.0, 11.0); // ~10.0 ns at 30-6000
    }

    [Fact]
    public void Unknown_Ic_StillProducesUsableSet_AndCarriesACaution()
    {
        var r = Compute(RamGeneration.Ddr5, MemoryIc.Unknown, 6000);
        Assert.True(r.CasLatency >= 10);
        Assert.Equal(r.Tras + r.Trp, r.Trc);
        Assert.Contains(r.Notes, n => n.Contains("IC inconnu", StringComparison.OrdinalIgnoreCase));
    }
}
