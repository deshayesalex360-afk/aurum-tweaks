using System.Text.Json.Serialization;
using AurumTweaks.Services;

namespace AurumTweaks.Models;

/// <summary>
/// Data model for a planned "per-game GPU-OC profile" feature: binds a set of OC values to a game name.
/// Aurum already owns game detection and a reversible apply pipeline, so this record plus the pure
/// <see cref="GameOcMatching"/> core are the ready foundation. The OC values are stored inline (flat,
/// JSON-friendly); voltage is deliberately absent because Aurum never writes it.
///
/// NOT YET WIRED — roadmap. There is no launch/exit watcher and no auto-apply today; nothing in the app
/// consumes this binding yet (see <see cref="GameOcMatching"/>). When it is built it must be OPT-IN and
/// user-visible — Aurum never silently applies an overclock in the background. Kept as tested foundation
/// so the feature can be added deliberately, not sold as a live capability.
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
