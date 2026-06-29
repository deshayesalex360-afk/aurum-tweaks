using System;
using System.IO;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>
/// The live state of Windows hibernation, derived from the registry value <c>HibernateEnabled</c> under
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\Power</c> — the value <c>powercfg /hibernate on|off</c> flips, read here
/// (code-page-immune, unlike parsing the localized <c>powercfg /a</c> text). Honesty rules: comparison goes through
/// <see cref="RegistryValue.Matches"/> so "0x1"/"1" and "0x0"/"0" compare numerically; an <b>absent</b> value is
/// reported as <see cref="IsUnknown"/> — never a fabricated "activée" or "désactivée", because the platform default
/// genuinely varies (laptops usually on, some desktop images off). The <c>Can*</c> gates refuse the action that
/// wouldn't change the known state, so no button is ever a guaranteed no-op; when the state is unknown both actions
/// are offered (a <c>powercfg /hibernate on|off</c> call is a legitimate, idempotent way to force a known state).
/// </summary>
public sealed record HibernationState(string? LiveValue, bool IsPresent)
{
    public bool IsEnabled => IsPresent && RegistryValue.Matches(LiveValue, "1", RegistryValueType.DWord);
    public bool IsDisabled => IsPresent && RegistryValue.Matches(LiveValue, "0", RegistryValueType.DWord);
    public bool IsUnknown => !IsEnabled && !IsDisabled;

    public bool CanEnable => !IsEnabled;
    public bool CanDisable => !IsDisabled;

    public string StateDisplay =>
        IsEnabled  ? "Activée"
        : IsDisabled ? "Désactivée"
        : "Inconnu";
}

/// <summary>
/// The live state of the « Démarrage rapide » (Fast Startup / hybrid shutdown) feature, from <c>HiberbootEnabled</c>
/// under <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power</c>. Two honesty subtleties are encoded here:
/// (1) Fast Startup is a hybrid-shutdown that writes a hiberfile, so it is <b>unavailable whenever hibernation is
/// disabled</b> — when <see cref="HibernationEnabled"/> is false the row reports « indisponible » and offers no
/// action, rather than pretending a toggle would matter; (2) Windows' default for a clean consumer install is
/// <b>enabled</b>, so an absent value (with hibernation on) reads as enabled-by-default, never as a fabricated "off".
/// </summary>
public sealed record FastStartupState(string? LiveValue, bool IsPresent, bool HibernationEnabled)
{
    public bool IsAvailable => HibernationEnabled;

    // Only meaningful while available. An absent value defaults to ON (the documented Windows behaviour); an explicit
    // "0" is the only thing that turns it off.
    private bool ConfiguredOff => IsPresent && RegistryValue.Matches(LiveValue, "0", RegistryValueType.DWord);

    public bool IsEnabled => IsAvailable && !ConfiguredOff;
    public bool IsDisabled => IsAvailable && ConfiguredOff;

    // Enable only when currently disabled, disable only when currently enabled, and never when unavailable —
    // so the buttons are never dead and never silently no-op.
    public bool CanEnable => IsAvailable && !IsEnabled;
    public bool CanDisable => IsAvailable && !IsDisabled;

    public string StateDisplay =>
        !IsAvailable ? "Indisponible (hibernation désactivée)"
        : IsEnabled  ? (IsPresent ? "Activé" : "Activé (défaut Windows)")
        : "Désactivé";
}

/// <summary>
/// Builds the exact <c>powercfg</c> invocation that enables or disables hibernation — isolated as a pure function so a
/// wrong argument can't silently fire the wrong command (and so the decision is pinned by a test without spawning a
/// process). <c>powercfg /hibernate on|off</c> does the real work: it creates or removes <c>hiberfil.sys</c>, which is
/// why we drive hibernation through it rather than poking the registry value directly.
/// </summary>
public static class PowercfgHibernateCommand
{
    public static (string FileName, string Args) Build(bool enable) =>
        ("powercfg.exe", enable ? "/hibernate on" : "/hibernate off");
}

/// <summary>
/// The whole page's picture: the two states plus the current size of <c>hiberfil.sys</c> when it can be measured.
/// The size is a real, honest number — it tells the user exactly how much disk disabling hibernation would free —
/// and is reported as « — » (never a fabricated 0) when the file can't be read. Pure, so the headline/format are tested.
/// </summary>
public sealed record SleepHibernationReport(
    HibernationState Hibernation,
    FastStartupState FastStartup,
    long? HiberfilBytes)
{
    public bool HasHiberfil => HiberfilBytes is > 0;
    public string HiberfilDisplay => HiberfilBytes is long b && b > 0 ? ByteSize.Format(b) : "—";

    public string Headline =>
        Hibernation.IsEnabled  ? (HasHiberfil ? $"Hibernation activée · hiberfil.sys ≈ {HiberfilDisplay}" : "Hibernation activée")
        : Hibernation.IsDisabled ? "Hibernation désactivée — l'espace disque de hiberfil.sys est récupéré"
        : "État de l'hibernation inconnu";
}

/// <summary>
/// « Veille &amp; hibernation » — manages Windows hibernation and the Fast Startup hybrid-shutdown. A thin front-end
/// that REUSES <see cref="IRegistryService"/> (to read both states code-page-immune, and to flip Fast Startup) and
/// <see cref="ProcessRunner"/> (to drive hibernation through real <c>powercfg /hibernate</c> calls); it adds no new
/// low-level surface, mirroring the Power Plan / Game Opti front-ends. Honest by construction: every state is read
/// back after a write, hibernation's on/off goes through the OS's own command (so <c>hiberfil.sys</c> is genuinely
/// created/removed), the freed disk space is the real measured file size, and the page promises no FPS gain — its
/// value is disk space and a true cold shutdown (useful for dual-boot and for forcing a clean driver reload).
/// </summary>
public sealed class SleepHibernationService : ISleepHibernationService
{
    // The canonical reflections of `powercfg /hibernate` and the Fast Startup toggle — both machine-wide, both ASCII.
    public const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    public const string HibernateValue = "HibernateEnabled";
    public const string SessionPowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    public const string FastStartupValue = "HiberbootEnabled";

    private readonly IRegistryService _registry;

    public SleepHibernationService(IRegistryService registry) => _registry = registry;

    public Task<SleepHibernationReport> GetReportAsync() => Task.Run(GetReport);

    private SleepHibernationReport GetReport()
    {
        bool hibPresent = _registry.TryReadValue("HKLM", PowerKey, HibernateValue, out var hibValue);
        var hibernation = new HibernationState(hibPresent ? hibValue : null, hibPresent);

        bool fsPresent = _registry.TryReadValue("HKLM", SessionPowerKey, FastStartupValue, out var fsValue);
        var fastStartup = new FastStartupState(fsPresent ? fsValue : null, fsPresent, hibernation.IsEnabled);

        return new SleepHibernationReport(hibernation, fastStartup, ReadHiberfilBytes());
    }

    public Task<bool> SetHibernationAsync(bool enable) => Task.Run(() => SetHibernation(enable));

    private static bool SetHibernation(bool enable)
    {
        var (fileName, args) = PowercfgHibernateCommand.Build(enable);
        var (exit, _) = ProcessRunner.Capture(fileName, args);
        return exit == 0;
    }

    public Task<bool> SetFastStartupAsync(bool enable) => Task.Run(() => SetFastStartup(enable));

    private bool SetFastStartup(bool enable) =>
        _registry.WriteValue("HKLM", SessionPowerKey, FastStartupValue, enable ? "1" : "0", RegistryValueType.DWord);

    // Best-effort size of hiberfil.sys at the system-drive root. Reading FileInfo.Length is metadata-only (no stream
    // open), so it works on the locked system file while elevated; any failure (absent/denied) yields null, surfaced
    // honestly as « — » rather than a fabricated 0.
    private static long? ReadHiberfilBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrEmpty(root)) return null;
            var info = new FileInfo(Path.Combine(root, "hiberfil.sys"));
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }
}
