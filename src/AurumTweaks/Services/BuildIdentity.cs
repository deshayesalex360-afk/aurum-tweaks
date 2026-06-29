using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AurumTweaks.Services;

/// <summary>
/// I/O probe for the running build's identity — the side-effecting half of the pure-core split behind the
/// « Transparence &amp; confiance » disclosure. It reads the version, hashes the executable on disk (reusing
/// <see cref="CatalogIntegrity.ComputeHash(Stream)"/> so the fingerprint is the exact same SHA-256 primitive the
/// catalog gate uses), and checks for an embedded Authenticode signature. The honest FORMATTING of these facts lives in
/// the tested <see cref="TransparencyReport"/>; this only gathers them and is fail-soft by design: any failure degrades
/// to an honest unknown (<c>null</c> hash, <see cref="ExecutableSignatureState.Indeterminate"/>) rather than a guess.
/// </summary>
public static class BuildIdentity
{
    /// <summary>The running executable's path — what the user actually launched. Falls back to the executing
    /// assembly's location if the process path is unavailable.</summary>
    public static string CurrentExecutablePath
        => Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;

    /// <summary>Probe the running build's identity. Never throws.</summary>
    public static BuildIdentityFacts Probe() => Probe(CurrentExecutablePath);

    /// <summary>Probe the build at <paramref name="executablePath"/>. Never throws: each field degrades independently to
    /// an honest unknown so a partial failure (e.g. an unreadable file) never blocks or fabricates the disclosure.</summary>
    public static BuildIdentityFacts Probe(string executablePath)
        => new(
            ResolveVersion(executablePath),
            HashExecutable(executablePath),
            ProbeSignature(executablePath),
            // Only surface a path the user can actually run Get-FileHash against — a missing file gets no command.
            File.Exists(executablePath) ? executablePath : null);

    private static string ResolveVersion(string executablePath)
    {
        try
        {
            if (File.Exists(executablePath))
            {
                var info = FileVersionInfo.GetVersionInfo(executablePath);
                var v = info.ProductVersion ?? info.FileVersion;
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
        }
        catch (FileNotFoundException) { /* fall through to the assembly version */ }
        catch (IOException) { /* fall through */ }

        // Always available even if the file probe fails — an assembly version is baked into the loaded image.
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "inconnue";
    }

    private static string? HashExecutable(string executablePath)
    {
        try
        {
            using var stream = File.OpenRead(executablePath);
            return CatalogIntegrity.ComputeHash(stream);
        }
        catch (IOException) { return null; }                 // honest absence — never a placeholder hash
        catch (UnauthorizedAccessException) { return null; }
    }

    private static ExecutableSignatureState ProbeSignature(string executablePath)
    {
        // A missing file can't be judged — say so rather than calling it « absente ».
        if (!File.Exists(executablePath)) return ExecutableSignatureState.Indeterminate;

        try
        {
            // CreateFromSignedFile throws CryptographicException when the file carries NO embedded Authenticode
            // signature; a returned certificate means a signature is present. Its validity is deliberately NOT
            // re-verified here — the disclosure reports « présente », never « vérifiée ».
            using var cert = X509Certificate.CreateFromSignedFile(executablePath);
            return ExecutableSignatureState.Present;
        }
        catch (CryptographicException) { return ExecutableSignatureState.Absent; }
        catch (IOException) { return ExecutableSignatureState.Indeterminate; }
        catch (UnauthorizedAccessException) { return ExecutableSignatureState.Indeterminate; }
    }
}
