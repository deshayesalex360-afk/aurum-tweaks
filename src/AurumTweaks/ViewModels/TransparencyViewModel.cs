using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AurumTweaks.ViewModels;

/// <summary>
/// The « Transparence &amp; confiance » page — the download-trust surface. It shows the honest disclosure rendered by
/// the pure <see cref="TransparencyReport"/>, and lets the user export it, copy it, or open the local data folder to
/// check the claims for themselves. Deliberately thin: the load-bearing honesty lives in the tested renderer; this VM
/// only gathers the live facts and writes the text. Facts are re-gathered on every refresh/export so the
/// « point de restauration » line always reflects the CURRENT setting, never a value frozen at construction.
/// </summary>
public partial class TransparencyViewModel : ObservableObject
{
    private readonly ITweakRepository _repo;
    private readonly IAppSettingsStore _settings;
    private readonly string _dataDirectory;

    // The running binary's identity (version + SHA-256 fingerprint + signature). Invariant for the process lifetime, so
    // it's probed once and reused — re-hashing the executable on every refresh/export/copy would be wasted work.
    private BuildIdentityFacts? _buildIdentity;

    [ObservableProperty] private string _reportText = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Completes when the first render has run — tests await it instead of racing the constructor.</summary>
    public Task Initialization { get; }

    public TransparencyViewModel(ITweakRepository repo, IAppSettingsStore settings)
    {
        _repo = repo;
        _settings = settings;
        _dataDirectory = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\AurumTweaks");
        Initialization = RefreshAsync();
    }

    // The disclosure's facts, read live each time: the catalog (counts per tier) and the integrity gate's verdict from
    // the repository, the restore-point policy from the user's settings. Pure decision lives in TransparencyFacts.Derive.
    private async Task<TransparencyFacts> GatherAsync()
    {
        var catalog = await _repo.LoadAllAsync();   // cached after the splash — returns immediately
        // Probe the executable once (hashing is off the UI thread); honest absence on failure, never a fabricated build.
        _buildIdentity ??= await Task.Run(BuildIdentity.Probe);
        return TransparencyFacts.Derive(
            catalog, _repo.RejectedFiles, _settings.Current.CreateRestorePointBeforeTweaks, _dataDirectory,
            _buildIdentity);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ReportText = TransparencyReport.Render(await GatherAsync(), DateTime.UtcNow);
        Status = string.Empty;
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Exporter la transparence",
            FileName = $"aurum-transparence-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texte (*.txt)|*.txt|Tous les fichiers (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) { Status = string.Empty; return; }

        try
        {
            await File.WriteAllTextAsync(dlg.FileName, TransparencyReport.Render(await GatherAsync(), DateTime.UtcNow));
            Status = "Document exporté.";
        }
        catch (IOException ex) { Status = $"Export impossible : {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { Status = $"Export impossible : {ex.Message}"; }
    }

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        try
        {
            Clipboard.SetText(TransparencyReport.Render(await GatherAsync(), DateTime.UtcNow));
            Status = "Document copié. Colle-le où tu veux (forum, Discord, avis).";
        }
        catch
        {
            Status = "Copie impossible (presse-papiers occupé). Utilise « Exporter » à la place.";
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => ShellLauncher.OpenLocal(_dataDirectory);
}
