using System;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

/// <summary>
/// The running app's licence authority. Holds the current verified <see cref="AppEdition"/> (Free until proven
/// otherwise) and is the ONLY place that turns a stored/pasted token into Premium — always through real ECDSA
/// verification against the embedded public key, never a trusted flag on disk. Every failure path (no key embedded,
/// no token, bad signature, expired, garbage) collapses to Free via <see cref="LicenseValidation"/>'s fail-safe, so a
/// missing or forged licence can never read as paid. Registered as a singleton so all gated ViewModels share one
/// verdict; <see cref="EditionChanged"/> fires only on a real transition so they can re-gate live without a relaunch.
/// </summary>
public sealed class LicenseService : ILicenseService
{
    private readonly ILicenseStore _store;
    private readonly ILicenseKeyRing _keyRing;

    private AppEdition _edition = AppEdition.Free;
    private string _statusReason = "not-loaded";
    private LicensePayload? _payload;

    public LicenseService(ILicenseStore store, ILicenseKeyRing keyRing)
    {
        _store = store;
        _keyRing = keyRing;
    }

    public AppEdition CurrentEdition => _edition;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_keyRing.PublicKeyBase64);
    public string StatusReason => _statusReason;
    public LicensePayload? CurrentPayload => _payload;

    public event EventHandler? EditionChanged;

    public async Task InitialiseAsync()
    {
        var token = await _store.LoadAsync();
        Apply(Evaluate(token));
    }

    public async Task<LicenseValidation> ActivateAsync(string token)
    {
        var result = Evaluate(token);

        // Persist ONLY a token that genuinely verifies — a rejected paste must never overwrite a working licence,
        // and an unverifiable string is never written as if it unlocked anything.
        if (result.IsValid)
            await _store.SaveAsync(token);

        Apply(result);
        return result;
    }

    public async Task DeactivateAsync()
    {
        await _store.ClearAsync();
        Apply(LicenseValidation.Invalid("deactivated"));
    }

    /// <summary>
    /// The pure decision: a (possibly null) token + the embedded key → a verdict, fail-safe to Free at every gap. No
    /// embedded key is « not-configured » (the as-shipped state); an embedded key that isn't valid key material is
    /// « bad-key » (a present-but-broken config — distinct from « not configured » so <see cref="IsConfigured"/> stays
    /// honest). Only a key that imports cleanly reaches the real <see cref="LicenseVerifier"/>.
    /// </summary>
    private LicenseValidation Evaluate(string? token)
    {
        var keyB64 = _keyRing.PublicKeyBase64;
        if (string.IsNullOrWhiteSpace(keyB64))
            return LicenseValidation.Invalid("not-configured");

        ECDsa? key = null;
        try
        {
            key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(keyB64), out _);
            return LicenseVerifier.Verify(token, key, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return LicenseValidation.Invalid("bad-key");
        }
        finally
        {
            key?.Dispose();
        }
    }

    private void Apply(LicenseValidation result)
    {
        var previous = _edition;
        _edition = result.Edition;
        _statusReason = result.Reason;
        _payload = result.Payload;

        // Change-detection guard: notify subscribers only on a genuine edition transition, never on a re-check that
        // lands on the same edition (re-activating an already-active licence, a second startup load).
        if (_edition != previous)
            EditionChanged?.Invoke(this, EventArgs.Empty);
    }
}
