using System.Collections.ObjectModel;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

public partial class DriversViewModel : ObservableObject
{
    private readonly IHardwareService _hardware;
    private readonly IDriverScanService _driverScan;

    [ObservableProperty] private HardwareInfo? _hardwareInfo;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private DriverScanReport? _scanReport;
    [ObservableProperty] private string _ddu = "https://www.wagnardsoft.com/";
    [ObservableProperty] private string _nvCleanstall = "https://www.techpowerup.com/download/techpowerup-nvcleanstall/";
    [ObservableProperty] private string _nvidiaProfileInspector = "https://nvidiaprofileinspector.net/";

    public ObservableCollection<string> Recommendations { get; } = new();

    /// <summary>Scanned drivers — problem devices first, then old drivers, then OK.</summary>
    public ObservableCollection<DriverInfo> Drivers { get; } = new();

    public DriversViewModel(IHardwareService hardware, IDriverScanService driverScan)
    {
        _hardware = hardware;
        _driverScan = driverScan;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        HardwareInfo = await _hardware.DetectAsync();
        LoadRecommendations();
        await ScanDriversAsync();
    }

    private void LoadRecommendations()
    {
        Recommendations.Clear();
        if (HardwareInfo?.GpuVendor == GpuVendor.Nvidia)
        {
            Recommendations.Add("NVIDIA Game Ready Driver — pour gaming compétitif. Toujours via NVIDIA App / site officiel.");
            Recommendations.Add("NVIDIA Studio Driver — pour création de contenu Adobe / DaVinci / Blender.");
            Recommendations.Add("NVCleanstall — installation minimale (sans GeForce Experience, sans télémétrie).");
            Recommendations.Add("NVIDIA Profile Inspector — settings cachés (Low Latency Ultra, Threaded Optimization, MSI mode).");
            Recommendations.Add("Pour Valorant : conserver HVCI + Secure Boot, sinon Vanguard refuse de démarrer.");
        }
        else if (HardwareInfo?.GpuVendor == GpuVendor.Amd)
        {
            Recommendations.Add("AMD Adrenalin — pilote officiel avec Radeon Software intégré.");
            Recommendations.Add("Anti-Lag 2 — réduit la latence d'entrée. Excellent pour eSport.");
            Recommendations.Add("Pour fix stutters AM4 : driver chipset à jour + AGESA 1.2.0.7+ pour fTPM fix.");
        }
    }

    [RelayCommand]
    private async Task ScanDriversAsync()
    {
        IsScanning = true;
        var report = await _driverScan.ScanAsync();
        ScanReport = report;
        Drivers.Clear();
        foreach (var d in report.Drivers)
            Drivers.Add(d);
        IsScanning = false;
    }

    [RelayCommand]
    private void OpenWindowsUpdate() => ShellLauncher.OpenLink("ms-settings:windowsupdate");

    [RelayCommand]
    private void OpenOptionalUpdates() => ShellLauncher.OpenLink("ms-settings:windowsupdate-optionalupdates");

    [RelayCommand]
    private void OpenDeviceManager() => ShellLauncher.OpenLocal("devmgmt.msc");

    [RelayCommand]
    private void OpenDdu() => ShellLauncher.OpenLink(Ddu);

    [RelayCommand]
    private void OpenNvCleanstall() => ShellLauncher.OpenLink(NvCleanstall);

    [RelayCommand]
    private void OpenNvProfileInspector() => ShellLauncher.OpenLink(NvidiaProfileInspector);
}
