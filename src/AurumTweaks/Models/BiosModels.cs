using System.Collections.Generic;

namespace AurumTweaks.Models;

/// <summary>
/// One BIOS setting documented in the BIOS guide. Aurum knows where to find it on
/// each vendor and what to recommend per tier.
/// </summary>
public class BiosSetting
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;             // canonical name
    public string Category { get; set; } = string.Empty;          // RAM, CPU, Platform, Storage, Security, Boot, Fan
    public string Description { get; set; } = string.Empty;

    /// <summary>BIOS menu path per vendor — e.g. ASUS → "Ai Tweaker &gt; FCLK Frequency".</summary>
    public Dictionary<BiosVendor, string> VendorPaths { get; set; } = new();

    /// <summary>Display name per vendor — e.g. ASUS "DOCP", MSI "A-XMP", Gigabyte "XMP".</summary>
    public Dictionary<BiosVendor, string> VendorAliases { get; set; } = new();

    public Dictionary<TweakTier, string> Recommendations { get; set; } = new();
    public string ExpectedGain { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; }
    public List<CpuFamily> Compatibility { get; set; } = new();
    public string ValidationTool { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// One checklist item shown in the BIOS interactive checklist.
/// </summary>
public class BiosChecklistItem
{
    public string SettingId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public TweakTier Tier { get; set; }
    public RiskLevel Risk { get; set; }
    public bool IsChecked { get; set; }
}

/// <summary>
/// Per-PC verdict for one BIOS setting: how it maps to THIS machine, what state we
/// detected from Windows, and the vendor-specific path to change it.
/// </summary>
public class BiosRecommendation
{
    public BiosSetting Setting { get; set; } = null!;
    public BiosCheckState State { get; set; } = BiosCheckState.Unknown;

    /// <summary>What we detected from Windows, e.g. "RAM à 4800 MT/s / notée 6000 → EXPO OFF".</summary>
    public string DetectedStateText { get; set; } = string.Empty;

    /// <summary>Menu path for the detected board vendor (falls back to a generic hint).</summary>
    public string VendorPath { get; set; } = string.Empty;
    public string VendorAlias { get; set; } = string.Empty;

    /// <summary>The recommendation string for the currently selected tier.</summary>
    public string TierRecommendation { get; set; } = string.Empty;

    /// <summary>Sort weight — higher floats to the top of the action list.</summary>
    public int Priority { get; set; }

    // Convenience pass-throughs for binding.
    public string Name => Setting?.Name ?? string.Empty;
    public string Category => Setting?.Category ?? string.Empty;
    public string Description => Setting?.Description ?? string.Empty;
    public string ExpectedGain => Setting?.ExpectedGain ?? string.Empty;
    public string ValidationTool => Setting?.ValidationTool ?? string.Empty;
    public string Notes => Setting?.Notes ?? string.Empty;
    public RiskLevel Risk => Setting?.Risk ?? RiskLevel.None;

    public bool IsActionNeeded => State == BiosCheckState.ActionNeeded;
    public bool IsCritical => Risk == RiskLevel.HardwareDamage;

    /// <summary>Short status chip text used by the BIOS report card.</summary>
    public string StateLabel => State switch
    {
        BiosCheckState.ActionNeeded => "À CHANGER",
        BiosCheckState.Optimal => "DÉJÀ OK",
        BiosCheckState.Verify => "À VÉRIFIER",
        _ => "GUIDE"
    };

    public bool HasDetectedState => !string.IsNullOrWhiteSpace(DetectedStateText);
}

/// <summary>
/// What Aurum could tell about a BIOS setting from inside Windows.
/// Honest about the limits: many settings simply can't be read back.
/// </summary>
public enum BiosCheckState
{
    Unknown,        // applicable but we have no signal → just a guide
    Optimal,        // detected already correct → nothing to do
    ActionNeeded,   // detected wrong/suboptimal → change it for a real gain
    Verify          // applicable, can't read state from Windows → check it in the BIOS
}

/// <summary>Personalized BIOS report: recommendations ranked for the detected machine.</summary>
public class BiosAdvisorReport
{
    public System.Collections.Generic.List<BiosRecommendation> Recommendations { get; set; } = new();
    public int ActionNeededCount { get; set; }
    public int VerifyCount { get; set; }
    public int OptimalCount { get; set; }
    public string PlatformSummary { get; set; } = string.Empty;
}

/// <summary>
/// What we can realistically do to change BIOS settings from inside Windows on THIS machine.
/// Honest by design: reboot-to-UEFI works everywhere; live read/write only exists on
/// OEM business machines (Dell/HP/Lenovo) via vendor WMI; DIY boards have no safe API.
/// </summary>
public class BiosApplyCapabilities
{
    /// <summary>True on UEFI firmware — we can reboot straight into the BIOS setup (universal, safe).</summary>
    public bool CanRebootToFirmware { get; set; }

    /// <summary>True when a vendor BIOS WMI provider (Dell/HP/Lenovo) is present and queryable.</summary>
    public bool VendorWmiAvailable { get; set; }

    /// <summary>The detected vendor WMI namespace, e.g. "root\\WMI" (Lenovo), "root\\HP\\InstrumentedBIOS".</summary>
    public string VendorWmiNamespace { get; set; } = string.Empty;

    /// <summary>Friendly vendor name for the WMI provider (Dell / HP / Lenovo).</summary>
    public string VendorName { get; set; } = string.Empty;

    /// <summary>How many BIOS settings the vendor provider exposed (read-only enumeration).</summary>
    public int VendorSettingCount { get; set; }

    /// <summary>One-line plain-language summary of the best available method.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Honest caveats shown to the user (bricking risk, OEM-only, etc.).</summary>
    public System.Collections.Generic.List<string> Notes { get; set; } = new();

    /// <summary>BIOS settings read live from the vendor WMI provider (OEM machines only).</summary>
    public System.Collections.Generic.List<VendorBiosSetting> VendorSettings { get; set; } = new();
}

/// <summary>One BIOS setting read live from an OEM vendor's WMI provider.</summary>
public class VendorBiosSetting
{
    public string Name { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    /// <summary>Available choices when the provider exposes them (e.g. "Enabled,Disabled").</summary>
    public string PossibleValues { get; set; } = string.Empty;
}

/// <summary>
/// RAM kit description used by the RAM calculator.
/// </summary>
public class RamKitProfile
{
    public string Name { get; set; } = string.Empty;
    public string RamType { get; set; } = "DDR4";                 // DDR4 or DDR5
    public int FrequencyMTs { get; set; }
    public int Capacity { get; set; }
    public int RankCount { get; set; }                            // 1 or 2 per DIMM
    public string MemoryIc { get; set; } = string.Empty;          // B-die, Hynix A-die, M-die, Micron Rev B...

    // Primary
    public int CL { get; set; }
    public int tRCD { get; set; }
    public int tRP { get; set; }
    public int tRAS { get; set; }
    public int tRC { get; set; }

    // Secondaries
    public int tRFC { get; set; }
    public int tRRDS { get; set; }
    public int tRRDL { get; set; }
    public int tFAW { get; set; }
    public int tWR { get; set; }

    public string Vdimm { get; set; } = "1.35V";
    public string Notes { get; set; } = string.Empty;
}
