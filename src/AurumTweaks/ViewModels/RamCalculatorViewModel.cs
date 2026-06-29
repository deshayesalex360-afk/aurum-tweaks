using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// Drives the DRAM timings calculator. All it does is feed user/hardware inputs into the pure
/// <see cref="DramCalculator"/> and surface the result live. It prefills generation + frequency
/// from detected RAM but never claims to know the IC (Windows can't read it).
/// </summary>
public partial class RamCalculatorViewModel : ObservableObject
{
    private readonly IHardwareService _hardware;
    private bool _loaded;

    /// <summary>A value + its French display label, for binding enum choices to combo boxes.</summary>
    public sealed record Option<T>(T Value, string Label);

    [ObservableProperty] private HardwareInfo? _hardwareInfo;
    [ObservableProperty] private RamGeneration _selectedGeneration = RamGeneration.Ddr5;
    [ObservableProperty] private IReadOnlyList<Option<MemoryIc>> _ics = Array.Empty<Option<MemoryIc>>();
    [ObservableProperty] private MemoryIc _selectedIc = MemoryIc.HynixADie;
    [ObservableProperty] private int _dataRateMtps = 6000;
    [ObservableProperty] private MemoryRank _selectedRank = MemoryRank.SingleRank;
    [ObservableProperty] private TimingPreset _selectedPreset = TimingPreset.Fast;
    [ObservableProperty] private DramTimingSet? _result;
    [ObservableProperty] private string _detectionNote = string.Empty;

    public IReadOnlyList<Option<RamGeneration>> Generations { get; } = new[]
    {
        new Option<RamGeneration>(RamGeneration.Ddr4, DramOptions.Label(RamGeneration.Ddr4)),
        new Option<RamGeneration>(RamGeneration.Ddr5, DramOptions.Label(RamGeneration.Ddr5))
    };

    public IReadOnlyList<Option<MemoryRank>> Ranks { get; } = new[]
    {
        new Option<MemoryRank>(MemoryRank.SingleRank, DramOptions.Label(MemoryRank.SingleRank)),
        new Option<MemoryRank>(MemoryRank.DualRank, DramOptions.Label(MemoryRank.DualRank))
    };

    public IReadOnlyList<Option<TimingPreset>> Presets { get; } = new[]
    {
        new Option<TimingPreset>(TimingPreset.Safe, DramOptions.Label(TimingPreset.Safe)),
        new Option<TimingPreset>(TimingPreset.Fast, DramOptions.Label(TimingPreset.Fast)),
        new Option<TimingPreset>(TimingPreset.Extreme, DramOptions.Label(TimingPreset.Extreme))
    };

    /// <summary>Common data rates offered as one-tap buttons.</summary>
    public IReadOnlyList<int> QuickRates { get; } = new[] { 3200, 3600, 3800, 4000, 6000, 6400, 8000 };

    public RamCalculatorViewModel(IHardwareService hardware)
    {
        _hardware = hardware;
        RebuildIcs();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var hw = await _hardware.DetectAsync();
            HardwareInfo = hw;

            if (hw.RamType.Contains("DDR4", StringComparison.OrdinalIgnoreCase))
                SelectedGeneration = RamGeneration.Ddr4;
            else if (hw.RamType.Contains("DDR5", StringComparison.OrdinalIgnoreCase))
                SelectedGeneration = RamGeneration.Ddr5;

            // Prefer the rated (XMP/EXPO) speed as the OC target; fall back to the running speed.
            int target = hw.RamRatedMhz > 0 ? hw.RamRatedMhz : hw.RamConfiguredMhz;
            if (target >= 1600) DataRateMtps = target;

            DetectionNote = BuildDetectionNote(hw);
        }
        catch
        {
            DetectionNote = "Détection RAM indisponible — saisis les paramètres manuellement.";
        }
        finally
        {
            _loaded = true;
            Recompute();
        }
    }

    private static string BuildDetectionNote(HardwareInfo hw)
    {
        string speed = hw.RamConfiguredMhz > 0 ? $"{hw.RamConfiguredMhz} MT/s actuels" : "vitesse inconnue";
        string rated = hw.RamRatedMhz > 0 ? $" · {hw.RamRatedMhz} MT/s notés (XMP/EXPO)" : "";
        string modules = hw.RamModuleCount > 0 ? $"{hw.RamModuleCount} module(s)" : "modules inconnus";
        string type = string.IsNullOrWhiteSpace(hw.RamType) ? "DDR ?" : hw.RamType;
        return $"Détecté : {type} · {modules} · {speed}{rated}. Le type d'IC n'est pas lisible par "
             + "Windows — sélectionne-le (Thaiphoon Burner) pour des timings adaptés.";
    }

    private void RebuildIcs()
    {
        var list = DramOptions.IcsFor(SelectedGeneration)
            .Select(ic => new Option<MemoryIc>(ic, DramOptions.Label(ic)))
            .ToList();
        Ics = list;
        if (list.All(o => o.Value != SelectedIc))
            SelectedIc = list[0].Value;
    }

    partial void OnSelectedGenerationChanged(RamGeneration value)
    {
        RebuildIcs();
        // Nudge the frequency into the new generation's typical band so the result stays sensible.
        if (value == RamGeneration.Ddr5 && DataRateMtps < 4000) DataRateMtps = 6000;
        else if (value == RamGeneration.Ddr4 && DataRateMtps > 4200) DataRateMtps = 3600;
        Recompute();
    }

    partial void OnSelectedIcChanged(MemoryIc value) => Recompute();
    partial void OnDataRateMtpsChanged(int value) => Recompute();
    partial void OnSelectedRankChanged(MemoryRank value) => Recompute();
    partial void OnSelectedPresetChanged(TimingPreset value) => Recompute();

    private void Recompute()
    {
        if (!_loaded) return;
        Result = DramCalculator.Compute(new DramCalculatorInput(
            SelectedGeneration, SelectedIc, DataRateMtps, SelectedRank, SelectedPreset));
    }

    [RelayCommand]
    private void SetFrequency(int mtps) => DataRateMtps = mtps;

    [RelayCommand]
    private void CopyTimings()
    {
        if (Result is null) return;
        var r = Result;
        string text =
            $"{DramOptions.Label(SelectedGeneration)}-{r.DataRateMtps} · {DramOptions.Label(SelectedIc)} · {DramOptions.Label(SelectedPreset)}\n" +
            $"Primaires : {r.CasLatency}-{r.Trcd}-{r.Trp}-{r.Tras}  (tRC {r.Trc}, {r.CommandRate})\n" +
            $"tRFC {r.Trfc} · tRRDS {r.TrrdS} · tRRDL {r.TrrdL} · tFAW {r.Tfaw}\n" +
            $"tWR {r.Twr} · tWTRS {r.Twtrs} · tWTRL {r.Twtrl} · tRTP {r.Trtp} · tCWL {r.Tcwl} · tREFI {r.Trefi}\n" +
            $"Latence réelle {r.TrueLatencyNs:0.0} ns · tCK {r.CycleTimeNs:0.000} ns";
        try
        {
            Clipboard.SetText(text);
            DetectionNote = "Timings copiés dans le presse-papiers.";
        }
        catch
        {
            // Clipboard can be momentarily locked by another process — non-fatal.
        }
    }
}
