using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AurumTweaks.Models;

namespace AurumTweaks.Services;

public sealed class TweakRepository : ITweakRepository
{
    private readonly string _tweaksRoot;
    private readonly IReadOnlyDictionary<string, string> _manifest;
    private readonly List<Tweak> _tweaks = new();
    private readonly Dictionary<string, Tweak> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _rejected = new();
    private bool _loaded;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>Production: the install dir's <c>Tweaks</c> folder, gated by the SHA-256 manifest baked into this assembly.</summary>
    public TweakRepository() : this(Path.Combine(AppContext.BaseDirectory, "Tweaks"), CatalogManifest.Hashes) { }

    /// <summary>
    /// Seam for tests: drive the loader over an arbitrary catalog root with an arbitrary trust manifest, so
    /// the integrity gate (a dropped / edited file is refused) can be exercised without a real install dir.
    /// </summary>
    internal TweakRepository(string tweaksRoot, IReadOnlyDictionary<string, string> manifest)
    {
        _tweaksRoot = tweaksRoot;
        _manifest = manifest;
    }

    /// <summary>
    /// Files the integrity gate refused, each as <c>"relative/path.json [Verdict]"</c>. Non-empty means the
    /// catalog on disk did not match the baked-in manifest — a dropped or edited file that was NOT loaded and
    /// whose operations can never reach the elevated executor. Surfaced so the UI can flag it honestly.
    /// </summary>
    public IReadOnlyList<string> RejectedFiles => _rejected;

    public async Task<IReadOnlyList<Tweak>> LoadAllAsync()
    {
        if (_loaded) return _tweaks;

        if (!Directory.Exists(_tweaksRoot)) { _loaded = true; return _tweaks; }

        foreach (var file in Directory.EnumerateFiles(_tweaksRoot, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                // INTEGRITY GATE (fail-closed). Read the bytes, hash them, and refuse anything the baked-in
                // manifest does not vouch for. Because the app runs elevated and these files become AS-ADMIN
                // PowerShell/Cmd/etc. operations, a file that is Unknown (dropped in) or Tampered (edited on
                // disk) is skipped entirely and loudly logged — its operations never reach TweakService. This
                // is the actual control for the writable-catalog EoP, so it only trusts an exact hash match.
                byte[] bytes = await File.ReadAllBytesAsync(file);
                string rel = Path.GetRelativePath(_tweaksRoot, file);
                var verdict = CatalogIntegrity.Verify(_manifest, rel, CatalogIntegrity.ComputeHash(bytes));
                if (verdict != CatalogFileVerdict.Trusted)
                {
                    _rejected.Add($"{CatalogIntegrity.NormalizeRelativePath(rel)} [{verdict}]");
                    Serilog.Log.Warning(
                        "REFUSED untrusted tweak file {Path} ({Verdict}): not loaded; its operations will not run. " +
                        "This is expected if a non-admin dropped or edited a file in a writable install directory.",
                        rel, verdict);
                    continue;
                }

                var batch = JsonSerializer.Deserialize<List<Tweak>>(bytes, JsonOpts);
                if (batch is null) continue;
                foreach (var t in batch)
                {
                    _tweaks.Add(t);
                    _byId[t.Id] = t;
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to read tweak file {Path}", file);
            }
        }

        _loaded = true;
        if (_rejected.Count > 0)
            Serilog.Log.Warning("Catalog integrity: refused {Count} untrusted file(s): {Files}",
                _rejected.Count, string.Join(", ", _rejected));
        Serilog.Log.Information("Loaded {Count} tweaks ({Rejected} refused)", _tweaks.Count, _rejected.Count);
        return _tweaks;
    }

    public Tweak? GetById(string id) => _byId.TryGetValue(id, out var t) ? t : null;
}
