using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AurumTweaks.Services.Interop;

/// <summary>
/// Minimal, honest Win32 display interop: enumerate true per-monitor modes and switch the refresh rate.
///
/// <para>This wraps the same user-mode APIs the Windows "Advanced display settings" page uses —
/// <c>EnumDisplayDevices</c>, <c>EnumDisplaySettings</c> and <c>ChangeDisplaySettingsEx</c>. There is no
/// kernel driver and no vBIOS write: <c>EnumDisplaySettings</c> only reports the modes the graphics driver
/// already advertises, and <c>ChangeDisplaySettingsEx</c> only applies one of them.</para>
///
/// <para><b>Safety model.</b> <see cref="ChangeMode"/> changes only the refresh rate at the monitor's CURRENT
/// resolution and bit depth — the panel is already syncing that resolution, so the change cannot leave it
/// unable to display. We additionally call <c>ChangeDisplaySettingsEx</c> with <c>CDS_TEST</c> first and bail
/// out if the OS says the mode is unsupported, so an invalid mode is never committed. The change is persisted
/// to the registry and survives a reboot; it is reversed by selecting another advertised rate. On a machine
/// where the API is unavailable every call simply reports no monitors / a failure code instead of throwing.</para>
/// </summary>
internal static class DisplayApi
{
    // ---- Raw value types handed back to the service (no domain coupling). ----

    /// <summary>A single mode exactly as the driver reports it.</summary>
    internal readonly record struct NativeMode(int Width, int Height, int Frequency, int Bpp, int Orientation);

    /// <summary>One attached monitor: its device path, friendly name, primary flag, current mode and advertised modes.</summary>
    internal sealed class NativeMonitor
    {
        public string DeviceName = string.Empty;   // \\.\DISPLAY1
        public string FriendlyName = string.Empty; // monitor DeviceString, e.g. "Generic PnP Monitor"
        public bool IsPrimary;
        public bool CurrentValid;
        public NativeMode Current;
        public List<NativeMode> Modes = new();
    }

    // ---- Native structures ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;          // POINTL.x (display devices use the position union)
        public int dmPositionY;          // POINTL.y
        public int dmDisplayOrientation; // DMDO_DEFAULT/90/180/270
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwFlags, IntPtr lParam);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;

    private const uint CDS_UPDATEREGISTRY = 0x01;
    private const uint CDS_TEST = 0x02;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_FAILED = -1;

    private const int DM_BITSPERPEL = 0x40000;
    private const int DM_PELSWIDTH = 0x80000;
    private const int DM_PELSHEIGHT = 0x100000;
    private const int DM_DISPLAYFREQUENCY = 0x400000;

    private static DEVMODE NewDevmode() => new()
    {
        dmDeviceName = string.Empty,
        dmFormName = string.Empty,
        dmSize = (short)Marshal.SizeOf<DEVMODE>()
    };

    /// <summary>Enumerate every desktop-attached, non-mirroring monitor with its current and advertised modes.</summary>
    internal static List<NativeMonitor> Enumerate()
    {
        var result = new List<NativeMonitor>();
        try
        {
            var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++, adapter.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
            {
                bool attached = (adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
                bool mirroring = (adapter.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0;
                if (!attached || mirroring) continue;

                var mon = new NativeMonitor
                {
                    DeviceName = adapter.DeviceName,
                    IsPrimary = (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                    FriendlyName = MonitorName(adapter.DeviceName)
                };

                var cur = NewDevmode();
                if (EnumDisplaySettings(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref cur))
                {
                    mon.CurrentValid = true;
                    mon.Current = new NativeMode(cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, cur.dmBitsPerPel, cur.dmDisplayOrientation);
                }

                // Collapse duplicate (w,h,hz,bpp) tuples the driver lists for different scan types.
                var seen = new HashSet<(int, int, int, int)>();
                var probe = NewDevmode();
                for (int m = 0; EnumDisplaySettings(adapter.DeviceName, m, ref probe); m++, probe = NewDevmode())
                {
                    if (probe.dmDisplayFrequency <= 1) continue; // 0/1 mean "driver default", not a real rate
                    var key = (probe.dmPelsWidth, probe.dmPelsHeight, probe.dmDisplayFrequency, probe.dmBitsPerPel);
                    if (seen.Add(key))
                        mon.Modes.Add(new NativeMode(probe.dmPelsWidth, probe.dmPelsHeight, probe.dmDisplayFrequency, probe.dmBitsPerPel, probe.dmDisplayOrientation));
                }

                result.Add(mon);
            }
        }
        catch { /* display enumeration unavailable on this session */ }
        return result;
    }

    /// <summary>Read the monitor's live mode (ENUM_CURRENT_SETTINGS) — used both for display and to MEASURE a change.</summary>
    internal static bool TryReadCurrent(string deviceName, out NativeMode mode)
    {
        try
        {
            var dm = NewDevmode();
            if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
            {
                mode = new NativeMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, dm.dmBitsPerPel, dm.dmDisplayOrientation);
                return true;
            }
        }
        catch { /* fall through */ }
        mode = default;
        return false;
    }

    /// <summary>
    /// Change the refresh rate at the given resolution. Validates with CDS_TEST first so an unsupported mode
    /// is never committed, then persists with CDS_UPDATEREGISTRY. Returns the raw DISP_CHANGE_* code.
    /// </summary>
    internal static int ChangeMode(string deviceName, int width, int height, int hz, int bpp)
    {
        try
        {
            var dm = NewDevmode();
            if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
                return DISP_CHANGE_FAILED;

            dm.dmPelsWidth = width;
            dm.dmPelsHeight = height;
            dm.dmDisplayFrequency = hz;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            if (bpp > 0)
            {
                dm.dmBitsPerPel = bpp;
                dm.dmFields |= DM_BITSPERPEL;
            }

            int test = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (test != DISP_CHANGE_SUCCESSFUL) return test;

            return ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        }
        catch
        {
            return DISP_CHANGE_FAILED;
        }
    }

    /// <summary>Resolve the friendly monitor name (the adapter's child device DeviceString).</summary>
    private static string MonitorName(string adapterDeviceName)
    {
        try
        {
            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(adapterDeviceName, 0, ref mon, 0))
                return mon.DeviceString?.Trim() ?? string.Empty;
        }
        catch { /* fall through */ }
        return string.Empty;
    }
}
