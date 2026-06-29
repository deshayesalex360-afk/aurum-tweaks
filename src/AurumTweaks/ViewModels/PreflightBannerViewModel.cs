using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// The reusable pre-flight safety banner shared by every apply surface (Tweaks, Dashboard…): it probes the machine's
/// safety-net posture — is System Restore available/readable, is a reboot already pending — so a page can warn BEFORE
/// the user applies anything (« testable sans peur »). A single shared singleton: the verdict is machine-global, so
/// one probe serves every surface and « Revérifier » on one page refreshes them all together. Honest by construction —
/// only a genuinely off/pending signal becomes a Caution; an all-clear posture stays quiet (<see cref="HasCaution"/>
/// false) and the probe NEVER fabricates a worry. The decision itself lives in the tested pure
/// <see cref="PreflightEvaluator"/>; this is the thin observable wrapper around <see cref="IPreflightService"/>.
/// Extracted from the Tweaks page so the dashboard's one-click « Appliquer le set » gets the SAME forecast without a
/// second copy of the glue — the two surfaces can never disagree on the posture they show.
/// </summary>
public partial class PreflightBannerViewModel : ObservableObject
{
    private readonly IPreflightService _preflight;

    /// <summary>Completes when the first probe has run — pages and tests await it instead of racing the constructor.
    /// Kept off any catalog-load path by its hosts so the (PowerShell-spawning) System Restore probe never blocks a
    /// page paint.</summary>
    public Task Initialization { get; }

    // The latest verdict. Null (resting) hides the banner until the first probe lands; the derived members below are
    // re-raised whenever it changes so the bound banner refreshes as one.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaution))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(Checks))]
    [NotifyPropertyChangedFor(nameof(Cautions))]
    [NotifyPropertyChangedFor(nameof(CanEnableRestore))]
    private PreflightVerdict? _verdict;

    [ObservableProperty] private bool _isChecking;

    public bool HasCaution => Verdict?.HasCaution == true;
    public string Summary => Verdict?.Summary ?? string.Empty;
    public IReadOnlyList<PreflightCheck> Checks => Verdict?.Checks ?? System.Array.Empty<PreflightCheck>();

    // Only the checks that genuinely need attention — the banner enumerates these so the user sees EXACTLY what's off
    // (e.g. « Restauration système indisponible »), never a vague count. Empty in the all-clear / opted-out state.
    public IReadOnlyList<PreflightCheck> Cautions =>
        Verdict?.Checks.Where(c => c.Severity == PreflightSeverity.Caution).ToList()
        ?? (IReadOnlyList<PreflightCheck>)System.Array.Empty<PreflightCheck>();

    // Gates the « Activer la Restauration système » one-click fix. True ONLY when the verdict carries the
    // unreadable-restore caution (the single pre-flight problem with an in-app fix; a pending reboot has none).
    // Not a dead button: clicking runs the real elevated Enable-ComputerRestore and re-probes — on a policy-locked
    // machine the attempt still runs and the persistent caution honestly reflects that nothing changed. Re-raised
    // with the verdict so the button appears/disappears as the posture updates.
    public bool CanEnableRestore =>
        Verdict is { } v && PreflightEvaluator.OffersRestoreRemediation(v);

    public PreflightBannerViewModel(IPreflightService preflight)
    {
        _preflight = preflight;
        Initialization = RefreshInternalAsync();
    }

    // Re-probe the safety-net posture on demand (the « Revérifier » button), e.g. after the user enables System
    // Restore or reboots. Shares the one off-thread evaluator with the constructor's initial probe.
    [RelayCommand]
    private Task Refresh() => RefreshInternalAsync();

    // One-click fix for the « Restauration système indisponible » caution: actually enable System Restore (the service
    // runs the elevated Enable-ComputerRestore), then re-probe and adopt the fresh verdict — clearing the caution iff
    // Windows now reads the net back, never a fabricated success. IsChecking guards the round-trip; the async command
    // also disables itself while running, so a double-click can't fire two enables.
    [RelayCommand]
    private async Task EnableRestore()
    {
        IsChecking = true;
        try
        {
            Verdict = await _preflight.EnableRestoreAndReprobeAsync();
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task RefreshInternalAsync()
    {
        IsChecking = true;
        try
        {
            Verdict = await _preflight.EvaluateAsync();
        }
        finally
        {
            IsChecking = false;
        }
    }
}
