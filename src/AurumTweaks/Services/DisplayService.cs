using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Services.Interop;

namespace AurumTweaks.Services;

// =====================================================================================================
//  Display (« Affichage ») — read TRUE per-monitor modes and switch the refresh rate.
//
//  Why this exists. The hardware page already reads displays through WMI Win32_VideoController, but that
//  source is per-GPU and notoriously reports the adapter's capability rather than the monitor's actual,
//  current mode. It is fine for a one-line "set Windows to max Hz" hint, but it cannot drive an honest,
//  per-monitor diagnostic and it cannot CHANGE anything. This service uses the Win32 display API
//  (EnumDisplayDevices / EnumDisplaySettings / ChangeDisplaySettingsEx) which is the same source the
//  Windows "Advanced display" settings use: real per-monitor current mode + the exact list of modes the
//  driver advertises, and a real, reversible way to apply one.
//
//  The honesty model.
//   • Detection never fabricates: an unreadable current mode is reported as such, and a monitor whose
//     mode list couldn't be enumerated yields "modes non énumérés" — never a fake "you are at max".
//   • No dead buttons: CanRaiseRefresh / RefreshOption.IsSelectable gate every control so a button only
//     appears when it would actually do something.
//   • Only the SAFE operation is offered: we change the refresh rate at the CURRENT resolution and bit
//     depth. The panel is already syncing that resolution, so raising the rate cannot black-screen it,
//     and we additionally validate the mode with CDS_TEST before committing (see Interop.DisplayApi).
//   • Success is MEASURED: after ChangeDisplaySettingsEx we re-read the live mode and report the value we
//     actually observe. A driver that silently keeps the old rate is reported as a mismatch, not success.
//   • We only ever offer modes the driver itself advertised — never a hand-built rate.
// =====================================================================================================

/// <summary>Screen rotation, as reported by DEVMODE.dmDisplayOrientation.</summary>
public enum DisplayOrientation
{
    Landscape,        // DMDO_DEFAULT (0)
    Portrait,         // DMDO_90 (1)
    LandscapeFlipped, // DMDO_180 (2)
    PortraitFlipped   // DMDO_270 (3)
}

/// <summary>The OS verdict for one ChangeDisplaySettingsEx call, mapped from the native DISP_CHANGE_* code.</summary>
public enum DisplayChangeStatus
{
    Succeeded,        // DISP_CHANGE_SUCCESSFUL
    RequiresRestart,  // DISP_CHANGE_RESTART — accepted but needs a reboot to take effect
    BadMode,          // DISP_CHANGE_BADMODE — the panel does not support this mode
    Failed,           // DISP_CHANGE_FAILED and any other non-success code
    NotAttempted      // nothing was sent to the OS (bad/empty request)
}

/// <summary>One display mode: resolution, refresh rate and colour depth.</summary>
public readonly record struct DisplayMode(int Width, int Height, int RefreshHz, int BitsPerPixel)
{
    public bool IsValid => Width > 0 && Height > 0;
    public string ResolutionLabel => $"{Width}×{Height}";
}

/// <summary>Pure refresh-rate maths over a list of advertised modes — no Win32, fully unit-testable.</summary>
public static class DisplayDiagnostics
{
    /// <summary>Highest refresh rate advertised at the given resolution, or 0 when no mode matches.</summary>
    public static int MaxRefreshAt(IEnumerable<DisplayMode> modes, int width, int height)
    {
        int max = 0;
        foreach (var m in modes)
            if (m.Width == width && m.Height == height && m.RefreshHz > max)
                max = m.RefreshHz;
        return max;
    }

    /// <summary>Distinct refresh rates advertised at the given resolution, ascending. Rates ≤ 0 are dropped.</summary>
    public static IReadOnlyList<int> RefreshRatesAt(IEnumerable<DisplayMode> modes, int width, int height)
    {
        var set = new SortedSet<int>();
        foreach (var m in modes)
            if (m.Width == width && m.Height == height && m.RefreshHz > 0)
                set.Add(m.RefreshHz);
        return set.ToList();
    }
}

/// <summary>One selectable refresh rate at a monitor's current resolution. Carries everything the apply command needs.</summary>
public readonly record struct RefreshOption(string DeviceName, int Width, int Height, int Hz, bool IsCurrent)
{
    public bool IsSelectable => !IsCurrent;
    public string Label => IsCurrent ? $"{Hz} Hz · actif" : $"{Hz} Hz";
}

/// <summary>The honest, computed state of one attached monitor. Built from the live Win32 read; all logic here is pure.</summary>
public sealed record MonitorState(
    string DeviceName,
    string FriendlyName,
    bool IsPrimary,
    bool CurrentReadable,
    DisplayMode Current,
    DisplayOrientation Orientation,
    IReadOnlyList<DisplayMode> SupportedModes)
{
    public bool HasModeList => SupportedModes.Count > 0;

    /// <summary>Best refresh rate the driver advertises at the resolution this monitor is currently running.</summary>
    public int MaxRefreshAtCurrent => DisplayDiagnostics.MaxRefreshAt(SupportedModes, Current.Width, Current.Height);

    public bool IsAtMaxRefresh => CurrentReadable && Current.RefreshHz > 0
                                  && MaxRefreshAtCurrent > 0 && Current.RefreshHz >= MaxRefreshAtCurrent;

    /// <summary>True only when a strictly-higher advertised rate exists — gates the « Passer à max » button so it is never a no-op.</summary>
    public bool CanRaiseRefresh => CurrentReadable && Current.RefreshHz > 0 && MaxRefreshAtCurrent > Current.RefreshHz;

    public IReadOnlyList<RefreshOption> RefreshOptions =>
        DisplayDiagnostics.RefreshRatesAt(SupportedModes, Current.Width, Current.Height)
            .Select(hz => new RefreshOption(DeviceName, Current.Width, Current.Height, hz, hz == Current.RefreshHz))
            .ToList();

    /// <summary>The « set to max » target as a ready-to-apply option (empty when nothing to raise).</summary>
    public RefreshOption RaiseTarget => new(DeviceName, Current.Width, Current.Height, MaxRefreshAtCurrent, false);

    public bool ShowRefreshOptions => CurrentReadable && RefreshOptions.Count > 1;

    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? DeviceName : FriendlyName;

    public string CurrentLabel => CurrentReadable && Current.IsValid
        ? $"{Current.Width}×{Current.Height} · {Current.RefreshHz} Hz · {Current.BitsPerPixel} bits"
        : "Mode courant illisible";

    public string OrientationLabel => Orientation switch
    {
        DisplayOrientation.Portrait => "Portrait (90°)",
        DisplayOrientation.LandscapeFlipped => "Paysage inversé (180°)",
        DisplayOrientation.PortraitFlipped => "Portrait inversé (270°)",
        _ => "Paysage"
    };

    public string RaiseLabel => $"Passer à {MaxRefreshAtCurrent} Hz";

    public string Verdict =>
        !CurrentReadable ? "Mode courant illisible — diagnostic indisponible."
        : MaxRefreshAtCurrent == 0 ? "Modes non énumérés — impossible de comparer à la fréquence maximale."
        : IsAtMaxRefresh ? $"✓ Fréquence maximale pour cette résolution ({MaxRefreshAtCurrent} Hz)."
        : $"{Current.RefreshHz} Hz actif alors que {MaxRefreshAtCurrent} Hz est disponible à cette résolution.";
}

/// <summary>The honest outcome of one refresh-rate change: the OS verdict plus the rate we actually measured afterwards.</summary>
public sealed record DisplayApplyOutcome(string DeviceName, int RequestedHz, int MeasuredHz, DisplayChangeStatus Status)
{
    /// <summary>Only « applied » when the OS reported success AND a fresh re-read confirms the new rate.</summary>
    public bool Verified => Status == DisplayChangeStatus.Succeeded && RequestedHz > 0 && MeasuredHz == RequestedHz;

    public string Summary => Status switch
    {
        DisplayChangeStatus.Succeeded when Verified => $"Fréquence appliquée et vérifiée : {MeasuredHz} Hz.",
        DisplayChangeStatus.Succeeded => $"Le système a accepté la demande mais l'écran tourne à {MeasuredHz} Hz (et non {RequestedHz} Hz).",
        DisplayChangeStatus.RequiresRestart => $"Changement vers {RequestedHz} Hz accepté — un redémarrage est nécessaire pour l'appliquer.",
        DisplayChangeStatus.BadMode => $"Mode {RequestedHz} Hz refusé : non pris en charge à cette résolution.",
        DisplayChangeStatus.Failed => $"Le passage à {RequestedHz} Hz a échoué (refusé par le pilote ou l'écran).",
        _ => "Aucun changement appliqué."
    };

    public static DisplayApplyOutcome NotAttempted(string device) => new(device, 0, 0, DisplayChangeStatus.NotAttempted);
}

/// <summary>Aggregate view across every attached monitor — drives the synthesis header.</summary>
public sealed record DisplayReport(IReadOnlyList<MonitorState> Monitors)
{
    public int Count => Monitors.Count;
    public bool Any => Monitors.Count > 0;

    /// <summary>How many monitors are running below the best advertised rate for their current resolution.</summary>
    public int BelowMaxCount => Monitors.Count(m => m.CanRaiseRefresh);

    /// <summary>True only when every monitor whose state we could read is confirmed at its maximum (unknowns don't count as max).</summary>
    public bool AllAtMax => Monitors.Count > 0
        && Monitors.All(m => !m.CurrentReadable || m.MaxRefreshAtCurrent == 0 || m.IsAtMaxRefresh)
        && Monitors.Any(m => m.IsAtMaxRefresh);

    public string Headline =>
        Monitors.Count == 0 ? "Aucun écran actif détecté."
        : BelowMaxCount > 0 ? $"{BelowMaxCount} écran(s) sous leur fréquence maximale — un changement est proposé."
        : AllAtMax ? "Tous les écrans tournent à leur fréquence maximale."
        : $"{Count} écran(s) détecté(s).";
}

public sealed class DisplayService : IDisplayService
{
    public Task<DisplayReport> GetReportAsync() => Task.Run(GetReport);

    private static DisplayReport GetReport()
    {
        var monitors = new List<MonitorState>();
        foreach (var nm in DisplayApi.Enumerate())
        {
            var modes = nm.Modes
                .Select(x => new DisplayMode(x.Width, x.Height, x.Frequency, x.Bpp))
                .ToList();
            var current = new DisplayMode(nm.Current.Width, nm.Current.Height, nm.Current.Frequency, nm.Current.Bpp);
            monitors.Add(new MonitorState(
                nm.DeviceName, nm.FriendlyName, nm.IsPrimary, nm.CurrentValid,
                current, MapOrientation(nm.Current.Orientation), modes));
        }
        return new DisplayReport(monitors);
    }

    public Task<DisplayApplyOutcome> SetRefreshRateAsync(string deviceName, int width, int height, int hz)
        => Task.Run(() => SetRefreshRate(deviceName, width, height, hz));

    private static DisplayApplyOutcome SetRefreshRate(string deviceName, int width, int height, int hz)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || hz <= 0 || width <= 0 || height <= 0)
            return DisplayApplyOutcome.NotAttempted(deviceName ?? string.Empty);

        // Keep the live bit depth and resolution; we only ever raise/lower the frequency (the black-screen-safe op).
        int bpp = DisplayApi.TryReadCurrent(deviceName, out var before) ? before.Bpp : 0;
        int code = DisplayApi.ChangeMode(deviceName, width, height, hz, bpp);
        var status = MapStatus(code);

        // Honesty: trust the measurement, not the return code. Report the rate Windows actually reports afterwards.
        int measured = DisplayApi.TryReadCurrent(deviceName, out var after) ? after.Frequency : 0;
        return new DisplayApplyOutcome(deviceName, hz, measured, status);
    }

    /// <summary>Pure mapping of the native DISP_CHANGE_* result to our honest status enum.</summary>
    internal static DisplayChangeStatus MapStatus(int dispChangeCode) => dispChangeCode switch
    {
        0 => DisplayChangeStatus.Succeeded,       // DISP_CHANGE_SUCCESSFUL
        1 => DisplayChangeStatus.RequiresRestart, // DISP_CHANGE_RESTART
        -2 => DisplayChangeStatus.BadMode,        // DISP_CHANGE_BADMODE
        _ => DisplayChangeStatus.Failed           // DISP_CHANGE_FAILED (-1) and the rest
    };

    /// <summary>Pure mapping of DEVMODE.dmDisplayOrientation to our orientation enum.</summary>
    internal static DisplayOrientation MapOrientation(int dmDisplayOrientation) => dmDisplayOrientation switch
    {
        1 => DisplayOrientation.Portrait,
        2 => DisplayOrientation.LandscapeFlipped,
        3 => DisplayOrientation.PortraitFlipped,
        _ => DisplayOrientation.Landscape
    };
}
