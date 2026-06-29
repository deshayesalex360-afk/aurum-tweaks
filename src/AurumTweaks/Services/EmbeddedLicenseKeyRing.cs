namespace AurumTweaks.Services;

/// <summary>
/// The shipped licence key ring: holds the seller's ECDSA P-256 <b>public</b> key as base64 SubjectPublicKeyInfo.
/// <para>
/// It is intentionally EMPTY in source control. The seller generates their own keypair OFFLINE with the keygen tool
/// (<c>tools/AurumTweaks.KeyGen</c>), keeps the private half secret forever, and pastes ONLY the public half into
/// <see cref="PublicKeyBase64"/> in their private build. Empty here means « not configured »: the app fails safe to
/// Free for everyone and <see cref="ILicenseService.IsConfigured"/> is false, so the UI never surfaces a broken
/// activation box — an honest "no paid tier yet" rather than a fake unlock. Embedding the public key is safe by
/// design: it can only VERIFY tokens, never mint them.
/// </para>
/// </summary>
public sealed class EmbeddedLicenseKeyRing : ILicenseKeyRing
{
    public string? PublicKeyBase64 => "";
}
