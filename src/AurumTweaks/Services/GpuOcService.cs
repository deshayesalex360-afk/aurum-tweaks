using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AurumTweaks.Models;
using AurumTweaks.Services.Interop;

namespace AurumTweaks.Services;

/// <summary>
/// GPU overclocking abstraction. On NVIDIA it is backed by the real, user-mode NVAPI wrapper
/// (<see cref="NvApi"/>): <b>core/memory clock offsets</b> always (frequency deltas the driver clamps
/// to the card's allowed V/F range, non-persistent across reboot), plus the <b>power limit</b> and
/// <b>temperature target</b> — each ONLY on cards where its community-documented layout passed an
/// on-card read verification. On AMD it is backed by <see cref="Interop.AdlxApi"/> (ADLX, AMD's
/// official documented API): <b>power limit</b>, <b>GPU max frequency</b> and <b>memory max frequency</b>
/// (the RDNA+ Tuning2 axes), each gated by ADLX's own per-GPU support flag; the NVIDIA-style offsets are
/// honestly referred to Adrenalin. Every native power/temp/frequency write is
/// confirmed by an immediate read-back before success is reported; the NVIDIA core/memory offset write
/// is a driver-clamped frequency delta (bounded to the card's V/F range), not a read-back-confirmed
/// value. Voltage is intentionally <i>never</i> applied. Nothing here ships a kernel driver, touches
/// the vBIOS, or unlocks voltage.
/// </summary>
public interface IGpuOcService
{
    /// <summary>Returns the current detected GPU and whether the native OC backend is available.</summary>
    Task<GpuOcBackendStatus> GetStatusAsync();

    /// <summary>Apply a GPU OC profile. Validated first; returns false + error when out of range or backend unavailable.</summary>
    Task<GpuOcApplyResult> ApplyAsync(GpuOcProfile profile);

    /// <summary>Read back the current state of every VERIFIED axis (offsets, and power/temp where their
    /// backend passed verification); unverified axes carry neutral placeholders. Null when unavailable.</summary>
    Task<GpuOcProfile?> ReadCurrentAsync();

    /// <summary>Reset every verified axis to its honest baseline: offsets to 0, power/temp to the card's
    /// own defaults (NVIDIA) or to the pre-Aurum value (AMD).</summary>
    Task<GpuOcApplyResult> ResetAsync();
}

/// <summary>Which verified native backend drives the power-limit axis — decides both capability and
/// the honest wording (NVAPI is community-documented; ADLX is AMD's official documented API).</summary>
public enum GpuPowerBackendKind
{
    None,
    NvapiCommunity,
    AdlxDocumented,
}

/// <summary><see cref="PowerBackend"/> is non-None ONLY after the on-card read verification passed
/// (window plausible + current value inside it) — never assumed from the vendor alone.
/// <see cref="PowerLimitDefaultPct"/> is the honest reset target: the card default on NVIDIA, and on
/// AMD the value read at Aurum startup (ADLX has no default getter, and ADLX tuning may persist across
/// reboots — so this is "the value when Aurum launched", not a guaranteed factory default).</summary>
public sealed record GpuOcBackendStatus(
    GpuVendor Vendor,
    string GpuName,
    bool BackendAvailable,
    string? BackendVersion = null,
    string? Message = null,
    GpuPowerBackendKind PowerBackend = GpuPowerBackendKind.None,
    int PowerLimitMinPct = 0,
    int PowerLimitMaxPct = 0,
    int PowerLimitDefaultPct = 100,
    bool TempLimitNative = false,
    int TempLimitMinC = 0,
    int TempLimitMaxC = 0,
    int TempLimitDefaultC = 0,
    bool GfxTuningNative = false,
    int GfxMaxFreqMinMhz = 0,
    int GfxMaxFreqMaxMhz = 0,
    int GfxMaxFreqDefaultMhz = 0,
    bool VramTuningNative = false,
    int VramMaxFreqMinMhz = 0,
    int VramMaxFreqMaxMhz = 0,
    int VramMaxFreqDefaultMhz = 0)
{
    /// <summary>Computed, single-source-of-truth: any verified backend drives the power axis.</summary>
    public bool PowerLimitNative => PowerBackend != GpuPowerBackendKind.None;
}

public sealed record GpuOcProfile(
    int CoreOffsetMhz,
    int MemoryOffsetMhz,
    int PowerLimitPct,
    int TempLimitC,
    int TargetVoltageMv)
{
    /// <summary>AMD GPU max frequency (ADLX Tuning2, driver scale). Non-positional so every existing
    /// 5-arg construction is unchanged; 0 = "not set / no change" (the VM only sends a real value on a
    /// verified AMD card, prefilled from the driver read).</summary>
    public int AmdMaxFreqMhz { get; init; }

    /// <summary>AMD max memory frequency (ADLX VRAM Tuning2, driver scale). Same neutral-0 contract.</summary>
    public int AmdMaxVramFreqMhz { get; init; }
}

/// <summary><see cref="Applied"/> is a short human summary of what was actually written (e.g. "core +150 MHz · mém +1200 MHz").</summary>
public sealed record GpuOcApplyResult(bool Success, string? Error = null, string? Applied = null);

/// <summary>
/// Pure, side-effect-free validation and clamping for GPU OC profiles. Deliberately split out of
/// <see cref="GpuOcService"/> so it can be unit-tested exhaustively without ever touching native
/// NVAPI — a native call with an in-range profile would really overclock the test machine's GPU.
/// </summary>
public static class GpuOcValidation
{
    public const int CoreMin = -500;
    public const int CoreMax = 1000;
    public const int MemMin = -2000;
    public const int MemMax = 3000;
    // Power/temp generic bounds are backstops for values that never reach a native write on their own
    // (power/temp only apply on a verified card, where the card window + clamp are authoritative). They
    // are deliberately the FULL plausibility envelope (GpuPowerLimit.IsPlausiblePcm 10–400 %,
    // GpuThermalLimit.IsPlausibleRaw 40–110 °C) so the generic gate can never reject a value the card's
    // own verified window offers — that mismatch was a dead slider range on real hardware.
    public const int PowerMin = 10;
    public const int PowerMax = 400;
    public const int TempMin = 40;
    public const int TempMax = 110;

    /// <summary>
    /// Backend-agnostic axes only (core/mem/temp). The power axis is deliberately NOT here: its valid
    /// window is backend-specific (NVIDIA absolute % vs AMD's Adrenalin-scale window, possibly negative),
    /// so each vendor path validates power against its own verified window instead.
    /// </summary>
    public static string? ValidateFrequencies(GpuOcProfile p)
    {
        if (p.CoreOffsetMhz < CoreMin || p.CoreOffsetMhz > CoreMax)
            return $"Offset core hors limites ({CoreMin} à +{CoreMax} MHz).";
        if (p.MemoryOffsetMhz < MemMin || p.MemoryOffsetMhz > MemMax)
            return $"Offset mémoire hors limites ({MemMin} à +{MemMax} MHz).";
        if (p.TempLimitC < TempMin || p.TempLimitC > TempMax)
            return $"Limite de température hors limites ({TempMin}–{TempMax} °C).";
        return null;
    }

    /// <summary>Full validation for the NVIDIA path (frequencies + the generic absolute-% power window).</summary>
    public static string? Validate(GpuOcProfile p)
    {
        var freq = ValidateFrequencies(p);
        if (freq is not null) return freq;
        if (p.PowerLimitPct < PowerMin || p.PowerLimitPct > PowerMax)
            return $"Power limit hors limites ({PowerMin}–{PowerMax} %).";
        return null;
    }

    /// <summary>Defense-in-depth: clamp every axis into range before a native write (the driver clamps too).</summary>
    public static GpuOcProfile Clamp(GpuOcProfile p) => p with
    {
        CoreOffsetMhz = Math.Clamp(p.CoreOffsetMhz, CoreMin, CoreMax),
        MemoryOffsetMhz = Math.Clamp(p.MemoryOffsetMhz, MemMin, MemMax),
        PowerLimitPct = Math.Clamp(p.PowerLimitPct, PowerMin, PowerMax),
        TempLimitC = Math.Clamp(p.TempLimitC, TempMin, TempMax)
    };
}

/// <summary>
/// Pure conversion and plausibility gates for the NVAPI client power-policies (power limit) backend.
/// That interface is community-documented, not NVIDIA-documented, so every value read from the driver
/// must pass these gates before Aurum trusts it — and the write path is only ever enabled after the
/// full read side proved plausible on the actual card. Side-effect-free → unit-testable.
/// </summary>
public static class GpuPowerLimit
{
    /// <summary>Per-cent-mille: the driver's unit, 100 000 = 100 %.</summary>
    public const int PcmPerPercent = 1000;

    public static int ToPcm(int pct) => pct * PcmPerPercent;

    public static int FromPcm(int pcm) => (int)Math.Round(pcm / (double)PcmPerPercent);

    /// <summary>A lone PCM value that could plausibly be a power limit: 10 % to 400 %.</summary>
    public static bool IsPlausiblePcm(int pcm) => pcm is >= 10_000 and <= 400_000;

    /// <summary>The min/default/max window reads sane: each bound plausible and correctly ordered.</summary>
    public static bool IsPlausibleWindow(int minPcm, int defPcm, int maxPcm) =>
        IsPlausiblePcm(minPcm) && IsPlausiblePcm(defPcm) && IsPlausiblePcm(maxPcm)
        && minPcm <= defPcm && defPcm <= maxPcm;

    /// <summary>The whole read side is coherent: plausible window AND the current target sits inside it
    /// AND the window contains at least one whole percent (so <see cref="ClampToCardPct"/> can never be
    /// asked to clamp into an empty integer range). This is the gate that turns the native write path on
    /// — anything less keeps it off.</summary>
    public static bool IsCoherent(int minPcm, int defPcm, int maxPcm, int currentPcm) =>
        IsPlausibleWindow(minPcm, defPcm, maxPcm) && currentPcm >= minPcm && currentPcm <= maxPcm
        && (int)Math.Ceiling(minPcm / (double)PcmPerPercent) <= (int)Math.Floor(maxPcm / (double)PcmPerPercent);

    /// <summary>The card's integer-% window bounds, rounded INWARD (ceil the min, floor the max) so every
    /// value they span is genuinely applyable. Single source of truth for BOTH the displayed slider bounds
    /// and the apply-time clamp — using <see cref="FromPcm"/> (Math.Round) for the displayed min would put
    /// a tick below the card's true floor that always gets bumped up.</summary>
    public static int CardMinPct(int minPcm) => (int)Math.Ceiling(minPcm / (double)PcmPerPercent);
    public static int CardMaxPct(int maxPcm) => (int)Math.Floor(maxPcm / (double)PcmPerPercent);

    /// <summary>Clamp a requested % into the card's own window. Safe only because <see cref="IsCoherent"/>
    /// guarantees ceil(min) ≤ floor(max) before this axis is ever enabled.</summary>
    public static int ClampToCardPct(int requestedPct, int minPcm, int maxPcm) =>
        Math.Clamp(requestedPct, CardMinPct(minPcm), CardMaxPct(maxPcm));
}

/// <summary>
/// Pure gates for the AMD ADLX power-limit window. ADLX is AMD's official documented API, but the
/// coherence gate still applies — a garbage read must keep the axis honestly unavailable, exactly
/// like the NVIDIA gates. Values live on Adrenalin's own scale (the driver's reported window, which
/// may be an offset range spanning negative values); Aurum round-trips values inside that window and
/// never re-interprets the scale. Side-effect-free → unit-testable.
/// </summary>
public static class AdlxPowerRange
{
    /// <summary>The window itself reads sane: ordered, within a credible span, step not degenerate.</summary>
    public static bool IsPlausible(int minPct, int maxPct, int stepPct) =>
        minPct < maxPct && minPct >= -100 && maxPct <= 400
        && stepPct >= 0 && stepPct <= maxPct - minPct;

    /// <summary>Plausible window AND the current value inside it — the gate enabling the native write path.</summary>
    public static bool IsCoherent(int minPct, int maxPct, int stepPct, int currentPct) =>
        IsPlausible(minPct, maxPct, stepPct) && currentPct >= minPct && currentPct <= maxPct;

    /// <summary>Clamp a requested value into the driver's window, aligned down onto the driver's step grid
    /// (anchored at min) so the driver never has a reason to silently round what Aurum sends.</summary>
    public static int Clamp(int requestedPct, int minPct, int maxPct, int stepPct)
    {
        int v = Math.Clamp(requestedPct, minPct, maxPct);
        if (stepPct > 1) v = minPct + (v - minPct) / stepPct * stepPct;
        return v;
    }
}

/// <summary>
/// Pure gates for the AMD ADLX GPU max-frequency window (MHz). Same trust model as the other backends:
/// the documented ADLX read must still pass a coherence gate before the write path is enabled. Values
/// are the driver's own — an absolute clock on pre-Navi4, an offset from base on Navi4+ — spanning a
/// wider envelope than the power scale (absolute clocks reach a few thousand MHz; offsets can be
/// negative). Aurum round-trips inside the reported window and never re-interprets the scale.
/// Side-effect-free → unit-testable.
/// </summary>
public static class GpuGfxRange
{
    /// <summary>Ordered window inside a credible frequency envelope (covers both absolute clocks and offsets).</summary>
    public static bool IsPlausible(int minMhz, int maxMhz, int stepMhz) =>
        minMhz < maxMhz && minMhz >= -2000 && maxMhz <= 6000
        && stepMhz >= 0 && stepMhz <= maxMhz - minMhz;

    /// <summary>Plausible window AND the current value inside it — the gate enabling the native write path.</summary>
    public static bool IsCoherent(int minMhz, int maxMhz, int stepMhz, int currentMhz) =>
        IsPlausible(minMhz, maxMhz, stepMhz) && currentMhz >= minMhz && currentMhz <= maxMhz;

    /// <summary>Clamp a requested MHz into the driver window, aligned down onto its step grid (anchored at min).</summary>
    public static int Clamp(int requestedMhz, int minMhz, int maxMhz, int stepMhz)
    {
        int v = Math.Clamp(requestedMhz, minMhz, maxMhz);
        if (stepMhz > 1) v = minMhz + (v - minMhz) / stepMhz * stepMhz;
        return v;
    }
}

/// <summary>
/// Pure conversion and plausibility gates for the NVAPI client thermal-policies (temperature target)
/// backend — the exact same trust model as <see cref="GpuPowerLimit"/>: community-documented layout,
/// so nothing is trusted until it reads plausible on the actual card, and the write path only exists
/// behind these gates. Values are °C in &lt;&lt;8 fixed point (256 = 1 °C). Side-effect-free → unit-testable.
/// </summary>
public static class GpuThermalLimit
{
    /// <summary>The driver's unit: °C × 256 (&lt;&lt;8 fixed point).</summary>
    public const int RawPerDegree = 256;

    public static int ToRaw(int celsius) => celsius * RawPerDegree;

    public static int FromRaw(int raw) => (int)Math.Round(raw / (double)RawPerDegree);

    /// <summary>A lone raw value that could plausibly be a GPU temp target: 40 °C to 110 °C.</summary>
    public static bool IsPlausibleRaw(int raw) => raw is >= 40 * RawPerDegree and <= 110 * RawPerDegree;

    /// <summary>The min/default/max window reads sane: each bound plausible and correctly ordered.</summary>
    public static bool IsPlausibleWindow(int minRaw, int defRaw, int maxRaw) =>
        IsPlausibleRaw(minRaw) && IsPlausibleRaw(defRaw) && IsPlausibleRaw(maxRaw)
        && minRaw <= defRaw && defRaw <= maxRaw;

    /// <summary>Plausible window AND the current target inside it AND the window contains at least one
    /// whole degree (so <see cref="ClampToCardC"/> is never asked to clamp into an empty integer range)
    /// — the gate that turns the native write path on.</summary>
    public static bool IsCoherent(int minRaw, int defRaw, int maxRaw, int currentRaw) =>
        IsPlausibleWindow(minRaw, defRaw, maxRaw) && currentRaw >= minRaw && currentRaw <= maxRaw
        && (int)Math.Ceiling(minRaw / (double)RawPerDegree) <= (int)Math.Floor(maxRaw / (double)RawPerDegree);

    /// <summary>The card's integer-°C window bounds, rounded INWARD — single source of truth for both the
    /// displayed slider bounds and the clamp (see <see cref="GpuPowerLimit.CardMinPct"/> for the why).</summary>
    public static int CardMinC(int minRaw) => (int)Math.Ceiling(minRaw / (double)RawPerDegree);
    public static int CardMaxC(int maxRaw) => (int)Math.Floor(maxRaw / (double)RawPerDegree);

    /// <summary>Clamp a requested °C into the card's own window (raw bounds from GetInfo), rounded inward.
    /// Safe only because <see cref="IsCoherent"/> guarantees ceil(min) ≤ floor(max) before enablement.</summary>
    public static int ClampToCardC(int requestedC, int minRaw, int maxRaw) =>
        Math.Clamp(requestedC, CardMinC(minRaw), CardMaxC(maxRaw));
}

/// <summary>
/// Pure honesty helper for the OC page. Which axes the native backend genuinely applies varies by
/// machine (NVIDIA: offsets always, power/temp when their community layouts verified on-card;
/// AMD: power only, via the documented ADLX API), and voltage is deliberately never applied. The page
/// still shows the non-applied sliders (genuinely useful as the numbers to dial into MSI Afterburner /
/// Adrenalin), so this single-sources the disclosure that keeps them from reading as Aurum-applied —
/// turning a would-be dead control into an honest "set this elsewhere" reference.
/// Side-effect-free → unit-testable.
/// </summary>
public static class GpuOcDisclosure
{
    /// <summary>Stock power limit (%): no change requested, so that axis has nothing to disclose.</summary>
    public const int NeutralPowerLimitPct = 100;

    /// <summary>Always-visible note under the sliders — states plainly what Aurum applies vs. what it never
    /// does, switching on which axes passed their on-card verification and which backend drives power (the
    /// NVAPI caveat says "undocumented"; ADLX is AMD's documented API and must not carry that caveat). The
    /// temperature clause only ever claims (never disclaims): the temp slider row is hidden entirely when
    /// unverified, so there is no visible control to disown.</summary>
    public static string SlidersNote(bool offsetsNative, GpuPowerBackendKind power, bool tempNative,
                                     bool gfxNative = false, bool vramNative = false)
    {
        bool powerNative = power != GpuPowerBackendKind.None;

        var applied = new List<string>();
        if (offsetsNative) applied.Add("les offsets core et mémoire");
        if (powerNative) applied.Add("le power limit");
        if (gfxNative) applied.Add("la fréquence GPU max");
        if (vramNative) applied.Add("la fréquence mémoire max");
        if (tempNative) applied.Add("la cible de température");
        if (applied.Count == 0)
            return "Aucun backend GPU natif vérifié sur cette machine — règle l'overclocking dans "
                 + "MSI Afterburner (NVIDIA) ou Adrenalin (AMD).";

        // The caveat is built compositionally so it can only ever attribute read-back to the axes that
        // genuinely have it (power/temp/gfx — NOT the NVIDIA offsets, whose write is driver-clamped, not
        // read-back confirmed) and can never paint the documented ADLX API as an undocumented NVAPI one.
        var clauses = new List<string>();

        // NVAPI community axes (power when driven by NVAPI, temperature) — read-back confirmed AND
        // flagged as undocumented-by-NVIDIA-but-community-standard.
        var nvapiAxes = new List<string>();
        if (power == GpuPowerBackendKind.NvapiCommunity) nvapiAxes.Add("power limit");
        if (tempNative) nvapiAxes.Add("température");
        if (nvapiAxes.Count > 0)
        {
            string axes = string.Join(" / ", nvapiAxes);
            clauses.Add(nvapiAxes.Count == 1
                ? $"NVAPI : l'écriture {axes} est confirmée par relecture ; cette interface n'est pas "
                  + "documentée par NVIDIA mais c'est celle qu'utilisent les outils d'overclocking"
                : $"NVAPI : les écritures {axes} sont confirmées par relecture ; ces interfaces ne sont pas "
                  + "documentées par NVIDIA mais ce sont celles qu'utilisent les outils d'overclocking");
        }

        // ADLX axes (AMD's official documented API: power limit / GPU max freq / memory max freq) —
        // read-back confirmed, never labelled undocumented.
        var adlxAxes = new List<string>();
        if (power == GpuPowerBackendKind.AdlxDocumented) adlxAxes.Add("power limit");
        if (gfxNative) adlxAxes.Add("fréquence GPU max");
        if (vramNative) adlxAxes.Add("fréquence mémoire max");
        if (adlxAxes.Count > 0)
        {
            string axes = string.Join(" / ", adlxAxes);
            clauses.Add(adlxAxes.Count == 1
                ? $"ADLX (API AMD officielle) : l'écriture {axes} est confirmée par relecture"
                : $"ADLX (API AMD officielle) : les écritures {axes} sont confirmées par relecture");
        }

        // NVIDIA offsets are applied via NVAPI too, but as driver-clamped deltas — no read-back claim.
        string caveat = clauses.Count > 0 ? $" ({string.Join(" ; ", clauses)})." : " (NVAPI).";

        var referred = new List<string>();
        if (!offsetsNative) referred.Add("offsets core/mémoire");
        if (!powerNative) referred.Add("power limit");
        referred.Add("voltage");
        string referredNote = $" Non appliqué(s) par Aurum : {string.Join(", ", referred)} — à régler dans "
                            + "MSI Afterburner (NVIDIA) ou Adrenalin (AMD).";

        return $"Aurum applique nativement {string.Join(", ", applied)}{caveat}{referredNote}";
    }

    /// <summary>
    /// The honest "these were not applied" clause to append after a successful apply, or empty when there is
    /// nothing to disclose. Power limit counts only when moved off 100 % (stock) AND the native power backend
    /// is unavailable — when it's verified-native the axis genuinely applies, so it must NOT be disclaimed.
    /// Voltage counts whenever a target is set, since Aurum never writes voltage. Keeps the apply status from
    /// implying a slider did something it didn't.
    /// </summary>
    public static string IgnoredAxesNote(GpuOcProfile p, bool powerNative)
    {
        var axes = new List<string>();
        if (!powerNative && p.PowerLimitPct != NeutralPowerLimitPct) axes.Add("power limit");
        if (p.TargetVoltageMv > 0) axes.Add("voltage");
        return axes.Count == 0
            ? string.Empty
            : $"{string.Join(" et ", axes)} non appliqué(s) par Aurum (à régler dans Afterburner/Adrenalin).";
    }
}

/// <summary>
/// Default implementation. NVIDIA offsets go through <see cref="NvApi"/>; everything else is reported
/// honestly rather than faked. The NVAPI handle is probed lazily and cached so a non-NVIDIA machine
/// pays the lookup once and then simply reports "unavailable".
/// </summary>
public sealed class GpuOcService : IGpuOcService
{
    private readonly IHardwareService _hardware;
    private readonly INvApi _nv;
    private readonly IAdlxApi _adlx;

    private bool _nvProbed;
    private IntPtr _nvGpu = IntPtr.Zero;
    private string _nvName = string.Empty;

    private bool _pwrProbed;
    private bool _pwrOk;
    private int _pwrMinPcm;
    private int _pwrDefPcm;
    private int _pwrMaxPcm;

    private bool _thermProbed;
    private bool _thermOk;
    private int _thermMinRaw;
    private int _thermDefRaw;
    private int _thermMaxRaw;

    private bool _adlxProbed;
    private bool _adlxOk;
    private AdlxGpuInfo? _adlxInfo;
    private int _adlxMinPct;
    private int _adlxMaxPct;
    private int _adlxStepPct;
    private int _adlxInitialPct;   // value read at Aurum startup — the honest AMD reset target (ADLX has no default getter)

    private bool _gfxProbed;
    private bool _gfxOk;
    private int _gfxMinMhz;
    private int _gfxMaxMhz;
    private int _gfxStepMhz;
    private int _gfxInitialMhz;    // GPU max freq read at startup — the honest reset target (ADLX has no default getter)

    private bool _vramProbed;
    private bool _vramOk;
    private int _vramMinMhz;
    private int _vramMaxMhz;
    private int _vramStepMhz;
    private int _vramInitialMhz;   // memory max freq read at startup — the honest reset target

    public GpuOcService(IHardwareService hardware, INvApi nv, IAdlxApi adlx)
    {
        _hardware = hardware;
        _nv = nv;
        _adlx = adlx;
    }

    /// <summary>Lazily resolve (and cache) the first NVAPI-drivable NVIDIA GPU handle. Never throws.</summary>
    private bool EnsureNvidia()
    {
        if (_nvProbed) return _nvGpu != IntPtr.Zero;
        _nvProbed = true;
        if (_nv.TryGetFirstGpu(out var gpu, out var name) && gpu != IntPtr.Zero)
        {
            _nvGpu = gpu;
            _nvName = name;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Lazily verify (once) that the community power-policies layout holds on THIS card: the
    /// min/default/max window must read plausible AND the current target must sit inside it.
    /// Anything less → the power axis honestly reports unavailable instead of trusting an
    /// undocumented layout blindly. Read-only — never writes during verification.
    /// </summary>
    private bool EnsurePowerVerified()
    {
        if (_pwrProbed) return _pwrOk;
        _pwrProbed = true;
        _pwrOk = _nv.TryReadPowerInfo(_nvGpu, out _pwrMinPcm, out _pwrDefPcm, out _pwrMaxPcm)
              && _nv.TryReadPowerTarget(_nvGpu, out int currentPcm)
              && GpuPowerLimit.IsCoherent(_pwrMinPcm, _pwrDefPcm, _pwrMaxPcm, currentPcm);
        return _pwrOk;
    }

    /// <summary>Same on-card verification contract as <see cref="EnsurePowerVerified"/>, for the
    /// temperature target. Read-only — never writes during verification.</summary>
    private bool EnsureThermalVerified()
    {
        if (_thermProbed) return _thermOk;
        _thermProbed = true;
        _thermOk = _nv.TryReadThermalInfo(_nvGpu, out _thermMinRaw, out _thermDefRaw, out _thermMaxRaw)
                && _nv.TryReadThermalLimit(_nvGpu, out int currentRaw)
                && GpuThermalLimit.IsCoherent(_thermMinRaw, _thermDefRaw, _thermMaxRaw, currentRaw);
        return _thermOk;
    }

    /// <summary>
    /// Lazily verify (once) the AMD ADLX power-limit path: ADLX must initialize, report
    /// IsSupportedManualPowerTuning for the GPU (AMD's own per-GPU gate — never guessed), and the
    /// window + current value must read coherent. The initial value is captured here as the honest
    /// reset target. Read-only — never writes during verification.
    /// </summary>
    private bool EnsureAmdPowerVerified()
    {
        if (_adlxProbed) return _adlxOk;
        _adlxProbed = true;
        _adlxOk = _adlx.TryGetFirstGpuInfo(out var info)
               && info.ManualPowerTuningSupported
               && _adlx.TryReadPowerLimit(out _adlxInitialPct, out _adlxMinPct, out _adlxMaxPct, out _adlxStepPct);
        if (_adlxOk) _adlxInfo = info;
        return _adlxOk;
    }

    /// <summary>
    /// Lazily verify (once) the AMD ADLX GPU max-frequency path: ADLX must report
    /// IsSupportedManualGFXTuning for the GPU (its own gate), the RDNA+ Tuning2 interface must be
    /// acquirable, and the window + current value must read coherent. Startup value captured as the
    /// honest reset target. Read-only — never writes during verification.
    /// </summary>
    private bool EnsureAmdGfxVerified()
    {
        if (_gfxProbed) return _gfxOk;
        _gfxProbed = true;
        _gfxOk = _adlx.TryGetFirstGpuInfo(out var info)
              && info.ManualGfxTuningSupported
              && _adlx.TryReadGfxMaxFreq(out _gfxInitialMhz, out _gfxMinMhz, out _gfxMaxMhz, out _gfxStepMhz);
        return _gfxOk;
    }

    /// <summary>Same on-card verification contract as <see cref="EnsureAmdGfxVerified"/>, for the AMD
    /// max memory frequency (ADLX VRAM Tuning2). Read-only — never writes during verification.</summary>
    private bool EnsureAmdVramVerified()
    {
        if (_vramProbed) return _vramOk;
        _vramProbed = true;
        _vramOk = _adlx.TryGetFirstGpuInfo(out var info)
               && info.ManualVramTuningSupported
               && _adlx.TryReadVramMaxFreq(out _vramInitialMhz, out _vramMinMhz, out _vramMaxMhz, out _vramStepMhz);
        return _vramOk;
    }

    public async Task<GpuOcBackendStatus> GetStatusAsync()
    {
        var hw = await _hardware.DetectAsync();
        switch (hw.GpuVendor)
        {
            case GpuVendor.Nvidia:
                if (EnsureNvidia())
                {
                    bool pwr = EnsurePowerVerified();
                    bool therm = EnsureThermalVerified();

                    var axes = "offsets core/mémoire (P0)";
                    if (pwr) axes += ", power limit";
                    if (therm) axes += ", cible température";

                    // The undocumented-interface + read-back clause names ONLY the axes that actually
                    // verified (a fixed "power/température" string would overclaim the inactive one).
                    var nvVerified = new List<string>();
                    if (pwr) nvVerified.Add("power limit");
                    if (therm) nvVerified.Add("température");
                    string readBackClause = nvVerified.Count switch
                    {
                        0 => "",
                        1 => $"Interface {nvVerified[0]} non documentée par NVIDIA mais utilisée par les outils "
                           + "d'OC ; chaque écriture est confirmée par relecture. ",
                        _ => $"Interfaces {string.Join(" / ", nvVerified)} non documentées par NVIDIA mais utilisées "
                           + "par les outils d'OC ; chaque écriture est confirmée par relecture. ",
                    };

                    string message =
                        $"NVAPI actif — {axes} applicables nativement. Offsets bornés par le driver, réglages "
                        + "non-persistants (remis à zéro au redémarrage), aucun déverrouillage de voltage. "
                        + (pwr
                            ? $"Power limit vérifié en lecture (fenêtre {GpuPowerLimit.CardMinPct(_pwrMinPcm)}–"
                              + $"{GpuPowerLimit.CardMaxPct(_pwrMaxPcm)} %, défaut {GpuPowerLimit.FromPcm(_pwrDefPcm)} %). "
                            : "Power limit : lecture non vérifiée sur cette carte → non appliqué (utiliser Afterburner). ")
                        + (therm
                            ? $"Cible température vérifiée en lecture (fenêtre {GpuThermalLimit.CardMinC(_thermMinRaw)}–"
                              + $"{GpuThermalLimit.CardMaxC(_thermMaxRaw)} °C, défaut {GpuThermalLimit.FromRaw(_thermDefRaw)} °C). "
                            : "Cible température : lecture non vérifiée → non appliquée. ")
                        + readBackClause
                        + "Voltage : jamais appliqué par Aurum.";

                    return new GpuOcBackendStatus(
                        GpuVendor.Nvidia,
                        string.IsNullOrEmpty(_nvName) ? hw.GpuPrimary : _nvName,
                        BackendAvailable: true,
                        BackendVersion: hw.GpuDriverVersion,
                        Message: message,
                        PowerBackend: pwr ? GpuPowerBackendKind.NvapiCommunity : GpuPowerBackendKind.None,
                        PowerLimitMinPct: pwr ? GpuPowerLimit.CardMinPct(_pwrMinPcm) : 0,
                        PowerLimitMaxPct: pwr ? GpuPowerLimit.CardMaxPct(_pwrMaxPcm) : 0,
                        PowerLimitDefaultPct: pwr ? GpuPowerLimit.FromPcm(_pwrDefPcm) : 100,
                        TempLimitNative: therm,
                        TempLimitMinC: therm ? GpuThermalLimit.CardMinC(_thermMinRaw) : 0,
                        TempLimitMaxC: therm ? GpuThermalLimit.CardMaxC(_thermMaxRaw) : 0,
                        TempLimitDefaultC: therm ? GpuThermalLimit.FromRaw(_thermDefRaw) : 0);
                }
                return new GpuOcBackendStatus(
                    GpuVendor.Nvidia, hw.GpuPrimary,
                    BackendAvailable: false,
                    BackendVersion: hw.GpuDriverVersion,
                    Message: "GPU NVIDIA détecté mais NVAPI n'a pas pu être initialisé (pilote absent ou trop ancien).");

            case GpuVendor.Amd:
            {
                bool amdPwr = EnsureAmdPowerVerified();
                bool amdGfx = EnsureAmdGfxVerified();
                bool amdVram = EnsureAmdVramVerified();
                bool adlxUp = _adlx.TryGetFirstGpuInfo(out var amdInfo);
                string amdName = adlxUp && !string.IsNullOrEmpty(amdInfo.Name) ? amdInfo.Name : hw.GpuPrimary;

                if (amdPwr || amdGfx || amdVram)
                {
                    var natives = new List<string>();
                    if (amdPwr) natives.Add("power limit");
                    if (amdGfx) natives.Add("fréquence GPU max");
                    if (amdVram) natives.Add("fréquence mémoire max");
                    string message =
                        $"ADLX actif (API AMD officielle) — {string.Join(", ", natives)} applicable(s) nativement. "
                        + (amdPwr
                            ? $"Power limit : fenêtre driver {_adlxMinPct} à {_adlxMaxPct} (échelle Adrenalin), "
                              + $"lancement {_adlxInitialPct}. "
                            : "")
                        + (amdGfx
                            ? $"Fréquence GPU max : fenêtre {_gfxMinMhz} à {_gfxMaxMhz} MHz (échelle driver), "
                              + $"lancement {_gfxInitialMhz} MHz. "
                            : "")
                        + (amdVram
                            ? $"Fréquence mémoire max : fenêtre {_vramMinMhz} à {_vramMaxMhz} MHz (échelle driver), "
                              + $"lancement {_vramInitialMhz} MHz. "
                            : "")
                        + "Chaque écriture est confirmée par relecture ; « Réinitialiser » restaure les valeurs "
                        + "de lancement (le réglage ADLX peut persister au redémarrage, comme dans Adrenalin). "
                        + (amdVram ? "" : "Fréquence mémoire : non appliquée → Adrenalin. ")
                        + "Voltage : jamais appliqué par Aurum.";

                    return new GpuOcBackendStatus(
                        GpuVendor.Amd, amdName,
                        BackendAvailable: true,
                        BackendVersion: hw.GpuDriverVersion,
                        Message: message,
                        PowerBackend: amdPwr ? GpuPowerBackendKind.AdlxDocumented : GpuPowerBackendKind.None,
                        PowerLimitMinPct: amdPwr ? _adlxMinPct : 0,
                        PowerLimitMaxPct: amdPwr ? _adlxMaxPct : 0,
                        PowerLimitDefaultPct: amdPwr ? _adlxInitialPct : 100,
                        GfxTuningNative: amdGfx,
                        GfxMaxFreqMinMhz: amdGfx ? _gfxMinMhz : 0,
                        GfxMaxFreqMaxMhz: amdGfx ? _gfxMaxMhz : 0,
                        GfxMaxFreqDefaultMhz: amdGfx ? _gfxInitialMhz : 0,
                        VramTuningNative: amdVram,
                        VramMaxFreqMinMhz: amdVram ? _vramMinMhz : 0,
                        VramMaxFreqMaxMhz: amdVram ? _vramMaxMhz : 0,
                        VramMaxFreqDefaultMhz: amdVram ? _vramInitialMhz : 0);
                }

                // Honest reason, branched on WHICH stage of verification actually failed — never
                // blaming AMD's support flags when it was really the window read that was unusable.
                string reason;
                if (!adlxUp)
                    reason = "OC AMD non implémenté nativement — ADLX indisponible (driver Adrenalin absent ou "
                           + "trop ancien). Utiliser AMD Adrenalin (Performances › Tuning).";
                else if (!amdInfo.ManualPowerTuningSupported && !amdInfo.ManualGfxTuningSupported && !amdInfo.ManualVramTuningSupported)
                    reason = $"ADLX initialisé (API AMD officielle) mais ce GPU{(amdInfo.IsIntegrated ? " intégré" : string.Empty)} "
                           + "ne déclare ni le tuning power limit ni le tuning fréquence (IsSupported* = non). "
                           + "OC AMD → Adrenalin (Performances › Tuning).";
                else
                    // A support flag said yes but the corresponding window read was incoherent (or the
                    // RDNA+ Tuning2 interface was unavailable) — honest about the real cause.
                    reason = "ADLX initialisé et ce GPU déclare supporter le tuning, mais la ou les fenêtres lues "
                           + "via le driver sont incohérentes (ou l'interface RDNA+ est absente) → non appliqué "
                           + "par prudence. OC AMD → Adrenalin (Performances › Tuning).";
                return new GpuOcBackendStatus(
                    GpuVendor.Amd, amdName,
                    BackendAvailable: false,
                    BackendVersion: hw.GpuDriverVersion,
                    Message: reason);
            }

            default:
                return new GpuOcBackendStatus(
                    GpuVendor.Unknown, hw.GpuPrimary, false, null,
                    "Aucun GPU NVIDIA ou AMD pilotable détecté — overclocking désactivé.");
        }
    }

    public async Task<GpuOcApplyResult> ApplyAsync(GpuOcProfile profile)
    {
        // 1. Backend-agnostic validation BEFORE touching anything native (core/mem/temp). The power
        //    window is backend-specific — NVIDIA's absolute % vs AMD's Adrenalin-scale window — so
        //    the power axis is validated/clamped inside each vendor path instead.
        var error = GpuOcValidation.ValidateFrequencies(profile);
        if (error is not null)
            return new GpuOcApplyResult(false, error);

        // Route by the SAME detected vendor the page displays (GetStatusAsync switches on it). Both
        // branches are gated on it symmetrically — otherwise, on an AMD-primary rig with a secondary
        // NVAPI-drivable NVIDIA card, EnsureNvidia() would succeed and Aurum would write to a GPU the
        // page never mentioned.
        var vendor = (await _hardware.DetectAsync()).GpuVendor;

        // 2. NVIDIA path: offsets always; power/temp only where their on-card verification passed.
        if (vendor == GpuVendor.Nvidia && EnsureNvidia())
        {
            error = GpuOcValidation.Validate(profile);   // adds the generic absolute-% power window
            if (error is not null)
                return new GpuOcApplyResult(false, error);

            // Defense-in-depth clamp, then write the core/memory offsets.
            var clamped = GpuOcValidation.Clamp(profile);
            if (!_nv.TrySetOffsets(_nvGpu, clamped.CoreOffsetMhz, clamped.MemoryOffsetMhz, out var nvError))
                return new GpuOcApplyResult(false, nvError);

            var applied = $"core {clamped.CoreOffsetMhz:+#;-#;0} MHz · mém {clamped.MemoryOffsetMhz:+#;-#;0} MHz";

            // Power limit — only on a card where the layout passed its read verification. 100 % is a
            // valid target there (= back to stock), so the write is unconditional, not gated on ≠100.
            if (EnsurePowerVerified())
            {
                int targetPct = GpuPowerLimit.ClampToCardPct(clamped.PowerLimitPct, _pwrMinPcm, _pwrMaxPcm);
                if (!_nv.TrySetPowerTarget(_nvGpu, GpuPowerLimit.ToPcm(targetPct), out var pwrError))
                    // Partial-state honesty: the offsets DID apply — say so inside the failure message
                    // rather than pretending an all-or-nothing outcome.
                    return new GpuOcApplyResult(false,
                        $"Offsets appliqués ({applied}) mais power limit NON appliqué : {pwrError}");

                applied += targetPct == clamped.PowerLimitPct
                    ? $" · power limit {targetPct} % (confirmé par relecture)"
                    : $" · power limit {clamped.PowerLimitPct} % demandé → {targetPct} % (borné par la carte, confirmé)";
            }

            // Temperature target — same contract as power: only on a verified card, always confirmed.
            if (EnsureThermalVerified())
            {
                int targetC = GpuThermalLimit.ClampToCardC(clamped.TempLimitC, _thermMinRaw, _thermMaxRaw);
                if (!_nv.TrySetThermalLimit(_nvGpu, GpuThermalLimit.ToRaw(targetC), out var thermError))
                    return new GpuOcApplyResult(false,
                        $"Appliqué partiellement ({applied}) mais cible température NON appliquée : {thermError}");

                applied += targetC == clamped.TempLimitC
                    ? $" · cible température {targetC} °C (confirmé par relecture)"
                    : $" · cible température {clamped.TempLimitC} °C demandée → {targetC} °C (bornée par la carte, confirmée)";
            }

            Serilog.Log.Information("GPU OC applied via NVAPI: {Applied}", applied);
            return new GpuOcApplyResult(true, null, applied);
        }

        // 3. AMD path: power limit and/or GPU/memory max frequency via ADLX (AMD's documented API), each
        //    only where its on-card verification passed. NVIDIA-style core/mem offsets aren't applied here.
        if (vendor == GpuVendor.Amd && (EnsureAmdPowerVerified() || EnsureAmdGfxVerified() || EnsureAmdVramVerified()))
        {
            var parts = new List<string>();

            if (EnsureAmdPowerVerified())
            {
                int target = AdlxPowerRange.Clamp(profile.PowerLimitPct, _adlxMinPct, _adlxMaxPct, _adlxStepPct);
                if (!_adlx.TrySetPowerLimit(target, out var powerError))
                    return new GpuOcApplyResult(false, $"Power limit AMD NON appliqué : {powerError}");
                parts.Add(target == profile.PowerLimitPct
                    ? $"power limit {target} (ADLX, confirmé par relecture)"
                    : $"power limit {profile.PowerLimitPct} demandé → {target} (borné par le driver, confirmé)");
            }

            if (EnsureAmdGfxVerified())
            {
                int target = GpuGfxRange.Clamp(profile.AmdMaxFreqMhz, _gfxMinMhz, _gfxMaxMhz, _gfxStepMhz);
                if (!_adlx.TrySetGfxMaxFreq(target, out var gfxError))
                    // Partial-state honesty: whatever already applied (e.g. power) is named in the failure.
                    return new GpuOcApplyResult(false, parts.Count > 0
                        ? $"Appliqué partiellement ({string.Join(" · ", parts)}) mais fréquence GPU max NON appliquée : {gfxError}"
                        : $"Fréquence GPU max AMD NON appliquée : {gfxError}");
                parts.Add(target == profile.AmdMaxFreqMhz
                    ? $"fréquence GPU max {target} MHz (ADLX, confirmé par relecture)"
                    : $"fréquence GPU max {profile.AmdMaxFreqMhz} MHz demandée → {target} MHz (bornée par le driver, confirmée)");
            }

            if (EnsureAmdVramVerified())
            {
                int target = GpuGfxRange.Clamp(profile.AmdMaxVramFreqMhz, _vramMinMhz, _vramMaxMhz, _vramStepMhz);
                if (!_adlx.TrySetVramMaxFreq(target, out var vramError))
                    return new GpuOcApplyResult(false, parts.Count > 0
                        ? $"Appliqué partiellement ({string.Join(" · ", parts)}) mais fréquence mémoire NON appliquée : {vramError}"
                        : $"Fréquence mémoire AMD NON appliquée : {vramError}");
                parts.Add(target == profile.AmdMaxVramFreqMhz
                    ? $"fréquence mémoire max {target} MHz (ADLX, confirmé par relecture)"
                    : $"fréquence mémoire max {profile.AmdMaxVramFreqMhz} MHz demandée → {target} MHz (bornée par le driver, confirmée)");
            }

            var appliedAmd = string.Join(" · ", parts);
            // The NVIDIA-style core/mem offset sliders exist on the page — a successful AMD apply must
            // say they did nothing (the AMD core clock is the separate ADLX max-frequency control).
            if (profile.CoreOffsetMhz != 0 || profile.MemoryOffsetMhz != 0)
                appliedAmd += " — offsets core/mémoire (style NVIDIA) non appliqués sur AMD (utiliser Adrenalin)";

            Serilog.Log.Information("GPU OC applied via ADLX: {Applied}", appliedAmd);
            return new GpuOcApplyResult(true, null, appliedAmd);
        }

        return new GpuOcApplyResult(false,
            "Application native indisponible : aucun backend GPU vérifié (NVIDIA NVAPI ou AMD ADLX) — "
            + "utiliser Afterburner ou Adrenalin.");
    }

    public async Task<GpuOcProfile?> ReadCurrentAsync()
    {
        var vendor = (await _hardware.DetectAsync()).GpuVendor;

        if (vendor == GpuVendor.Nvidia && EnsureNvidia() && _nv.TryReadOffsets(_nvGpu, out int core, out int mem))
        {
            // Power/temp are reported only from a verified read; voltage is never read natively —
            // unverified axes carry neutral placeholders (100 / 0 / 0), never invented measurements.
            int powerPct = EnsurePowerVerified() && _nv.TryReadPowerTarget(_nvGpu, out int pcm)
                ? GpuPowerLimit.FromPcm(pcm)
                : 100;
            int tempC = EnsureThermalVerified() && _nv.TryReadThermalLimit(_nvGpu, out int raw)
                ? GpuThermalLimit.FromRaw(raw)
                : 0;
            return new GpuOcProfile(core, mem, powerPct, tempC, 0);
        }
        // AMD: report the verified axes (power / gfx max freq); everything else is a neutral placeholder.
        // Return a profile ONLY when every verified axis reads cleanly — otherwise null, so the VM
        // prefills both from the captured startup defaults (real measurements, right scale) rather than
        // a placeholder that a blind apply could clamp to an extreme.
        if (vendor == GpuVendor.Amd)
        {
            int power = 100, gfx = 0, vram = 0;
            bool pv = EnsureAmdPowerVerified();
            bool gv = EnsureAmdGfxVerified();
            bool vv = EnsureAmdVramVerified();
            bool pOk = !pv || _adlx.TryReadPowerLimit(out power, out _, out _, out _);
            bool gOk = !gv || _adlx.TryReadGfxMaxFreq(out gfx, out _, out _, out _);
            bool vOk = !vv || _adlx.TryReadVramMaxFreq(out vram, out _, out _, out _);
            if ((pv || gv || vv) && pOk && gOk && vOk)
                return new GpuOcProfile(0, 0, power, 0, 0) { AmdMaxFreqMhz = gfx, AmdMaxVramFreqMhz = vram };
        }
        return null;
    }

    public async Task<GpuOcApplyResult> ResetAsync()
    {
        var vendor = (await _hardware.DetectAsync()).GpuVendor;

        if (vendor == GpuVendor.Nvidia && EnsureNvidia())
        {
            if (!_nv.TrySetOffsets(_nvGpu, 0, 0, out var nvError))
                return new GpuOcApplyResult(false, nvError);

            var summary = "offsets remis à 0";
            if (EnsurePowerVerified())
            {
                // Reset means the card's own default (read from GetInfo), not an assumed 100 %.
                if (!_nv.TrySetPowerTarget(_nvGpu, _pwrDefPcm, out var pwrError))
                    return new GpuOcApplyResult(false,
                        $"Offsets remis à 0, mais power limit NON remis au défaut : {pwrError}");
                summary += $" · power limit remis au défaut carte ({GpuPowerLimit.FromPcm(_pwrDefPcm)} %)";
            }
            if (EnsureThermalVerified())
            {
                if (!_nv.TrySetThermalLimit(_nvGpu, _thermDefRaw, out var thermError))
                    return new GpuOcApplyResult(false,
                        $"Réinitialisation partielle ({summary}), mais cible température NON remise au défaut : {thermError}");
                summary += $" · cible température remise au défaut carte ({GpuThermalLimit.FromRaw(_thermDefRaw)} °C)";
            }

            Serilog.Log.Information("GPU OC reset via NVAPI: {Summary}", summary);
            return new GpuOcApplyResult(true, null, summary);
        }

        if (vendor == GpuVendor.Amd && (EnsureAmdPowerVerified() || EnsureAmdGfxVerified() || EnsureAmdVramVerified()))
        {
            // ADLX has no default getter — the honest reset target for each axis is the value read at
            // Aurum startup (captured at verification time), not a guaranteed factory default.
            var parts = new List<string>();
            if (EnsureAmdPowerVerified())
            {
                if (!_adlx.TrySetPowerLimit(_adlxInitialPct, out var powerError))
                    return new GpuOcApplyResult(false, $"Power limit AMD NON restauré : {powerError}");
                parts.Add($"power limit → {_adlxInitialPct}");
            }
            if (EnsureAmdGfxVerified())
            {
                if (!_adlx.TrySetGfxMaxFreq(_gfxInitialMhz, out var gfxError))
                    return new GpuOcApplyResult(false, parts.Count > 0
                        ? $"Réinitialisation partielle ({string.Join(" · ", parts)}), mais fréquence GPU max NON restaurée : {gfxError}"
                        : $"Fréquence GPU max AMD NON restaurée : {gfxError}");
                parts.Add($"fréquence GPU max → {_gfxInitialMhz} MHz");
            }
            if (EnsureAmdVramVerified())
            {
                if (!_adlx.TrySetVramMaxFreq(_vramInitialMhz, out var vramError))
                    return new GpuOcApplyResult(false, parts.Count > 0
                        ? $"Réinitialisation partielle ({string.Join(" · ", parts)}), mais fréquence mémoire NON restaurée : {vramError}"
                        : $"Fréquence mémoire AMD NON restaurée : {vramError}");
                parts.Add($"fréquence mémoire max → {_vramInitialMhz} MHz");
            }

            var summary = $"restauré aux valeurs relevées au lancement d'Aurum : {string.Join(" · ", parts)} (ADLX confirmé)";
            Serilog.Log.Information("GPU OC reset via ADLX: {Summary}", summary);
            return new GpuOcApplyResult(true, null, summary);
        }

        return new GpuOcApplyResult(false,
            "Réinitialisation native indisponible : aucun backend GPU vérifié (NVIDIA NVAPI ou AMD ADLX).");
    }
}
