using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using AurumTweaks.Services;

namespace AurumTweaks.KeyGen;

/// <summary>
/// The SELLER-side offline keygen for Aurum Tweaks' freemium licensing — the genuine completion of the licence chain
/// that the consumer app deliberately cannot do (it ships only the public key, which can verify but never mint). It
/// reuses the app's OWN <see cref="LicenseIssuer"/> / <see cref="LicenseVerifier"/> via a project reference, so a token
/// it produces is guaranteed by construction to verify in the app — no second crypto implementation to drift.
///
/// Three commands: <c>genkey</c> (mint a fresh P-256 keypair: keep the private half secret forever, paste the public
/// half into EmbeddedLicenseKeyRing), <c>issue</c> (sign a licence for a buyer), <c>verify</c> (self-check a token
/// against the public key before sending it). All output is French because the operator (the seller) is French.
/// </summary>
internal static class Program
{
    private const string PrivateKeyFileName = "aurum-license-private.key";

    private static int Main(string[] args)
    {
        // The entire output is French (accents) and uses ✓/✗/⚠ glyphs; force UTF-8 so it renders on the default
        // Windows console codepage instead of mojibake. Cosmetic only — guarded so it can never block issuing a key.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected/exotic console: ignore */ }

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "genkey" => CmdGenKey(Cli.Parse(args)),
                "issue"  => CmdIssue(Cli.Parse(args)),
                "verify" => CmdVerify(Cli.Parse(args)),
                "help" or "--help" or "-h" => PrintUsageReturning(0),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            // A keygen mistake must be loud, never a half-written key file or a silently-wrong token.
            Console.Error.WriteLine($"Erreur : {ex.Message}");
            return 1;
        }
    }

    // ---- genkey ----------------------------------------------------------------------------------------------------

    private static int CmdGenKey(Cli cli)
    {
        var outDir = cli.Get("out") ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);
        var privatePath = Path.Combine(outDir, PrivateKeyFileName);

        if (File.Exists(privatePath) && !cli.Has("force"))
        {
            Console.Error.WriteLine(
                $"Refus : « {privatePath} » existe déjà. Écraser une clé privée invaliderait TOUTES les licences déjà " +
                "vendues. Ajoutez --force seulement si vous êtes certain de vouloir repartir de zéro.");
            return 1;
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateB64 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
        var publicB64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        // The private key is written restrictively-named to a file (not just printed) so it can't be lost in scrollback;
        // the public key is printed because the seller pastes it into source.
        File.WriteAllText(privatePath, privateB64);

        Console.WriteLine("Paire de clés ECDSA P-256 générée.");
        Console.WriteLine();
        Console.WriteLine($"  CLÉ PRIVÉE  → écrite dans : {privatePath}");
        Console.WriteLine("  ⚠  Gardez-la SECRÈTE et hors du dépôt git (ne la committez jamais, ne la partagez jamais).");
        Console.WriteLine("     Quiconque la possède peut émettre des licences. Sauvegardez-la : sans elle, vous ne");
        Console.WriteLine("     pourrez plus émettre de nouvelles clés pour cette même clé publique.");
        Console.WriteLine();
        Console.WriteLine("  CLÉ PUBLIQUE (à coller dans l'application) :");
        Console.WriteLine();
        Console.WriteLine($"    {publicB64}");
        Console.WriteLine();
        Console.WriteLine("  Collez-la dans src/AurumTweaks/Services/EmbeddedLicenseKeyRing.cs :");
        Console.WriteLine($"      public string? PublicKeyBase64 => \"{publicB64}\";");
        Console.WriteLine();
        Console.WriteLine("  Dès que cette clé publique est intégrée, le palier Gratuit (Tranquille) s'applique et les");
        Console.WriteLine("  surfaces Premium (Avancé, Extrême, OC GPU) se verrouillent jusqu'à activation d'une clé.");
        return 0;
    }

    // ---- issue -----------------------------------------------------------------------------------------------------

    private static int CmdIssue(Cli cli)
    {
        var keyArg = cli.Get("key");
        var email = cli.Get("email");
        if (keyArg is null || email is null)
        {
            Console.Error.WriteLine("Usage : issue --key <fichier|base64> --email <acheteur> [--edition Premium|Free] " +
                                    "[--days N | --expires AAAA-MM-JJ]");
            return 1;
        }

        var (payload, error) = LicenseRequest.TryBuild(
            email, cli.Get("edition"), cli.Get("days"), cli.Get("expires"), DateTime.UtcNow);
        if (payload is null)
        {
            Console.Error.WriteLine($"Erreur : {error}");
            return 1;
        }

        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            signer.ImportPkcs8PrivateKey(Convert.FromBase64String(ReadFileOrLiteral(keyArg)), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            Console.Error.WriteLine("Erreur : clé privée illisible. Attendu : le contenu base64 d'aurum-license-private.key " +
                                    "(ou le chemin vers ce fichier).");
            return 1;
        }

        var token = LicenseIssuer.Issue(payload, signer);

        Console.WriteLine("Licence émise.");
        Console.WriteLine();
        PrintPayload(payload);
        Console.WriteLine();
        Console.WriteLine("  TOKEN (à remettre à l'acheteur — il le colle dans l'onglet Licence) :");
        Console.WriteLine();
        Console.WriteLine($"    {token}");
        Console.WriteLine();
        Console.WriteLine("  Vérifiez-le avant l'envoi :  verify --pub <clé publique> --token \"<token ci-dessus>\"");
        return 0;
    }

    // ---- verify ----------------------------------------------------------------------------------------------------

    private static int CmdVerify(Cli cli)
    {
        var pubArg = cli.Get("pub");
        var tokenArg = cli.Get("token");
        if (pubArg is null || tokenArg is null)
        {
            Console.Error.WriteLine("Usage : verify --pub <fichier|base64> --token <fichier|token>");
            return 1;
        }

        using var publicKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ReadFileOrLiteral(pubArg)), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            Console.Error.WriteLine("Erreur : clé publique illisible (base64 SubjectPublicKeyInfo attendu).");
            return 1;
        }

        // Verify against the SAME core the app runs, so this self-check is the app's real verdict, not an approximation.
        var result = LicenseVerifier.Verify(ReadFileOrLiteral(tokenArg), publicKey, DateTime.UtcNow);

        Console.WriteLine(result.IsValid ? "✓ Token VALIDE." : "✗ Token REFUSÉ.");
        Console.WriteLine($"  Verdict   : {LicenseStatusText.French(result.Reason)}");
        // A valid token carries a payload (which prints its own Édition line); only when there's none — a refused token —
        // do we surface the fail-safe edition, so the verdict isn't bare. This avoids the duplicate Édition line.
        if (result.Payload is { } p)
            PrintPayload(p);
        else
            Console.WriteLine($"  Édition   : {LicenseStatusText.FrenchEdition(result.Edition)}");
        return result.IsValid ? 0 : 1;
    }

    // ---- shared printing / helpers ---------------------------------------------------------------------------------

    private static void PrintPayload(LicensePayload p)
    {
        Console.WriteLine($"  Édition   : {LicenseStatusText.FrenchEdition(p.Edition)}");
        Console.WriteLine($"  Émise à   : {p.LicensedTo}");
        Console.WriteLine($"  Émise le  : {p.IssuedUtc:yyyy-MM-dd} (UTC)");
        Console.WriteLine(p.ExpiresUtc is { } e
            ? $"  Expire le : {e:yyyy-MM-dd} (UTC)"
            : "  Expire le : jamais (licence perpétuelle)");
    }

    // Treat an argument as a file path when it points at one, else as the literal value itself — so the seller can pass
    // either the private-key FILE or paste the base64 directly, without a separate flag.
    private static string ReadFileOrLiteral(string value)
        => File.Exists(value) ? File.ReadAllText(value).Trim() : value.Trim();

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Commande inconnue : « {command} ».");
        PrintUsage();
        return 1;
    }

    private static int PrintUsageReturning(int code)
    {
        PrintUsage();
        return code;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Aurum Tweaks — outil de licences (hors-ligne, côté vendeur)");
        Console.WriteLine();
        Console.WriteLine("Commandes :");
        Console.WriteLine("  genkey [--out <dossier>] [--force]");
        Console.WriteLine("      Génère une paire de clés ECDSA P-256. Écrit la clé privée et imprime la clé publique");
        Console.WriteLine("      à intégrer dans l'application.");
        Console.WriteLine();
        Console.WriteLine("  issue --key <fichier|base64> --email <acheteur> [--edition Premium|Free]");
        Console.WriteLine("        [--days N | --expires AAAA-MM-JJ]");
        Console.WriteLine("      Émet un token de licence signé. Sans --days/--expires : licence perpétuelle.");
        Console.WriteLine();
        Console.WriteLine("  verify --pub <fichier|base64> --token <fichier|token>");
        Console.WriteLine("      Vérifie un token contre la clé publique (à faire avant chaque envoi).");
    }
}

/// <summary>
/// Tiny <c>--flag value</c> parser. Bare flags (e.g. <c>--force</c>) register as present with an empty value. The first
/// token (the command) is ignored. Deliberately minimal — a seller tool needs clarity, not a parsing framework.
/// </summary>
internal sealed class Cli
{
    private readonly Dictionary<string, string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static Cli Parse(string[] args)
    {
        var cli = new Cli();
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var name = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                cli._flags[name] = args[++i];
            else
                cli._flags[name] = string.Empty;   // bare flag
        }
        return cli;
    }

    public string? Get(string name) => _flags.TryGetValue(name, out var v) && v.Length > 0 ? v : null;
    public bool Has(string name) => _flags.ContainsKey(name);
}

/// <summary>
/// The pure decision at the heart of <c>issue</c>: turn the CLI options into a <see cref="LicensePayload"/> or a clear
/// error — kept side-effect-free (no crypto, no I/O; "now" is passed in) so the money-bearing rules are obvious and
/// could be unit-tested in isolation. The edition defaults to Premium (the only edition worth selling), and expiry is
/// resolved from AT MOST ONE of --days / --expires; supplying both is rejected rather than silently picking one, so a
/// dated key is never accidentally minted as perpetual (or vice-versa).
/// </summary>
internal static class LicenseRequest
{
    public static (LicensePayload? Payload, string? Error) TryBuild(
        string email, string? editionArg, string? daysArg, string? expiresArg, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(email))
            return (null, "l'adresse de l'acheteur (--email) est requise.");

        AppEdition edition = AppEdition.Premium;
        if (editionArg is not null && !Enum.TryParse(editionArg, ignoreCase: true, out edition))
            return (null, $"édition inconnue « {editionArg} » (attendu : Premium ou Free).");

        if (daysArg is not null && expiresArg is not null)
            return (null, "choisissez --days OU --expires, pas les deux.");

        DateTime? expiresUtc = null;
        if (daysArg is not null)
        {
            if (!int.TryParse(daysArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days <= 0)
                return (null, $"--days doit être un entier positif (reçu « {daysArg} »).");
            expiresUtc = nowUtc.AddDays(days);
        }
        else if (expiresArg is not null)
        {
            if (!DateTime.TryParseExact(expiresArg, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry))
                return (null, $"--expires doit être une date AAAA-MM-JJ (reçu « {expiresArg} »).");
            if (expiry <= nowUtc)
                return (null, $"--expires est dans le passé ({expiresArg}) : la licence serait déjà expirée.");
            expiresUtc = expiry;
        }

        return (new LicensePayload(edition, email.Trim(), nowUtc, expiresUtc), null);
    }
}
