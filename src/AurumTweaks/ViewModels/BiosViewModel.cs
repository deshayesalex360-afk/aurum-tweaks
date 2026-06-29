using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

public partial class BiosViewModel : ObservableObject
{
    private readonly IHardwareService _hardware;
    private readonly IBiosAdvisorService _advisor;
    private readonly IBiosApplyService _apply;

    [ObservableProperty] private HardwareInfo? _hardwareInfo;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private TweakTier _selectedTier = TweakTier.Tranquille;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand))]
    private BiosAdvisorReport? _report;

    [ObservableProperty] private BiosApplyCapabilities? _applyCapabilities;
    [ObservableProperty] private string _applyStatus = string.Empty;
    [ObservableProperty] private string _reportStatus = string.Empty;

    /// <summary>Gates the copy/export report commands — nothing to share until the scan has produced a report.</summary>
    public bool CanShareReport => Report is not null;

    /// <summary>Personalized, ranked BIOS actions for the detected machine (ActionNeeded first).</summary>
    public ObservableCollection<BiosRecommendation> Recommendations { get; } = new();
    public ObservableCollection<RamKitProfile> RamKits { get; } = new();

    /// <summary>BIOS settings read live from an OEM vendor WMI provider (Dell/HP/Lenovo only).</summary>
    public ObservableCollection<VendorBiosSetting> VendorSettings { get; } = new();

    public BiosViewModel(IHardwareService hardware, IBiosAdvisorService advisor, IBiosApplyService apply)
    {
        _hardware = hardware;
        _advisor = advisor;
        _apply = apply;
        _ = InitialiseAsync();
    }

    private async Task InitialiseAsync()
    {
        IsLoading = true;
        HardwareInfo = await _hardware.DetectAsync();
        LoadRamKits();
        BuildReport();

        ApplyCapabilities = await _apply.DetectCapabilitiesAsync(HardwareInfo);
        VendorSettings.Clear();
        foreach (var vs in ApplyCapabilities.VendorSettings)
            VendorSettings.Add(vs);

        IsLoading = false;
    }

    /// <summary>Re-run the advisor against the already-detected hardware for the chosen tier.</summary>
    private void BuildReport()
    {
        if (HardwareInfo is null)
            return;

        var report = _advisor.BuildReport(HardwareInfo, SelectedTier);
        Report = report;

        Recommendations.Clear();
        foreach (var rec in report.Recommendations)
            Recommendations.Add(rec);
    }

    private void LoadRamKits()
    {
        RamKits.Clear();
        RamKits.Add(new RamKitProfile
        {
            Name = "DDR4-3600 CL16 Hynix (sweet spot Ryzen 5000)",
            RamType = "DDR4",
            FrequencyMTs = 3600,
            CL = 16, tRCD = 19, tRP = 19, tRAS = 36, tRC = 55,
            tRFC = 320, tRRDS = 4, tRRDL = 6, tFAW = 16, tWR = 12,
            MemoryIc = "Hynix CJR/DJR",
            Vdimm = "1.40V"
        });
        RamKits.Add(new RamKitProfile
        {
            Name = "DDR4-3600 CL14 Samsung B-die (tight)",
            RamType = "DDR4",
            FrequencyMTs = 3600,
            CL = 14, tRCD = 15, tRP = 15, tRAS = 28, tRC = 43,
            tRFC = 280, tRRDS = 4, tRRDL = 4, tFAW = 16, tWR = 10,
            MemoryIc = "Samsung B-die",
            Vdimm = "1.45V"
        });
        RamKits.Add(new RamKitProfile
        {
            Name = "DDR5-6000 CL30 Hynix A-die (sweet spot Ryzen 7000)",
            RamType = "DDR5",
            FrequencyMTs = 6000,
            CL = 30, tRCD = 36, tRP = 36, tRAS = 76, tRC = 112,
            tRFC = 480, tRRDS = 8, tRRDL = 12, tFAW = 32, tWR = 48,
            MemoryIc = "Hynix A-die",
            Vdimm = "1.40V"
        });
        RamKits.Add(new RamKitProfile
        {
            Name = "DDR5-6400 CL30 Hynix M/A-die (Ryzen 9000)",
            RamType = "DDR5",
            FrequencyMTs = 6400,
            CL = 30, tRCD = 38, tRP = 38, tRAS = 80, tRC = 118,
            tRFC = 520, tRRDS = 8, tRRDL = 12, tFAW = 32, tWR = 48,
            MemoryIc = "Hynix M-die",
            Vdimm = "1.40V"
        });
    }

    partial void OnSelectedTierChanged(TweakTier value) => BuildReport();

    [RelayCommand]
    private void RefreshHardware() => _ = InitialiseAsync();

    /// <summary>Reboot straight into the UEFI/BIOS setup (universal, safe). 8-second countdown.</summary>
    [RelayCommand]
    private async Task RebootToFirmwareAsync()
    {
        ApplyStatus = "Redémarrage vers le BIOS dans 8 secondes… (clique sur Annuler pour stopper)";
        var result = await _apply.RebootToFirmwareAsync();
        ApplyStatus = result.Success
            ? "Redémarrage programmé. Le PC va entrer dans l'UEFI."
            : result.Error ?? "Échec du redémarrage vers l'UEFI.";
    }

    /// <summary>Abort the pending reboot countdown.</summary>
    [RelayCommand]
    private async Task CancelRebootAsync()
    {
        var result = await _apply.CancelRebootAsync();
        ApplyStatus = result.Success ? "Redémarrage annulé." : result.Error ?? string.Empty;
    }

    /// <summary>Copy the shareable plain-text BIOS report to the clipboard — the « est-ce que mon BIOS est bien réglé pour
    /// le jeu / l'OC ? » paste a user drops on a forum or Discord. Gated by <see cref="CanShareReport"/> so it never
    /// copies before the hardware scan has produced a report.</summary>
    [RelayCommand(CanExecute = nameof(CanShareReport))]
    private void CopyReport()
    {
        if (Report is null) return;
        try
        {
            System.Windows.Clipboard.SetText(BiosTextReport.Render(Report, SelectedTier, DateTime.UtcNow));
            ReportStatus = "Rapport BIOS copié — colle-le où tu veux (forum, Discord).";
        }
        catch
        {
            // The clipboard can be momentarily locked by another app — a copy failure is never fatal.
            ReportStatus = "Copie impossible (presse-papiers occupé). Utilise « Exporter… » à la place.";
        }
    }

    /// <summary>Save the shareable BIOS report as a .txt file — same content as the clipboard copy. The file-write is the
    /// only untested glue; <see cref="BiosTextReport"/> does the honesty-bearing work and is unit-tested.</summary>
    [RelayCommand(CanExecute = nameof(CanShareReport))]
    private async Task ExportReportAsync()
    {
        if (Report is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exporter le rapport BIOS",
            FileName = $"aurum-bios-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, BiosTextReport.Render(Report, SelectedTier, DateTime.UtcNow));
            ReportStatus = "Rapport BIOS exporté.";
        }
        catch (IOException ex) { ReportStatus = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { ReportStatus = $"Export impossible : {ex.Message}"; }
    }
}
