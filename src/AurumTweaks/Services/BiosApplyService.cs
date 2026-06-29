using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// The pure honesty core of <see cref="BiosApplyService"/>: given what we detected (UEFI vs Legacy, whether
/// an OEM BIOS WMI provider answered, the board vendor), decide the one-line summary and the caveats we show.
/// Extracted (same pattern as <c>HardwareClassification</c> / <c>InputTuningLogic</c> / <c>ProfilePresets</c>)
/// so the load-bearing promises can be pinned without WMI or native firmware calls:
///   • a DIY board must be told there is NO safe write API and that raw NVRAM writes can BRICK the board —
///     "Aurum ne le fait jamais automatiquement" (the app never writes the BIOS);
///   • an OEM machine's live settings are surfaced "en lecture seule" (read-only), writes deferred to the
///     official vendor tool;
///   • Legacy/CSM firmware must be told reboot-to-UEFI is unavailable, not silently promised.
/// </summary>
public static class BiosApplyAdvisor
{
    /// <summary>
    /// Populate <paramref name="caps"/>.<c>Summary</c> and append the honest caveats to its <c>Notes</c>,
    /// from the capability flags already set plus the detected board vendor on <paramref name="hw"/>.
    /// </summary>
    public static void BuildSummaryAndNotes(BiosApplyCapabilities caps, HardwareInfo hw)
    {
        if (caps.CanRebootToFirmware)
            caps.Notes.Add("Méthode universelle et sûre : redémarrer directement dans l'UEFI (pas besoin de marteler Suppr/F2 au boot).");
        else
            caps.Notes.Add("Firmware Legacy/CSM détecté : le redémarrage direct vers l'UEFI n'est pas disponible. Entre dans le BIOS via la touche Suppr/F2 au démarrage.");

        if (caps.VendorWmiAvailable)
        {
            caps.Summary = $"Machine {caps.VendorName} : {caps.VendorSettingCount} réglages BIOS lisibles en direct via le provider WMI {caps.VendorName}.";
            caps.Notes.Add($"L'écriture des réglages se fait de façon fiable via l'outil officiel {caps.VendorName} (Dell Command | Configure, HP BCU, Lenovo Commercial Vantage) — Aurum les lit ici en lecture seule.");
        }
        else
        {
            var v = hw.DetectedBiosVendor;
            bool isDiy = v is BiosVendor.Asus or BiosVendor.Msi or BiosVendor.Gigabyte or BiosVendor.Asrock or BiosVendor.Biostar;
            if (isDiy)
            {
                caps.Summary = $"Carte {v} (DIY) : pas d'API sûre pour écrire le BIOS depuis Windows. Méthode recommandée : redémarrer dans l'UEFI puis suivre le guide ci-dessus.";
                caps.Notes.Add("Écrire directement la NVRAM (style RU.efi/setup_var) peut BRIQUER la carte — Aurum ne le fait jamais automatiquement.");
            }
            else
            {
                caps.Summary = caps.CanRebootToFirmware
                    ? "Aucun provider BIOS OEM détecté. Méthode disponible : redémarrer directement dans l'UEFI."
                    : "Aucun provider BIOS OEM détecté et firmware non-UEFI.";
            }
        }
    }
}

/// <summary>
/// Implements "modify the BIOS from Windows" as honestly as it can be done:
///   • Universal &amp; safe — reboot straight into the UEFI setup (<c>shutdown /r /fw</c>).
///   • OEM machines — read BIOS settings live through the Dell/HP/Lenovo WMI providers.
///   • DIY boards (ASUS/MSI/Gigabyte/ASRock) — no safe public write API exists (bricking
///     risk via raw NVRAM), so we surface the manual guide + reboot-to-UEFI instead.
/// </summary>
public sealed class BiosApplyService : IBiosApplyService
{
    private const uint FirmwareTypeBios = 1;
    private const uint FirmwareTypeUefi = 2;

    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out uint firmwareType);

    public async Task<BiosApplyCapabilities> DetectCapabilitiesAsync(HardwareInfo hw)
    {
        return await Task.Run(() =>
        {
            var caps = new BiosApplyCapabilities
            {
                CanRebootToFirmware = IsUefi()
            };

            ProbeVendorWmi(caps);

            BiosApplyAdvisor.BuildSummaryAndNotes(caps, hw);
            return caps;
        });
    }

    public async Task<TweakApplyResult> RebootToFirmwareAsync()
    {
        // /r reboot, /fw into firmware setup, /t 8 short countdown, /c message.
        var ok = await RunShutdownAsync("/r /fw /t 8 /c \"Aurum Tweaks : redémarrage vers le BIOS/UEFI\"");
        return ok
            ? new TweakApplyResult(true, RequiresReboot: true)
            : new TweakApplyResult(false, "Le redémarrage vers l'UEFI a échoué. Sur certaines machines (firmware Legacy/CSM), cette option n'est pas disponible — entre dans le BIOS avec la touche Suppr/F2 au démarrage.");
    }

    public async Task<TweakApplyResult> CancelRebootAsync()
    {
        var ok = await RunShutdownAsync("/a");
        return ok
            ? new TweakApplyResult(true)
            : new TweakApplyResult(false, "Aucun redémarrage en attente à annuler.");
    }

    // ---- UEFI detection ------------------------------------------------

    private static bool IsUefi()
    {
        try
        {
            if (GetFirmwareType(out uint t))
                return t == FirmwareTypeUefi;
        }
        catch
        {
            // GetFirmwareType is Win8+; if it ever fails, fall through.
        }
        return false;
    }

    // ---- Vendor WMI probing (Dell / HP / Lenovo) -----------------------

    private static void ProbeVendorWmi(BiosApplyCapabilities caps)
    {
        // Lenovo: root\WMI → Lenovo_BiosSetting (CurrentSetting = "Name,Value")
        if (TryProbe(@"root\WMI", "SELECT * FROM Lenovo_BiosSetting", ParseLenovo, caps, "Lenovo"))
            return;

        // HP: root\HP\InstrumentedBIOS → HP_BIOSSetting (Name + Value/CurrentValue)
        if (TryProbe(@"root\HP\InstrumentedBIOS", "SELECT * FROM HP_BIOSSetting", ParseHp, caps, "HP"))
            return;

        // Dell: root\dcim\sysman → DCIM_BIOSEnumeration (AttributeName + CurrentValue + PossibleValues)
        if (TryProbe(@"root\dcim\sysman", "SELECT * FROM DCIM_BIOSEnumeration", ParseDell, caps, "Dell"))
            return;
    }

    private static bool TryProbe(
        string ns, string query,
        Action<ManagementObject, List<VendorBiosSetting>> parse,
        BiosApplyCapabilities caps, string vendorName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(ns), new ObjectQuery(query));
            var results = new List<VendorBiosSetting>();
            foreach (ManagementObject mo in searcher.Get())
            {
                try { parse(mo, results); }
                catch { /* skip malformed entries */ }
                if (results.Count >= 300) break;   // safety cap
            }

            if (results.Count == 0)
                return false;

            caps.VendorWmiAvailable = true;
            caps.VendorWmiNamespace = ns;
            caps.VendorName = vendorName;
            caps.VendorSettings = results;
            caps.VendorSettingCount = results.Count;
            return true;
        }
        catch
        {
            return false;   // namespace/class not present on this machine
        }
    }

    private static void ParseLenovo(ManagementObject mo, List<VendorBiosSetting> outList)
    {
        var raw = mo["CurrentSetting"]?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return;

        // Format: "SettingName,Value" (value section may itself contain ';[Optional:...]').
        var comma = raw.IndexOf(',');
        if (comma <= 0) return;

        var name = raw.Substring(0, comma).Trim();
        var rest = raw.Substring(comma + 1).Trim();
        var value = rest;
        var semi = rest.IndexOf(';');
        if (semi >= 0) value = rest.Substring(0, semi).Trim();

        outList.Add(new VendorBiosSetting { Name = name, CurrentValue = value });
    }

    private static void ParseHp(ManagementObject mo, List<VendorBiosSetting> outList)
    {
        var name = mo["Name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;

        var value = mo["Value"]?.ToString() ?? mo["CurrentValue"]?.ToString() ?? string.Empty;
        outList.Add(new VendorBiosSetting { Name = name.Trim(), CurrentValue = value.Trim() });
    }

    private static void ParseDell(ManagementObject mo, List<VendorBiosSetting> outList)
    {
        var name = mo["AttributeName"]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;

        var current = ToScalar(mo["CurrentValue"]);
        var possible = ToScalar(mo["PossibleValues"]);
        outList.Add(new VendorBiosSetting
        {
            Name = name.Trim(),
            CurrentValue = current,
            PossibleValues = possible
        });
    }

    /// <summary>Flatten a WMI value that may be a string[] (Dell uses arrays) into a readable string.</summary>
    private static string ToScalar(object? value)
    {
        if (value is string[] arr)
            return string.Join(", ", arr);
        return value?.ToString() ?? string.Empty;
    }

    // ---- Process helper ------------------------------------------------

    private static async Task<bool> RunShutdownAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
