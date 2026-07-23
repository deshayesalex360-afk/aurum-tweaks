using System.Text.Json.Serialization;
using AurumTweaks.Services;

namespace AurumTweaks.Models;

/// <summary>
/// Binds a GPU-OC profile to a detected game, so Aurum can apply it when that game is active and revert
/// when it exits (the marquee "per-game profile" feature — Aurum already owns game detection + a
/// reversible apply pipeline). The OC values are stored inline (flat, JSON-friendly); voltage is
/// deliberately absent because Aurum never writes it. This is the persisted binding record only — the
/// matching decision is the pure <see cref="GameOcMatching"/> core, and the launch/exit watcher +
/// auto-apply live in the service layer.
/// </summary>
public sealed class GameOcBinding
{
    [JsonPropertyName("gameName")] public string GameName { get; set; } = "";
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonPropertyName("coreOffsetMhz")] public int CoreOffsetMhz { get; set; }
    [JsonPropertyName("memoryOffsetMhz")] public int MemoryOffsetMhz { get; set; }
    [JsonPropertyName("powerLimitPct")] public int PowerLimitPct { get; set; } = 100;
    [JsonPropertyName("tempLimitC")] public int TempLimitC { get; set; } = 83;
    [JsonPropertyName("amdMaxFreqMhz")] public int AmdMaxFreqMhz { get; set; }
    [JsonPropertyName("amdMaxVramFreqMhz")] public int AmdMaxVramFreqMhz { get; set; }

    /// <summary>Materialise the bound OC values into the profile the apply pipeline consumes. Voltage is
    /// always the neutral 900 mV placeholder — Aurum never applies voltage, so the binding never stores it.</summary>
    public GpuOcProfile ToProfile() =>
        new(CoreOffsetMhz, MemoryOffsetMhz, PowerLimitPct, TempLimitC, 900)
        {
            AmdMaxFreqMhz = AmdMaxFreqMhz,
            AmdMaxVramFreqMhz = AmdMaxVramFreqMhz,
        };
}
