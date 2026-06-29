using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using Microsoft.Win32;

namespace AurumTweaks.Services;

/// <summary>
/// The service start-type vocabulary shared by the engine's two halves so they can never silently disagree:
/// the apply path (<see cref="ServiceManagerService.SetStartupType"/>) writes one of these, and the detection
/// path (<see cref="ServiceManagerService.TryGetStartupType"/>) reads the live value BACK as the same string —
/// so on-system state detection round-trips. A catalog <c>startupApply</c>/<c>startupRevert</c> MUST be
/// canonical: the lenient alias <c>"auto"</c> applies fine but reads back as <c>"Automatic"</c>, which would make
/// the tweak detect as not-applied and leave the "✓ Appliqué" badge dark on a service that's genuinely live —
/// a silent false negative. Pinned by <c>TweakCatalogIntegrityTests.EveryServiceOp_UsesACanonicalStartupType…</c>.
/// </summary>
public static class ServiceStartup
{
    /// <summary>Targets that both apply cleanly AND read back identically (case-insensitively). Mirrors the
    /// names <see cref="ServiceManagerService.TryGetStartupType"/> emits, minus "Unknown" (a read-only
    /// sentinel for an unexpected Start value — never a valid target to set).</summary>
    public static readonly IReadOnlyList<string> Canonical = new[]
    {
        "Boot", "System", "Automatic", "DelayedAuto", "Manual", "Disabled"
    };

    /// <summary>True when <paramref name="startupType"/> is a canonical, round-trippable target (case-insensitive).</summary>
    public static bool IsCanonical(string? startupType) =>
        startupType is not null && Canonical.Contains(startupType, StringComparer.OrdinalIgnoreCase);
}

public sealed class ServiceManagerService : IServiceManagerService
{
    private const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services";

    public bool TryGetStartupType(string serviceName, out string? startupType)
    {
        startupType = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}");
            if (key is null) return false;
            var raw = key.GetValue("Start") as int?;
            var delayed = (key.GetValue("DelayedAutostart") as int?) == 1;
            startupType = raw switch
            {
                0 => "Boot",
                1 => "System",
                2 => delayed ? "DelayedAuto" : "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown"
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetStartupType(string serviceName, string startupType)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}", writable: true);
            if (key is null) return false;

            int value = startupType.ToLowerInvariant() switch
            {
                "boot" => 0,
                "system" => 1,
                "automatic" or "auto" => 2,
                "delayedauto" => 2,
                "manual" => 3,
                "disabled" => 4,
                _ => 3
            };
            key.SetValue("Start", value, RegistryValueKind.DWord);
            key.SetValue("DelayedAutostart",
                startupType.Equals("DelayedAuto", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool StopService(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status == ServiceControllerStatus.Running ||
                sc.Status == ServiceControllerStatus.StartPending)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
