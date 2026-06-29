using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the pure integrity core <see cref="CatalogIntegrity"/> — the decision that protects the elevated
/// executor from a dropped/edited tweak file (see the writable-catalog EoP in the class doc).
///
/// The honesty surface here is FAIL-CLOSED VERIFICATION: a "Trusted" verdict must reflect a real SHA-256
/// match and nothing else. We prove the hash is genuine SHA-256 (RFC test vectors, not a home-grown digest),
/// that path normalisation can't be used to slip past a key, and that every non-exact case is refused as
/// Tampered or Unknown — never silently allowed.
/// </summary>
public class CatalogIntegrityTests
{
    // ---- ComputeHash is REAL SHA-256 -----------------------------------------

    [Fact]
    public void ComputeHash_MatchesKnownSha256Vectors()
    {
        // Standard SHA-256 vectors: empty input, and "abc". If ComputeHash ever drifted to a different/weaker
        // digest, the whole "we verified the catalog" claim would be a lie — these catch that.
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            CatalogIntegrity.ComputeHash(Array.Empty<byte>()));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            CatalogIntegrity.ComputeHash(new byte[] { 0x61, 0x62, 0x63 })); // "abc"
    }

    [Fact]
    public void ComputeHash_StreamAndByteOverloads_Agree()
    {
        var data = Encoding.UTF8.GetBytes("[{\"id\":\"x\"}]");
        using var ms = new MemoryStream(data);
        Assert.Equal(CatalogIntegrity.ComputeHash(data), CatalogIntegrity.ComputeHash(ms));
    }

    [Fact]
    public void ComputeHash_IsContentSensitive_OneFlippedByteChangesTheHash()
    {
        var a = CatalogIntegrity.ComputeHash(new byte[] { 0x61, 0x62, 0x63 });
        var b = CatalogIntegrity.ComputeHash(new byte[] { 0x61, 0x62, 0x64 });
        Assert.NotEqual(a, b);
    }

    // ---- Path normalisation --------------------------------------------------

    [Theory]
    [InlineData("tranquille\\01-foo.json", "tranquille/01-foo.json")]
    [InlineData("/leading", "leading")]
    [InlineData("\\leading", "leading")]
    [InlineData("a/b.json", "a/b.json")]
    public void NormalizeRelativePath_CanonicalisesSeparators(string input, string expected)
        => Assert.Equal(expected, CatalogIntegrity.NormalizeRelativePath(input));

    // ---- Verify: only an exact (known path, matching hash) is Trusted --------

    private static Dictionary<string, string> Manifest(string path, string hash)
        => new(StringComparer.OrdinalIgnoreCase) { [path] = hash };

    [Fact]
    public void Verify_ExactPathAndHash_IsTrusted()
        => Assert.Equal(CatalogFileVerdict.Trusted,
            CatalogIntegrity.Verify(Manifest("a/b.json", "abcd"), "a/b.json", "abcd"));

    [Fact]
    public void Verify_KnownPath_WrongHash_IsTampered()
        => Assert.Equal(CatalogFileVerdict.Tampered,
            CatalogIntegrity.Verify(Manifest("a/b.json", "abcd"), "a/b.json", "ffff"));

    [Fact]
    public void Verify_PathNotInManifest_IsUnknown()
        => Assert.Equal(CatalogFileVerdict.Unknown,
            CatalogIntegrity.Verify(Manifest("a/b.json", "abcd"), "dropped.json", "abcd"));

    [Fact]
    public void Verify_NormalisesWindowsSeparators_SoABackslashPathStillMatches()
        => Assert.Equal(CatalogFileVerdict.Trusted,
            CatalogIntegrity.Verify(Manifest("a/b.json", "abcd"), "a\\b.json", "abcd"));

    [Fact]
    public void Verify_HashComparison_IsCaseInsensitiveHex()
        => Assert.Equal(CatalogFileVerdict.Trusted,
            CatalogIntegrity.Verify(Manifest("a/b.json", "ABCD"), "a/b.json", "abcd"));

    [Fact]
    public void Verify_AgainstEmptyManifest_IsAlwaysUnknown_FailClosed()
        => Assert.Equal(CatalogFileVerdict.Unknown,
            CatalogIntegrity.Verify(new Dictionary<string, string>(), "anything.json", "abcd"));
}
