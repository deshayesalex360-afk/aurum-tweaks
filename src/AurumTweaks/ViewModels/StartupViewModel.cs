using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// "Démarrage" page — the startup-program manager. Everything here is genuinely wired: it lists the real
/// programs Windows launches at logon (registry Run + Startup folders, both scopes) and toggles them through a
/// reversible move (Aurum's backup key / an <c>AurumDisabled</c> folder), never a fake switch. A disable that
/// the OS rejects comes back as the unchanged real state after the re-scan — we never paint a success we didn't
/// achieve. The category/impact guidance is heuristic and labelled "indicatif" in the view.
/// </summary>
public partial class StartupViewModel : ObservableObject
{
    private readonly IStartupManagerService _startup;

    public ObservableCollection<StartupEntry> Entries { get; } = new();

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isBusy;

    public StartupViewModel(IStartupManagerService startup)
    {
        _startup = startup;
        _ = ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        Status = "Analyse des programmes au démarrage…";
        var list = await _startup.ScanAsync();
        Entries.Clear();
        foreach (var e in list) Entries.Add(e);

        int enabled = list.Count(e => e.IsEnabled);
        Status = list.Count == 0
            ? "Aucun programme au démarrage détecté."
            : $"{list.Count} programme(s) au démarrage · {enabled} actif(s), {list.Count - enabled} désactivé(s).";
        IsBusy = false;
    }

    /// <summary>Flip one entry between active and disabled, then re-scan so the list reflects the real machine.</summary>
    [RelayCommand]
    private async Task ToggleAsync(StartupEntry? entry)
    {
        if (entry is null) return;
        IsBusy = true;
        await _startup.SetEnabledAsync(entry, !entry.IsEnabled);
        await ScanAsync();
    }

    /// <summary>Open Windows' own Startup-apps page — the user can cross-check there; we don't pretend to be the only source.</summary>
    [RelayCommand]
    private void OpenWindowsStartup() => ShellLauncher.OpenLink("ms-settings:startupapps");
}
