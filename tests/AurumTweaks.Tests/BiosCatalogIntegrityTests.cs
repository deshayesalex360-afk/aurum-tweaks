using System;
using System.Collections.Generic;
using System.Linq;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Validates the BIOS knowledge base — the static <see cref="BiosCatalog"/> that powers the BIOS guide.
/// <see cref="BiosAdvisorServiceTests"/> already covers how the advisor <i>filters/ranks</i> these cards
/// for a given PC; this is the complementary safety net for the catalog's <i>internal honesty</i>, pinning
/// the promises every card makes in the UI: ids are non-empty and unique; the name/category/description/gain
/// are filled (no blank fields); the category buckets correctly; every card advises on all three tiers
/// (no blank "recommendation" for whichever tier the user picked); every card spells out the menu path for
/// each of the big-four DIY vendors (no dead "go to … nothing" instruction); compatibility lists are clean;
/// and — the load-bearing safety rule — any High/HardwareDamage card carries a written caveat justifying its
/// scary badge (e.g. the VSOC ≤ 1.30 V burn-risk cap).
///
/// A failure here is a real content defect (a blank path, a missing tier, an unjustified danger badge), not
/// a flaky test — fix the catalog data, don't weaken the assertion.
/// </summary>
public class BiosCatalogIntegrityTests
{
    private static readonly List<BiosSetting> Catalog = BiosCatalog.All();

    // The four DIY board vendors every card documents a path for. (OEM Dell/HP/Lenovo are handled
    // separately via live vendor WMI in BiosApplyService, not through this guide.)
    private static readonly BiosVendor[] DiyVendors =
        { BiosVendor.Asus, BiosVendor.Msi, BiosVendor.Gigabyte, BiosVendor.Asrock };

    // The category buckets the model documents and the UI groups by.
    private static readonly HashSet<string> KnownCategories =
        new(StringComparer.Ordinal) { "RAM", "CPU", "Platform", "Storage", "Security", "Boot", "Fan" };

    private static readonly TweakTier[] AllTiers =
        { TweakTier.Tranquille, TweakTier.Avance, TweakTier.Extreme };

    [Fact]
    public void Catalog_Loads_AndIsNonTrivial()
        => Assert.True(Catalog.Count >= 25,
            $"Expected a substantial BIOS knowledge base (≥25 settings); got {Catalog.Count}.");

    [Fact]
    public void EverySetting_HasNonEmptyId_AndIdsAreUnique()
    {
        Assert.All(Catalog, s => Assert.False(string.IsNullOrWhiteSpace(s.Id), "a BIOS setting has an empty id"));

        var dupes = Catalog
            .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} (x{g.Count()})")
            .ToList();

        Assert.True(dupes.Count == 0, "duplicate BIOS setting ids: " + string.Join(", ", dupes));
    }

    [Fact]
    public void EverySetting_HasCoreDisplayText()
    {
        var problems = new List<string>();
        foreach (var s in Catalog)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) problems.Add($"{s.Id}: empty Name");
            if (string.IsNullOrWhiteSpace(s.Category)) problems.Add($"{s.Id}: empty Category");
            if (string.IsNullOrWhiteSpace(s.Description)) problems.Add($"{s.Id}: empty Description");
            if (string.IsNullOrWhiteSpace(s.ExpectedGain)) problems.Add($"{s.Id}: empty ExpectedGain");
        }

        Assert.True(problems.Count == 0,
            "BIOS cards with empty display text:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void EverySetting_HasKnownCategory()
    {
        var bad = Catalog
            .Where(s => !KnownCategories.Contains(s.Category))
            .Select(s => $"{s.Id} → '{s.Category}'")
            .ToList();

        Assert.True(bad.Count == 0,
            "settings with an unknown Category (would mis-bucket in the UI): " + string.Join(", ", bad));
    }

    [Fact]
    public void EverySetting_AdvisesOnAllThreeTiers()
    {
        var problems = new List<string>();
        foreach (var s in Catalog)
            foreach (var tier in AllTiers)
                if (!s.Recommendations.TryGetValue(tier, out var rec) || string.IsNullOrWhiteSpace(rec))
                    problems.Add($"{s.Id}: missing/empty {tier} recommendation");

        Assert.True(problems.Count == 0,
            "BIOS cards with a missing per-tier recommendation:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void EverySetting_DocumentsAllFourDiyVendorPaths_NonEmpty()
    {
        var problems = new List<string>();
        foreach (var s in Catalog)
            foreach (var v in DiyVendors)
                if (!s.VendorPaths.TryGetValue(v, out var path) || string.IsNullOrWhiteSpace(path))
                    problems.Add($"{s.Id}: missing/empty {v} path");

        Assert.True(problems.Count == 0,
            "BIOS cards with a missing vendor menu path (dead instruction):\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void VendorAliases_WhenPresent_AreNonEmpty()
    {
        var problems = new List<string>();
        foreach (var s in Catalog)
            foreach (var kvp in s.VendorAliases)
                if (string.IsNullOrWhiteSpace(kvp.Value))
                    problems.Add($"{s.Id}: empty alias for {kvp.Key}");

        Assert.True(problems.Count == 0,
            "BIOS cards with an empty vendor alias:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void Compatibility_WhenPresent_HasNoUnknownFamily_NorDuplicates()
    {
        // Empty Compatibility = universal (valid). When a card narrows to families, Unknown is meaningless
        // (it never matches a detected family) and duplicates hint at a copy-paste slip.
        var problems = new List<string>();
        foreach (var s in Catalog.Where(s => s.Compatibility.Count > 0))
        {
            if (s.Compatibility.Contains(CpuFamily.Unknown))
                problems.Add($"{s.Id}: Compatibility contains Unknown");

            var dupes = s.Compatibility
                .GroupBy(f => f)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key.ToString())
                .ToList();
            if (dupes.Count > 0)
                problems.Add($"{s.Id}: duplicate families {string.Join(",", dupes)}");
        }

        Assert.True(problems.Count == 0,
            "BIOS cards with a bad Compatibility list:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void DangerousSettings_CarryAWrittenCaveat()
    {
        // A High/HardwareDamage risk badge must be backed by a Notes caveat — the honesty rule for the
        // scariest cards (e.g. the VSOC ≤ 1.30 V cap born of the 2023 X3D burn incidents).
        var problems = Catalog
            .Where(s => s.Risk >= RiskLevel.High && string.IsNullOrWhiteSpace(s.Notes))
            .Select(s => $"{s.Id} ({s.Risk})")
            .ToList();

        Assert.True(problems.Count == 0,
            "high-risk BIOS cards without a written Notes caveat: " + string.Join(", ", problems));
    }
}
