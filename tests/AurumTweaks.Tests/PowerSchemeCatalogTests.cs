using System;
using System.Collections.Generic;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the "which plan" decision logic behind the Alimentation page: <see cref="PowerSchemeCatalog"/> (stable
/// French labels/advice for the well-known schemes, preferred over powercfg's code-page-fragile localized name)
/// and <see cref="UltimateAction"/> (how « Performances ultimes » is surfaced). The load-bearing case is the
/// duplicated-Ultimate one: that plan carries a fresh GUID, so it must be recognised by its ASCII name and
/// re-activated rather than blindly duplicated again.
/// </summary>
public class PowerSchemeCatalogTests
{
    public static IEnumerable<object[]> KnownSchemes => new[]
    {
        new object[] { PowerSchemeCatalog.Balanced },
        new object[] { PowerSchemeCatalog.HighPerformance },
        new object[] { PowerSchemeCatalog.PowerSaver },
        new object[] { PowerSchemeCatalog.Ultimate },
    };

    [Theory]
    [MemberData(nameof(KnownSchemes))]
    public void Label_And_Advice_AreNonEmpty_ForEveryKnownScheme(Guid id)
    {
        Assert.False(string.IsNullOrWhiteSpace(PowerSchemeCatalog.Label(id)));
        Assert.False(string.IsNullOrWhiteSpace(PowerSchemeCatalog.Advice(id)));
    }

    [Fact]
    public void Label_NamesUltimatePlanInFrench()
        => Assert.Equal("Performances ultimes", PowerSchemeCatalog.Label(PowerSchemeCatalog.Ultimate));

    [Fact]
    public void Label_And_Advice_UnknownScheme_ReturnNull()
    {
        var custom = new Guid("99999999-9999-9999-9999-999999999999");
        Assert.Null(PowerSchemeCatalog.Label(custom));
        Assert.Null(PowerSchemeCatalog.Advice(custom));
    }

    [Fact]
    public void KnownSchemeGuids_AreDistinct()
    {
        var set = new HashSet<Guid>
        {
            PowerSchemeCatalog.Balanced, PowerSchemeCatalog.HighPerformance,
            PowerSchemeCatalog.PowerSaver, PowerSchemeCatalog.Ultimate
        };
        Assert.Equal(4, set.Count);
    }
}

/// <summary>Pins <see cref="UltimateAction.Resolve"/> — the pure decision of whether « Performances ultimes »
/// can simply be activated or must first be duplicated onto this edition of Windows.</summary>
public class UltimateActionTests
{
    private static readonly Guid Other = new("11111111-1111-1111-1111-111111111111");

    private static PowerScheme Scheme(Guid id, string name, bool active = false) => new(id, name, active);

    [Fact]
    public void Resolve_BaseGuidPresent_ActivatesIt()
    {
        var schemes = new[]
        {
            Scheme(PowerSchemeCatalog.Balanced, "Balanced", active: true),
            Scheme(PowerSchemeCatalog.Ultimate, "Ultimate Performance"),
        };
        var action = UltimateAction.Resolve(schemes);

        Assert.Equal(UltimateActionKind.ActivateExisting, action.Kind);
        Assert.Equal(PowerSchemeCatalog.Ultimate, action.Scheme);
    }

    [Fact]
    public void Resolve_DuplicatedUltimate_RecognisedByName_AndActivated()
    {
        // A duplicated Ultimate has a FRESH guid but keeps the ASCII "Ultimate Performance" name — must be reused.
        var schemes = new[]
        {
            Scheme(PowerSchemeCatalog.Balanced, "Balanced", active: true),
            Scheme(Other, "Ultimate Performance"),
        };
        var action = UltimateAction.Resolve(schemes);

        Assert.Equal(UltimateActionKind.ActivateExisting, action.Kind);
        Assert.Equal(Other, action.Scheme);
    }

    [Fact]
    public void Resolve_BaseGuidWins_OverANamedDuplicate()
    {
        var schemes = new[]
        {
            Scheme(Other, "Ultimate Performance"),
            Scheme(PowerSchemeCatalog.Ultimate, "Ultimate Performance"),
        };
        Assert.Equal(PowerSchemeCatalog.Ultimate, UltimateAction.Resolve(schemes).Scheme);
    }

    [Fact]
    public void Resolve_NotPresent_DuplicatesTheBaseScheme()
    {
        var schemes = new[]
        {
            Scheme(PowerSchemeCatalog.Balanced, "Balanced", active: true),
            Scheme(PowerSchemeCatalog.HighPerformance, "High performance"),
        };
        var action = UltimateAction.Resolve(schemes);

        Assert.Equal(UltimateActionKind.Duplicate, action.Kind);
        Assert.Equal(PowerSchemeCatalog.Ultimate, action.Scheme);
    }

    [Fact]
    public void Resolve_EmptyList_DuplicatesTheBaseScheme()
        => Assert.Equal(UltimateActionKind.Duplicate, UltimateAction.Resolve(Array.Empty<PowerScheme>()).Kind);
}
