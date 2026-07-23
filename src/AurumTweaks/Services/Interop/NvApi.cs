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
    private const int NVAPI_INVALID_USER_PRIVILEGE = -137;   // write refused without elevation

    // ---- Client power policies (power limit) ------------------------------------------------
    // Community function ids — the same undocumented-by-NVIDIA interface every OC tool
    // (Afterburner-class) uses. The V1 layouts below were VERIFIED LIVE on this project's dev GPU
    // (RTX 4080 SUPER, driver 32.0.16.1062, 2026-07-20) with a read-only sweep probe: the driver
    // accepts exactly (184 | 1<<16) for GetInfo and (72 | 1<<16) for GetStatus, and the
    // per-cent-mille values sit at the offsets below (read: min 46 875 / default 100 000 /
    // max 120 313; current 119 000 — matching the machine's real Afterburner setting). A wrong
    // size/version fails safe (NVAPI_INCOMPATIBLE_STRUCT_VERSION) and writes nothing.
    private const uint ID_PowerPoliciesGetInfo   = 0x34206D86;
    private const uint ID_PowerPoliciesGetStatus = 0x70916171;
    private const uint ID_PowerPoliciesSetStatus = 0xAD95F5ED;

    private const int PWR_INFO_SIZE = 184;
    private const uint PWR_INFO_VER1 = PWR_INFO_SIZE | (1u << 16);
    private const int PWR_INFO_MIN = 20;            // per-cent-mille (100 000 = 100 %)
    private const int PWR_INFO_DEF = 32;
    private const int PWR_INFO_MAX = 44;

    private const int PWR_STAT_SIZE = 72;
    private const uint PWR_STAT_VER1 = PWR_STAT_SIZE | (1u << 16);
    private const int PWR_STAT_COUNT = 4;           // u32 entry count
    private const int PWR_STAT_ENTRIES = 8;         // entries[4], 16 bytes each
    private const int PWR_STAT_ENTRY_STRIDE = 16;
    private const int PWR_STAT_POWER_IN_ENTRY = 8;  // per-cent-mille target within an entry

    // ---- Client thermal policies (temperature target) ----------------------------------------
    // Same community interface family as the power policies. V1 layouts VERIFIED LIVE on the dev
    // RTX 4080 SUPER (2026-07-20) by the same read-only sweep probe: GetInfo accepts exactly
    // (88 | 1<<16) and read min 16 640 / default 21 504 / max 22 528 — i.e. 65 / 84 / 88 °C in
    // <<8 fixed point, matching the card's documented Afterburner window; GetLimit accepts
    // (40 | 1<<16) and read 22 528 = 88 °C, the machine's real raised temp target. Values are
    // °C × 256. A wrong size/version fails safe and writes nothing.
    private const uint ID_ThermalPoliciesGetInfo  = 0x0D258BB5;
    private const uint ID_ThermalPoliciesGetLimit = 0xE9C425A1;
    private const uint ID_ThermalPoliciesSetLimit = 0x34C0B13D;

    private const int THERM_INFO_SIZE = 88;
    private const uint THERM_INFO_VER1 = THERM_INFO_SIZE | (1u << 16);
    private const int THERM_INFO_MIN = 16;          // °C << 8
    private const int THERM_INFO_DEF = 20;
    private const int THERM_INFO_MAX = 24;

    private const int THERM_LIMIT_SIZE = 40;
    private const uint THERM_LIMIT_VER1 = THERM_LIMIT_SIZE | (1u << 16);
    private const int THERM_LIMIT_COUNT = 4;        // u32 entry count
    private const int THERM_LIMIT_ENTRIES = 8;      // entries[4], 8 bytes each: {controller, value}
    private const int THERM_LIMIT_ENTRY_STRIDE = 8;
    private const int THERM_LIMIT_VALUE_IN_ENTRY = 4;

    // ---- Client fan coolers (manual fan % + tachometer) --------------------------------------
    // The interface Afterburner-class tools use on Turing+/Ada (legacy SetCoolerLevels is deprecated
    // there). V1 layouts VERIFIED LIVE on the dev RTX 4080 SUPER: GetStatus accepts (1704 | 1<<16) and
    // read count=2 fans + a real 1098 RPM tachometer; GetControl accepts (1452 | 1<<16) with the
    // ASYMMETRIC header (count at offset 8, entries at 44). A wrong size/version fails safe and writes
    // nothing. Every set is a read-modify-write of the control buffer + a status read-back confirmation.
    private const uint ID_FanCoolersGetStatus  = 0x35AED5E8;
    private const uint ID_FanCoolersGetControl = 0x814B209F;
    private const uint ID_FanCoolersSetControl = 0xA58971A5;

    private const int FAN_STATUS_SIZE = 1704;
    private const uint FAN_STATUS_VER1 = FAN_STATUS_SIZE | (1u << 16);
    private const int FAN_STATUS_COUNT = 4;         // u32 count
    private const int FAN_STATUS_ENTRIES = 40;      // header: ver, count, reserved[8]
    private const int FAN_STATUS_ENTRY_STRIDE = 52;
    private const int FAN_STATUS_RPM_IN_ENTRY = 4;
    private const int FAN_STATUS_LEVEL_IN_ENTRY = 16;   // fan % (0..100)

    private const int FAN_CONTROL_SIZE = 1452;
    private const uint FAN_CONTROL_VER1 = FAN_CONTROL_SIZE | (1u << 16);
    private const int FAN_CONTROL_COUNT = 8;        // u32 count (asymmetric: reserved1 before it)
    private const int FAN_CONTROL_ENTRIES = 44;     // header: ver, reserved1, count, reserved2[8]
    private const int FAN_CONTROL_ENTRY_STRIDE = 44;
    private const int FAN_CONTROL_LEVEL_IN_ENTRY = 4;
    private const int FAN_CONTROL_MODE_IN_ENTRY = 8;    // 0 = auto, 1 = manual
    private const int FAN_MODE_AUTO = 0;
    private const int FAN_MODE_MANUAL = 1;

    private static bool _initTried;
    private static bool _initOk;
    private static EnumPhysicalGPUsDelegate? _enum;
    private static GetFullNameDelegate? _getName;
    private static Pstates20Delegate? _getPstates;
    private static Pstates20Delegate? _setPstates;
    private static Pstates20Delegate? _pwrGetInfo;
    private static Pstates20Delegate? _pwrGetStatus;
    private static Pstates20Delegate? _pwrSetStatus;
    private static Pstates20Delegate? _thermGetInfo;
    private static Pstates20Delegate? _thermGetLimit;
    private static Pstates20Delegate? _thermSetLimit;
    private static Pstates20Delegate? _fanGetStatus;
    private static Pstates20Delegate? _fanGetControl;
    private static Pstates20Delegate? _fanSetControl;

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

            // Power/thermal-policy functions are optional extras: their absence must not disable
            // the core offsets backend, so they deliberately don't participate in _initOk.
            _pwrGetInfo = GetDelegate<Pstates20Delegate>(ID_PowerPoliciesGetInfo);
            _pwrGetStatus = GetDelegate<Pstates20Delegate>(ID_PowerPoliciesGetStatus);
            _pwrSetStatus = GetDelegate<Pstates20Delegate>(ID_PowerPoliciesSetStatus);
            _thermGetInfo = GetDelegate<Pstates20Delegate>(ID_ThermalPoliciesGetInfo);
            _thermGetLimit = GetDelegate<Pstates20Delegate>(ID_ThermalPoliciesGetLimit);
            _thermSetLimit = GetDelegate<Pstates20Delegate>(ID_ThermalPoliciesSetLimit);
            _fanGetStatus = GetDelegate<Pstates20Delegate>(ID_FanCoolersGetStatus);
            _fanGetControl = GetDelegate<Pstates20Delegate>(ID_FanCoolersGetControl);
            _fanSetControl = GetDelegate<Pstates20Delegate>(ID_FanCoolersSetControl);

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

            // Bound the clock loop by the driver-reported numClocks (global header, offset 12). The
            // trailing clock slots we zeroed before the call read back as domain 0 — which equals
            // DOMAIN_GRAPHICS — so iterating all 8 would let an empty slot overwrite the real core
            // offset with 0. Only the first numClocks entries are valid.
            int numClocks = Marshal.ReadInt32(buf, 12);
            if (numClocks is <= 0 or > 8) numClocks = 8;

            for (int i = 0; i < numPstates; i++)
            {
                int pBase = HEADER + i * PSTATE_STRIDE;
                if (Marshal.ReadInt32(buf, pBase) != PSTATE_P0) continue;   // want P0 only

                for (int c = 0; c < numClocks; c++)
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

    /// <summary>Read the card's power-limit window (min/default/max, per-cent-mille). Strict: false unless the triplet reads plausible and ordered.</summary>
    public static bool TryReadPowerInfo(IntPtr gpu, out int minPcm, out int defPcm, out int maxPcm)
    {
        minPcm = defPcm = maxPcm = 0;
        if (!TryInitialize() || _pwrGetInfo is null || gpu == IntPtr.Zero) return false;

        IntPtr buf = Marshal.AllocHGlobal(PWR_INFO_SIZE);
        try
        {
            Zero(buf, PWR_INFO_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)PWR_INFO_VER1));
            if (_pwrGetInfo(gpu, buf) != NVAPI_OK) return false;

            minPcm = Marshal.ReadInt32(buf, PWR_INFO_MIN);
            defPcm = Marshal.ReadInt32(buf, PWR_INFO_DEF);
            maxPcm = Marshal.ReadInt32(buf, PWR_INFO_MAX);
            // The layout is community-documented, not NVIDIA-documented: one implausible field
            // and the whole power feature honestly reports unavailable rather than trusting it.
            return GpuPowerLimit.IsPlausibleWindow(minPcm, defPcm, maxPcm);
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

    /// <summary>Read the current power-limit target (per-cent-mille) from the first plausible status entry.</summary>
    public static bool TryReadPowerTarget(IntPtr gpu, out int currentPcm)
    {
        currentPcm = 0;
        if (!TryInitialize() || _pwrGetStatus is null || gpu == IntPtr.Zero) return false;

        IntPtr buf = Marshal.AllocHGlobal(PWR_STAT_SIZE);
        try
        {
            Zero(buf, PWR_STAT_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)PWR_STAT_VER1));
            if (_pwrGetStatus(gpu, buf) != NVAPI_OK) return false;
            return TryLocatePowerEntry(buf, out _, out currentPcm);
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

    /// <summary>
    /// Set the power-limit target (per-cent-mille). Read-modify-write: the buffer GetStatus just
    /// returned is patched in exactly one field and written back (unknown fields preserved), then
    /// re-read — success is reported ONLY when the read-back equals the requested value, so a
    /// driver that ignored or re-clamped the write can never be presented as a success.
    /// </summary>
    public static bool TrySetPowerTarget(IntPtr gpu, int targetPcm, out string error)
    {
        error = string.Empty;
        if (!TryInitialize() || _pwrGetStatus is null || _pwrSetStatus is null || gpu == IntPtr.Zero)
        {
            error = "NVAPI power limit indisponible.";
            return false;
        }

        IntPtr buf = Marshal.AllocHGlobal(PWR_STAT_SIZE);
        try
        {
            Zero(buf, PWR_STAT_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)PWR_STAT_VER1));
            if (_pwrGetStatus(gpu, buf) != NVAPI_OK)
            {
                error = "Lecture préalable du power limit refusée par le driver.";
                return false;
            }
            if (!TryLocatePowerEntry(buf, out int offset, out _))
            {
                error = "Disposition power limit inattendue — écriture refusée par prudence.";
                return false;
            }

            Marshal.WriteInt32(buf, offset, targetPcm);
            int status = _pwrSetStatus(gpu, buf);
            if (status == NVAPI_INVALID_USER_PRIVILEGE)
            {
                error = "Écriture refusée : privilèges insuffisants (lancer Aurum en administrateur).";
                return false;
            }
            if (status != NVAPI_OK)
            {
                error = $"NVAPI a refusé l'écriture du power limit (code {status}).";
                return false;
            }

            // Confirmation read-back — the honesty gate: no confirmed read, no claimed success.
            Zero(buf, PWR_STAT_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)PWR_STAT_VER1));
            if (_pwrGetStatus(gpu, buf) != NVAPI_OK || !TryLocatePowerEntry(buf, out _, out int readBack))
            {
                error = "Écriture envoyée mais relecture impossible — état non confirmé.";
                return false;
            }
            if (readBack != targetPcm)
            {
                error = $"Écriture non confirmée : le driver rapporte {GpuPowerLimit.FromPcm(readBack)} % "
                      + $"au lieu de {GpuPowerLimit.FromPcm(targetPcm)} %.";
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
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Read the card's temperature-target window (min/default/max, °C·256). Strict: false unless the triplet reads plausible and ordered.</summary>
    public static bool TryReadThermalInfo(IntPtr gpu, out int minRaw, out int defRaw, out int maxRaw)
    {
        minRaw = defRaw = maxRaw = 0;
        if (!TryInitialize() || _thermGetInfo is null || gpu == IntPtr.Zero) return false;

        IntPtr buf = Marshal.AllocHGlobal(THERM_INFO_SIZE);
        try
        {
            Zero(buf, THERM_INFO_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)THERM_INFO_VER1));
            if (_thermGetInfo(gpu, buf) != NVAPI_OK) return false;

            minRaw = Marshal.ReadInt32(buf, THERM_INFO_MIN);
            defRaw = Marshal.ReadInt32(buf, THERM_INFO_DEF);
            maxRaw = Marshal.ReadInt32(buf, THERM_INFO_MAX);
            // Community-documented layout: one implausible field and the whole thermal feature
            // honestly reports unavailable rather than trusting it.
            return GpuThermalLimit.IsPlausibleWindow(minRaw, defRaw, maxRaw);
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

    /// <summary>Read the current temperature target (°C·256) from the first plausible limit entry.</summary>
    public static bool TryReadThermalLimit(IntPtr gpu, out int currentRaw)
    {
        currentRaw = 0;
        if (!TryInitialize() || _thermGetLimit is null || gpu == IntPtr.Zero) return false;

        IntPtr buf = Marshal.AllocHGlobal(THERM_LIMIT_SIZE);
        try
        {
            Zero(buf, THERM_LIMIT_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)THERM_LIMIT_VER1));
            if (_thermGetLimit(gpu, buf) != NVAPI_OK) return false;
            return TryLocateThermalEntry(buf, out _, out currentRaw);
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

    /// <summary>
    /// Set the temperature target (°C·256). Same honesty contract as the power write: read-modify-write
    /// of the exact buffer GetLimit just returned, then a mandatory read-back — success is reported ONLY
    /// when the re-read equals the requested value.
    /// </summary>
    public static bool TrySetThermalLimit(IntPtr gpu, int targetRaw, out string error)
    {
        error = string.Empty;
        if (!TryInitialize() || _thermGetLimit is null || _thermSetLimit is null || gpu == IntPtr.Zero)
        {
            error = "NVAPI cible température indisponible.";
            return false;
        }

        IntPtr buf = Marshal.AllocHGlobal(THERM_LIMIT_SIZE);
        try
        {
            Zero(buf, THERM_LIMIT_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)THERM_LIMIT_VER1));
            if (_thermGetLimit(gpu, buf) != NVAPI_OK)
            {
                error = "Lecture préalable de la cible température refusée par le driver.";
                return false;
            }
            if (!TryLocateThermalEntry(buf, out int offset, out _))
            {
                error = "Disposition cible température inattendue — écriture refusée par prudence.";
                return false;
            }

            Marshal.WriteInt32(buf, offset, targetRaw);
            int status = _thermSetLimit(gpu, buf);
            if (status == NVAPI_INVALID_USER_PRIVILEGE)
            {
                error = "Écriture refusée : privilèges insuffisants (lancer Aurum en administrateur).";
                return false;
            }
            if (status != NVAPI_OK)
            {
                error = $"NVAPI a refusé l'écriture de la cible température (code {status}).";
                return false;
            }

            Zero(buf, THERM_LIMIT_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)THERM_LIMIT_VER1));
            if (_thermGetLimit(gpu, buf) != NVAPI_OK || !TryLocateThermalEntry(buf, out _, out int readBack))
            {
                error = "Écriture envoyée mais relecture impossible — état non confirmé.";
                return false;
            }
            if (readBack != targetRaw)
            {
                error = $"Écriture non confirmée : le driver rapporte {GpuThermalLimit.FromRaw(readBack)} °C "
                      + $"au lieu de {GpuThermalLimit.FromRaw(targetRaw)} °C.";
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
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Find the limit entry holding the plausible °C·256 target; returns its byte offset.</summary>
    private static bool TryLocateThermalEntry(IntPtr limitBuf, out int valueOffset, out int valueRaw)
    {
        valueOffset = 0;
        valueRaw = 0;
        int count = Marshal.ReadInt32(limitBuf, THERM_LIMIT_COUNT);
        if (count is < 1 or > 4) return false;
        for (int i = 0; i < count; i++)
        {
            int off = THERM_LIMIT_ENTRIES + i * THERM_LIMIT_ENTRY_STRIDE + THERM_LIMIT_VALUE_IN_ENTRY;
            int v = Marshal.ReadInt32(limitBuf, off);
            if (GpuThermalLimit.IsPlausibleRaw(v))
            {
                valueOffset = off;
                valueRaw = v;
                return true;
            }
        }
        return false;
    }

    /// <summary>Find the status entry holding the plausible per-cent-mille target; returns its byte offset.</summary>
    private static bool TryLocatePowerEntry(IntPtr statusBuf, out int powerOffset, out int powerPcm)
    {
        powerOffset = 0;
        powerPcm = 0;
        int count = Marshal.ReadInt32(statusBuf, PWR_STAT_COUNT);
        if (count is < 1 or > 4) return false;
        for (int i = 0; i < count; i++)
        {
            int off = PWR_STAT_ENTRIES + i * PWR_STAT_ENTRY_STRIDE + PWR_STAT_POWER_IN_ENTRY;
            int v = Marshal.ReadInt32(statusBuf, off);
            if (GpuPowerLimit.IsPlausiblePcm(v))
            {
                powerOffset = off;
                powerPcm = v;
                return true;
            }
        }
        return false;
    }

    /// <summary>Read the current fan level (%) and tachometer (RPM) from the first plausible cooler entry.
    /// Read-only; a mis-read layout (implausible level/rpm/count) reports unavailable rather than trusting it.</summary>
    public static bool TryReadFanStatus(IntPtr gpu, out int levelPct, out int rpm)
    {
        levelPct = rpm = 0;
        if (!TryInitialize() || _fanGetStatus is null || gpu == IntPtr.Zero) return false;

        IntPtr buf = Marshal.AllocHGlobal(FAN_STATUS_SIZE);
        try
        {
            Zero(buf, FAN_STATUS_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)FAN_STATUS_VER1));
            if (_fanGetStatus(gpu, buf) != NVAPI_OK) return false;

            int count = Marshal.ReadInt32(buf, FAN_STATUS_COUNT);
            if (count is < 1 or > 4) return false;
            for (int i = 0; i < count; i++)
            {
                int e = FAN_STATUS_ENTRIES + i * FAN_STATUS_ENTRY_STRIDE;
                int lvl = Marshal.ReadInt32(buf, e + FAN_STATUS_LEVEL_IN_ENTRY);
                int r = Marshal.ReadInt32(buf, e + FAN_STATUS_RPM_IN_ENTRY);
                if (GpuFanSafety.IsPlausiblePercent(lvl) && GpuFanSafety.IsPlausibleRpm(r))
                {
                    levelPct = lvl;
                    rpm = r;
                    return true;
                }
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

    /// <summary>
    /// Set a manual fan level (%) on every cooler. Read-modify-write of the control buffer (unknown fields
    /// preserved), then a status read-back — success is reported ONLY when the read-back level equals the
    /// requested value. The requested % is pushed through <see cref="GpuFanSafety.ClampManualPercent"/> so
    /// Aurum can never drive the fan below the hard safety floor, whatever the caller passes.
    /// </summary>
    public static bool TrySetFanManual(IntPtr gpu, int requestedPct, out string error)
    {
        int pct = GpuFanSafety.ClampManualPercent(requestedPct);
        return TryWriteFanControl(gpu, FAN_MODE_MANUAL, pct, confirmLevel: pct, out error);
    }

    /// <summary>Hand fan control back to the driver's automatic curve on every cooler.</summary>
    public static bool TrySetFanAuto(IntPtr gpu, out string error)
        => TryWriteFanControl(gpu, FAN_MODE_AUTO, level: 0, confirmLevel: null, out error);

    private static bool TryWriteFanControl(IntPtr gpu, int mode, int level, int? confirmLevel, out string error)
    {
        error = string.Empty;
        if (!TryInitialize() || _fanGetControl is null || _fanSetControl is null || gpu == IntPtr.Zero)
        {
            error = "NVAPI contrôle ventilateur indisponible.";
            return false;
        }

        IntPtr buf = Marshal.AllocHGlobal(FAN_CONTROL_SIZE);
        try
        {
            Zero(buf, FAN_CONTROL_SIZE);
            Marshal.WriteInt32(buf, 0, unchecked((int)FAN_CONTROL_VER1));
            if (_fanGetControl(gpu, buf) != NVAPI_OK)
            {
                error = "Lecture préalable du contrôle ventilateur refusée par le driver.";
                return false;
            }
            int count = Marshal.ReadInt32(buf, FAN_CONTROL_COUNT);
            if (count is < 1 or > 4)
            {
                error = "Disposition contrôle ventilateur inattendue — écriture refusée par prudence.";
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                int e = FAN_CONTROL_ENTRIES + i * FAN_CONTROL_ENTRY_STRIDE;
                Marshal.WriteInt32(buf, e + FAN_CONTROL_MODE_IN_ENTRY, mode);
                if (mode == FAN_MODE_MANUAL) Marshal.WriteInt32(buf, e + FAN_CONTROL_LEVEL_IN_ENTRY, level);
            }

            int status = _fanSetControl(gpu, buf);
            if (status == NVAPI_INVALID_USER_PRIVILEGE)
            {
                error = "Écriture refusée : privilèges insuffisants (lancer Aurum en administrateur).";
                return false;
            }
            if (status != NVAPI_OK)
            {
                error = $"NVAPI a refusé le contrôle ventilateur (code {status}).";
                return false;
            }

            // Confirm a manual level by read-back (auto has no target level to confirm).
            if (confirmLevel is int want)
            {
                if (!TryReadFanStatus(gpu, out int back, out _))
                {
                    error = "Écriture envoyée mais relecture impossible — état non confirmé.";
                    return false;
                }
                if (back != want)
                {
                    error = $"Écriture non confirmée : le driver rapporte {back} % au lieu de {want} %.";
                    return false;
                }
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
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void Zero(IntPtr ptr, int size)
    {
        for (int i = 0; i < size; i += 4)
            Marshal.WriteInt32(ptr, i, 0);
    }
}

/// <summary>
/// Injectable seam over the static <see cref="NvApi"/>. Exists so <c>GpuOcService</c>'s orchestration
/// (vendor routing, multi-axis apply, partial-failure honesty, read-back decisions) is unit-testable
/// with a fake instead of a real NVIDIA card — and so all NVAPI entry points are serialized behind one
/// lock (the static class has no synchronization; two overlapping VM commands could otherwise interleave
/// writes and produce a spurious "unconfirmed" failure). Delegates verbatim; adds only the lock.
/// </summary>
public interface INvApi
{
    bool TryGetFirstGpu(out IntPtr gpu, out string name);
    bool TryReadOffsets(IntPtr gpu, out int coreMhz, out int memMhz);
    bool TrySetOffsets(IntPtr gpu, int coreMhz, int memMhz, out string error);
    bool TryReadPowerInfo(IntPtr gpu, out int minPcm, out int defPcm, out int maxPcm);
    bool TryReadPowerTarget(IntPtr gpu, out int currentPcm);
    bool TrySetPowerTarget(IntPtr gpu, int targetPcm, out string error);
    bool TryReadThermalInfo(IntPtr gpu, out int minRaw, out int defRaw, out int maxRaw);
    bool TryReadThermalLimit(IntPtr gpu, out int currentRaw);
    bool TrySetThermalLimit(IntPtr gpu, int targetRaw, out string error);
    bool TryReadFanStatus(IntPtr gpu, out int levelPct, out int rpm);
    bool TrySetFanManual(IntPtr gpu, int requestedPct, out string error);
    bool TrySetFanAuto(IntPtr gpu, out string error);
}

/// <summary>Production <see cref="INvApi"/>: every call is delegated to the static wrapper under a single
/// process-wide lock, giving NVAPI the same "serialized behind one lock" contract AdlxApi already has.</summary>
public sealed class NvApiBackend : INvApi
{
    private static readonly object Sync = new();

    public bool TryGetFirstGpu(out IntPtr gpu, out string name)
    { lock (Sync) { return NvApi.TryGetFirstGpu(out gpu, out name); } }

    public bool TryReadOffsets(IntPtr gpu, out int coreMhz, out int memMhz)
    { lock (Sync) { return NvApi.TryReadOffsets(gpu, out coreMhz, out memMhz); } }

    public bool TrySetOffsets(IntPtr gpu, int coreMhz, int memMhz, out string error)
    { lock (Sync) { return NvApi.TrySetOffsets(gpu, coreMhz, memMhz, out error); } }

    public bool TryReadPowerInfo(IntPtr gpu, out int minPcm, out int defPcm, out int maxPcm)
    { lock (Sync) { return NvApi.TryReadPowerInfo(gpu, out minPcm, out defPcm, out maxPcm); } }

    public bool TryReadPowerTarget(IntPtr gpu, out int currentPcm)
    { lock (Sync) { return NvApi.TryReadPowerTarget(gpu, out currentPcm); } }

    public bool TrySetPowerTarget(IntPtr gpu, int targetPcm, out string error)
    { lock (Sync) { return NvApi.TrySetPowerTarget(gpu, targetPcm, out error); } }

    public bool TryReadThermalInfo(IntPtr gpu, out int minRaw, out int defRaw, out int maxRaw)
    { lock (Sync) { return NvApi.TryReadThermalInfo(gpu, out minRaw, out defRaw, out maxRaw); } }

    public bool TryReadThermalLimit(IntPtr gpu, out int currentRaw)
    { lock (Sync) { return NvApi.TryReadThermalLimit(gpu, out currentRaw); } }

    public bool TrySetThermalLimit(IntPtr gpu, int targetRaw, out string error)
    { lock (Sync) { return NvApi.TrySetThermalLimit(gpu, targetRaw, out error); } }

    public bool TryReadFanStatus(IntPtr gpu, out int levelPct, out int rpm)
    { lock (Sync) { return NvApi.TryReadFanStatus(gpu, out levelPct, out rpm); } }

    public bool TrySetFanManual(IntPtr gpu, int requestedPct, out string error)
    { lock (Sync) { return NvApi.TrySetFanManual(gpu, requestedPct, out error); } }

    public bool TrySetFanAuto(IntPtr gpu, out string error)
    { lock (Sync) { return NvApi.TrySetFanAuto(gpu, out error); } }
}
