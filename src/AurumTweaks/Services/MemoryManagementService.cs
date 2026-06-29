using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  « Mémoire vive » — a live RAM-composition view plus the HONEST version of what tools like ISLC / RAMMap do.
//  The composition is read from the kernel's own page-list counters (NtQuerySystemInformation /
//  SystemMemoryListInformation — the exact source RAMMap uses), so the standby/free/modified split is language-
//  immune and exact (page counts, not a localized perf-counter name nor a lossy float). The flush actions are REAL
//  (NtSetSystemInformation / SystemMemoryListInformation) and MEASURED: the composition is re-read afterwards, so
//  the figure shown is the standby that genuinely disappeared — never an estimate. The load-bearing honesty line:
//  emptying the standby cache does NOT increase "available" memory (standby was already available) and is NOT an
//  FPS boost — Windows keeps that cache on purpose and rebuilds it. The page says that plainly instead of selling
//  the myth every snake-oil "RAM booster" sells.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A snapshot of physical-memory composition, in bytes. Total/Available come from GlobalMemoryStatusEx (the figures
/// Task Manager shows); Standby/Free/Modified come from the kernel page lists. <see cref="DetailAvailable"/> is false
/// when the page-list query couldn't be read — then the standby/free/modified split is reported as "—" (never a
/// fabricated 0), while Total/Available stay valid. Pure: all derivation is unit-tested.
/// </summary>
public sealed record MemoryComposition(
    long TotalBytes, long AvailableBytes, long StandbyBytes, long FreeBytes, long ModifiedBytes, bool DetailAvailable)
{
    /// <summary>Actively in use = total minus what Windows reports as available. Matches Task Manager's "in use".</summary>
    public long InUseBytes => Math.Max(0, TotalBytes - AvailableBytes);

    public bool HasData => TotalBytes > 0;

    public double InUsePercent => Pct(InUseBytes);
    public double AvailablePercent => Pct(AvailableBytes);
    public double StandbyPercent => Pct(StandbyBytes);
    public double FreePercent => Pct(FreeBytes);
    public double ModifiedPercent => Pct(ModifiedBytes);

    public string TotalDisplay => ByteSize.Format(TotalBytes);
    public string InUseDisplay => ByteSize.Format(InUseBytes);
    public string AvailableDisplay => ByteSize.Format(AvailableBytes);

    // Standby/Free/Modified are only meaningful when the page-list read worked — otherwise "—", never a fake 0.
    public string StandbyDisplay => DetailAvailable ? ByteSize.Format(StandbyBytes) : "—";
    public string FreeDisplay => DetailAvailable ? ByteSize.Format(FreeBytes) : "—";
    public string ModifiedDisplay => DetailAvailable ? ByteSize.Format(ModifiedBytes) : "—";

    // Reuse LatencyFormat.Percent (same assembly): fr-FR, clamped 0–100, one decimal + " %" — no duplicate formatter.
    public string InUsePercentDisplay => LatencyFormat.Percent(InUsePercent);
    public string AvailablePercentDisplay => LatencyFormat.Percent(AvailablePercent);
    public string StandbyPercentDisplay => DetailAvailable ? LatencyFormat.Percent(StandbyPercent) : "—";
    public string FreePercentDisplay => DetailAvailable ? LatencyFormat.Percent(FreePercent) : "—";
    public string ModifiedPercentDisplay => DetailAvailable ? LatencyFormat.Percent(ModifiedPercent) : "—";

    private double Pct(long part) => TotalBytes <= 0 ? 0 : Math.Clamp((double)part / TotalBytes * 100.0, 0, 100);

    public static MemoryComposition Empty { get; } = new(0, 0, 0, 0, 0, false);
}

/// <summary>Which kernel memory list a flush targets.</summary>
public enum MemoryFlushKind
{
    /// <summary>Purge the standby cache (the canonical "ISLC" action — drops the file cache held in reserve).</summary>
    StandbyList,

    /// <summary>Empty every process working set (more aggressive — apps repage on next access).</summary>
    WorkingSets
}

/// <summary>One flush action with its French label and honest advice. Pure metadata — no behaviour.</summary>
public sealed record MemoryFlushAction(MemoryFlushKind Kind, string Label, string Advice);

/// <summary>
/// The curated flush actions. Deliberately just the two meaningful, well-understood RAMMap-class commands; the advice
/// strings carry the honesty (no FPS gain, "available" doesn't move). The kind set is pinned by a test so a careless
/// future addition can't slip a misleading action onto the page.
/// </summary>
public static class MemoryFlushCatalog
{
    public static IReadOnlyList<MemoryFlushAction> Actions { get; } = new[]
    {
        new MemoryFlushAction(MemoryFlushKind.StandbyList,
            "Vider le cache standby",
            "Force Windows à relâcher le cache de fichiers gardé « en réserve » (standby). Les octets passent du "
            + "cache à la mémoire libre — la mémoire DISPONIBLE ne bouge pas, car le standby y était déjà compté. "
            + "Pratique avant un gros chargement ou un benchmark « à froid » ; ce n'est pas un gain de FPS, et "
            + "Windows reconstruit ce cache ensuite."),
        new MemoryFlushAction(MemoryFlushKind.WorkingSets,
            "Vider les working sets",
            "Demande à chaque processus de relâcher sa mémoire de travail vers le standby. Plus agressif : les "
            + "applications repagineront au prochain accès, d'où une possible micro-latence passagère. À réserver au "
            + "diagnostic ; aucun gain de performance attendu."),
    };

    public static string Label(MemoryFlushKind kind) =>
        Actions.FirstOrDefault(a => a.Kind == kind)?.Label ?? kind.ToString();
}

/// <summary>
/// The SYSTEM_MEMORY_LIST_COMMAND values, isolated as a pure mapping so the kind→command decision is unit-pinned: a
/// wrong constant here would silently fire the wrong kernel command, which is exactly the kind of invisible mistake
/// the honesty mandate forbids.
/// </summary>
public static class MemoryListCommand
{
    public const int MemoryEmptyWorkingSets = 2;
    public const int MemoryPurgeStandbyList = 4;

    public static int ForKind(MemoryFlushKind kind) => kind switch
    {
        MemoryFlushKind.StandbyList => MemoryPurgeStandbyList,
        MemoryFlushKind.WorkingSets => MemoryEmptyWorkingSets,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

/// <summary>
/// The result of a real flush: whether the kernel call was invoked successfully, plus the composition measured
/// immediately before and after. Pure, so the honesty rule is pinned — the headline figure is a MEASURED delta
/// (before−after standby for a purge; after−before available for a working-set empty), clamped at zero so noise
/// can't fabricate a number. <see cref="DidSomething"/> distinguishes "freed X" from "ran but nothing moved" from
/// "the call failed".
/// </summary>
public sealed record MemoryFlushOutcome(MemoryFlushKind Kind, bool Invoked, MemoryComposition Before, MemoryComposition After)
{
    /// <summary>Standby cache that was dropped (moved standby→free) — the meaningful figure for a standby purge.</summary>
    public long StandbyReleased => Math.Max(0, Before.StandbyBytes - After.StandbyBytes);

    /// <summary>Memory that moved into the available pool — the meaningful figure for a working-set empty.</summary>
    public long AvailableGained => Math.Max(0, After.AvailableBytes - Before.AvailableBytes);

    /// <summary>The figure the UI headlines, chosen per kind.</summary>
    public long Headline => Kind == MemoryFlushKind.StandbyList ? StandbyReleased : AvailableGained;
    public string HeadlineDisplay => ByteSize.Format(Headline);

    /// <summary>Did the call run AND produce a measured, positive change? (A standby purge with no page-list detail
    /// yields a 0 standby delta, so this is correctly false — the UI then says "effectué, non mesurable".)</summary>
    public bool DidSomething => Invoked && Headline > 0;

    public static MemoryFlushOutcome Failed(MemoryFlushKind kind) =>
        new(kind, false, MemoryComposition.Empty, MemoryComposition.Empty);
}

/// <summary>Pure one-line summary of a composition for the status strip — honest about standby being cache, not waste.</summary>
public static class MemoryAdvice
{
    public static string Summarize(MemoryComposition c)
    {
        if (!c.HasData) return "Composition mémoire indisponible.";

        return c.DetailAvailable
            ? $"{c.InUseDisplay} en cours d'utilisation · {c.StandbyDisplay} en cache (standby) · {c.FreeDisplay} libre(s) sur {c.TotalDisplay}."
            : $"{c.InUseDisplay} en cours d'utilisation · {c.AvailableDisplay} disponible(s) sur {c.TotalDisplay} (détail standby/libre indisponible).";
    }
}

/// <summary>
/// Native glue for the memory page — fully guarded so any failure degrades to an honest "indisponible" / "échoué"
/// rather than a fabricated number. Reads the page lists by explicit offset (the x64 SYSTEM_MEMORY_LIST_INFORMATION
/// is a run of ULONG_PTR counters) to dodge any marshalling surprise, mirroring the latency probe.
/// </summary>
internal static class MemoryProbe
{
    private const int SystemMemoryListInformation = 0x50; // 80 — same class for both query (read) and set (flush)

    public static MemoryComposition Read()
    {
        long total = 0, avail = 0;
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            total = (long)Math.Min(status.ullTotalPhys, long.MaxValue);
            avail = (long)Math.Min(status.ullAvailPhys, long.MaxValue);
        }

        if (TryReadPageLists(out long standby, out long free, out long modified))
            return new MemoryComposition(total, avail, standby, free, modified, DetailAvailable: true);

        return new MemoryComposition(total, avail, 0, 0, 0, DetailAvailable: false);
    }

    public static bool Flush(MemoryFlushKind kind)
    {
        MemoryPrivilege.EnableProfilePrivilege();   // the flush needs SeProfileSingleProcessPrivilege
        int command = MemoryListCommand.ForKind(kind);
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, command);
            return NtSetSystemInformation(SystemMemoryListInformation, buffer, sizeof(int)) == 0; // STATUS_SUCCESS
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TryReadPageLists(out long standbyBytes, out long freeBytes, out long modifiedBytes)
    {
        standbyBytes = freeBytes = modifiedBytes = 0;
        MemoryPrivilege.EnableProfilePrivilege();   // querying the memory list also needs the privilege

        const int size = 256;       // SYSTEM_MEMORY_LIST_INFORMATION is 176 bytes on x64 — over-allocate
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            int s = NtQuerySystemInformation(SystemMemoryListInformation, buffer, size, out int returned);
            // We read fields through offset 103 (the 8-entry priority array); require at least that many bytes.
            if (s != 0 || returned < 104) return false;

            long pageSize = Environment.SystemPageSize;
            long zeroPages = Marshal.ReadInt64(buffer, 0);
            long freePages = Marshal.ReadInt64(buffer, 8);
            long modifiedPages = Marshal.ReadInt64(buffer, 16);
            long modifiedNoWritePages = Marshal.ReadInt64(buffer, 24);

            long standbyPages = 0;                              // PageCountByPriority[0..7] @ offset 40, 8 bytes each
            for (int i = 0; i < 8; i++) standbyPages += Marshal.ReadInt64(buffer, 40 + i * 8);

            standbyBytes = standbyPages * pageSize;
            freeBytes = (zeroPages + freePages) * pageSize;
            modifiedBytes = (modifiedPages + modifiedNoWritePages) * pageSize;
            return true;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);
}

/// <summary>
/// Enables SeProfileSingleProcessPrivilege on the current (already-elevated) process token — required to query and to
/// flush the kernel memory lists. Best-effort and fully guarded: if the privilege can't be enabled the subsequent
/// query/flush simply fails, and the page reports that honestly rather than faking success.
/// </summary>
internal static class MemoryPrivilege
{
    private const string SeProfileSingleProcess = "SeProfileSingleProcessPrivilege";
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x0002;

    public static void EnableProfilePrivilege()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
                return;
            try
            {
                if (!LookupPrivilegeValue(null, SeProfileSingleProcess, out LUID luid)) return;
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }
        catch { /* best-effort — failure surfaces honestly as an unreadable query / failed flush */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public int LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public int PrivilegeCount; public LUID Luid; public int Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// The I/O service behind « Mémoire vive ». The decision logic (composition math, outcome math, advice, the
/// kind→command map) lives in the pure cores above and is what the tests pin; this only samples and fires the
/// kernel call, then re-reads so the reported delta is real.
/// </summary>
public sealed class MemoryManagementService : IMemoryManagementService
{
    public Task<MemoryComposition> GetCompositionAsync() => Task.Run(MemoryProbe.Read);

    public Task<MemoryFlushOutcome> FlushAsync(MemoryFlushKind kind) => Task.Run(() =>
    {
        var before = MemoryProbe.Read();
        bool invoked = MemoryProbe.Flush(kind);
        var after = MemoryProbe.Read();
        return new MemoryFlushOutcome(kind, invoked, before, after);
    });
}
