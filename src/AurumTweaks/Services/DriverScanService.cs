using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure, WMI-free driver classification + report wording, split out of <see cref="DriverScanService"/>
/// (the same pattern as <c>GpuOcValidation</c> / <c>NetworkRouteMath</c>) so the honesty-bearing rules —
/// which drivers we nag about, how a Windows error code reads, and the headline summary — can be pinned
/// by tests without touching WMI or the wall clock.
/// </summary>
public static class DriverClassification
{
    /// <summary>A non-Microsoft driver older than this (~3 years) is "worth checking" for an update.</summary>
    public const int OldDriverAgeDays = 365 * 3;

    /// <summary>
    /// True when a driver is worth re-checking: it has a known date, is NOT a Microsoft inbox driver
    /// (those being old is normal/stable — we don't nag), and is older than <see cref="OldDriverAgeDays"/>
    /// relative to <paramref name="asOf"/>. We never claim a newer version exists — only that it's worth a look.
    /// </summary>
    public static bool IsWorthChecking(DateTime? driverDate, string manufacturer, string providerName, DateTime asOf)
    {
        if (driverDate is null)
            return false;

        // Microsoft inbox drivers being old is normal/stable — don't nag.
        if ((manufacturer ?? string.Empty).Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
            (providerName ?? string.Empty).Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            return false;

        return (asOf - driverDate.Value).TotalDays > OldDriverAgeDays;
    }

    /// <summary>Human-readable French text for a Windows Device Manager ConfigManagerErrorCode.</summary>
    public static string MapErrorCode(int code) => code switch
    {
        1 => "Périphérique mal configuré.",
        3 => "Pilote corrompu ou mémoire insuffisante.",
        10 => "Le périphérique ne peut pas démarrer.",
        12 => "Ressources insuffisantes (IRQ/mémoire).",
        14 => "Nécessite un redémarrage pour fonctionner.",
        18 => "Réinstalle les pilotes de ce périphérique.",
        19 => "Registre corrompu pour ce périphérique.",
        22 => "Périphérique désactivé.",
        28 => "Pilotes NON installés (manquants).",
        31 => "Le périphérique ne fonctionne pas correctement (pilote indisponible).",
        37 => "Le pilote a renvoyé une erreur à l'initialisation.",
        39 => "Pilote manquant ou corrompu.",
        43 => "Windows a arrêté le périphérique (erreur signalée).",
        45 => "Périphérique actuellement absent (déconnecté).",
        _ => $"Erreur Gestionnaire de périphériques (code {code})."
    };

    /// <summary>The headline sentence: problem devices first, then old-driver count, else an all-clear.</summary>
    public static string BuildSummary(int problemCount, int oldCount, int totalCount)
    {
        if (problemCount > 0)
            return $"{problemCount} périphérique(s) en erreur à corriger, {oldCount} pilote(s) ancien(s) à vérifier sur {totalCount} analysés.";
        if (oldCount > 0)
            return $"Aucun périphérique en erreur. {oldCount} pilote(s) ancien(s) à vérifier sur {totalCount} analysés.";
        return $"Aucun problème détecté — {totalCount} pilotes analysés et à jour.";
    }
}

/// <summary>
/// Enumerates installed drivers (Win32_PnPSignedDriver) and problem devices
/// (Win32_PnPEntity.ConfigManagerErrorCode) to produce a Snappy-style report.
/// Honest by design: with no offline driver database we don't claim a newer version
/// exists — we flag problem devices (always actionable) and old non-Microsoft drivers
/// (worth checking), and route updates through Windows Update / vendor pages.
/// </summary>
public sealed class DriverScanService : IDriverScanService
{
    // Device classes worth surfacing for a gaming/perf machine (others are noise).
    private static readonly HashSet<string> InterestingClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "DISPLAY", "NET", "MEDIA", "AUDIOENDPOINT", "USB", "HDC", "SCSIADAPTER",
        "BLUETOOTH", "MONITOR", "SYSTEM", "FIRMWARE", "PROCESSOR", "KEYBOARD", "MOUSE", "HIDCLASS"
    };

    public async Task<DriverScanReport> ScanAsync()
    {
        return await Task.Run(() =>
        {
            var report = new DriverScanReport();
            var byName = new Dictionary<string, DriverInfo>(StringComparer.OrdinalIgnoreCase);

            CollectSignedDrivers(byName);
            MergeProblemDevices(byName);

            report.Drivers = byName.Values
                .OrderByDescending(d => d.Priority)
                .ThenBy(d => d.DeviceClass)
                .ThenBy(d => d.DeviceName)
                .ToList();

            report.ProblemCount = report.Drivers.Count(d => d.IsProblem);
            report.OldCount = report.Drivers.Count(d => d.IsOld && !d.IsProblem);
            report.TotalCount = report.Drivers.Count;
            report.Summary = DriverClassification.BuildSummary(
                report.ProblemCount, report.OldCount, report.TotalCount);
            return report;
        });
    }

    private static void CollectSignedDrivers(Dictionary<string, DriverInfo> byName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, InfName, HardWareID, IsSigned FROM Win32_PnPSignedDriver");

            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["DeviceName"]?.ToString();
                var deviceClass = mo["DeviceClass"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (!InterestingClasses.Contains(deviceClass))
                    continue;

                var manufacturer = mo["Manufacturer"]?.ToString() ?? string.Empty;
                var date = ParseCimDate(mo["DriverDate"]?.ToString());

                var info = new DriverInfo
                {
                    DeviceName = name.Trim(),
                    DeviceClass = deviceClass,
                    Manufacturer = manufacturer,
                    ProviderName = mo["DriverProviderName"]?.ToString() ?? string.Empty,
                    DriverVersion = mo["DriverVersion"]?.ToString() ?? string.Empty,
                    DriverDate = date,
                    InfName = mo["InfName"]?.ToString() ?? string.Empty,
                    HardwareId = FirstHardwareId(mo["HardWareID"])
                };

                info.IsOld = DriverClassification.IsWorthChecking(
                    info.DriverDate, info.Manufacturer, info.ProviderName, DateTime.Now);

                // Keep the most informative entry if a device name repeats.
                if (!byName.TryGetValue(info.DeviceName, out var existing) || IsBetter(info, existing))
                    byName[info.DeviceName] = info;
            }
        }
        catch
        {
            // WMI unavailable — leave whatever we have.
        }
    }

    private static void MergeProblemDevices(Dictionary<string, DriverInfo> byName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPClass, ConfigManagerErrorCode, Manufacturer FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");

            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                int code = 0;
                try { code = Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0); }
                catch { }
                if (code == 0)
                    continue;

                var problemText = DriverClassification.MapErrorCode(code);

                if (byName.TryGetValue(name.Trim(), out var existing))
                {
                    existing.IsProblem = true;
                    existing.ProblemText = problemText;
                }
                else
                {
                    byName[name.Trim()] = new DriverInfo
                    {
                        DeviceName = name.Trim(),
                        DeviceClass = mo["PNPClass"]?.ToString() ?? string.Empty,
                        Manufacturer = mo["Manufacturer"]?.ToString() ?? string.Empty,
                        IsProblem = true,
                        ProblemText = problemText
                    };
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsBetter(DriverInfo candidate, DriverInfo existing)
    {
        // Prefer the entry that has a real date, then the newer one.
        if (candidate.DriverDate is not null && existing.DriverDate is null) return true;
        if (candidate.DriverDate is null) return false;
        if (existing.DriverDate is null) return true;
        return candidate.DriverDate > existing.DriverDate;
    }

    private static string FirstHardwareId(object? raw)
    {
        if (raw is string[] arr && arr.Length > 0)
            return arr[0];
        return raw?.ToString() ?? string.Empty;
    }

    private static DateTime? ParseCimDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try { return ManagementDateTimeConverter.ToDateTime(raw); }
        catch { }
        // Fallback: some providers report yyyyMMdd.
        if (raw.Length >= 8 && DateTime.TryParseExact(raw.Substring(0, 8), "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return null;
    }
}
