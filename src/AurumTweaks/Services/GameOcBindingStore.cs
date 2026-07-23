using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AurumTweaks.Models;
using Serilog;

namespace AurumTweaks.Services;

/// <summary>Persistence for the per-game GPU-OC bindings. JSON-store idiom: best-effort, never throws,
/// returns an empty list on any I/O/parse trouble (a missing/corrupt file must never crash the app).
/// Foundation for the planned per-game feature — not yet registered in DI nor consumed by the app.</summary>
public interface IGameOcBindingStore
{
    IReadOnlyList<GameOcBinding> Load();
    void Save(IReadOnlyList<GameOcBinding> bindings);
}

/// <summary>File-backed store at <c>%LOCALAPPDATA%\AurumTweaks\game-oc-bindings.json</c>.</summary>
public sealed class GameOcBindingStore : IGameOcBindingStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public GameOcBindingStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AurumTweaks", "game-oc-bindings.json"))
    {
    }

    // Test seam (InternalsVisibleTo AurumTweaks.Tests) so a temp-dir round-trip needs no real LOCALAPPDATA.
    internal GameOcBindingStore(string path) => _path = path;

    public IReadOnlyList<GameOcBinding> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<GameOcBinding>();
            var list = JsonSerializer.Deserialize<List<GameOcBinding>>(File.ReadAllText(_path), Options);
            return list ?? (IReadOnlyList<GameOcBinding>)Array.Empty<GameOcBinding>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load game OC bindings from {Path}", _path);
            return Array.Empty<GameOcBinding>();
        }
    }

    public void Save(IReadOnlyList<GameOcBinding> bindings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(bindings, Options));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save game OC bindings to {Path}", _path);
        }
    }
}

/// <summary>No-op fallback (e.g. tests that don't care about persistence).</summary>
public sealed class NullGameOcBindingStore : IGameOcBindingStore
{
    public static readonly NullGameOcBindingStore Instance = new();
    public IReadOnlyList<GameOcBinding> Load() => Array.Empty<GameOcBinding>();
    public void Save(IReadOnlyList<GameOcBinding> bindings) { }
}
