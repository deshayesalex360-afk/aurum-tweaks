using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// Pure, WMI-free input-tuning logic split out of <see cref="InputDeviceService"/> (the same pattern
/// as <c>NetworkRouteMath</c> / <c>DriverClassification</c> / <c>HardwareClassification</c>). This is the
/// honesty core of the HIDUSBF-style feature: the bus classifier, the polling-rate guidance (which must
/// always tell the user the true rate is unreadable without a kernel driver — never fabricate a number),
/// the mouse-acceleration advice, and the headline summary. Pinned by tests without touching WMI /
/// running processes / the registry.
/// </summary>
public static class InputTuningLogic
{
    /// <summary>Map a PNPDeviceID prefix to a human bus + whether it's wireless. Unknown/empty → generic "HID".</summary>
    public static (string Bus, bool Wireless) ClassifyBus(string pnp)
    {
        if (string.IsNullOrWhiteSpace(pnp))
            return ("HID", false);

        var id = pnp.ToUpperInvariant();
        if (id.StartsWith("USB"))         return ("USB", false);
        if (id.StartsWith("BTHLE"))       return ("Bluetooth LE", true);
        if (id.StartsWith("BTHENUM") || id.StartsWith("BTH")) return ("Bluetooth", true);
        if (id.StartsWith("ACPI"))        return ("PS/2", false);
        if (id.Contains("VEN_") && id.Contains("PS2")) return ("PS/2", false);
        if (id.StartsWith("HID"))
        {
            // HID-over-USB is the common case; HID alone tells us it's a HID-class device.
            if (id.Contains("&COL") || id.Contains("VID_"))
                return ("USB (HID)", false);
            return ("HID", false);
        }
        return ("HID", false);
    }

    /// <summary>
    /// The plain-language polling-rate / input-latency guidance. The first line is the load-bearing
    /// honesty statement (true polling rate is unreadable without a kernel driver); the second adapts to
    /// whether vendor software is running; the rest is universal, anti-cheat-aware advice.
    /// </summary>
    public static List<string> BuildGuidance(IReadOnlyList<string> detectedSoftware)
    {
        var g = new List<string>
        {
            "Le vrai polling rate USB (125 / 500 / 1000 / 4000 / 8000 Hz) ne se lit pas de façon fiable depuis Windows sans pilote noyau (c'est exactement ce qu'installe HIDUSBF)."
        };

        if (detectedSoftware.Count > 0)
            g.Add($"Logiciel constructeur détecté ({string.Join(", ", detectedSoftware)}) : règle le polling rate, le DPI et l'Angle Snapping directement là — c'est plus sûr que de forcer le pilote USB.");
        else
            g.Add("Aucun logiciel constructeur détecté en cours d'exécution. Si ta souris/clavier supporte un onboard memory, configure le polling rate via son app officielle puis enregistre le profil sur le périphérique.");

        g.Add("1000 Hz est le standard sûr et universel. 4000/8000 Hz n'apportent un gain perceptible que sur écran 240 Hz+ et augmentent l'usage CPU — à réserver aux configs très haut de gamme.");
        g.Add("Désactive l'Angle Snapping / accélération dans le logiciel souris, et garde un seul logiciel de périphérique actif à la fois (les conflits G HUB + Synapse + iCUE causent des micro-freezes).");
        g.Add("HIDUSBF (Sweetlow) reste l'outil de référence pour forcer le polling rate d'un périphérique qui ne l'expose pas — mais il installe un pilote non signé : à utiliser en connaissance de cause, hors anti-cheat strict.");
        return g;
    }

    /// <summary>Advice for the Windows pointer-acceleration ("Enhance pointer precision") state we CAN read.</summary>
    public static string MouseAccelerationText(bool on) => on
        ? "L'accélération de la souris (« Améliorer la précision du pointeur ») est ACTIVE. Pour viser de façon constante en jeu, désactive-la : Paramètres → Souris → Paramètres avancés."
        : "L'accélération de la souris est désactivée — parfait pour un visée 1:1 constante en jeu.";

    /// <summary>The one-line headline: device counts · vendor-software count · mouse-accel state.</summary>
    public static string BuildSummary(int mouseCount, int keyboardCount, int otherHidCount, int softwareCount, bool mouseAccelerationOn)
    {
        var parts = new List<string>();
        if (mouseCount > 0)
            parts.Add($"{mouseCount} souris");
        if (keyboardCount > 0)
            parts.Add($"{keyboardCount} clavier(s)");
        if (otherHidCount > 0)
            parts.Add($"{otherHidCount} autre(s) HID");

        var head = parts.Count > 0 ? string.Join(" · ", parts) : "Aucun périphérique HID détecté";
        var soft = softwareCount > 0
            ? $" · {softwareCount} logiciel(s) constructeur actif(s)"
            : "";
        var accel = mouseAccelerationOn ? " · accélération souris ON" : " · accélération souris OFF";
        return head + soft + accel;
    }
}

/// <summary>
/// HIDUSBF-style input report, honest by design. We CAN: list HID devices + how they're
/// connected (USB/Bluetooth/PS-2), detect the vendor config app that owns the real polling
/// rate (G HUB, Synapse, iCUE, SteelSeries GG…), and read/flag Windows pointer acceleration.
/// We CANNOT read the true USB polling rate without a kernel filter driver (which HIDUSBF
/// installs and we deliberately don't ship), so we guide instead of fabricating a number.
/// </summary>
public sealed class InputDeviceService : IInputDeviceService
{
    private readonly IRegistryService _registry;

    public InputDeviceService(IRegistryService registry) => _registry = registry;

    // Vendor configuration software: process name → friendly label. If it's running, the
    // real polling rate / DPI / RGB is configured there, not in Windows.
    private static readonly (string Process, string Label)[] PeripheralApps =
    {
        ("lghub_agent",        "Logitech G HUB"),
        ("LCore",              "Logitech Gaming Software (legacy)"),
        ("RazerAppEngine",     "Razer Synapse"),
        ("RzSynapse",          "Razer Synapse (legacy)"),
        ("iCUE",               "Corsair iCUE"),
        ("Corsair.Service",    "Corsair iCUE (service)"),
        ("SteelSeriesGG",      "SteelSeries GG"),
        ("SteelSeriesEngine",  "SteelSeries Engine (legacy)"),
        ("GloriousCoreHelper", "Glorious CORE"),
        ("GloriousCore",       "Glorious CORE"),
        ("Synapse3Host",       "Razer Synapse 3"),
        ("EpomakerDriver",     "Epomaker"),
        ("WootingControlCenter","Wooting"),
        ("FanatecControl",     "Fanatec"),
        ("vJoyConf",           "vJoy"),
    };

    public async Task<InputTuningReport> ScanAsync()
    {
        return await Task.Run(() =>
        {
            var report = new InputTuningReport();

            CollectPointingDevices(report);
            CollectKeyboards(report);
            DetectSoftware(report);
            ReadMouseAcceleration(report);
            report.Guidance.AddRange(InputTuningLogic.BuildGuidance(report.DetectedSoftware));

            report.MouseCount = report.Devices.Count(d => d.DeviceType == "Souris");
            report.KeyboardCount = report.Devices.Count(d => d.DeviceType == "Clavier");
            int otherHid = report.Devices.Count - report.MouseCount - report.KeyboardCount;
            report.Summary = InputTuningLogic.BuildSummary(
                report.MouseCount, report.KeyboardCount, otherHid, report.DetectedSoftware.Count, report.MouseAccelerationOn);
            return report;
        });
    }

    private static void CollectPointingDevices(InputTuningReport report)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Manufacturer, PNPDeviceID, HardwareType FROM Win32_PointingDevice");

            foreach (ManagementObject mo in searcher.Get())
            {
                var name = Clean(mo["Name"]?.ToString()) ?? Clean(mo["HardwareType"]?.ToString());
                var pnp = mo["PNPDeviceID"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    name = "Périphérique de pointage";

                var (bus, wireless) = InputTuningLogic.ClassifyBus(pnp);
                report.Devices.Add(new InputDeviceInfo
                {
                    Name = name!,
                    DeviceType = "Souris",
                    Manufacturer = Clean(mo["Manufacturer"]?.ToString()) ?? string.Empty,
                    Bus = bus,
                    IsWireless = wireless,
                    PnpDeviceId = pnp
                });
            }
        }
        catch
        {
            // WMI unavailable — keep going.
        }
    }

    private static void CollectKeyboards(InputTuningReport report)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Description, PNPDeviceID FROM Win32_Keyboard");

            foreach (ManagementObject mo in searcher.Get())
            {
                var name = Clean(mo["Description"]?.ToString()) ?? Clean(mo["Name"]?.ToString());
                var pnp = mo["PNPDeviceID"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    name = "Clavier";

                var (bus, wireless) = InputTuningLogic.ClassifyBus(pnp);
                report.Devices.Add(new InputDeviceInfo
                {
                    Name = name!,
                    DeviceType = "Clavier",
                    Manufacturer = string.Empty,
                    Bus = bus,
                    IsWireless = wireless,
                    PnpDeviceId = pnp
                });
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void DetectSoftware(InputTuningReport report)
    {
        foreach (var (proc, label) in PeripheralApps)
        {
            try
            {
                // GetProcessesByName hands back Process components that own native handles — dispose them
                // (we only need the count), the same way BenchmarkService does, so a scan never leaks.
                var procs = Process.GetProcessesByName(proc);
                try
                {
                    if (procs.Length > 0 && !report.DetectedSoftware.Contains(label))
                        report.DetectedSoftware.Add(label);
                }
                finally
                {
                    foreach (var p in procs) p.Dispose();
                }
            }
            catch
            {
                // Access denied on a process — ignore and continue.
            }
        }
    }

    private void ReadMouseAcceleration(InputTuningReport report)
    {
        // "Enhance pointer precision" = HKCU\Control Panel\Mouse\MouseSpeed != 0.
        // (Pairs with MouseThreshold1/2; MouseSpeed is the reliable on/off flag.)
        if (_registry.TryReadValue("HKCU", @"Control Panel\Mouse", "MouseSpeed", out var raw) &&
            int.TryParse(raw, out var speed))
        {
            report.MouseAccelerationOn = speed != 0;
        }
        else
        {
            report.MouseAccelerationOn = false;
        }

        report.MouseAccelerationText = InputTuningLogic.MouseAccelerationText(report.MouseAccelerationOn);
    }

    private static string? Clean(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
