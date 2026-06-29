using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the honesty core of the "modify the BIOS from Windows" feature (<see cref="BiosApplyAdvisor"/>,
/// extracted from <see cref="BiosApplyService"/>). These strings are promises about what the app will and
/// will NOT do to firmware — the single most damage-bearing honesty surface in the product:
///   • a DIY board (ASUS/MSI/Gigabyte/ASRock/Biostar) must be told there is NO safe write API and that raw
///     NVRAM writes can BRICK the board — and that Aurum never does it automatically;
///   • an OEM machine's live settings must be surfaced as read-only, with writes deferred to the vendor tool;
///   • Legacy/CSM firmware must be told reboot-to-UEFI is unavailable, never silently promised.
/// A regression that softened any of these would let the UI imply a capability the app deliberately lacks.
/// </summary>
public class BiosApplyAdvisorTests
{
    private static (BiosApplyCapabilities caps, HardwareInfo hw) Run(
        bool uefi, bool vendorWmi, BiosVendor board, string vendorName = "", int settingCount = 0)
    {
        var caps = new BiosApplyCapabilities
        {
            CanRebootToFirmware = uefi,
            VendorWmiAvailable = vendorWmi,
            VendorName = vendorName,
            VendorSettingCount = settingCount
        };
        var hw = new HardwareInfo { DetectedBiosVendor = board };
        BiosApplyAdvisor.BuildSummaryAndNotes(caps, hw);
        return (caps, hw);
    }

    // ---- DIY boards: the load-bearing "we never write your BIOS" promise -------------

    [Theory]
    [InlineData(BiosVendor.Asus)]
    [InlineData(BiosVendor.Msi)]
    [InlineData(BiosVendor.Gigabyte)]
    [InlineData(BiosVendor.Asrock)]
    [InlineData(BiosVendor.Biostar)]
    public void DiyBoard_NoWmi_WarnsNoSafeWriteApi_AndNeverAutoWrites(BiosVendor board)
    {
        var (caps, _) = Run(uefi: true, vendorWmi: false, board: board);

        Assert.Contains("(DIY)", caps.Summary);
        Assert.Contains("pas d'API sûre", caps.Summary);
        Assert.Contains(board.ToString(), caps.Summary); // names the actual detected board
        // The non-negotiable caveat: bricking risk + the app never does it automatically.
        Assert.Contains(caps.Notes, n => n.Contains("BRIQUER") && n.Contains("jamais automatiquement"));
    }

    // ---- OEM machines: live settings are READ-ONLY, writes via the vendor tool -------

    [Fact]
    public void OemWmi_SaysSettingsAreReadOnly_AndDefersWritesToVendorTool()
    {
        var (caps, _) = Run(uefi: true, vendorWmi: true, board: BiosVendor.Dell,
            vendorName: "Dell", settingCount: 42);

        Assert.Contains("Dell", caps.Summary);
        Assert.Contains("42", caps.Summary);
        Assert.Contains("lisibles en direct", caps.Summary);
        // Honesty: we READ, we don't write — and we point at the official tool for writes.
        Assert.Contains(caps.Notes, n => n.Contains("lecture seule"));
    }

    [Fact]
    public void OemWmi_DoesNotEmitTheDiyBrickWarning()
    {
        // The read-only OEM path must never carry the DIY NVRAM-bricking caveat — wrong context, and it
        // would needlessly scare a user on a supported, safe vendor-WMI machine.
        var (caps, _) = Run(uefi: true, vendorWmi: true, board: BiosVendor.Lenovo,
            vendorName: "Lenovo", settingCount: 120);

        Assert.DoesNotContain(caps.Notes, n => n.Contains("BRIQUER"));
    }

    // ---- Firmware type: never silently promise reboot-to-UEFI on Legacy/CSM ---------

    [Fact]
    public void UefiFirmware_OffersTheUniversalSafeReboot()
    {
        var (caps, _) = Run(uefi: true, vendorWmi: false, board: BiosVendor.Unknown);
        Assert.Contains(caps.Notes, n => n.Contains("universelle et sûre"));
    }

    [Fact]
    public void LegacyFirmware_SaysRebootToUefiIsUnavailable()
    {
        var (caps, _) = Run(uefi: false, vendorWmi: false, board: BiosVendor.Unknown);
        Assert.Contains(caps.Notes, n =>
            n.Contains("Legacy/CSM") && n.Contains("n'est pas disponible"));
    }

    // ---- Non-OEM, non-DIY fallback: honest "nothing special detected" ---------------

    [Fact]
    public void UnknownBoard_NoWmi_Uefi_FallsBackToRebootHint_WithoutBrickWarning()
    {
        var (caps, _) = Run(uefi: true, vendorWmi: false, board: BiosVendor.Unknown);

        Assert.Contains("Aucun provider BIOS OEM détecté", caps.Summary);
        Assert.DoesNotContain(caps.Notes, n => n.Contains("BRIQUER")); // brick warning is DIY-only
    }

    [Fact]
    public void UnknownBoard_NoWmi_NoUefi_StatesBothLimitationsPlainly()
        => Assert.Equal(
            "Aucun provider BIOS OEM détecté et firmware non-UEFI.",
            Run(uefi: false, vendorWmi: false, board: BiosVendor.Unknown).caps.Summary);
}
