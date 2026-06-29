using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>
/// The single, scheme-validated way the UI opens an external link or a local resource. The app runs
/// elevated (<c>requireAdministrator</c>), so handing an unvalidated string to ShellExecute is an
/// elevation sink: a value that resolves to an executable or a dangerous protocol would launch as admin.
/// So we allow-list. <see cref="OpenLink"/> launches only an absolute http/https/ms-settings/mailto URI;
/// <see cref="OpenLocal"/> opens only an existing file/folder or a bare Windows console (devmgmt.msc, *.cpl)
/// that ShellExecute resolves from System32. Anything else is refused and logged — never guessed at and
/// launched anyway. The <c>IsAllowed*</c> predicates are pure so the policy is unit-tested without spawning
/// a process; this replaces four copy-pasted <c>OpenUrl</c>/<c>StartProcess</c> helpers across the ViewModels.
/// </summary>
public static class ShellLauncher
{
    private static readonly HashSet<string> AllowedLinkSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "ms-settings", "mailto" };

    /// <summary>True when <paramref name="target"/> is an absolute URI whose scheme is allow-listed and safe.</summary>
    public static bool IsAllowedLink(string? target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && AllowedLinkSchemes.Contains(uri.Scheme);

    /// <summary>
    /// True when <paramref name="target"/> is an existing file/folder, or a bare management console /
    /// control-panel applet (no path separators, ".msc"/".cpl") that ShellExecute resolves from System32.
    /// A bare "*.exe" or a relative path is intentionally refused.
    /// </summary>
    public static bool IsAllowedLocal(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (File.Exists(target) || Directory.Exists(target)) return true;
        bool hasSeparator = target.IndexOf('\\') >= 0 || target.IndexOf('/') >= 0;
        return !hasSeparator
            && (target.EndsWith(".msc", StringComparison.OrdinalIgnoreCase)
             || target.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Open a web/settings link if it passes <see cref="IsAllowedLink"/>; otherwise log and no-op.</summary>
    public static bool OpenLink(string? target)
    {
        if (!IsAllowedLink(target)) { Log.Warning("ShellLauncher refused non-allowlisted link {Target}", target); return false; }
        return Start(target!);
    }

    /// <summary>Open a local file/folder/console if it passes <see cref="IsAllowedLocal"/>; otherwise log and no-op.</summary>
    public static bool OpenLocal(string? target)
    {
        if (!IsAllowedLocal(target)) { Log.Warning("ShellLauncher refused non-allowlisted local target {Target}", target); return false; }
        return Start(target!);
    }

    private static bool Start(string target)
    {
        try
        {
            // UseShellExecute hands back the shell-spawned process (browser/explorer) or null when the
            // handler is reused; dispose our handle either way (matches the other Process.Start sites) —
            // it releases only our reference, never closes the opened app.
            using var p = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ShellLauncher failed to open {Target}", target);
            return false;
        }
    }
}
