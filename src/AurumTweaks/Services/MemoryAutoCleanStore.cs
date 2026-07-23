using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>Persistence for the « auto-nettoyage » settings. JSON-store idiom: best-effort, never throws, returns the
/// disabled <see cref="MemoryAutoCleanSettings.Default"/> on any missing/corrupt file — so a bad file can never crash
/// the app nor silently leave a surprise background flush enabled.</summary>
public interface IMemoryAutoCleanStore
{
    MemoryAutoCleanSettings Load();
    void Save(MemoryAutoCleanSettings settings);
}

/// <summary>File-backed store at <c>%LOCALAPPDATA%\AurumTweaks\memory-autoclean.json</c>.</summary>
public sealed class MemoryAutoCleanStore : IMemoryAutoCleanStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public MemoryAutoCleanStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AurumTweaks", "memory-autoclean.json"))
    {
    }

    // Test seam (InternalsVisibleTo AurumTweaks.Tests) so a temp-dir round-trip needs no real LOCALAPPDATA.
    internal MemoryAutoCleanStore(string path) => _path = path;

    public MemoryAutoCleanSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return MemoryAutoCleanSettings.Default;
            var loaded = JsonSerializer.Deserialize<MemoryAutoCleanSettings>(File.ReadAllText(_path), Options);
            // Clamp on the way out so a hand-edited threshold/interval can't drive an absurd policy.
            return loaded is null ? MemoryAutoCleanSettings.Default : loaded.Normalized();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load memory auto-clean settings from {Path}", _path);
            return MemoryAutoCleanSettings.Default;
        }
    }

    public void Save(MemoryAutoCleanSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings.Normalized(), Options));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save memory auto-clean settings to {Path}", _path);
        }
    }
}

/// <summary>In-memory fallback (tests / a run that shouldn't touch disk). Holds the last saved value; defaults to disabled.</summary>
public sealed class NullMemoryAutoCleanStore : IMemoryAutoCleanStore
{
    private MemoryAutoCleanSettings _settings = MemoryAutoCleanSettings.Default;
    public MemoryAutoCleanSettings Load() => _settings;
    public void Save(MemoryAutoCleanSettings settings) => _settings = settings.Normalized();
}
