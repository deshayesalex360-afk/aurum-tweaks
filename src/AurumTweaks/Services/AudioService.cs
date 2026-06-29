using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  « Son » — the audio settings Windows actually exposes, focused on the ONE tweak gamers care about that we can
//  WRITE *and* VERIFY: the Communications "ducking" preference. When Windows detects a communication stream (Discord,
//  an in-game voice chat, a browser call) it ATTENUATES every other sound — including the game — by default. The
//  classic Sound applet's "Communications" tab maps 1:1 to a single REG_DWORD,
//  HKCU\Software\Microsoft\Multimedia\Audio\UserDuckingPreference (0 = reduce 80 %, 1 = reduce 50 %, 2 = mute all,
//  3 = do nothing). Gamers want 3 so game audio never dims mid-fight. The write is real, reversible (restore the
//  Windows default 0) and its success is MEASURED by re-reading the value — a silently-refused write reports failure,
//  never a fabricated success. Everything else audio (exclusive mode, sample rate / bit depth, spatial audio, per-app
//  volume) is only settable in Windows' own panels, so we read what we honestly can (the active sound scheme, the
//  audio devices) and hand the rest off to mmsys.cpl / ms-settings instead of faking controls.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The Communications "ducking" preference, classified from the REG_DWORD. Unknown = present but unrecognised.</summary>
public enum AudioDucking { ReduceOther80, ReduceOther50, MuteOthers, DoNothing, Unknown }

/// <summary>
/// Pure parse/format/label for the ducking preference and the registry location it lives at. The DWORD↔enum mapping
/// is the exact contract Windows' Communications tab uses, pinned by tests so the displayed wording and the value we
/// write can never silently drift apart.
/// </summary>
public static class AudioDuckingInfo
{
    public const string Hive = "HKCU";
    public const string Key = @"Software\Microsoft\Multimedia\Audio";
    public const string ValueName = "UserDuckingPreference";

    public static AudioDucking Parse(string? raw)
    {
        if (!RegistryValue.TryParseDword(raw, out var v)) return AudioDucking.Unknown;
        return v switch
        {
            0 => AudioDucking.ReduceOther80,
            1 => AudioDucking.ReduceOther50,
            2 => AudioDucking.MuteOthers,
            3 => AudioDucking.DoNothing,
            _ => AudioDucking.Unknown
        };
    }

    public static string ToRegistryValue(AudioDucking d) => d switch
    {
        AudioDucking.ReduceOther80 => "0",
        AudioDucking.ReduceOther50 => "1",
        AudioDucking.MuteOthers    => "2",
        AudioDucking.DoNothing     => "3",
        _ => "0"
    };

    public static string Describe(AudioDucking d) => d switch
    {
        AudioDucking.ReduceOther80 => "Réduire de 80 % le volume des autres sons",
        AudioDucking.ReduceOther50 => "Réduire de 50 % le volume des autres sons",
        AudioDucking.MuteOthers    => "Couper tous les autres sons",
        AudioDucking.DoNothing     => "Ne rien faire",
        _ => "Indéterminé"
    };
}

/// <summary>Honest verdict severity (mirrors the tri-state colored dot used across the app).</summary>
public enum AudioVerdict { Ok, Info, Warning }

/// <summary>The indicative recommendation text. Pure, so the wording is pinned by tests.</summary>
public sealed record AudioRecommendation(AudioVerdict Verdict, string Headline, string Detail);

/// <summary>
/// Turns the ducking preference into an honest recommendation. "Do nothing" is the gaming-recommended Ok state;
/// "mute all" is WARNED (it silences the game too); the two "reduce" modes are Info (Windows will dim the game).
/// </summary>
public static class AudioDuckingAdvisor
{
    public static AudioRecommendation Assess(AudioDucking current) => current switch
    {
        AudioDucking.DoNothing => new(AudioVerdict.Ok,
            "« Ne rien faire » — le réglage recommandé pour le jeu",
            "Windows ne baissera plus le volume du jeu quand une application de communication (Discord, chat vocal, "
            + "navigateur) ouvre un flux audio. Le volume du jeu reste constant."),

        AudioDucking.MuteOthers => new(AudioVerdict.Warning,
            "« Couper tous les autres sons » — déconseillé en jeu",
            "Dès qu'une application de communication s'active, tout le reste — y compris le son du jeu — est coupé. "
            + "Passe à « Ne rien faire » pour garder l'audio du jeu."),

        AudioDucking.Unknown => new(AudioVerdict.Info,
            "Préférence de communication indéterminée",
            "La valeur lue n'est pas reconnue. Applique « Ne rien faire » pour un comportement connu et stable en jeu."),

        _ => new(AudioVerdict.Info,
            "Windows réduit le volume des autres sons pendant les communications",
            "Quand une application de communication ouvre un flux audio, Windows atténue tout le reste (dont le jeu). "
            + "Choisis « Ne rien faire » pour garder un volume de jeu constant.")
    };
}

/// <summary>
/// Result of a ducking write. MEASURED: <see cref="FromVerified"/> sets Ok only when a fresh re-read equals the value
/// we asked for — a silently-refused write reports failure here, never a fabricated success.
/// </summary>
public sealed record AudioActionOutcome(bool Ok, string Message)
{
    public static AudioActionOutcome FromVerified(AudioDucking actual, AudioDucking desired) => actual == desired
        ? new(true, $"Réglage appliqué : « {AudioDuckingInfo.Describe(desired)} ». Prise en compte immédiate pour la prochaine communication audio.")
        : new(false, "La modification a été refusée par le système (valeur inchangée).");

    public static AudioActionOutcome Failed { get; } = new(false, "Échec de la modification du réglage audio.");
}

/// <summary>One audio device as WMI (Win32_SoundDevice) reports it. Pure display only — read-only inventory.</summary>
public sealed record AudioDevice(string Name, string Manufacturer, string Status)
{
    public string NameDisplay => string.IsNullOrWhiteSpace(Name) ? "Périphérique audio" : Name.Trim();
    public string ManufacturerDisplay => string.IsNullOrWhiteSpace(Manufacturer) ? "—" : Manufacturer.Trim();
    public string StatusDisplay => string.IsNullOrWhiteSpace(Status) ? "—" : Status.Trim();

    /// <summary>WMI reports a healthy device as "OK"; anything else (or blank) is surfaced verbatim, never hidden.</summary>
    public bool IsOk => string.Equals(Status?.Trim(), "OK", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Pure label for the active system sound scheme read from HKCU\AppEvents\Schemes (its default value).</summary>
public static class AudioSoundScheme
{
    public static string Describe(string? raw) => (raw?.Trim()) switch
    {
        null or "" => "Indéterminé",
        ".None"    => "Aucun son",
        ".Default" => "Windows par défaut",
        var s      => s.TrimStart('.')
    };

    public static bool IsSilent(string? raw) => string.Equals(raw?.Trim(), ".None", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Display-ready audio report. The ducking preference is the load-bearing part (read + verdict + the one reversible
/// write); the active sound scheme and the device inventory are honest read-only extras. <see cref="IsExplicit"/> is
/// false when no preference value exists yet — Windows then applies its default (reduce 80 %), and the page says so
/// instead of pretending a value was read. <see cref="CanApplyRecommended"/> / <see cref="CanRestoreDefault"/> keep
/// each write from ever being a no-op button. Built by the pure <see cref="From"/> factory, which the tests pin.
/// </summary>
public sealed record AudioReport
{
    public bool IsExplicit { get; init; }
    public AudioDucking Ducking { get; init; }
    public string DuckingDisplay { get; init; } = "";
    public bool IsRecommended { get; init; }
    public bool CanApplyRecommended { get; init; }
    public bool CanRestoreDefault { get; init; }
    public AudioVerdict Verdict { get; init; }
    public string Headline { get; init; } = "";
    public string Detail { get; init; } = "";
    public string SchemeDisplay { get; init; } = "";
    public bool SystemSoundsSilent { get; init; }
    public IReadOnlyList<AudioDevice> Devices { get; init; } = Array.Empty<AudioDevice>();

    public bool VerdictOk => Verdict == AudioVerdict.Ok;
    public bool VerdictInfo => Verdict == AudioVerdict.Info;
    public bool VerdictWarn => Verdict == AudioVerdict.Warning;
    public bool HasDevices => Devices.Count > 0;
    public int DeviceCount => Devices.Count;

    public static AudioReport From(bool readOk, string? duckingRaw, string? schemeRaw = null,
        IReadOnlyList<AudioDevice>? devices = null)
    {
        // Absent value (TryRead false) → Windows' implicit default is "reduce 80 %"; report that honestly rather than
        // pretending a value was read. Only a present-but-garbage value is Unknown.
        var current = readOk ? AudioDuckingInfo.Parse(duckingRaw) : AudioDucking.ReduceOther80;
        var rec = AudioDuckingAdvisor.Assess(current);

        // Honesty about no-ops: the "Ne rien faire" write is hidden only when the value is ALREADY explicitly 3; an
        // absent value (implicit 80 % default) still needs the write. "Restore default 80 %" is offered only when the
        // explicit state isn't already that default (and isn't an unreadable value).
        bool canApply = !(readOk && current == AudioDucking.DoNothing);
        bool canRestore = readOk && current != AudioDucking.ReduceOther80 && current != AudioDucking.Unknown;

        string headline = readOk
            ? rec.Headline
            : "Aucune préférence définie — Windows réduit de 80 % les autres sons par défaut";

        return new AudioReport
        {
            IsExplicit = readOk,
            Ducking = current,
            DuckingDisplay = AudioDuckingInfo.Describe(current),
            IsRecommended = current == AudioDucking.DoNothing,
            CanApplyRecommended = canApply,
            CanRestoreDefault = canRestore,
            Verdict = rec.Verdict,
            Headline = headline,
            Detail = rec.Detail,
            SchemeDisplay = AudioSoundScheme.Describe(schemeRaw),
            SystemSoundsSilent = AudioSoundScheme.IsSilent(schemeRaw),
            Devices = devices ?? Array.Empty<AudioDevice>()
        };
    }

    public static AudioReport Failed { get; } = new()
    {
        IsExplicit = false,
        Ducking = AudioDucking.Unknown,
        DuckingDisplay = AudioDuckingInfo.Describe(AudioDucking.Unknown),
        IsRecommended = false,
        CanApplyRecommended = true,
        CanRestoreDefault = false,
        Verdict = AudioVerdict.Info,
        Headline = "Lecture des réglages audio impossible.",
        Detail = "Tu peux tout de même appliquer « Ne rien faire » pour un comportement connu en jeu.",
        SchemeDisplay = "Indéterminé",
        Devices = Array.Empty<AudioDevice>()
    };
}

/// <summary>
/// I/O service behind « Son ». All decision logic lives in the pure cores above (what the tests pin); this only reads
/// the registry + WMI and performs the one reversible ducking write, then re-reads so the reported result is the
/// MEASURED state, never an assumed one.
/// </summary>
public sealed class AudioService : IAudioService
{
    private readonly IRegistryService _registry;

    public AudioService(IRegistryService registry) => _registry = registry;

    public Task<AudioReport> GetReportAsync() => Task.Run(ReadReport);

    public Task<AudioActionOutcome> SetDuckingAsync(AudioDucking desired) => Task.Run(() => SetDucking(desired));

    private AudioReport ReadReport()
    {
        try
        {
            bool readOk = _registry.TryReadValue(
                AudioDuckingInfo.Hive, AudioDuckingInfo.Key, AudioDuckingInfo.ValueName, out var duckingRaw);
            // The active system sound scheme is the default value of HKCU\AppEvents\Schemes (".None" = silent).
            _registry.TryReadValue("HKCU", @"AppEvents\Schemes", "", out var schemeRaw);
            return AudioReport.From(readOk, duckingRaw, schemeRaw, ReadDevices());
        }
        catch
        {
            return AudioReport.Failed;   // honest "lecture impossible", never fabricated state
        }
    }

    private AudioActionOutcome SetDucking(AudioDucking desired)
    {
        try
        {
            if (desired == AudioDucking.Unknown) return AudioActionOutcome.Failed;

            bool wrote = _registry.WriteValue(
                AudioDuckingInfo.Hive, AudioDuckingInfo.Key, AudioDuckingInfo.ValueName,
                AudioDuckingInfo.ToRegistryValue(desired), RegistryValueType.DWord);
            if (!wrote) return AudioActionOutcome.Failed;

            // Honesty: never trust the write — re-read and report the MEASURED preference. A refused write reads back
            // as the old (or no) value and FromVerified reports failure.
            var actual = _registry.TryReadValue(
                AudioDuckingInfo.Hive, AudioDuckingInfo.Key, AudioDuckingInfo.ValueName, out var raw)
                ? AudioDuckingInfo.Parse(raw)
                : AudioDucking.Unknown;
            return AudioActionOutcome.FromVerified(actual, desired);
        }
        catch
        {
            return AudioActionOutcome.Failed;
        }
    }

    private static List<AudioDevice> ReadDevices()
    {
        var list = new List<AudioDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer, Status FROM Win32_SoundDevice");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
                using (mo)
                {
                    list.Add(new AudioDevice(
                        mo["Name"] as string ?? "",
                        mo["Manufacturer"] as string ?? "",
                        mo["Status"] as string ?? ""));
                }
        }
        catch
        {
            // WMI unavailable — the page degrades to "no devices listed", never a fabricated entry.
        }
        return list;
    }
}
