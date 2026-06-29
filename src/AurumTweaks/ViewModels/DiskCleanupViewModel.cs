using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Nettoyage disque » page — a measured temp/cache reclaimer. Everything here is genuinely wired: it reads the real
/// on-disk size of a curated set of known-safe folders, and a clean actually deletes their contents then re-measures,
/// so the « espace libéré » figure is the space that genuinely disappeared — never the optimistic estimate. The page
/// is honest about scope and risk: deletion is irreversible (unlike the reversible tweaks) but safe because Windows
/// recreates these folders; locked files are reported as kept rather than silently counted as freed; and the riskier
/// reclaimable space (WinSxS, Windows.old, Corbeille, points de restauration) is handed to Windows' own cleanmgr
/// rather than automated here.
/// </summary>
public partial class DiskCleanupViewModel : ObservableObject
{
    private readonly IDiskCleanupService _service;

    public ObservableCollection<CleanupItem> Items { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _totalDisplay = "0 o";
    [ObservableProperty] private bool _isBusy;

    public DiskCleanupViewModel(IDiskCleanupService service)
    {
        _service = service;
        _ = ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        Status = "Analyse des fichiers temporaires…";
        var report = await _service.ScanAsync();

        Items.Clear();
        foreach (var item in report.Items) Items.Add(item);

        TotalDisplay = report.TotalDisplay;
        Status = report.ReclaimableCount > 0
            ? $"{report.TotalDisplay} récupérable(s) sur {report.ReclaimableCount} emplacement(s)."
            : "Aucun fichier temporaire à nettoyer — déjà propre.";
        IsBusy = false;
    }

    /// <summary>Clear one location, then re-scan so the list reflects the real machine — and headline the bytes that
    /// genuinely disappeared, not the pre-scan estimate.</summary>
    [RelayCommand]
    private async Task CleanAsync(CleanupItem? item)
    {
        if (item is null || !item.HasReclaimable) return;   // nothing to reclaim → no real action to take
        IsBusy = true;
        Status = $"Nettoyage : {item.Label}…";
        var outcome = await _service.CleanAsync(item.Category);
        await ScanAsync();
        Status = DescribeOutcome(outcome, item.Label);
    }

    /// <summary>Clear every curated location in one pass, then re-scan and report the aggregate real freed space.</summary>
    [RelayCommand]
    private async Task CleanAllAsync()
    {
        IsBusy = true;
        Status = "Nettoyage de tous les emplacements…";
        var outcome = await _service.CleanAllAsync();
        await ScanAsync();
        Status = DescribeOutcome(outcome, "Nettoyage complet");
    }

    /// <summary>Open Windows' own Disk Cleanup (cleanmgr) for the riskier reclaimable space we deliberately don't
    /// automate — WinSxS, Windows.old, the Recycle Bin, restore points.</summary>
    [RelayCommand]
    private void OpenWindowsCleanup() =>
        ShellLauncher.OpenLocal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cleanmgr.exe"));

    /// <summary>Open Windows' Storage settings (Assistant de stockage) — the modern complement to this page.</summary>
    [RelayCommand]
    private void OpenStorageSettings() => ShellLauncher.OpenLink("ms-settings:storagesense");

    // Tell apart the four honest endings: nothing there, all locked, fully cleared, partially cleared.
    private static string DescribeOutcome(CleanupOutcome o, string scope)
    {
        if (o.Freed <= 0)
            return o.BytesBefore <= 0
                ? $"{scope} : rien à nettoyer."
                : $"{scope} : aucun espace libéré — fichiers verrouillés (en cours d'utilisation).";

        return o.FullyCleared
            ? $"{scope} : {o.FreedDisplay} libéré(s)."
            : $"{scope} : {o.FreedDisplay} libéré(s) · des fichiers verrouillés ont été conservés.";
    }
}
