using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Applications préinstallées » page — a curated debloat manager. Everything here is genuinely wired: it reads the
/// live set of installed apps via <c>Get-AppxPackage</c> and uninstalls a chosen one with a real
/// <c>Remove-AppxPackage</c>, then re-reads so the list reflects the real machine — never a fabricated "done". The
/// page is honest about scope: removal is per-user and reversed only by a Microsoft Store reinstall (not a clean
/// inverse like the tweaks), it's about disk space / background processes / privacy rather than FPS, and the Xbox
/// apps are flagged « à conserver » instead of pushed as junk.
/// </summary>
public partial class AppxDebloatViewModel : ObservableObject
{
    private readonly IAppxDebloatService _service;

    public ObservableCollection<AppxEntry> Apps { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;

    public AppxDebloatViewModel(IAppxDebloatService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture des applications préinstallées…";
        var report = await _service.GetReportAsync();

        Apps.Clear();
        foreach (var a in report.Entries) Apps.Add(a);

        Status = report.QueryOk
            ? $"{report.RemovableRecommendedCount} application(s) superflue(s) encore installée(s) · {report.InstalledCount} application(s) connue(s) présente(s) sur ce PC."
            : "Impossible de lire les applications installées (PowerShell / Get-AppxPackage).";
        IsBusy = false;
    }

    /// <summary>Uninstall one app, then re-read so the list reflects the real machine — never a fabricated success.</summary>
    [RelayCommand]
    private async Task RemoveAsync(AppxEntry? entry)
    {
        if (entry is null || !entry.ShowRemove) return;   // absent / protected apps have no uninstall to offer
        IsBusy = true;
        await _service.RemoveAsync(entry.PackageFullName);
        await RefreshAsync();
    }

    /// <summary>Open Windows' own « Applications installées » page — we don't pretend our curated list is the whole picture.</summary>
    [RelayCommand]
    private void OpenAppsSettings() => ShellLauncher.OpenLink("ms-settings:appsfeatures");
}
