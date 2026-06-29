using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>One HID input device (mouse / keyboard / controller) and how it's connected.</summary>
public class InputDeviceInfo
{
    public string Name { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;     // Souris, Clavier, Manette, HID
    public string Manufacturer { get; set; } = string.Empty;
    public string Bus { get; set; } = string.Empty;            // USB, Bluetooth, PS/2, HID
    public bool IsWireless { get; set; }
    public string PnpDeviceId { get; set; } = string.Empty;

    public string Summary
    {
        get
        {
            var bus = string.IsNullOrWhiteSpace(Bus) ? "" : $" · {Bus}";
            var wl = IsWireless ? " · sans-fil" : "";
            return $"{DeviceType}{bus}{wl}";
        }
    }
}

/// <summary>
/// HIDUSBF-style input report: connected input devices, the polling-rate situation
/// (honest — Windows can't read true polling rate without a kernel driver), detected
/// peripheral software, and current mouse settings we CAN read/act on safely.
/// </summary>
public class InputTuningReport
{
    public List<InputDeviceInfo> Devices { get; set; } = new();

    /// <summary>Vendor configuration apps we found running (G HUB, Synapse, iCUE, SteelSeries GG…).</summary>
    public List<string> DetectedSoftware { get; set; } = new();

    /// <summary>True when at least one vendor configuration app is running (drives card visibility).</summary>
    public bool HasDetectedSoftware => DetectedSoftware.Count > 0;

    /// <summary>Plain-language polling-rate / input-latency guidance.</summary>
    public List<string> Guidance { get; set; } = new();

    /// <summary>True when Windows pointer acceleration ("Enhance pointer precision") is ON.</summary>
    public bool MouseAccelerationOn { get; set; }
    public string MouseAccelerationText { get; set; } = string.Empty;

    public int MouseCount { get; set; }
    public int KeyboardCount { get; set; }
    public string Summary { get; set; } = string.Empty;

    public string HidUsbfUrl { get; set; } = "https://www.majorgeeks.com/files/details/hidusbf.html";
}
