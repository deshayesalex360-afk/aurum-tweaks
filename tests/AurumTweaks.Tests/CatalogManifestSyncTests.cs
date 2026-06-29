using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Guards the trust anchor itself. The baked-in <see cref="CatalogManifest"/> is only meaningful if it
/// actually describes the catalog that ships — a stale manifest would either refuse legit tweaks
/// (fail-closed but broken UX) or, worse, leave the door open if entries were copy-pasted loosely.
///
/// This recomputes SHA-256 over the REAL <c>/Tweaks</c> (linked into the test output) and asserts the
/// manifest matches it EXACTLY — no missing, extra, or mismatched entries — then drives the production
/// loader end-to-end to confirm the gate passes every shipped file with zero rejections. A failure here is
/// a real defect: someone edited the catalog without updating the trusted hashes. The failure message prints
/// the exact entries to paste back into <see cref="CatalogManifest"/>, so a legitimate edit is a one-copy fix
/// and an illegitimate drift is impossible to miss.
/// </summary>
public class CatalogManifestSyncTests
{
    private static string TweaksRoot => Path.Combine(AppContext.BaseDirectory, "Tweaks");

    private static SortedDictionary<string, string> HashRealCatalog()
    {
        var actual = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(TweaksRoot, "*.json", SearchOption.AllDirectories))
        {
            var rel = CatalogIntegrity.NormalizeRelativePath(Path.GetRelativePath(TweaksRoot, file));
            actual[rel] = CatalogIntegrity.ComputeHash(File.ReadAllBytes(file));
        }
        return actual;
    }

    [Fact]
    public void BakedInManifest_MatchesShippedCatalog_Exactly()
    {
        Assert.True(Directory.Exists(TweaksRoot),
            $"shipped catalog not found at {TweaksRoot} (csproj <Content> link missing?)");

        var actual = HashRealCatalog();
        var manifest = CatalogManifest.Hashes;

        var missing = actual.Keys.Where(k => !manifest.ContainsKey(k)).ToList();  // on disk, manifest silent
        var extra = manifest.Keys.Where(k => !actual.ContainsKey(k)).ToList();     // manifest claims, not on disk
        var mismatched = actual
            .Where(kv => manifest.TryGetValue(kv.Key, out var h)
                         && !string.Equals(h, kv.Value, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        var paste = string.Join(Environment.NewLine,
            actual.Select(kv => $"            [\"{kv.Key}\"] = \"{kv.Value}\","));

        Assert.True(missing.Count == 0 && extra.Count == 0 && mismatched.Count == 0,
            "CatalogManifest is out of sync with /Tweaks.\n" +
            $"  missing (on disk, not in manifest): {string.Join(", ", missing)}\n" +
            $"  extra (in manifest, not on disk):   {string.Join(", ", extra)}\n" +
            $"  hash mismatch:                      {string.Join(", ", mismatched)}\n" +
            "Replace the CatalogManifest.Hashes entries with:\n" + paste);
    }

    [Fact]
    public void Manifest_CoversTheWholeCatalog_NoEmptyHashes()
    {
        Assert.NotEmpty(CatalogManifest.Hashes);
        Assert.All(CatalogManifest.Hashes, kv =>
        {
            Assert.False(string.IsNullOrWhiteSpace(kv.Key), "manifest has an empty key");
            // A SHA-256 lowercase-hex digest is exactly 64 hex chars; anything else is a malformed entry.
            Assert.Matches("^[0-9a-f]{64}$", kv.Value);
        });
    }

    [Fact]
    public async Task RealCatalog_LoadsThroughTheGate_WithZeroRejections()
    {
        // End-to-end honesty: the production manifest must vouch for every shipped file, so the integrity gate
        // loads the real catalog with NOTHING refused. If this fails, the gate would be silently dropping
        // legitimate tweaks in production.
        var repo = new TweakRepository();
        var tweaks = await repo.LoadAllAsync();

        Assert.Empty(repo.RejectedFiles);
        Assert.True(tweaks.Count >= 50, $"expected the gated catalog to still load (≥50 tweaks); got {tweaks.Count}");
    }
}
