using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace AurumTweaks.Services;

/// <summary>
/// Verdict for one candidate catalog file weighed against the baked-in manifest.
/// </summary>
public enum CatalogFileVerdict
{
    /// <summary>Path is in the manifest AND the content hash matches — safe to load and (later) execute.</summary>
    Trusted,

    /// <summary>Path is in the manifest but the content hash differs — the file was edited on disk.</summary>
    Tampered,

    /// <summary>Path is not in the manifest at all — an unknown / dropped-in file.</summary>
    Unknown
}

/// <summary>
/// Pure, process-free integrity core for the shipped tweak catalog.
///
/// WHY THIS EXISTS (the threat). The app runs elevated (app.manifest = requireAdministrator) and
/// <see cref="TweakService"/> executes each tweak's PowerShell / Cmd / Bcdedit / AppX / ScheduledTask
/// operations AS ADMIN. A tweak's <c>Script</c> is arbitrary code <i>by design</i>, so the real security
/// control is CATALOG INTEGRITY, not input sanitisation. If the install directory is writable by a standard
/// user — true for any portable / xcopy layout, e.g. running from Downloads — a non-admin process can drop
/// <c>Tweaks\evil.json</c> and it would run elevated on the next "Apply": a classic writable-directory +
/// auto-elevation Elevation-of-Privilege.
///
/// THE DEFENCE. Every shipped JSON's SHA-256 is baked into the assembly (<see cref="CatalogManifest"/>).
/// Before the loader trusts a file it recomputes the hash and refuses anything the manifest does not vouch
/// for. The manifest lives inside the admin-owned binary, so editing or dropping a <i>data</i> file on disk
/// no longer changes what we will execute. This guarantee holds only while the binary itself is
/// write-protected: a standard user who can replace the assembly (e.g. a portable copy unzipped to a
/// user-writable folder) can swap the baked manifest too, defeating the gate — so the app must ship under
/// %ProgramFiles% (or be code-signed and verified) for catalog integrity to mean anything.
///
/// HONESTY. The logic is deliberately pure (bytes / strings in, verdict out) and fail-closed: ONLY an exact
/// (known path, matching hash) pair is <see cref="CatalogFileVerdict.Trusted"/>; everything else is
/// Tampered or Unknown and must not run. There is no "assume safe" branch — a "trusted" verdict always
/// reflects a real hash match, never a default.
/// </summary>
public static class CatalogIntegrity
{
    /// <summary>Lowercase-hex SHA-256 of a byte buffer.</summary>
    public static string ComputeHash(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    /// <summary>Lowercase-hex SHA-256 of a stream (reads to the end).</summary>
    public static string ComputeHash(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    /// <summary>
    /// Normalise a catalog-relative path to the manifest's canonical form: forward slashes, no leading
    /// separator. Manifest keys are stored this way so a file enumerated as <c>tranquille\01-foo.json</c>
    /// on Windows matches the <c>tranquille/01-foo.json</c> key.
    /// </summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return relativePath.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>
    /// Decide whether a single file is trustworthy. Pure: the caller supplies the manifest, the file's
    /// catalog-relative path, and its already-computed content hash. The path is normalised before lookup
    /// and the hash is compared case-insensitively (hex). Anything not exactly matched is refused.
    /// </summary>
    public static CatalogFileVerdict Verify(
        IReadOnlyDictionary<string, string> manifest, string relativePath, string actualHash)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(actualHash);

        var key = NormalizeRelativePath(relativePath);
        if (!manifest.TryGetValue(key, out var expected))
            return CatalogFileVerdict.Unknown;

        return string.Equals(expected, actualHash, StringComparison.OrdinalIgnoreCase)
            ? CatalogFileVerdict.Trusted
            : CatalogFileVerdict.Tampered;
    }
}
