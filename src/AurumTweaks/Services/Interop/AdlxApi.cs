using System;
using System.Runtime.InteropServices;

namespace AurumTweaks.Services.Interop;

/// <summary>Read-only identity + capability snapshot of the first AMD GPU, as reported by ADLX itself.</summary>
public sealed record AdlxGpuInfo(
    string Name,
    bool IsIntegrated,
    bool ManualPowerTuningSupported,
    bool ManualGfxTuningSupported,
    bool ManualVramTuningSupported);

/// <summary>
/// Minimal, honest ADLX interop for AMD GPU power-limit tuning.
///
/// <para>ADLX (<c>amdadlx64.dll</c>) is AMD's <b>official, documented</b> GPU-tuning SDK — it ships
/// with every Adrenalin driver and runs entirely in user mode. Unlike the NVIDIA power/thermal
/// policies (community-documented), every call used here is documented by AMD, and support is gated
/// by ADLX's own <c>IsSupportedManualPowerTuning</c> per-GPU flag — never guessed from a GPU name.</para>
///
/// <para><b>Provenance.</b> Export names, vtable slot indices, IID strings and units were extracted
/// verbatim from the official SDK headers (GPUOpen-LibrariesAndSDKs/ADLX, branch main, 2026-07-20):
/// entry points are cdecl; vtable methods are stdcall; <c>adlx_bool</c> is 1 byte; power-limit values
/// are on the same scale Adrenalin shows (the driver's own <c>GetPowerLimitRange</c> window — Aurum
/// round-trips values inside that window and never re-interprets the scale). ADLX object ABI:
/// interface pointer → vtable pointer → function-pointer slots; <c>IADLXSystem</c> is a non-refcounted
/// singleton, every other interface used here is refcounted (exactly one Release per received pointer,
/// one extra after QueryInterface).</para>
///
/// <para><b>Honesty contract.</b> Every step checks ADLX_SUCCEEDED; any failure degrades to an honest
/// "unavailable" instead of a fabricated value, and the write path reports success ONLY after a
/// read-back equals the requested value. On a non-AMD machine the DLL is absent and everything
/// reports unavailable instead of throwing. All calls are serialized behind one lock (ADLX headers
/// are silent on thread affinity).</para>
/// </summary>
internal static class AdlxApi
{
    // ---- ADLX_RESULT (ADLXDefines.h, sequential from 0) --------------------------------------
    private const int ADLX_OK = 0;
    private const int ADLX_RESET_NEEDED = 18;      // manual set refused while one-click auto-tuning is active
    private static bool Succeeded(int r) => r is >= 0 and <= 2;   // OK / ALREADY_ENABLED / ALREADY_INITIALIZED

    // ---- version passed to ADLXInitialize: ADLX_MAKE_FULL_VER(1, 5, 0, 124) -------------------
    private const ulong FULL_VERSION = ((ulong)1 << 48) | ((ulong)5 << 32) | ((ulong)0 << 16) | 124;

    // ---- vtable slot indices, pinned from the C-section Vtbl structs of the official headers ---
    private const int SYS_GETGPUS = 1;             // IADLXSystem::GetGPUs
    private const int SYS_GETGPUTUNINGSERVICES = 8;

    private const int IFACE_RELEASE = 1;           // slot 1 on every refcounted ADLX interface
    private const int IFACE_QUERYINTERFACE = 2;

    private const int LIST_SIZE = 3;               // IADLXGPUList (IADLXList base)
    private const int LIST_AT_GPULIST = 11;        // typed At

    private const int GPU_TYPE = 5;                // ADLX_GPU_TYPE: 0 undefined, 1 integrated, 2 discrete
    private const int GPU_NAME = 7;

    private const int TUN_IS_MANUAL_GFX = 8;       // IADLXGPUTuningServices::IsSupportedManualGFXTuning
    private const int TUN_IS_MANUAL_VRAM = 9;
    private const int TUN_IS_MANUAL_POWER = 11;
    private const int TUN_GET_MANUAL_GFX = 14;     // returns generic IADLXInterface**
    private const int TUN_GET_MANUAL_VRAM = 15;
    private const int TUN_GET_MANUAL_POWER = 17;

    private const int PWR_GET_RANGE = 3;           // IADLXManualPowerTuning
    private const int PWR_GET = 4;
    private const int PWR_SET = 5;

    // IADLXManualGraphicsTuning2 (RDNA+ min/max/voltage model). We drive the GPU MAX frequency only —
    // the OC-relevant "core clock" axis. Min frequency and voltage (slots 3/4/5 and 9/10/11) are
    // deliberately left to Adrenalin (voltage is NEVER applied by Aurum, per the honesty mandate).
    private const int GFX2_GET_MAXFREQ_RANGE = 6;
    private const int GFX2_GET_MAXFREQ = 7;
    private const int GFX2_SET_MAXFREQ = 8;

    // IADLXManualVRAMTuning2 (RDNA+ memory model). We drive the MAX memory frequency only; the memory
    // timing-level selector (slots 3-6) is left to Adrenalin.
    private const int VRAM2_GET_MAXFREQ_RANGE = 7;
    private const int VRAM2_GET_MAXFREQ = 8;
    private const int VRAM2_SET_MAXFREQ = 9;

    private const string POWER_TUNING_IID = "IADLXManualPowerTuning";        // wide string in ADLX
    private const string GFX_TUNING2_IID = "IADLXManualGraphicsTuning2";     // RDNA+ manual GFX interface
    private const string VRAM_TUNING2_IID = "IADLXManualVRAMTuning2";        // RDNA+ manual VRAM interface

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlxIntRange
    {
        public int MinValue;
        public int MaxValue;
        public int Step;
    }

    // ---- delegate shapes (vtable methods are ADLX_STD_CALL) -----------------------------------
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate(ulong version, out IntPtr ppSystem);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutPtrDelegate(IntPtr pThis, out IntPtr ppOut);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr pThis);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr pThis, [MarshalAs(UnmanagedType.LPWStr)] string interfaceId, out IntPtr ppOut);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint SizeDelegate(IntPtr pThis);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AtGpuListDelegate(IntPtr pThis, uint location, out IntPtr ppGpu);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutIntDelegate(IntPtr pThis, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutAnsiDelegate(IntPtr pThis, out IntPtr ppAnsi);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GpuSupportDelegate(IntPtr pThis, IntPtr pGpu, out byte supported);   // adlx_bool = 1 byte
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GpuOutPtrDelegate(IntPtr pThis, IntPtr pGpu, out IntPtr ppOut);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutRangeDelegate(IntPtr pThis, out AdlxIntRange range);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetIntDelegate(IntPtr pThis, int value);

    private static readonly object Sync = new();

    private static bool _initTried;
    private static IntPtr _system = IntPtr.Zero;       // non-refcounted singleton — never released
    private static IntPtr _gpu = IntPtr.Zero;          // first GPU: one ref held for the app's lifetime
    private static IntPtr _tuning = IntPtr.Zero;       // tuning services: same lifetime
    private static AdlxGpuInfo? _info;

    /// <summary>Dispatch a vtable slot: pInterface → pVtbl → pVtbl[slot].</summary>
    private static TDelegate Fn<TDelegate>(IntPtr iface, int slot) where TDelegate : Delegate
    {
        IntPtr vtbl = Marshal.ReadIntPtr(iface);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(fn);
    }

    private static void Release(IntPtr iface)
    {
        if (iface != IntPtr.Zero)
            Fn<ReleaseDelegate>(iface, IFACE_RELEASE)(iface);
    }

    /// <summary>Lazily load amdadlx64.dll and initialize the ADLX system singleton. Never throws.</summary>
    private static bool TryInitialize()
    {
        if (_initTried) return _system != IntPtr.Zero;
        _initTried = true;
        try
        {
            // Default .NET search includes System32, where the driver installs the DLL.
            if (!NativeLibrary.TryLoad("amdadlx64.dll", out IntPtr lib)) return false;
            if (!NativeLibrary.TryGetExport(lib, "ADLXInitialize", out IntPtr initPtr)) return false;

            var init = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(initPtr);
            if (!Succeeded(init(FULL_VERSION, out IntPtr system)) || system == IntPtr.Zero) return false;

            _system = system;
            return true;
        }
        catch
        {
            // Absent/too-old driver or any interop failure → honestly unavailable.
            return false;
        }
    }

    /// <summary>
    /// Enumerate the first AMD GPU and read its identity + tuning-capability flags (read-only).
    /// The GPU handle and tuning-services interface are cached for the app's lifetime.
    /// </summary>
    public static bool TryGetFirstGpuInfo(out AdlxGpuInfo info)
    {
        lock (Sync)
        {
            info = _info ?? new AdlxGpuInfo(string.Empty, false, false, false, false);
            if (_info is not null) return true;
            if (!TryInitialize()) return false;

            IntPtr list = IntPtr.Zero;
            IntPtr gpu = IntPtr.Zero;
            IntPtr tuning = IntPtr.Zero;
            try
            {
                if (!Succeeded(Fn<OutPtrDelegate>(_system, SYS_GETGPUS)(_system, out list)) || list == IntPtr.Zero)
                    return false;
                if (Fn<SizeDelegate>(list, LIST_SIZE)(list) == 0)
                    return false;
                if (!Succeeded(Fn<AtGpuListDelegate>(list, LIST_AT_GPULIST)(list, 0, out gpu)) || gpu == IntPtr.Zero)
                    return false;

                string name = string.Empty;
                if (Fn<OutAnsiDelegate>(gpu, GPU_NAME)(gpu, out IntPtr pName) == ADLX_OK && pName != IntPtr.Zero)
                    name = Marshal.PtrToStringAnsi(pName) ?? string.Empty;   // ADLX-owned buffer: copy, never free

                bool integrated = Fn<OutIntDelegate>(gpu, GPU_TYPE)(gpu, out int gpuType) == ADLX_OK && gpuType == 1;

                if (!Succeeded(Fn<OutPtrDelegate>(_system, SYS_GETGPUTUNINGSERVICES)(_system, out tuning)) || tuning == IntPtr.Zero)
                    return false;

                bool powerOk = Fn<GpuSupportDelegate>(tuning, TUN_IS_MANUAL_POWER)(tuning, gpu, out byte p) == ADLX_OK && p != 0;
                bool gfxOk = Fn<GpuSupportDelegate>(tuning, TUN_IS_MANUAL_GFX)(tuning, gpu, out byte g) == ADLX_OK && g != 0;
                bool vramOk = Fn<GpuSupportDelegate>(tuning, TUN_IS_MANUAL_VRAM)(tuning, gpu, out byte v) == ADLX_OK && v != 0;

                _gpu = gpu;
                _tuning = tuning;
                gpu = IntPtr.Zero;       // ownership transferred to the cached fields —
                tuning = IntPtr.Zero;    // the finally block must not release them.
                _info = new AdlxGpuInfo(name, integrated, powerOk, gfxOk, vramOk);
                info = _info;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(gpu);
                Release(tuning);
                Release(list);           // the list is only needed to fetch the GPU handle
            }
        }
    }

    /// <summary>Acquire the IADLXManualPowerTuning interface (generic Get + QueryInterface). Both refs are owned by the caller.</summary>
    private static bool TryGetPowerInterface(out IntPtr generic, out IntPtr power)
    {
        generic = IntPtr.Zero;
        power = IntPtr.Zero;
        if (_tuning == IntPtr.Zero || _gpu == IntPtr.Zero) return false;
        if (!Succeeded(Fn<GpuOutPtrDelegate>(_tuning, TUN_GET_MANUAL_POWER)(_tuning, _gpu, out generic)) || generic == IntPtr.Zero)
            return false;
        if (!Succeeded(Fn<QueryInterfaceDelegate>(generic, IFACE_QUERYINTERFACE)(generic, POWER_TUNING_IID, out power)) || power == IntPtr.Zero)
        {
            Release(generic);
            generic = IntPtr.Zero;
            return false;
        }
        return true;
    }

    /// <summary>Read the driver's power-limit window and current value (all on Adrenalin's own scale). Read-only.</summary>
    public static bool TryReadPowerLimit(out int currentPct, out int minPct, out int maxPct, out int stepPct)
    {
        currentPct = minPct = maxPct = stepPct = 0;
        lock (Sync)
        {
            if (!TryGetFirstGpuInfo(out var info) || !info.ManualPowerTuningSupported) return false;

            IntPtr generic = IntPtr.Zero, power = IntPtr.Zero;
            try
            {
                if (!TryGetPowerInterface(out generic, out power)) return false;
                if (Fn<OutRangeDelegate>(power, PWR_GET_RANGE)(power, out AdlxIntRange r) != ADLX_OK) return false;
                if (Fn<OutIntDelegate>(power, PWR_GET)(power, out int cur) != ADLX_OK) return false;

                minPct = r.MinValue;
                maxPct = r.MaxValue;
                stepPct = r.Step;
                currentPct = cur;
                // Official API, but the coherence gate still applies: garbage is never exposed.
                return AdlxPowerRange.IsCoherent(minPct, maxPct, stepPct, currentPct);
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(power);
                Release(generic);
            }
        }
    }

    /// <summary>
    /// Set the power limit (on the driver's own scale). Same honesty contract as the NVIDIA writes:
    /// success is reported ONLY when an immediate read-back equals the requested value.
    /// </summary>
    public static bool TrySetPowerLimit(int targetPct, out string error)
    {
        error = string.Empty;
        lock (Sync)
        {
            if (!TryGetFirstGpuInfo(out var info) || !info.ManualPowerTuningSupported)
            {
                error = "ADLX power limit indisponible sur ce GPU.";
                return false;
            }

            IntPtr generic = IntPtr.Zero, power = IntPtr.Zero;
            try
            {
                if (!TryGetPowerInterface(out generic, out power))
                {
                    error = "Interface ADLX power tuning inaccessible.";
                    return false;
                }

                int status = Fn<SetIntDelegate>(power, PWR_SET)(power, targetPct);
                if (status == ADLX_RESET_NEEDED)
                {
                    // Documented ADLX behavior: manual writes are refused while one-click auto-tuning is
                    // active. Aurum never silently calls ResetToFactory (it would clobber all tuning state).
                    error = "Écriture refusée : l'auto-tuning AMD est actif — désactive-le dans Adrenalin d'abord.";
                    return false;
                }
                if (!Succeeded(status))
                {
                    error = $"ADLX a refusé l'écriture du power limit (code {status}).";
                    return false;
                }

                if (Fn<OutIntDelegate>(power, PWR_GET)(power, out int readBack) != ADLX_OK)
                {
                    error = "Écriture envoyée mais relecture impossible — état non confirmé.";
                    return false;
                }
                if (readBack != targetPct)
                {
                    error = $"Écriture non confirmée : le driver rapporte {readBack} au lieu de {targetPct}.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                Release(power);
                Release(generic);
            }
        }
    }

    /// <summary>Acquire IADLXManualGraphicsTuning2 (RDNA+ model) for the first GPU. Both refs are owned by
    /// the caller. Fails (returns false) on older cards that only expose the state-list Tuning1 model —
    /// that write path needs a state-list interface Aurum does not integrate, so it stays honestly
    /// "Adrenalin only" rather than half-implemented.</summary>
    private static bool TryGetGfxInterface(out IntPtr generic, out IntPtr gfx)
    {
        generic = IntPtr.Zero;
        gfx = IntPtr.Zero;
        if (_tuning == IntPtr.Zero || _gpu == IntPtr.Zero) return false;
        if (!Succeeded(Fn<GpuOutPtrDelegate>(_tuning, TUN_GET_MANUAL_GFX)(_tuning, _gpu, out generic)) || generic == IntPtr.Zero)
            return false;
        if (!Succeeded(Fn<QueryInterfaceDelegate>(generic, IFACE_QUERYINTERFACE)(generic, GFX_TUNING2_IID, out gfx)) || gfx == IntPtr.Zero)
        {
            Release(generic);
            generic = IntPtr.Zero;
            return false;
        }
        return true;
    }

    /// <summary>Read the driver's GPU max-frequency window and current value (MHz — absolute pre-Navi4,
    /// an offset from base on Navi4+; Aurum round-trips inside the driver's own window either way). Read-only.</summary>
    public static bool TryReadGfxMaxFreq(out int currentMhz, out int minMhz, out int maxMhz, out int stepMhz)
    {
        currentMhz = minMhz = maxMhz = stepMhz = 0;
        lock (Sync)
        {
            if (!TryGetFirstGpuInfo(out var info) || !info.ManualGfxTuningSupported) return false;

            IntPtr generic = IntPtr.Zero, gfx = IntPtr.Zero;
            try
            {
                if (!TryGetGfxInterface(out generic, out gfx)) return false;
                if (Fn<OutRangeDelegate>(gfx, GFX2_GET_MAXFREQ_RANGE)(gfx, out AdlxIntRange r) != ADLX_OK) return false;
                if (Fn<OutIntDelegate>(gfx, GFX2_GET_MAXFREQ)(gfx, out int cur) != ADLX_OK) return false;

                minMhz = r.MinValue;
                maxMhz = r.MaxValue;
                stepMhz = r.Step;
                currentMhz = cur;
                return GpuGfxRange.IsCoherent(minMhz, maxMhz, stepMhz, currentMhz);
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(gfx);
                Release(generic);
            }
        }
    }

    /// <summary>Set the GPU max frequency (driver scale). Same honesty contract as every other write:
    /// success is reported ONLY when an immediate read-back equals the requested value.</summary>
    public static bool TrySetGfxMaxFreq(int targetMhz, out string error)
    {
        error = string.Empty;
        lock (Sync)
        {
            if (!TryGetFirstGpuInfo(out var info) || !info.ManualGfxTuningSupported)
            {
                error = "ADLX GFX tuning indisponible sur ce GPU.";
                return false;
            }

            IntPtr generic = IntPtr.Zero, gfx = IntPtr.Zero;
            try
            {
                if (!TryGetGfxInterface(out generic, out gfx))
                {
                    error = "Interface ADLX GFX tuning (RDNA+) inaccessible — GPU trop ancien (modèle state-list non intégré).";
                    return false;
                }

                int status = Fn<SetIntDelegate>(gfx, GFX2_SET_MAXFREQ)(gfx, targetMhz);
                if (status == ADLX_RESET_NEEDED)
                {
                    error = "Écriture refusée : l'auto-tuning AMD est actif — désactive-le dans Adrenalin d'abord.";
                    return false;
                }
                if (!Succeeded(status))
                {
                    error = $"ADLX a refusé l'écriture de la fréquence max (code {status}).";
                    return false;
                }

                if (Fn<OutIntDelegate>(gfx, GFX2_GET_MAXFREQ)(gfx, out int readBack) != ADLX_OK)
                {
                    error = "Écriture envoyée mais relecture impossible — état non confirmé.";
                    return false;
                }
                if (readBack != targetMhz)
                {
                    error = $"Écriture non confirmée : le driver rapporte {readBack} au lieu de {targetMhz}.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                Release(gfx);
                Release(generic);
            }
        }
    }

    /// <summary>Acquire IADLXManualVRAMTuning2 (RDNA+ memory model) for the first GPU. Both refs owned by
    /// the caller. Fails on older state-list VRAM cards (Tuning1) — that write path is not integrated.</summary>
    private static bool TryGetVramInterface(out IntPtr generic, out IntPtr vram)
    {
        generic = IntPtr.Zero;
        vram = IntPtr.Zero;
        if (_tuning == IntPtr.Zero || _gpu == IntPtr.Zero) return false;
        if (!Succeeded(Fn<GpuOutPtrDelegate>(_tuning, TUN_GET_MANUAL_VRAM)(_tuning, _gpu, out generic)) || generic == IntPtr.Zero)
            return false;
        if (!Succeeded(Fn<QueryInterfaceDelegate>(generic, IFACE_QUERYINTERFACE)(generic, VRAM_TUNING2_IID, out vram)) || vram == IntPtr.Zero)
        {
            Release(generic);
            generic = IntPtr.Zero;
            return false;
        }
        return true;
    }

    /// <summary>Read the driver's max memory-frequency window and current value (MHz, driver scale). Read-only.</summary>
    public static bool TryReadVramMaxFreq(out int currentMhz, out int minMhz, out int maxMhz, out int stepMhz)
    {
        currentMhz = minMhz = maxMhz = stepMhz = 0;
        lock (Sync)
        {
            if (!TryGetFirstGpuInfo(out var info) || !info.ManualVramTuningSupported) return false;

            IntPtr generic = IntPtr.Zero, vram = IntPtr.Zero;
            try
            {
                if (!TryGetVramInterface(out generic, out vram)) return false;
                if (Fn<OutRangeDelegate>(vram, VRAM2_GET_MAXFREQ_RANGE)(vram, out AdlxIntRange r) != ADLX_OK) return false;
                if (Fn<OutIntDelegate>(vram, VRAM2_GET_MAXFREQ)(vram, out int cur) != ADLX_OK) return false;

                minMhz = r.MinValue;
                maxMhz = r.MaxValue;
                stepMhz = r.Step;
                currentMhz = cur;
                return GpuGfxRange.IsCoherent(minMhz, maxMhz, stepMhz, currentMhz);
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(vram);
                Release(generic);
            }
        }
    }

    /// <summary>Set the max memory frequency (driver scale). Success reported ONLY when an immediate read-back equals the request.</summary>
    public static bool TrySetVramMaxFreq(int targetMhz, out string error)
    {
        error = string.Empty;
        lock (Sync)
        {
            if (!TryGetFirstGpuInfo(out var info) || !info.ManualVramTuningSupported)
            {
                error = "ADLX VRAM tuning indisponible sur ce GPU.";
                return false;
            }

            IntPtr generic = IntPtr.Zero, vram = IntPtr.Zero;
            try
            {
                if (!TryGetVramInterface(out generic, out vram))
                {
                    error = "Interface ADLX VRAM tuning (RDNA+) inaccessible — GPU trop ancien (modèle state-list non intégré).";
                    return false;
                }

                int status = Fn<SetIntDelegate>(vram, VRAM2_SET_MAXFREQ)(vram, targetMhz);
                if (status == ADLX_RESET_NEEDED)
                {
                    error = "Écriture refusée : l'auto-tuning AMD est actif — désactive-le dans Adrenalin d'abord.";
                    return false;
                }
                if (!Succeeded(status))
                {
                    error = $"ADLX a refusé l'écriture de la fréquence mémoire (code {status}).";
                    return false;
                }

                if (Fn<OutIntDelegate>(vram, VRAM2_GET_MAXFREQ)(vram, out int readBack) != ADLX_OK)
                {
                    error = "Écriture envoyée mais relecture impossible — état non confirmé.";
                    return false;
                }
                if (readBack != targetMhz)
                {
                    error = $"Écriture non confirmée : le driver rapporte {readBack} au lieu de {targetMhz}.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                Release(vram);
                Release(generic);
            }
        }
    }
}

/// <summary>
/// Injectable seam over the static <see cref="AdlxApi"/>, so <c>GpuOcService</c>'s AMD orchestration
/// (power/gfx/vram apply, partial-failure honesty, the null-on-partial-read decision) is unit-testable
/// with a fake instead of a real tunable AMD card. Delegates verbatim — <see cref="AdlxApi"/> already
/// serializes every call behind its own lock, so no extra synchronization is added here.
/// </summary>
public interface IAdlxApi
{
    bool TryGetFirstGpuInfo(out AdlxGpuInfo info);
    bool TryReadPowerLimit(out int currentPct, out int minPct, out int maxPct, out int stepPct);
    bool TrySetPowerLimit(int targetPct, out string error);
    bool TryReadGfxMaxFreq(out int currentMhz, out int minMhz, out int maxMhz, out int stepMhz);
    bool TrySetGfxMaxFreq(int targetMhz, out string error);
    bool TryReadVramMaxFreq(out int currentMhz, out int minMhz, out int maxMhz, out int stepMhz);
    bool TrySetVramMaxFreq(int targetMhz, out string error);
}

/// <summary>Production <see cref="IAdlxApi"/>: thin pass-through to the static ADLX wrapper.</summary>
public sealed class AdlxBackend : IAdlxApi
{
    public bool TryGetFirstGpuInfo(out AdlxGpuInfo info) => AdlxApi.TryGetFirstGpuInfo(out info);

    public bool TryReadPowerLimit(out int currentPct, out int minPct, out int maxPct, out int stepPct)
        => AdlxApi.TryReadPowerLimit(out currentPct, out minPct, out maxPct, out stepPct);

    public bool TrySetPowerLimit(int targetPct, out string error) => AdlxApi.TrySetPowerLimit(targetPct, out error);

    public bool TryReadGfxMaxFreq(out int currentMhz, out int minMhz, out int maxMhz, out int stepMhz)
        => AdlxApi.TryReadGfxMaxFreq(out currentMhz, out minMhz, out maxMhz, out stepMhz);

    public bool TrySetGfxMaxFreq(int targetMhz, out string error) => AdlxApi.TrySetGfxMaxFreq(targetMhz, out error);

    public bool TryReadVramMaxFreq(out int currentMhz, out int minMhz, out int maxMhz, out int stepMhz)
        => AdlxApi.TryReadVramMaxFreq(out currentMhz, out minMhz, out maxMhz, out stepMhz);

    public bool TrySetVramMaxFreq(int targetMhz, out string error) => AdlxApi.TrySetVramMaxFreq(targetMhz, out error);
}
