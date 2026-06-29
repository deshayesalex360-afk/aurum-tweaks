using System.Globalization;
using System.Runtime.InteropServices;

namespace AurumTweaks.Services;

/// <summary>How fine the active timer resolution is, relative to the Windows default — the verdict the page badges.</summary>
public enum TimerResolutionLevel { High, Medium, Default, Unknown }

/// <summary>
/// One honest snapshot of the system timer resolution, in 100-nanosecond units exactly as <c>NtQueryTimerResolution</c>
/// hands them back. The API's naming is inverted, so we expose the values by what they MEAN, not by their confusing
/// labels: the API's « Maximum resolution » is the MOST precise the platform supports (the smallest 100-ns value,
/// ≈ 0.5 ms) and its « Minimum resolution » is the LEAST precise default (≈ 15.6 ms). Everything is the real read or
/// — when <see cref="QueryOk"/> is false — an honest « indisponible », never a fabricated zero.
/// </summary>
public sealed record TimerResolutionReading(bool QueryOk, uint CurrentHundredNs, uint MinHundredNs, uint MaxHundredNs)
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>The active resolution in milliseconds — the value the verdict thresholds read.</summary>
    public double CurrentMs => CurrentHundredNs / 10000.0;

    public string CurrentDisplay => Ms(CurrentHundredNs);
    public string BestDisplay => Ms(MaxHundredNs);      // API « Maximum resolution » = most precise supported
    public string DefaultDisplay => Ms(MinHundredNs);   // API « Minimum resolution » = coarse Windows default

    public TimerResolutionLevel Level
    {
        get
        {
            if (!QueryOk) return TimerResolutionLevel.Unknown;
            if (CurrentMs <= 1.0) return TimerResolutionLevel.High;
            if (CurrentMs < 10.0) return TimerResolutionLevel.Medium;
            return TimerResolutionLevel.Default;
        }
    }

    public string Headline => Level switch
    {
        TimerResolutionLevel.High => $"Résolution fine — {CurrentDisplay}",
        TimerResolutionLevel.Medium => $"Résolution intermédiaire — {CurrentDisplay}",
        TimerResolutionLevel.Default => $"Résolution par défaut — {CurrentDisplay}",
        _ => "Résolution indisponible"
    };

    /// <summary>A factual one-liner about what the current resolution means — never an invented FPS/latency figure.</summary>
    public string Detail => Level switch
    {
        TimerResolutionLevel.High =>
            "Un programme maintient le minuteur système à haute précision — bon pour la régularité du timing en jeu et de l'audio.",
        TimerResolutionLevel.Medium =>
            $"Plus fine que le défaut, sans atteindre le maximum supporté ({BestDisplay}).",
        TimerResolutionLevel.Default =>
            $"Aucun programme ne force une précision plus fine pour l'instant. Le maximum supporté est {BestDisplay}.",
        _ =>
            "NtQueryTimerResolution n'a pas pu lire la résolution du minuteur (droits insuffisants ou source absente)."
    };

    private static string Ms(uint hundredNs) => $"{(hundredNs / 10000.0).ToString("0.##", Fr)} ms";
}

/// <summary>
/// Reads the live system timer resolution via the user-mode <c>NtQueryTimerResolution</c> ntdll export — no driver, no
/// NVRAM, and strictly READ-ONLY (Aurum never calls the set counterpart). A non-zero NTSTATUS or any interop failure
/// degrades to an honest <c>QueryOk=false</c> reading rather than a fabricated value, mirroring the latency diagnostic.
/// </summary>
public sealed class TimerResolutionService : ITimerResolutionService
{
    public TimerResolutionReading Read()
    {
        try
        {
            int status = NtQueryTimerResolution(out uint min, out uint max, out uint current);
            return status == 0
                ? new TimerResolutionReading(true, current, min, max)
                : new TimerResolutionReading(false, 0, 0, 0);
        }
        catch
        {
            return new TimerResolutionReading(false, 0, 0, 0);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint MinimumResolution, out uint MaximumResolution, out uint CurrentResolution);
}
