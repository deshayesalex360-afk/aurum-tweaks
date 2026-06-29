using System;
using System.IO;
using System.Reflection;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Smoke-tests the <see cref="BuildIdentity"/> I/O probe against REAL binaries on disk (the test assembly itself), so
/// the download-trust facts the « Transparence » page shows are genuinely measured, never fabricated. The probe is the
/// side-effecting half of the split; its honest FORMATTING is pinned separately in <see cref="TransparencyReportTests"/>.
/// </summary>
public class BuildIdentityTests
{
    private static string ThisAssemblyPath => Assembly.GetExecutingAssembly().Location;

    [Fact]
    public void Probe_OnARealBinary_ReturnsVersion_A64HexFingerprint_MatchingTheCatalogPrimitive()
    {
        var facts = BuildIdentity.Probe(ThisAssemblyPath);

        Assert.False(string.IsNullOrWhiteSpace(facts.AppVersion));
        Assert.NotNull(facts.ExecutableSha256);
        Assert.Matches("^[0-9a-f]{64}$", facts.ExecutableSha256!);     // lowercase-hex SHA-256, well-formed

        // Anti-drift: the fingerprint IS the catalog gate's SHA-256 over the same bytes — one hashing primitive, not two.
        using var stream = File.OpenRead(ThisAssemblyPath);
        Assert.Equal(CatalogIntegrity.ComputeHash(stream), facts.ExecutableSha256);

        Assert.Equal(ThisAssemblyPath, facts.ExecutablePath);   // the real path, so the Get-FileHash command is runnable
    }

    [Fact]
    public void Probe_OnAnUnsignedBinary_ReportsSignatureAbsent_NeverPresent()
    {
        // The test build isn't code-signed, so the managed Authenticode probe must report « absente » — proving the
        // signature line reflects a real check and can never over-claim a signature that isn't there.
        var facts = BuildIdentity.Probe(ThisAssemblyPath);

        Assert.Equal(ExecutableSignatureState.Absent, facts.ExecutableSignature);
    }

    [Fact]
    public void Probe_OnAMissingFile_DegradesHonestly_NoThrow_NoFabricatedHash()
    {
        var missing = Path.Combine(Path.GetTempPath(), "aurum-nonexistent-" + Guid.NewGuid().ToString("N") + ".exe");

        var facts = BuildIdentity.Probe(missing);   // must not throw

        Assert.Null(facts.ExecutableSha256);                                   // honest absence, not a placeholder
        Assert.Null(facts.ExecutablePath);                                     // no path ⇒ no Get-FileHash command shown
        Assert.Equal(ExecutableSignatureState.Indeterminate, facts.ExecutableSignature);
        Assert.False(string.IsNullOrWhiteSpace(facts.AppVersion));             // falls back to the assembly version
    }
}
