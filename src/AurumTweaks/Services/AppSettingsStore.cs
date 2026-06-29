using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace AurumTweaks.Services;

public interface IAppSettingsStore
{
    AppSettings Current { get; }
    Task LoadAsync();
    Task SaveAsync();
}

public sealed class AppSettings
{
    public string Language { get; set; } = "fr-FR";
    public bool CreateRestorePointBeforeTweaks { get; set; } = true;
    public bool StrictCompetitiveAntiCheat { get; set; } = false;
    public bool TelemetryEnabled { get; set; } = false;
    public bool HasSeenWelcome { get; set; } = false;
    public DateTime LastLaunchUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persists app settings to %LOCALAPPDATA%\AurumTweaks\settings.json.
/// </summary>
public sealed class AppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    public AppSettings Current { get; private set; } = new();

    public AppSettingsStore()
    {
        var dir = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\AurumTweaks");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        // Best-effort sync load at construction so other services can read defaults.
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOpts);
                if (s is not null) Current = s;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to load app settings, using defaults");
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_path)) return;
            await using var s = File.OpenRead(_path);
            var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(s, JsonOpts);
            if (loaded is not null) Current = loaded;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to load app settings");
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            await using var s = File.Create(_path);
            await JsonSerializer.SerializeAsync(s, Current, JsonOpts);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to save app settings");
        }
    }
}
