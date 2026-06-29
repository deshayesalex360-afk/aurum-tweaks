using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AurumTweaks.Services;

/// <summary>
/// The signed contents of a licence: which edition it grants, who it was issued to, when, and an optional expiry (for
/// subscriptions / time-limited keys). This is the exact object whose UTF-8 JSON bytes are signed — the token carries
/// those bytes verbatim, so verification re-checks the received bytes and never has to re-serialize (canonicalisation
/// is a non-issue). <see cref="LicensedTo"/> is purely informational (an email / order id), never a secret.
/// </summary>
public sealed record LicensePayload(
    AppEdition Edition,
    string LicensedTo,
    DateTime IssuedUtc,
    DateTime? ExpiresUtc);

/// <summary>
/// The verdict of checking a licence token. <see cref="IsValid"/> false ALWAYS pairs with <see cref="AppEdition.Free"/>
/// — the fail-safe: anything we cannot cryptographically prove (missing, malformed, wrong signature, expired) grants
/// nothing. <see cref="Reason"/> is a stable internal code (the UI maps it to French); it never leaks key material.
/// </summary>
public sealed record LicenseValidation(bool IsValid, AppEdition Edition, string Reason, LicensePayload? Payload)
{
    public static LicenseValidation Invalid(string reason) => new(false, AppEdition.Free, reason, null);
    public static LicenseValidation Valid(LicensePayload payload) => new(true, payload.Edition, "ok", payload);
}

/// <summary>Shared JSON shape for licence payloads — the SAME options must serialize (issue) and deserialize (verify),
/// or a round-trip would silently fail. The enum travels as its name ("Premium"), so reordering the enum never
/// reinterprets an old licence as a different edition.</summary>
internal static class LicenseJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>
/// The pure, offline licence check — the honesty core of the whole freemium model. Given a pasted token and the app's
/// embedded ECDSA <b>public</b> key, it returns what edition (if any) the token genuinely proves. It is REAL crypto
/// (<see cref="ECDsa.VerifyData(byte[], byte[], HashAlgorithmName)"/> over P-256/SHA-256): a user cannot fabricate a
/// valid token without the seller's private key, which never ships. It is deliberately honest about its limits — this
/// guards against <i>forging</i> a licence, not against a determined cracker patching the binary to skip the call;
/// that is true of all client-side licensing, and we never claim otherwise. Pure (now is a parameter) so every branch
/// — good, tampered, malformed, expired — is unit-testable with an ephemeral keypair, no disk and no embedded secret.
/// </summary>
public static class LicenseVerifier
{
    private const char Separator = '.';

    public static LicenseValidation Verify(string? token, ECDsa publicKey, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(token))
            return LicenseValidation.Invalid("empty");

        string[] parts = token.Trim().Split(Separator);
        if (parts.Length != 2)
            return LicenseValidation.Invalid("malformed");

        byte[] payloadBytes, signature;
        try
        {
            payloadBytes = Convert.FromBase64String(parts[0]);
            signature = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return LicenseValidation.Invalid("malformed");
        }

        // The one check that matters: were these exact payload bytes signed by the holder of the private key?
        if (!publicKey.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
            return LicenseValidation.Invalid("signature");

        LicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes, LicenseJson.Options);
        }
        catch (JsonException)
        {
            return LicenseValidation.Invalid("payload");
        }

        if (payload is null)
            return LicenseValidation.Invalid("payload");

        // A signed-but-expired licence reverts to Free — honest for time-limited / subscription keys.
        if (payload.ExpiresUtc is { } expiry && nowUtc > expiry)
            return LicenseValidation.Invalid("expired");

        return LicenseValidation.Valid(payload);
    }
}

/// <summary>
/// Mints a signed token from a payload and the seller's ECDSA <b>private</b> key. This is the SELLER side — it runs in
/// the offline keygen tool, NEVER in the shipped consumer app (which only ever holds the public key). Kept beside the
/// verifier so the two provably agree on format and JSON shape, and so the keygen and the round-trip tests share one
/// implementation. Producing a token is meaningless without the private key, so shipping this code grants nothing.
/// </summary>
public static class LicenseIssuer
{
    public static string Issue(LicensePayload payload, ECDsa privateKey)
    {
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, LicenseJson.Options);
        byte[] signature = privateKey.SignData(payloadBytes, HashAlgorithmName.SHA256);
        return $"{Convert.ToBase64String(payloadBytes)}.{Convert.ToBase64String(signature)}";
    }
}

/// <summary>
/// The one place a stable internal licence <see cref="LicenseValidation.Reason"/> code (or an <see cref="AppEdition"/>)
/// becomes French UI copy. Pure and total — every code the verifier/service can emit maps to a real sentence, and the
/// default is a safe « unknown » rather than leaking the raw code, so the Licence page can never show a blank or an
/// English token. Kept beside the codes it translates (same file as <see cref="LicenseVerifier"/>) so adding a reason
/// and adding its French text is one edit in one place — the anti-drift discipline applied to licence status, and a
/// test enumerates the codes to prove none was left untranslated.
/// </summary>
public static class LicenseStatusText
{
    /// <summary>French for a single status code. The honesty point of « signature » in particular: we say the key
    /// wasn't issued for this product, not the vaguer « invalid », so a user who pasted a key for some other app
    /// understands why it was refused.</summary>
    public static string French(string reason) => reason switch
    {
        "ok"             => "Licence valide.",
        "not-configured" => "La gestion des licences n'est pas activée dans cette version.",
        "not-loaded"     => "Licence pas encore vérifiée.",
        "empty"          => "Aucune clé fournie.",
        "malformed"      => "Clé illisible — le format ne correspond pas à une clé Aurum Tweaks.",
        "signature"      => "Signature invalide — cette clé n'a pas été émise pour Aurum Tweaks.",
        "payload"        => "Le contenu de la clé est illisible.",
        "expired"        => "Licence expirée.",
        "deactivated"    => "Licence retirée.",
        "bad-key"        => "La clé de vérification intégrée à cette version est défectueuse.",
        _                => "État de licence inconnu."
    };

    /// <summary>The edition's own French name, for headings and badges.</summary>
    public static string FrenchEdition(AppEdition edition) => edition switch
    {
        AppEdition.Premium => "Premium",
        _                  => "Gratuite"
    };

    /// <summary>
    /// The headline shown at the top of the Licence page — the honest, complete story for each of the three real
    /// states. « Not configured » (the as-shipped build) says plainly that everything is unlocked and there is no paid
    /// tier yet, so we never imply a paywall that doesn't exist. Configured-Free names exactly what Premium would add,
    /// from the same catalogue the gates read; configured-Premium thanks the buyer.
    /// </summary>
    public static string FrenchSummary(bool configured, AppEdition edition)
    {
        if (!configured)
            return "Vous avez accès à toutes les fonctionnalités. La gestion des licences Premium n'est pas activée dans cette version.";

        return edition == AppEdition.Premium
            ? "Édition Premium — toutes les fonctionnalités sont débloquées. Merci de votre soutien !"
            : "Édition Gratuite — le palier Tranquille et le monitoring sont inclus. Activez une clé Premium pour débloquer les tweaks Avancés, Extrêmes et l'overclocking GPU.";
    }
}
