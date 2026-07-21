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
/// official documented API): <b>power limit</b> only, gated by ADLX's own per-GPU support flag;
/// offsets are honestly referred to Adrenalin. The <b>power-limit and temperature</b> writes are
/// confirmed by an immediate read-back before success is reported; the core/memory offset write is a
/// driver-clamped frequency delta (the driver bounds it to the card's V/F range), not a read-back-
/// confirmed value. Voltage is intentionally <i>never</i> applied. Nothing here ships a kernel driver,
/// touches the vBIOS, or unlocks voltage.
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
    int TempLimitDefaultC = 0)
{
    /// <summary>Computed, single-source-of-truth: any verified backend drives the power axis.</summary>
    public bool PowerLimitNative => PowerBackend != GpuPowerBackendKind.None;
}

public sealed record GpuOcProfile(
    int CoreOffsetMhz,
    int MemoryOffsetMhz,
    int PowerLimitPct,
    int TempLimitC,
    int TargetVoltageMv);

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

    /// <summary>Clamp a requested % into the card's own window (PCM bounds from GetInfo), conservatively
    /// rounded inward so the clamped value is always genuinely inside the window. Safe only because
    /// <see cref="IsCoherent"/> guarantees ceil(min) ≤ floor(max) before this axis is ever enabled.</summary>
    public static int ClampToCardPct(int requestedPct, int minPcm, int maxPcm) =>
        Math.Clamp(requestedPct,
            (int)Math.Ceiling(minPcm / (double)PcmPerPercent),
            (int)Math.Floor(maxPcm / (double)PcmPerPercent));
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

    /// <summary>Clamp a requested °C into the card's own window (raw bounds from GetInfo), rounded inward.
    /// Safe only because <see cref="IsCoherent"/> guarantees ceil(min) ≤ floor(max) before enablement.</summary>
    public static int ClampToCardC(int requestedC, int minRaw, int maxRaw) =>
        Math.Clamp(requestedC,
            (int)Math.Ceiling(minRaw / (double)RawPerDegree),
            (int)Math.Floor(maxRaw / (double)RawPerDegree));
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
    public static string SlidersNote(bool offsetsNative, GpuPowerBackendKind power, bool tempNative)
    {
        bool powerNative = power != GpuPowerBackendKind.None;

        var applied = new List<string>();
        if (offsetsNative) applied.Add("les offsets core et mémoire");
        if (powerNative) applied.Add("le power limit");
        if (tempNative) applied.Add("la cible de température");
        if (applied.Count == 0)
            return "Aucun backend GPU natif vérifié sur cette machine — règle l'overclocking dans "
                 + "MSI Afterburner (NVIDIA) ou Adrenalin (AMD).";

        // The caveat is built compositionally so it can only ever attribute read-back to the axes that
        // genuinely have it (power/temp — NOT the offsets, whose write is driver-clamped, not read-back
        // confirmed) and can never paint the documented ADLX API as an undocumented NVAPI interface.
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

        // ADLX power (AMD's official documented API) — read-back confirmed, never labelled undocumented.
        if (power == GpuPowerBackendKind.AdlxDocumented)
            clauses.Add("ADLX (API AMD officielle) : l'écriture power limit est confirmée par relecture");

        // Offsets are applied via NVAPI too, but as driver-clamped frequency deltas — no read-back claim.
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

    public GpuOcService(IHardwareService hardware)
    {
        _hardware = hardware;
    }

    /// <summary>Lazily resolve (and cache) the first NVAPI-drivable NVIDIA GPU handle. Never throws.</summary>
    private bool EnsureNvidia()
    {
        if (_nvProbed) return _nvGpu != IntPtr.Zero;
        _nvProbed = true;
        if (NvApi.TryGetFirstGpu(out var gpu, out var name) && gpu != IntPtr.Zero)
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
        _pwrOk = NvApi.TryReadPowerInfo(_nvGpu, out _pwrMinPcm, out _pwrDefPcm, out _pwrMaxPcm)
              && NvApi.TryReadPowerTarget(_nvGpu, out int currentPcm)
              && GpuPowerLimit.IsCoherent(_pwrMinPcm, _pwrDefPcm, _pwrMaxPcm, currentPcm);
        return _pwrOk;
    }

    /// <summary>Same on-card verification contract as <see cref="EnsurePowerVerified"/>, for the
    /// temperature target. Read-only — never writes during verification.</summary>
    private bool EnsureThermalVerified()
    {
        if (_thermProbed) return _thermOk;
        _thermProbed = true;
        _thermOk = NvApi.TryReadThermalInfo(_nvGpu, out _thermMinRaw, out _thermDefRaw, out _thermMaxRaw)
                && NvApi.TryReadThermalLimit(_nvGpu, out int currentRaw)
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
        _adlxOk = AdlxApi.TryGetFirstGpuInfo(out var info)
               && info.ManualPowerTuningSupported
               && AdlxApi.TryReadPowerLimit(out _adlxInitialPct, out _adlxMinPct, out _adlxMaxPct, out _adlxStepPct);
        if (_adlxOk) _adlxInfo = info;
        return _adlxOk;
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
                    string message =
                        $"NVAPI actif — {axes} applicables nativement. Offsets bornés par le driver, réglages "
                        + "non-persistants (remis à zéro au redémarrage), aucun déverrouillage de voltage. "
                        + (pwr
                            ? $"Power limit vérifié en lecture (fenêtre {GpuPowerLimit.FromPcm(_pwrMinPcm)}–"
                              + $"{GpuPowerLimit.FromPcm(_pwrMaxPcm)} %, défaut {GpuPowerLimit.FromPcm(_pwrDefPcm)} %). "
                            : "Power limit : lecture non vérifiée sur cette carte → non appliqué (utiliser Afterburner). ")
                        + (therm
                            ? $"Cible température vérifiée en lecture (fenêtre {GpuThermalLimit.FromRaw(_thermMinRaw)}–"
                              + $"{GpuThermalLimit.FromRaw(_thermMaxRaw)} °C, défaut {GpuThermalLimit.FromRaw(_thermDefRaw)} °C). "
                            : "Cible température : lecture non vérifiée → non appliquée. ")
                        + (pwr || therm
                            ? "Interfaces power/température non documentées par NVIDIA mais utilisées par les outils "
                              + "d'OC ; chaque écriture est confirmée par relecture. "
                            : "")
                        + "Voltage : jamais appliqué par Aurum.";

                    return new GpuOcBackendStatus(
                        GpuVendor.Nvidia,
                        string.IsNullOrEmpty(_nvName) ? hw.GpuPrimary : _nvName,
                        BackendAvailable: true,
                        BackendVersion: hw.GpuDriverVersion,
                        Message: message,
                        PowerBackend: pwr ? GpuPowerBackendKind.NvapiCommunity : GpuPowerBackendKind.None,
                        PowerLimitMinPct: pwr ? GpuPowerLimit.FromPcm(_pwrMinPcm) : 0,
                        PowerLimitMaxPct: pwr ? GpuPowerLimit.FromPcm(_pwrMaxPcm) : 0,
                        PowerLimitDefaultPct: pwr ? GpuPowerLimit.FromPcm(_pwrDefPcm) : 100,
                        TempLimitNative: therm,
                        TempLimitMinC: therm ? GpuThermalLimit.FromRaw(_thermMinRaw) : 0,
                        TempLimitMaxC: therm ? GpuThermalLimit.FromRaw(_thermMaxRaw) : 0,
                        TempLimitDefaultC: therm ? GpuThermalLimit.FromRaw(_thermDefRaw) : 0);
                }
                return new GpuOcBackendStatus(
                    GpuVendor.Nvidia, hw.GpuPrimary,
                    BackendAvailable: false,
                    BackendVersion: hw.GpuDriverVersion,
                    Message: "GPU NVIDIA détecté mais NVAPI n'a pas pu être initialisé (pilote absent ou trop ancien).");

            case GpuVendor.Amd:
            {
                if (EnsureAmdPowerVerified())
                {
                    return new GpuOcBackendStatus(
                        GpuVendor.Amd,
                        string.IsNullOrEmpty(_adlxInfo!.Name) ? hw.GpuPrimary : _adlxInfo.Name,
                        BackendAvailable: true,
                        BackendVersion: hw.GpuDriverVersion,
                        Message: "ADLX actif (API AMD officielle) — power limit applicable nativement : fenêtre "
                               + $"driver {_adlxMinPct} à {_adlxMaxPct} (échelle Adrenalin), valeur au lancement "
                               + $"{_adlxInitialPct}. Chaque écriture est confirmée par relecture. « Réinitialiser » "
                               + "restaure cette valeur de lancement (le réglage ADLX peut persister au redémarrage, "
                               + "comme dans Adrenalin). Offsets core/mémoire : non appliqués par Aurum sur AMD → "
                               + "Adrenalin (Performances › Tuning). Voltage : jamais appliqué par Aurum.",
                        PowerBackend: GpuPowerBackendKind.AdlxDocumented,
                        PowerLimitMinPct: _adlxMinPct,
                        PowerLimitMaxPct: _adlxMaxPct,
                        PowerLimitDefaultPct: _adlxInitialPct);
                }

                // Honest reason, branched on WHICH stage of verification actually failed — never
                // blaming AMD's support flag when it was really the window read that was unusable.
                bool adlxUp = AdlxApi.TryGetFirstGpuInfo(out var amdInfo);
                string reason;
                if (!adlxUp)
                    reason = "OC AMD non implémenté nativement — ADLX indisponible (driver Adrenalin absent ou "
                           + "trop ancien). Utiliser AMD Adrenalin (Performances › Tuning).";
                else if (!amdInfo.ManualPowerTuningSupported)
                    reason = $"ADLX initialisé (API AMD officielle) mais ce GPU{(amdInfo.IsIntegrated ? " intégré" : string.Empty)} "
                           + "ne permet pas le tuning manuel du power limit (IsSupportedManualPowerTuning = non). "
                           + "OC AMD → Adrenalin (Performances › Tuning).";
                else
                    // Flag said yes but the window/current read was incoherent — honest about the real cause.
                    reason = "ADLX initialisé et ce GPU déclare supporter le tuning, mais la fenêtre du power "
                           + "limit lue via le driver est incohérente → non appliqué par prudence. "
                           + "OC AMD → Adrenalin (Performances › Tuning).";
                return new GpuOcBackendStatus(
                    GpuVendor.Amd,
                    adlxUp && !string.IsNullOrEmpty(amdInfo.Name) ? amdInfo.Name : hw.GpuPrimary,
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
            if (!NvApi.TrySetOffsets(_nvGpu, clamped.CoreOffsetMhz, clamped.MemoryOffsetMhz, out var nvError))
                return new GpuOcApplyResult(false, nvError);

            var applied = $"core {clamped.CoreOffsetMhz:+#;-#;0} MHz · mém {clamped.MemoryOffsetMhz:+#;-#;0} MHz";

            // Power limit — only on a card where the layout passed its read verification. 100 % is a
            // valid target there (= back to stock), so the write is unconditional, not gated on ≠100.
            if (EnsurePowerVerified())
            {
                int targetPct = GpuPowerLimit.ClampToCardPct(clamped.PowerLimitPct, _pwrMinPcm, _pwrMaxPcm);
                if (!NvApi.TrySetPowerTarget(_nvGpu, GpuPowerLimit.ToPcm(targetPct), out var pwrError))
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
                if (!NvApi.TrySetThermalLimit(_nvGpu, GpuThermalLimit.ToRaw(targetC), out var thermError))
                    return new GpuOcApplyResult(false,
                        $"Appliqué partiellement ({applied}) mais cible température NON appliquée : {thermError}");

                applied += targetC == clamped.TempLimitC
                    ? $" · cible température {targetC} °C (confirmé par relecture)"
                    : $" · cible température {clamped.TempLimitC} °C demandée → {targetC} °C (bornée par la carte, confirmée)";
            }

            Serilog.Log.Information("GPU OC applied via NVAPI: {Applied}", applied);
            return new GpuOcApplyResult(true, null, applied);
        }

        // 3. AMD path: power limit only (ADLX, AMD's documented API) — offsets are honestly not applied.
        if (vendor == GpuVendor.Amd && EnsureAmdPowerVerified())
        {
            int target = AdlxPowerRange.Clamp(profile.PowerLimitPct, _adlxMinPct, _adlxMaxPct, _adlxStepPct);
            if (!AdlxApi.TrySetPowerLimit(target, out var adlxError))
                return new GpuOcApplyResult(false, $"Power limit AMD NON appliqué : {adlxError}");

            var appliedAmd = target == profile.PowerLimitPct
                ? $"power limit {target} (ADLX, confirmé par relecture)"
                : $"power limit {profile.PowerLimitPct} demandé → {target} (borné par le driver, confirmé)";
            // The core/mem sliders exist on the page — a successful AMD apply must say they did nothing.
            if (profile.CoreOffsetMhz != 0 || profile.MemoryOffsetMhz != 0)
                appliedAmd += " — offsets core/mémoire non appliqués sur AMD (utiliser Adrenalin)";

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

        if (vendor == GpuVendor.Nvidia && EnsureNvidia() && NvApi.TryReadOffsets(_nvGpu, out int core, out int mem))
        {
            // Power/temp are reported only from a verified read; voltage is never read natively —
            // unverified axes carry neutral placeholders (100 / 0 / 0), never invented measurements.
            int powerPct = EnsurePowerVerified() && NvApi.TryReadPowerTarget(_nvGpu, out int pcm)
                ? GpuPowerLimit.FromPcm(pcm)
                : 100;
            int tempC = EnsureThermalVerified() && NvApi.TryReadThermalLimit(_nvGpu, out int raw)
                ? GpuThermalLimit.FromRaw(raw)
                : 0;
            return new GpuOcProfile(core, mem, powerPct, tempC, 0);
        }
        // AMD: only the verified power value is a real measurement; every other axis is a neutral placeholder.
        if (vendor == GpuVendor.Amd && EnsureAmdPowerVerified()
            && AdlxApi.TryReadPowerLimit(out int amdCur, out _, out _, out _))
            return new GpuOcProfile(0, 0, amdCur, 0, 0);
        return null;
    }

    public async Task<GpuOcApplyResult> ResetAsync()
    {
        var vendor = (await _hardware.DetectAsync()).GpuVendor;

        if (vendor == GpuVendor.Nvidia && EnsureNvidia())
        {
            if (!NvApi.TrySetOffsets(_nvGpu, 0, 0, out var nvError))
                return new GpuOcApplyResult(false, nvError);

            var summary = "offsets remis à 0";
            if (EnsurePowerVerified())
            {
                // Reset means the card's own default (read from GetInfo), not an assumed 100 %.
                if (!NvApi.TrySetPowerTarget(_nvGpu, _pwrDefPcm, out var pwrError))
                    return new GpuOcApplyResult(false,
                        $"Offsets remis à 0, mais power limit NON remis au défaut : {pwrError}");
                summary += $" · power limit remis au défaut carte ({GpuPowerLimit.FromPcm(_pwrDefPcm)} %)";
            }
            if (EnsureThermalVerified())
            {
                if (!NvApi.TrySetThermalLimit(_nvGpu, _thermDefRaw, out var thermError))
                    return new GpuOcApplyResult(false,
                        $"Réinitialisation partielle ({summary}), mais cible température NON remise au défaut : {thermError}");
                summary += $" · cible température remise au défaut carte ({GpuThermalLimit.FromRaw(_thermDefRaw)} °C)";
            }

            Serilog.Log.Information("GPU OC reset via NVAPI: {Summary}", summary);
            return new GpuOcApplyResult(true, null, summary);
        }

        if (vendor == GpuVendor.Amd && EnsureAmdPowerVerified())
        {
            // ADLX has no default getter — the honest reset target is the value read at Aurum startup
            // (captured at verification time), not a guaranteed factory default.
            if (!AdlxApi.TrySetPowerLimit(_adlxInitialPct, out var adlxError))
                return new GpuOcApplyResult(false, $"Power limit AMD NON restauré : {adlxError}");

            var summary = $"power limit restauré à la valeur relevée au lancement d'Aurum ({_adlxInitialPct}, ADLX confirmé)";
            Serilog.Log.Information("GPU OC reset via ADLX: {Summary}", summary);
            return new GpuOcApplyResult(true, null, summary);
        }

        return new GpuOcApplyResult(false,
            "Réinitialisation native indisponible : aucun backend GPU vérifié (NVIDIA NVAPI ou AMD ADLX).");
    }
}
