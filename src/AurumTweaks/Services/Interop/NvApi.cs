using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AurumTweaks.Services.Interop;

/// <summary>
/// Minimal, honest NVAPI interop for GPU clock-offset overclocking.
///
/// <para>NVAPI (<c>nvapi64.dll</c>) ships with every NVIDIA driver and runs entirely in user
/// mode — no kernel driver, no vBIOS modification, no voltage unlock. This wrapper exposes only
/// what every overclocking tool uses: the core/memory clock <b>offsets</b> (deltas) of
/// performance state P0, read and written through <c>NvAPI_GPU_Get/SetPstates20</c>.</para>
///
/// <para><b>Safety model.</b> Every NVAPI struct is versioned as <c>(sizeof | version&lt;&lt;16)</c>.
/// We build a buffer of the exact native size and write only the handful of fields we need into
/// otherwise-zeroed memory. If that size were ever wrong, the driver returns
/// <c>NVAPI_INCOMPATIBLE_STRUCT_VERSION</c> and does nothing — a bad layout fails safe; it never
/// writes garbage. Offsets are frequency deltas only: the driver clamps them to the card's allowed
/// V/F range and they reset on reboot (non-persistent), so the worst realistic case of a bad value
/// is a recoverable driver reset, never hardware damage. On a non-NVIDIA machine the DLL is absent
/// and every call reports "unavailable" instead of throwing.</para>
/// </summary>
internal static class NvApi
{
    // NVAPI exposes a single export; every function is reached by its stable 32-bit id.
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NvApi_QueryInterface(uint id);

    // Function ids — identical magic numbers across every public NVAPI wrapper.
    private const uint ID_Initialize       = 0x0150E828;
    private const uint ID_EnumPhysicalGPUs = 0xE5AC921F;
    private const uint ID_GPU_GetFullName  = 0xCEEE8E9F;
    private const uint ID_GPU_GetPstates20 = 0x6FF81213;
    private const uint ID_GPU_SetPstates20 = 0x0F4DAE6B;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGPUsDelegate([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetFullNameDelegate(IntPtr gpu, [Out] byte[] name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Pstates20Delegate(IntPtr gpu, IntPtr info);

    // ---- NV_GPU_PERF_PSTATES20_INFO_V2 byte layout (x64, 4-byte fields) ----
    // Sizes: 16 pstates, 8 clocks/pstate, 4 base-voltages/pstate. Total = 7416 bytes.
    private const int SIZE = 7416;
    private const uint VER2 = SIZE | (2u << 16);   // MAKE_NVAPI_VERSION(..., 2)
    private const int HEADER = 20;                  // version, bIsEditable, numPstates, numClocks, numBaseVoltages
    private const int PSTATE_STRIDE = 456;          // pstateId, bIsEditable, clocks[8]*44, baseVoltages[4]*24
    private const int CLOCKS_OFFSET = 8;            // within a pstate, after pstateId + bIsEditable
    private const int CLOCK_STRIDE = 44;            // domainId, typeId, bIsEditable, freqDelta(12), data(20)
    private const int FREQDELTA_VALUE = 12;         // signed delta in kHz, within a clock entry

    private const int DOMAIN_GRAPHICS = 0;          // NVAPI_GPU_PUBLIC_CLOCK_GRAPHICS
    private const int DOMAIN_MEMORY = 4;            // NVAPI_GPU_PUBLIC_CLOCK_MEMORY
    private const int PSTATE_P0 = 0;               // performance state P0 (3D)
    private const int NVAPI_OK = 0;

    private static bool _initTried;
    private static bool _initOk;
    private static EnumPhysicalGPUsDelegate? _enum;
    private static GetFullNameDelegate? _getName;
    private static Pstates20Delegate? _getPstates;
    private static Pstates20Delegate? _setPstates;

    private static TDelegate? GetDelegate<TDelegate>(uint id) where TDelegate : Delegate
    {
        var ptr = NvApi_QueryInterface(id);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<TDelegate>(ptr);
    }

    /// <summary>Lazily initialise NVAPI once. Returns false (never throws) when unavailable.</summary>
    public static bool TryInitialize()
    {
        if (_initTried) return _initOk;
        _initTried = true;
        try
        {
            var init = GetDelegate<InitializeDelegate>(ID_Initialize);
            if (init is null || init() != NVAPI_OK)
                return false;

            _enum = GetDelegate<EnumPhysicalGPUsDelegate>(ID_EnumPhysicalGPUs);
            _getName = GetDelegate<GetFullNameDelegate>(ID_GPU_GetFullName);
            _getPstates = GetDelegate<Pstates20Delegate>(ID_GPU_GetPstates20);
            _setPstates = GetDelegate<Pstates20Delegate>(ID_GPU_SetPstates20);

            _initOk = _enum is not null && _getPstates is not null && _setPstates is not null;
            return _initOk;
        }
        catch
        {
            // DllNotFoundException on non-NVIDIA machines, or any P/Invoke failure → unavailable.
            return false;
        }
    }

    /// <summary>Get the first physical NVIDIA GPU handle and its marketing name.</summary>
    public static bool TryGetFirstGpu(out IntPtr gpu, out string name)
    {
        gpu = IntPtr.Zero;
        name = string.Empty;
        if (!TryInitialize() || _enum is null) return false;
        try
        {
            var handles = new IntPtr[64];   // NVAPI_MAX_PHYSICAL_GPUS
            if (_enum(handles, out int count) != NVAPI_OK || count <= 0) return false;
            gpu = handles[0];
            if (gpu == IntPtr.Zero) return false;

            if (_getName is not null)
            {
                var buf = new byte[64];     // NvAPI_ShortString
                if (_getName(gpu, buf) == NVAPI_OK)
                    name = Encoding.ASCII.GetString(buf).TrimEnd('\0', ' ');
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Read the current P0 core and memory clock offsets (MHz).</summary>
    public static bool TryReadOffsets(IntPtr gpu, out int coreMhz, out int memMhz)
    {
        coreMhz = 0;
        memMhz = 0;
        if (!TryInitialize() || _getPstates is null || gpu == IntPtr.Zero) return false;

        IntPtr buf = Marshal.AllocHGlobal(SIZE);
        try
        {
            Zero(buf, SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)VER2));
            if (_getPstates(gpu, buf) != NVAPI_OK) return false;

            int numPstates = Marshal.ReadInt32(buf, 8);
            if (numPstates is <= 0 or > 16) numPstates = 16;

            for (int i = 0; i < numPstates; i++)
            {
                int pBase = HEADER + i * PSTATE_STRIDE;
                if (Marshal.ReadInt32(buf, pBase) != PSTATE_P0) continue;   // want P0 only

                for (int c = 0; c < 8; c++)
                {
                    int cBase = pBase + CLOCKS_OFFSET + c * CLOCK_STRIDE;
                    int domain = Marshal.ReadInt32(buf, cBase);
                    int deltaKhz = Marshal.ReadInt32(buf, cBase + FREQDELTA_VALUE);
                    if (domain == DOMAIN_GRAPHICS) coreMhz = deltaKhz / 1000;
                    else if (domain == DOMAIN_MEMORY) memMhz = deltaKhz / 1000;
                }
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Apply P0 core and memory clock offsets (MHz). Frequency deltas only — driver-clamped, non-persistent.</summary>
    public static bool TrySetOffsets(IntPtr gpu, int coreMhz, int memMhz, out string error)
    {
        error = string.Empty;
        if (!TryInitialize() || _setPstates is null || gpu == IntPtr.Zero)
        {
            error = "NVAPI indisponible (aucun GPU NVIDIA pilotable).";
            return false;
        }

        IntPtr buf = Marshal.AllocHGlobal(SIZE);
        try
        {
            Zero(buf, SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)VER2));  // version
            Marshal.WriteInt32(buf, 8, 1);                      // numPstates = 1 (we only describe P0)
            Marshal.WriteInt32(buf, 12, 2);                     // numClocks  = 2 (graphics + memory)

            int pBase = HEADER;                                 // pstates[0]
            Marshal.WriteInt32(buf, pBase, PSTATE_P0);          // pstateId = P0
            Marshal.WriteInt32(buf, pBase + 4, 1);              // bIsEditable

            int core = pBase + CLOCKS_OFFSET;                   // clocks[0] = graphics
            Marshal.WriteInt32(buf, core, DOMAIN_GRAPHICS);
            Marshal.WriteInt32(buf, core + 8, 1);               // bIsEditable
            Marshal.WriteInt32(buf, core + FREQDELTA_VALUE, coreMhz * 1000);

            int mem = pBase + CLOCKS_OFFSET + CLOCK_STRIDE;     // clocks[1] = memory
            Marshal.WriteInt32(buf, mem, DOMAIN_MEMORY);
            Marshal.WriteInt32(buf, mem + 8, 1);                // bIsEditable
            Marshal.WriteInt32(buf, mem + FREQDELTA_VALUE, memMhz * 1000);

            int status = _setPstates(gpu, buf);
            if (status != NVAPI_OK)
            {
                error = $"NVAPI a refusé l'application (code {status}).";
                return false;
            }
            return true;
        }
        catch (DllNotFoundException)
        {
            error = "nvapi64.dll introuvable (driver NVIDIA absent).";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void Zero(IntPtr ptr, int size)
    {
        for (int i = 0; i < size; i += 4)
            Marshal.WriteInt32(ptr, i, 0);
    }
}
