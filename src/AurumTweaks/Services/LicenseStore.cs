using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>
/// JSON-free flat-file licence store under <c>%LOCALAPPDATA%\AurumTweaks\License\license.key</c> — the token is a
/// single opaque string, so it is written and read verbatim with no serialization. Mirrors the side-store discipline
/// of <see cref="ScoreHistoryStore"/>: every operation swallows and logs I/O failures because the licence layer must
/// fail safe to Free, never throw into startup. A missing file is the normal unlicensed state (returns null without a
/// warning); only an actual read/write fault is logged. Nothing here is secret — the token is inert without the
/// seller's private key.
/// </summary>
public sealed class LicenseStore : ILicenseStore
{
    private readonly string _file;

    public LicenseStore()
    {
        var dir = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\AurumTweaks\\License");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "license.key");
    }

    public async Task<string?> LoadAsync()
    {
        if (!File.Exists(_file)) return null;   // unlicensed is normal, not an error to log
        try
        {
            var token = await File.ReadAllTextAsync(_file);
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load licence token {File}", _file);
            return null;
        }
    }

    public async Task SaveAsync(string token)
    {
        try
        {
            await File.WriteAllTextAsync(_file, token);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save licence token {File}", _file);
        }
    }

    public Task ClearAsync()
    {
        // Delete directly — File.Delete is a no-op on an absent file and the directory is created in the ctor, so the
        // TOCTOU existence-check would guard nothing.
        try
        {
            File.Delete(_file);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clear licence token {File}", _file);
        }
        return Task.CompletedTask;
    }
}
