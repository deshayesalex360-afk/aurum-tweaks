using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using AurumTweaks.Models;
using Microsoft.Win32;

namespace AurumTweaks.Services;

/// <summary>
/// Pure, WMI-free hardware-string classifiers split out of <see cref="HardwareService"/>
/// (the same pattern as <c>NetworkRouteMath</c> / <c>DriverClassification</c>). They turn the raw
/// Win32_* strings into the <see cref="CpuFamily"/> / <see cref="BiosVendor"/> / <see cref="GpuVendor"/>
/// enums that gate every downstream recommendation: <c>DetectedFamily</c> drives both the adaptive
/// engine's CpuFamilies filter and the BIOS advisor's per-CPU ranking, so a misclassification here
/// silently mis-targets BIOS/tweak advice (e.g. AM4 vs AM5). Pulled out so the rules can be pinned by
/// tests without touching WMI.
/// </summary>
public static class HardwareClassification
{
    /// <summary>
    /// Map a CPU product string (Win32_Processor.Name) to its <see cref="CpuFamily"/>. The X3D parts
    /// are keyed off the 4-digit model number's <b>generation</b> digit (5800X3D → 5000-series), NOT the
    /// marketing tier ("Ryzen 7"): the 5800X3D is a tier-7 name on an AM4/Zen3 die and the 7950X3D a
    /// tier-9 name on a 7000-series die, so a tier-based rule would put both on the wrong platform.
    /// Unrecognised or empty input → <see cref="CpuFamily.Unknown"/> (honest "couldn't tell").
    /// </summary>
    public static CpuFamily ClassifyCpu(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CpuFamily.Unknown;

        var n = name.ToUpperInvariant();
        if (n.Contains("RYZEN"))
        {
            // Key off the generation (first digit of the 4-digit model), mirroring the non-X3D branch
            // below — never the "Ryzen N" tier, which crosses generations (5800X3D, 7950X3D).
            if (n.Contains("X3D"))
            {
                if (n.Contains("9950") || n.Contains("9900") || n.Contains("9800")) return CpuFamily.Ryzen9000X3D;
                if (n.Contains("7950") || n.Contains("7900") || n.Contains("7800")) return CpuFamily.Ryzen7000X3D;
                if (n.Contains("5800") || n.Contains("5600")) return CpuFamily.Ryzen5000X3D;
            }
            if (n.Contains("9950") || n.Contains("9900") || n.Contains("9700") || n.Contains("9600")) return CpuFamily.Ryzen9000;
            if (n.Contains("7950") || n.Contains("7900") || n.Contains("7700") || n.Contains("7600")) return CpuFamily.Ryzen7000;
            if (n.Contains("5950") || n.Contains("5900") || n.Contains("5800") || n.Contains("5700") || n.Contains("5600") || n.Contains("5500")) return CpuFamily.Ryzen5000;
            if (n.Contains("3950") || n.Contains("3900") || n.Contains("3700") || n.Contains("3600")) return CpuFamily.Ryzen3000;
        }
        if (n.Contains("CORE"))
        {
            // "Core Ultra" covers Meteor/Arrow/Lunar Lake — one bucket on purpose (see CpuFamily.IntelCoreUltra).
            // Checked before the 12/13/14 digits so e.g. "Core Ultra 5 125H" isn't mistaken for a 12th-gen part.
            if (n.Contains("ULTRA")) return CpuFamily.IntelCoreUltra;
            if (n.Contains("14")) return CpuFamily.IntelCore14;
            if (n.Contains("13")) return CpuFamily.IntelCore13;
            if (n.Contains("12")) return CpuFamily.IntelCore12;
        }
        return CpuFamily.Unknown;
    }

    /// <summary>Map a motherboard/system manufacturer string to its <see cref="BiosVendor"/> (drives the DIY vendor menu paths). Unrecognised → <see cref="BiosVendor.Unknown"/>.</summary>
    public static BiosVendor ClassifyBios(string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
            return BiosVendor.Unknown;

        var m = manufacturer.ToUpperInvariant();
        if (m.Contains("ASUS")) return BiosVendor.Asus;
        if (m.Contains("MSI") || m.Contains("MICRO-STAR")) return BiosVendor.Msi;
        if (m.Contains("GIGABYTE")) return BiosVendor.Gigabyte;
        if (m.Contains("ASROCK")) return BiosVendor.Asrock;
        if (m.Contains("BIOSTAR")) return BiosVendor.Biostar;
        if (m.Contains("DELL")) return BiosVendor.Dell;
        if (m.Contains("HP") || m.Contains("HEWLETT")) return BiosVendor.Hp;
        if (m.Contains("LENOVO")) return BiosVendor.Lenovo;
        return BiosVendor.Unknown;
    }

    /// <summary>Map a GPU product string to its <see cref="GpuVendor"/> (gates GPU-OC applicability). Unrecognised → <see cref="GpuVendor.Unknown"/>.</summary>
    public static GpuVendor ClassifyGpu(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return GpuVendor.Unknown;

        var n = name.ToUpperInvariant();
        if (n.Contains("NVIDIA") || n.Contains("GEFORCE") || n.Contains("RTX") || n.Contains("GTX")) return GpuVendor.Nvidia;
        if (n.Contains("RADEON") || n.Contains("AMD")) return GpuVendor.Amd;
        if (n.Contains("ARC") || n.Contains("INTEL")) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }
}

public sealed class HardwareService : IHardwareService
{
    public Task<HardwareInfo> DetectAsync() => Task.Run(() =>
    {
        var info = new HardwareInfo();

        try
        {
            using var cpu = new ManagementObjectSearcher("SELECT * FROM Win32_Processor").Get().Cast<ManagementObject>().FirstOrDefault();
            if (cpu is not null)
            {
                info.CpuName = (cpu["Name"]?.ToString() ?? "Unknown").Trim();
                info.CpuVendor = cpu["Manufacturer"]?.ToString() ?? string.Empty;
                info.CpuCores = Convert.ToInt32(cpu["NumberOfCores"] ?? 0);
                info.CpuThreads = Convert.ToInt32(cpu["NumberOfLogicalProcessors"] ?? 0);
                // ThreadCount = silicon's max threads. When SMT/HT is disabled in BIOS,
                // NumberOfLogicalProcessors drops to the core count but ThreadCount stays at the max.
                info.CpuMaxThreads = Convert.ToInt32(cpu["ThreadCount"] ?? 0);
                if (info.CpuMaxThreads < info.CpuThreads)
                    info.CpuMaxThreads = info.CpuThreads;   // field unavailable on some platforms → no false "SMT off"
                info.CpuArchitecture = cpu["Architecture"]?.ToString() ?? string.Empty;
                info.CpuMaxClockMhz = Convert.ToInt32(cpu["MaxClockSpeed"] ?? 0);
                info.DetectedFamily = HardwareClassification.ClassifyCpu(info.CpuName);
                try
                {
                    var vt = cpu["VirtualizationFirmwareEnabled"];
                    if (vt is not null)
                    {
                        info.VirtualizationEnabled = Convert.ToBoolean(vt);
                        info.VirtualizationStatus = info.VirtualizationEnabled ? TriState.Yes : TriState.No;
                    }
                }
                catch { /* property not present on some platforms */ }
            }

            using var board = new ManagementObjectSearcher("SELECT * FROM Win32_BaseBoard").Get().Cast<ManagementObject>().FirstOrDefault();
            if (board is not null)
            {
                info.MotherboardManufacturer = board["Manufacturer"]?.ToString() ?? "Unknown";
                info.MotherboardModel = board["Product"]?.ToString() ?? "Unknown";
                info.DetectedBiosVendor = HardwareClassification.ClassifyBios(info.MotherboardManufacturer);
                info.ChipsetName = DeriveChipset(info.MotherboardModel, info.CpuName);
            }

            using var bios = new ManagementObjectSearcher("SELECT * FROM Win32_BIOS").Get().Cast<ManagementObject>().FirstOrDefault();
            if (bios is not null)
            {
                info.BiosVersion = bios["SMBIOSBIOSVersion"]?.ToString() ?? string.Empty;
                var rawDate = bios["ReleaseDate"]?.ToString();
                if (!string.IsNullOrWhiteSpace(rawDate))
                {
                    try { info.BiosReleaseDate = ManagementDateTimeConverter.ToDateTime(rawDate); }
                    catch { /* non-CIM date format on some OEM firmwares */ }
                }
            }

            // GPU (primary, the first non-Microsoft Basic adapter)
            using var gpuSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            foreach (ManagementObject g in gpuSearcher.Get())
            {
                var name = g["Name"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Basic", StringComparison.OrdinalIgnoreCase)) continue;
                info.GpuPrimary = name;
                info.GpuDriverVersion = g["DriverVersion"]?.ToString() ?? string.Empty;
                info.GpuVendor = HardwareClassification.ClassifyGpu(name);
                var drvDate = g["DriverDate"]?.ToString();
                if (!string.IsNullOrWhiteSpace(drvDate))
                {
                    try { info.GpuDriverDate = ManagementDateTimeConverter.ToDateTime(drvDate); }
                    catch { /* non-CIM date format */ }
                }
                break;
            }

            using var ram = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory");
            long totalRam = 0;
            int moduleCount = 0;
            string ramType = string.Empty;
            int ramSpeed = 0;       // configured / running speed
            int ramRated = 0;       // module rated / max speed
            foreach (ManagementObject m in ram.Get())
            {
                var cap = Convert.ToInt64(m["Capacity"] ?? 0L);
                totalRam += cap;
                moduleCount++;
                var smbType = Convert.ToInt32(m["SMBIOSMemoryType"] ?? 0);
                var thisType = smbType switch
                {
                    26 => "DDR4",
                    34 => "DDR5",
                    24 => "DDR3",
                    _ => ramType
                };
                if (!string.IsNullOrEmpty(thisType)) ramType = thisType;
                var cfg = Convert.ToInt32(m["ConfiguredClockSpeed"] ?? 0);
                var rated = Convert.ToInt32(m["Speed"] ?? 0);
                ramSpeed = Math.Max(ramSpeed, cfg);
                ramRated = Math.Max(ramRated, rated);

                info.MemoryModules.Add(new MemoryModule
                {
                    Slot = m["DeviceLocator"]?.ToString()?.Trim() ?? string.Empty,
                    BankLabel = m["BankLabel"]?.ToString()?.Trim() ?? string.Empty,
                    Manufacturer = m["Manufacturer"]?.ToString()?.Trim() ?? string.Empty,
                    PartNumber = m["PartNumber"]?.ToString()?.Trim() ?? string.Empty,
                    CapacityBytes = cap,
                    ConfiguredMhz = cfg,
                    RatedMhz = rated,
                    RamType = thisType
                });
            }
            info.TotalRamBytes = totalRam;
            info.RamModuleCount = moduleCount;
            info.RamType = ramType;
            info.RamConfiguredMhz = ramSpeed;
            info.RamRatedMhz = ramRated;
            info.RamSpeedMhz = ramSpeed > 0 ? $"{ramSpeed} MT/s" : string.Empty;
            info.RamSlotCount = CountMemorySlots();
            info.MemoryChannelSummary = BuildChannelSummary(info.MemoryModules, info.RamSlotCount);

            // Storage devices (drives SSD-only tweak gating + per-disk display).
            info.StorageDevices = DetectStorageDevices();
            {
                bool hasSsd = info.StorageDevices.Any(d => d.MediaType is "SSD" or "SCM");
                bool hasHdd = info.StorageDevices.Any(d => d.MediaType == "HDD");
                info.SystemDriveIsSsd = hasSsd || !hasHdd;
            }

            // Displays + max refresh rate (drives "set Windows to max Hz" advice).
            info.Displays = DetectDisplays(out int maxHz);
            info.MaxRefreshRateHz = maxHz;

            // Chassis type → laptop vs desktop (drives power/parking recommendations).
            try
            {
                using var enc = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
                foreach (ManagementObject e in enc.Get())
                {
                    if (e["ChassisTypes"] is ushort[] types)
                    {
                        foreach (var t in types)
                        {
                            // 8=Portable 9=Laptop 10=Notebook 11=Hand Held 12=Docking 14=Sub Notebook
                            // 18=Expansion Chassis? no — 30=Tablet 31=Convertible 32=Detachable
                            if (t is 8 or 9 or 10 or 11 or 12 or 14 or 30 or 31 or 32) { info.IsLaptop = true; break; }
                        }
                    }
                }
            }
            catch { /* enclosure info may be unavailable on some OEM systems */ }

            using var os = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem").Get().Cast<ManagementObject>().FirstOrDefault();
            if (os is not null)
            {
                info.OsCaption = os["Caption"]?.ToString() ?? string.Empty;
                info.OsVersion = os["Version"]?.ToString() ?? string.Empty;
                info.OsBuild = os["BuildNumber"]?.ToString() ?? string.Empty;
                info.IsWindows11 = info.OsCaption.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);
                info.IsLtsc = info.OsCaption.Contains("LTSC", StringComparison.OrdinalIgnoreCase)
                              || info.OsCaption.Contains("IoT", StringComparison.OrdinalIgnoreCase);
            }

            // Detect anti-cheat presence by checking for known services / processes.
            info.VanguardDetected = ServiceExists("vgc") || ServiceExists("vgk");
            info.EacDetected = ServiceExists("EasyAntiCheat") || ServiceExists("EasyAntiCheat_EOS");
            info.BattlEyeDetected = ServiceExists("BEService") || ServiceExists("BEDaisy");
            info.FaceItAcDetected = ServiceExists("FACEIT");

            // VBS / HVCI / TPM / Secure Boot via Win32_DeviceGuard.
            try
            {
                using var dg = new ManagementObjectSearcher(
                    @"\\.\root\Microsoft\Windows\DeviceGuard",
                    "SELECT * FROM Win32_DeviceGuard").Get().Cast<ManagementObject>().FirstOrDefault();
                if (dg is not null)
                {
                    var running = dg["SecurityServicesRunning"] as int[];
                    if (running is not null)
                    {
                        info.VbsRunning = running.Length > 0;
                        info.HvciRunning = running.Contains(2);
                    }
                }
            }
            catch { /* WMI namespace may not exist on older systems */ }

            // Secure Boot — readable from the registry without elevation.
            try
            {
                using var sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
                var val = sb?.GetValue("UEFISecureBootEnabled");
                if (val is not null)
                {
                    bool on = Convert.ToInt32(val) == 1;
                    info.SecureBootEnabled = on;
                    info.SecureBootStatus = on ? TriState.Yes : TriState.No;
                }
            }
            catch { /* legacy BIOS / key absent */ }

            // TPM — via the dedicated security WMI namespace (may need elevation).
            try
            {
                using var tpm = new ManagementObjectSearcher(
                    @"\\.\root\CIMV2\Security\MicrosoftTpm",
                    "SELECT * FROM Win32_Tpm").Get().Cast<ManagementObject>().FirstOrDefault();
                if (tpm is not null)
                {
                    bool enabled = Convert.ToBoolean(tpm["IsEnabled_InitialValue"] ?? false);
                    info.TpmEnabled = enabled;
                    info.TpmStatus = enabled ? TriState.Yes : TriState.No;
                    var spec = tpm["SpecVersion"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(spec)) info.TpmSpecVersion = spec.Split(',')[0].Trim();
                }
            }
            catch { /* Win32_Tpm requires elevation; leave Unknown */ }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Hardware detection partially failed");
        }

        return info;
    });

    /// <summary>
    /// Enumerates physical disks (model, media type, bus type, size). Media type falls back
    /// to spindle speed when unspecified (0 RPM ⇒ solid state).
    /// </summary>
    private static List<StorageDevice> DetectStorageDevices()
    {
        var list = new List<StorageDevice>();
        try
        {
            using var disks = new ManagementObjectSearcher(
                @"\\.\root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, SpindleSpeed, BusType, Size FROM MSFT_PhysicalDisk");
            foreach (ManagementObject d in disks.Get())
            {
                var media = Convert.ToInt32(d["MediaType"] ?? 0);   // 3=HDD 4=SSD 5=SCM
                string mediaStr = media switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" };
                if (mediaStr == "Unknown")
                {
                    var spindle = Convert.ToInt64(d["SpindleSpeed"] ?? -1L);
                    if (spindle == 0) mediaStr = "SSD";
                    else if (spindle > 0) mediaStr = "HDD";
                }
                var bus = Convert.ToInt32(d["BusType"] ?? 0);
                string busStr = bus switch
                {
                    17 => "NVMe",
                    11 => "SATA",
                    7 => "USB",
                    8 => "RAID",
                    10 => "SAS",
                    3 => "ATA",
                    _ => bus == 0 ? string.Empty : $"Bus{bus}"
                };
                list.Add(new StorageDevice
                {
                    Model = d["FriendlyName"]?.ToString()?.Trim() ?? string.Empty,
                    MediaType = mediaStr,
                    BusType = busStr,
                    SizeBytes = Convert.ToInt64(d["Size"] ?? 0L)
                });
            }
        }
        catch { /* Storage namespace unavailable → empty list (callers assume SSD) */ }
        return list;
    }

    /// <summary>Enumerates active displays and reports the fastest refresh rate seen.</summary>
    private static List<DisplayInfo> DetectDisplays(out int maxRefresh)
    {
        var list = new List<DisplayInfo>();
        int max = 0;
        try
        {
            using var vc = new ManagementObjectSearcher(
                "SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, MaxRefreshRate FROM Win32_VideoController");
            foreach (ManagementObject c in vc.Get())
            {
                var w = Convert.ToInt32(c["CurrentHorizontalResolution"] ?? 0);
                var h = Convert.ToInt32(c["CurrentVerticalResolution"] ?? 0);
                if (w == 0 || h == 0) continue; // inactive / headless adapter
                var cur = Convert.ToInt32(c["CurrentRefreshRate"] ?? 0);
                var mx = Convert.ToInt32(c["MaxRefreshRate"] ?? 0);
                max = Math.Max(max, Math.Max(cur, mx));
                list.Add(new DisplayInfo
                {
                    Name = c["Name"]?.ToString()?.Trim() ?? string.Empty,
                    Width = w,
                    Height = h,
                    CurrentRefreshHz = cur,
                    MaxRefreshHz = mx
                });
            }
        }
        catch { /* video controller info unavailable */ }
        maxRefresh = max;
        return list;
    }

    /// <summary>Number of physical DIMM slots on the board (Win32_PhysicalMemoryArray).</summary>
    private static int CountMemorySlots()
    {
        try
        {
            using var arr = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray")
                .Get().Cast<ManagementObject>().FirstOrDefault();
            if (arr is not null) return Convert.ToInt32(arr["MemoryDevices"] ?? 0);
        }
        catch { /* unavailable on some OEM firmwares */ }
        return 0;
    }

    /// <summary>Human summary of the memory layout, e.g. "2 × 16 GB (dual-channel probable) — 2/4 slots".</summary>
    private static string BuildChannelSummary(List<MemoryModule> mods, int slots)
    {
        if (mods.Count == 0) return string.Empty;
        var caps = mods.Select(x => (int)Math.Round(x.CapacityGb)).ToList();
        bool uniform = caps.Distinct().Count() == 1;
        string layout = uniform ? $"{mods.Count} × {caps[0]} GB" : string.Join(" + ", caps.Select(c => $"{c} GB"));
        string channel = mods.Count >= 2
            ? (mods.Count % 2 == 0 ? "dual-channel probable" : "config asymétrique")
            : "single-channel";
        string slotInfo = slots > 0 ? $" — {mods.Count}/{slots} slots" : string.Empty;
        return $"{layout} ({channel}){slotInfo}";
    }

    /// <summary>
    /// Best-effort chipset name. Most desktop board model strings embed the chipset
    /// (e.g. "ROG STRIX X670E-E"), so we token-match known AMD/Intel chipsets.
    /// </summary>
    private static string DeriveChipset(string boardModel, string cpuName)
    {
        var m = (boardModel ?? string.Empty).ToUpperInvariant();
        // Order matters: match the more specific token first (e.g. X670E before X670).
        string[] tokens =
        {
            // AMD AM5 / sTR5
            "X870E", "X870", "B850", "B840", "X670E", "X670", "B650E", "B650", "A620", "TRX50", "WRX90",
            // AMD AM4
            "X570", "B550", "A520", "X470", "B450", "X370", "B350", "A320",
            // Intel LGA1851 / LGA1700
            "Z890", "B860", "H810", "Z790", "Z690", "B760", "B660", "H770", "H670", "H610",
            // Intel LGA1200
            "Z590", "Z490", "B560", "B460", "H570", "H510", "H470"
        };
        foreach (var t in tokens)
            if (m.Contains(t)) return t;
        return string.Empty;
    }

    private static bool ServiceExists(string name)
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(name);
            _ = sc.Status;
            return true;
        }
        catch { return false; }
    }

}
