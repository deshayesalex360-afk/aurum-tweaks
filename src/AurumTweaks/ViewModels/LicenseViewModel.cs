using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AurumTweaks.ViewModels;

/// <summary>
/// The Licence page: the honest, user-facing face of the freemium model. It only ever DISPLAYS what
/// <see cref="ILicenseService"/> decides — it never grants an edition itself — and it re-reads that verdict on every
/// <see cref="ILicenseService.EditionChanged"/> so a key activated here, or an expiry detected at startup, shows the
/// same truth. The page adapts to the three real states: « not configured » (no paid tier in this build → everything
/// is already unlocked, so we hide the activation box and say so), configured-Free (show the paste box and exactly
/// what Premium adds), and Premium (show who it's licensed to, the expiry, and a way to deactivate).
/// </summary>
public partial class LicenseViewModel : ObservableObject
{
    private readonly ILicenseService _license;

    [ObservableProperty] private string _editionLabel = "Gratuite";
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _statusLine = string.Empty;
    [ObservableProperty] private bool _isPremium;
    [ObservableProperty] private bool _licensingConfigured;
    [ObservableProperty] private bool _showActivation;
    [ObservableProperty] private bool _showUnlockedNote;
    [ObservableProperty] private string _licensedTo = string.Empty;
    [ObservableProperty] private string _expiryLine = string.Empty;
    [ObservableProperty] private string _tokenInput = string.Empty;
    [ObservableProperty] private string _activationMessage = string.Empty;

    /// <summary>The Premium perks, in catalogue order, rendered straight from the SAME source the runtime gates read
    /// (<see cref="EntitlementPolicy.AllPremiumFeatures"/>) — the paywall promise and the lock cannot drift.</summary>
    public IReadOnlyList<string> PremiumFeatures { get; } =
        EntitlementPolicy.AllPremiumFeatures.Select(PremiumFeatureLabels.French).ToList();

    public LicenseViewModel(ILicenseService license)
    {
        _license = license;
        _license.EditionChanged += (_, _) => Refresh();
        Refresh();
    }

    // Pull every derived display value from the service's current verdict. Called on construction and on each real
    // edition transition. Deliberately does NOT touch ActivationMessage — that is feedback owned by the commands, so a
    // background EditionChanged (e.g. startup load) never wipes a message the user is reading.
    private void Refresh()
    {
        var edition = _license.CurrentEdition;
        var configured = _license.IsConfigured;
        var payload = _license.CurrentPayload;

        LicensingConfigured = configured;
        IsPremium = edition == AppEdition.Premium;
        EditionLabel = LicenseStatusText.FrenchEdition(edition);
        Summary = LicenseStatusText.FrenchSummary(configured, edition);
        StatusLine = LicenseStatusText.French(_license.StatusReason);

        // Show the paste box only when there is a paid tier to unlock (configured) AND it isn't already unlocked.
        ShowActivation = configured && !IsPremium;
        // The « everything's free here » note belongs only to the as-shipped, no-key build.
        ShowUnlockedNote = !configured;

        LicensedTo = payload?.LicensedTo ?? string.Empty;
        ExpiryLine = payload is null
            ? string.Empty
            : payload.ExpiresUtc is { } expiry
                ? $"Valide jusqu'au {expiry.ToLocalTime():dd/MM/yyyy}."
                : "Licence perpétuelle, sans expiration.";
    }

    // Verify and (only if genuine) persist a pasted key. An empty paste is a no-op with a nudge; a rejected key shows
    // the precise French reason and never clears what the user typed, so they can fix and retry. The service does the
    // real crypto and the save-only-when-valid guarantee — this command just relays the verdict honestly.
    [RelayCommand]
    private async Task ActivateAsync()
    {
        var token = (TokenInput ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            ActivationMessage = "Collez d'abord votre clé de licence.";
            return;
        }

        var result = await _license.ActivateAsync(token);
        if (result.IsValid)
        {
            ActivationMessage = "Licence activée. Merci !";
            TokenInput = string.Empty;
        }
        else
        {
            ActivationMessage = $"Clé refusée : {LicenseStatusText.French(result.Reason)}";
        }

        Refresh();
    }

    [RelayCommand]
    private async Task DeactivateAsync()
    {
        await _license.DeactivateAsync();
        ActivationMessage = "Licence retirée. Vous êtes repassé à l'édition Gratuite.";
        Refresh();
    }
}
