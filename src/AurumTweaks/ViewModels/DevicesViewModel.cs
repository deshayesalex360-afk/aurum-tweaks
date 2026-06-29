using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// "Périphériques" page — HIDUSBF-style input tuning. Lists connected HID devices and how
/// they're wired, surfaces the vendor app that owns the real polling rate, flags Windows
/// pointer acceleration (which we CAN act on), and routes to HIDUSBF for the rest.
/// </summary>
public partial class DevicesViewModel : ObservableObject
{
    private readonly IInputDeviceService _input;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string? _status;

    // Gates « Copier le rapport »: enabled only once a scan has produced a report, so the button is never a no-op.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    private InputTuningReport? _report;

    /// <summary>Connected mice / keyboards / controllers.</summary>
    public ObservableCollection<InputDeviceInfo> Devices { get; } = new();

    /// <summary>Vendor configuration apps detected running (G HUB, Synapse, iCUE…).</summary>
    public ObservableCollection<string> DetectedSoftware { get; } = new();

    /// <summary>Plain-language polling-rate / input-latency guidance.</summary>
    public ObservableCollection<string> Guidance { get; } = new();

    public DevicesViewModel(IInputDeviceService input)
    {
        _input = input;
        _ = ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;

        var report = await _input.ScanAsync();
        Report = report;

        Devices.Clear();
        foreach (var d in report.Devices)
            Devices.Add(d);

        DetectedSoftware.Clear();
        foreach (var s in report.DetectedSoftware)
            DetectedSoftware.Add(s);

        Guidance.Clear();
        foreach (var g in report.Guidance)
            Guidance.Add(g);

        IsScanning = false;
    }

    /// <summary>Copy the shareable input-devices paste — the real read-back state, never sent anywhere — to the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyReport))]
    private void CopyReport()
    {
        if (Report is null) return;
        var text = InputDeviceTextReport.Render(Report, DateTime.UtcNow);
        try
        {
            System.Windows.Clipboard.SetText(text);
            Status = "Rapport copié dans le presse-papiers.";
        }
        catch (Exception)
        {
            Status = "Impossible d'accéder au presse-papiers pour l'instant.";
        }
    }

    private bool CanCopyReport() => Report is not null;

    [RelayCommand]
    private void OpenMouseSettings() => ShellLauncher.OpenLink("ms-settings:mousetouchpad");

    [RelayCommand]
    private void OpenHidUsbf() => ShellLauncher.OpenLink(Report?.HidUsbfUrl ?? "https://www.majorgeeks.com/files/details/hidusbf.html");

    [RelayCommand]
    private void OpenDeviceManager() => ShellLauncher.OpenLocal("devmgmt.msc");
}
