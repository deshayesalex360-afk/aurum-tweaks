using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// « Services Windows » page — a curated, reversible service manager. Everything here is genuinely wired: it reads
/// each well-known service's real startup type + running state from Windows, and every change is a real
/// <c>SetStartupType</c> (registry Start DWORD) the page RE-READS so a write Windows rejects comes back as the
/// unchanged true state — never a fabricated « done ». It's honest about scope: this is confidentialité &amp;
/// légèreté, not an FPS lever; gaming/perf services are flagged « à conserver »; and « Manuel (déclenché) » is
/// preferred over « Désactivé » wherever a feature is only occasionally useful.
/// </summary>
public partial class ServiceControlViewModel : ObservableObject
{
    private readonly IServiceControlService _service;

    public ObservableCollection<ServiceEntry> Services { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;

    public ServiceControlViewModel(IServiceControlService service)
    {
        _service = service;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Lecture de l'état des services…";
        var report = await _service.GetReportAsync();

        Services.Clear();
        foreach (var s in report.Entries) Services.Add(s);

        Status = report.QueryOk
            ? $"{report.ActionableCount} service(s) optimisable(s) · {report.PresentCount} service(s) connu(s) présent(s) sur ce PC."
            : "Impossible de lire l'état des services Windows.";
        IsBusy = false;
    }

    private Task SetStartupAsync(ServiceEntry? entry, string canonicalTarget)
    {
        // Only tunable, non-Keep services expose action buttons; ignore anything else defensively.
        if (entry is null || !entry.ShowActions) return Task.CompletedTask;
        return ApplyAsync(entry.ServiceName, canonicalTarget);
    }

    private async Task ApplyAsync(string serviceName, string canonicalTarget)
    {
        IsBusy = true;
        await _service.SetStartupAsync(serviceName, canonicalTarget);
        await RefreshAsync();   // re-read so the row reflects the real machine, never a fabricated success
    }

    [RelayCommand]
    private Task SetAuto(ServiceEntry? entry) => SetStartupAsync(entry, "Automatic");

    [RelayCommand]
    private Task SetManual(ServiceEntry? entry) => SetStartupAsync(entry, "Manual");

    [RelayCommand]
    private Task SetDisabled(ServiceEntry? entry) => SetStartupAsync(entry, "Disabled");

    /// <summary>Open Windows' own Services console — we don't pretend our curated list is the whole picture.</summary>
    [RelayCommand]
    private void OpenServices() => ShellLauncher.OpenLocal("services.msc");
}
