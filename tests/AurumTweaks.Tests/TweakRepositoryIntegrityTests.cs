using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Drives the REAL <see cref="TweakRepository"/> loader over a throwaway on-disk catalog with a controlled
/// trust manifest, to prove the security control actually bites: a file the manifest does not vouch for is
/// NOT loaded, so its operations can never reach the elevated executor.
///
/// This is the unit-level reproduction of the writable-catalog Elevation-of-Privilege: the app runs as admin,
/// and TweakService runs each tweak's PowerShell/Cmd/etc. AS ADMIN. If a standard user can drop or edit a
/// JSON in the install dir, those ops would run elevated on the next Apply. The gate's job is to refuse such
/// files. These tests assert refusal for both shapes of attack (Unknown = dropped in, Tampered = edited in
/// place) and confirm the legit file alongside still loads.
/// </summary>
public sealed class TweakRepositoryIntegrityTests : IDisposable
{
    private readonly string _root;

    public TweakRepositoryIntegrityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aurum-catalog-itg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    // A benign, well-formed tweak (one registry op).
    private const string GoodTweakJson =
        "[{\"id\":\"good-1\",\"name\":{\"fr\":\"Bon\",\"en\":\"Good\"}," +
        "\"operations\":[{\"type\":\"Registry\",\"hive\":\"HKCU\",\"key\":\"k\",\"name\":\"n\",\"apply\":\"1\",\"revert\":\"0\"}]}]";

    // The attacker payload: a tweak whose op is an elevated PowerShell command. This is exactly what the gate
    // must keep out of the loaded set so it never reaches TweakService / powershell.exe.
    private const string EvilTweakJson =
        "[{\"id\":\"evil\",\"name\":{\"fr\":\"x\",\"en\":\"x\"}," +
        "\"operations\":[{\"type\":\"PowerShell\",\"script\":\"Start-Process calc.exe\",\"revertScript\":\"echo x\"}]}]";

    private string WriteCatalogFile(string relative, string json)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json);
        return full;
    }

    private static string HashOf(string fullPath) => CatalogIntegrity.ComputeHash(File.ReadAllBytes(fullPath));

    private static Dictionary<string, string> EmptyManifest() => new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task TrustedFile_Loads_AndIsNotRejected()
    {
        var good = WriteCatalogFile("tranquille/good.json", GoodTweakJson);
        var manifest = EmptyManifest();
        manifest["tranquille/good.json"] = HashOf(good);

        var repo = new TweakRepository(_root, manifest);
        var tweaks = await repo.LoadAllAsync();

        Assert.Single(tweaks);
        Assert.NotNull(repo.GetById("good-1"));
        Assert.Empty(repo.RejectedFiles);
    }

    [Fact]
    public async Task DroppedFile_NotInManifest_IsRefused_AndItsAdminOpsNeverLoad()
    {
        // The core EoP scenario: a non-admin drops Tweaks\evil.json into a writable install dir. The manifest
        // (here empty — it vouches for nothing) doesn't list it, so it must be refused as Unknown.
        WriteCatalogFile("evil.json", EvilTweakJson);

        var repo = new TweakRepository(_root, EmptyManifest());
        var tweaks = await repo.LoadAllAsync();

        Assert.Empty(tweaks);
        Assert.Null(repo.GetById("evil"));
        Assert.Contains(repo.RejectedFiles, r => r.Contains("evil.json") && r.Contains(nameof(CatalogFileVerdict.Unknown)));
    }

    [Fact]
    public async Task EditedTrustedFile_IsRefused_AsTampered()
    {
        // The manifest vouches for the ORIGINAL bytes; the attacker then rewrites the file in place to inject
        // an elevated PowerShell op. The recomputed hash no longer matches → Tampered → not loaded.
        var path = WriteCatalogFile("tranquille/good.json", GoodTweakJson);
        var manifest = EmptyManifest();
        manifest["tranquille/good.json"] = HashOf(path); // hash of the clean content

        File.WriteAllText(path, EvilTweakJson);           // tamper after the manifest was fixed

        var repo = new TweakRepository(_root, manifest);
        var tweaks = await repo.LoadAllAsync();

        Assert.Empty(tweaks);
        Assert.Null(repo.GetById("evil"));
        Assert.Null(repo.GetById("good-1"));
        Assert.Contains(repo.RejectedFiles, r => r.Contains("good.json") && r.Contains(nameof(CatalogFileVerdict.Tampered)));
    }

    [Fact]
    public async Task MixedCatalog_LoadsTheTrustedFile_ButRefusesTheDroppedOneBesideIt()
    {
        var good = WriteCatalogFile("tranquille/good.json", GoodTweakJson);
        WriteCatalogFile("extreme/evil.json", EvilTweakJson);

        var manifest = EmptyManifest();
        manifest["tranquille/good.json"] = HashOf(good); // only the good file is vouched for

        var repo = new TweakRepository(_root, manifest);
        var tweaks = await repo.LoadAllAsync();

        Assert.Single(tweaks);
        Assert.NotNull(repo.GetById("good-1"));
        Assert.Null(repo.GetById("evil"));
        Assert.Single(repo.RejectedFiles);
    }

    [Fact]
    public async Task EmptyManifest_RefusesEveryFile_FailClosed()
    {
        // Defensive default: if the manifest is empty (e.g. generation failed), the gate refuses everything
        // rather than waving the whole catalog through. Fail-closed, never fail-open.
        WriteCatalogFile("a.json", GoodTweakJson);
        WriteCatalogFile("sub/b.json", GoodTweakJson);

        var repo = new TweakRepository(_root, EmptyManifest());
        var tweaks = await repo.LoadAllAsync();

        Assert.Empty(tweaks);
        Assert.Equal(2, repo.RejectedFiles.Count);
    }

    [Fact]
    public async Task WrongHashInManifest_IsRefused_NotWavedThrough()
    {
        // A manifest entry that exists for the path but holds a bogus hash must NOT be treated as trust.
        WriteCatalogFile("tranquille/good.json", GoodTweakJson);
        var manifest = EmptyManifest();
        manifest["tranquille/good.json"] = new string('0', 64); // plausible-looking but wrong

        var repo = new TweakRepository(_root, manifest);
        var tweaks = await repo.LoadAllAsync();

        Assert.Empty(tweaks);
        Assert.Contains(repo.RejectedFiles, r => r.Contains(nameof(CatalogFileVerdict.Tampered)));
    }

    [Fact]
    public async Task MissingCatalogDirectory_LoadsNothing_WithoutThrowing()
    {
        var repo = new TweakRepository(Path.Combine(_root, "does-not-exist"), EmptyManifest());
        var tweaks = await repo.LoadAllAsync();
        Assert.Empty(tweaks);
        Assert.Empty(repo.RejectedFiles);
    }
}
