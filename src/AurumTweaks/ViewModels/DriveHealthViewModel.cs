using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Santé des disques » page — a read-only drive-health view. Everything shown is real: each physical disk's health,
/// capacity, temperature, SSD wear, power-on hours and uncorrected-error count come straight from Windows
/// (Get-PhysicalDisk + Get-StorageReliabilityCounter, i.e. Windows' own SMART interpretation), and a metric the drive
/// doesn't expose stays « — » rather than a fabricated zero. There is deliberately no « repair » button — the page
/// can't honestly fix a drive — only honest hand-offs to Windows' own drive optimiser and storage settings.
/// </summary>
public partial class DriveHealthViewModel : ObservableObject
{
    private readonly IDriveHealthService _service;
    private DriveHealthReport? _lastReport;

    public ObservableCollection<DriveHealthInfo> Drives { get; } = new();

    [ObservableProperty] private string _headline = "Lecture de l'état des disques…";
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _queryFailed;

    // Gates « Copier le rapport » / « Exporter… » so they can't fire before a real read has produced something to share.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand))]
    private bool _hasReport;

    public DriveHealthViewModel(IDriveHealthService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;   // ignore a click while the initial/last read is still running — never re-enter
        IsBusy = true;
        Status = "Lecture de l'état des disques…";
        var report = await _service.GetReportAsync();
        _lastReport = report;

        Drives.Clear();
        foreach (var drive in report.Drives) Drives.Add(drive);

        QueryFailed = !report.QueryOk;
        Headline = report.Headline;
        Status = report.QueryOk
            ? $"{report.Count} disque(s) physique(s) · {report.HealthyCount} sain(s), {report.WatchCount} à surveiller, {report.CriticalCount} en alerte."
            : "Impossible de lire l'état des disques — module de stockage Windows indisponible ou accès refusé.";
        HasReport = true;
        IsBusy = false;
    }

    /// <summary>Open Windows' own drive optimiser (defragmentation HDD / TRIM SSD) — the genuinely useful maintenance
    /// action this page can't perform itself.</summary>
    [RelayCommand]
    private void OpenOptimizer() =>
        ShellLauncher.OpenLocal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dfrgui.exe"));

    /// <summary>Open Windows' Storage settings (disques et volumes) — the modern complement to this page.</summary>
    [RelayCommand]
    private void OpenStorageSettings() => ShellLauncher.OpenLink("ms-settings:disksandvolumes");

    /// <summary>Copy the shareable plain-text drive-health report to the clipboard — the « is my SSD dying? » paste a
    /// user drops on a forum or a support ticket. Gated by <see cref="HasReport"/> so it never copies an empty sheet.</summary>
    [RelayCommand(CanExecute = nameof(HasReport))]
    private void CopyReport()
    {
        if (_lastReport is null) return;
        var text = DriveHealthTextReport.Render(_lastReport, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = "Rapport copié dans le presse-papiers.";
        }
        catch (Exception)
        {
            // The clipboard can be momentarily locked by another app — a copy failure is never fatal.
            Status = "Impossible d'accéder au presse-papiers pour l'instant.";
        }
    }

    /// <summary>Save the same shareable drive-health report as a .txt file — the file complement to the clipboard copy
    /// (attach to a support ticket, keep before a reinstall, or the fallback when the clipboard is locked). Gated by
    /// <see cref="HasReport"/>; the renderer is unit-tested and the file write is the only untested glue.</summary>
    [RelayCommand(CanExecute = nameof(HasReport))]
    private async Task ExportReportAsync()
    {
        if (_lastReport is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exporter le rapport de santé des disques",
            FileName = $"aurum-sante-disques-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, DriveHealthTextReport.Render(_lastReport, DateTime.UtcNow));
            Status = "Rapport de santé des disques exporté.";
        }
        catch (IOException ex) { Status = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Status = $"Export impossible : {ex.Message}"; }
    }
}
