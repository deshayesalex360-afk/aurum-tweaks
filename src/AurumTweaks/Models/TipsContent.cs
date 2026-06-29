using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>
/// Content for the Tips tab — OS recommendations, custom OS comparison, hardware mods.
/// </summary>
public class OsRecommendation
{
    public string Name { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TargetAudience { get; set; } = string.Empty;
    public string Performance { get; set; } = string.Empty;
    public string SecurityLevel { get; set; } = string.Empty;
    public AntiCheatMatrix AntiCheat { get; set; } = new();
    public bool IsOfficial { get; set; }
    public bool IsLegalToRedistribute { get; set; }
    public List<string> Pros { get; set; } = new();
    public List<string> Cons { get; set; } = new();
    public string OfficialUrl { get; set; } = string.Empty;
    public string RecommendationVerdict { get; set; } = string.Empty;
}

/// <summary>
/// Hardware modification recommendation (RAM upgrade, NVMe, cooler, etc.)
/// </summary>
public class HardwareModTip
{
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;          // RAM, Storage, Cooling, PSU, Case, GPU, CPU
    public string Description { get; set; } = string.Empty;
    public string ExpectedGain { get; set; } = string.Empty;
    public string PriceRange { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; }
    public List<string> Recommendations { get; set; } = new();
}
