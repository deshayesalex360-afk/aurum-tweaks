using System;
using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>
/// One driver/device entry from the Snappy-style scan: what's installed, how old it is,
/// and whether Windows flags it as a problem device.
/// </summary>
public class DriverInfo
{
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;     // Display, Net, Media, USB, HDC...
    public string Manufacturer { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public DateTime? DriverDate { get; set; }
    public string InfName { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;

    /// <summary>Windows reports a Device Manager error (ConfigManagerErrorCode != 0).</summary>
    public bool IsProblem { get; set; }
    public string ProblemText { get; set; } = string.Empty;

    /// <summary>Driver is old enough to be worth checking for an update (non-Microsoft, &gt; ~3 years).</summary>
    public bool IsOld { get; set; }

    public int AgeYears => DriverDate is null ? -1 : (int)((DateTime.Now - DriverDate.Value).TotalDays / 365.0);

    public string DriverDateText => DriverDate is null ? "date inconnue" : DriverDate.Value.ToString("yyyy-MM-dd");

    public string StatusLabel => IsProblem ? "PROBLÈME" : IsOld ? "À VÉRIFIER" : "OK";

    /// <summary>Sort weight — problems first, then old, then fine.</summary>
    public int Priority => IsProblem ? 1000 : IsOld ? 500 : 100;

    public string VersionSummary
    {
        get
        {
            var v = string.IsNullOrWhiteSpace(DriverVersion) ? "?" : DriverVersion;
            return $"v{v} · {DriverDateText}";
        }
    }
}

/// <summary>Result of a full driver scan: the device list plus headline counts.</summary>
public class DriverScanReport
{
    public List<DriverInfo> Drivers { get; set; } = new();
    public int ProblemCount { get; set; }
    public int OldCount { get; set; }
    public int TotalCount { get; set; }
    public string Summary { get; set; } = string.Empty;
}
