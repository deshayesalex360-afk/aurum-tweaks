using System;
using AurumTweaks.Models;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>Pins the single source of truth for each tweak category's French label — the dashboard's per-category
/// score bars (via the converter) and the shareable text report both read from here, so a reworded label can't drift
/// between the two surfaces. Every defined enum value must map to a real French word, never the raw enum-name
/// fallback that would leak an English identifier into the French UI.</summary>
public class TweakCategoryLabelsTests
{
    [Theory]
    [InlineData(TweakCategory.PrivacyTelemetry, "Confidentialité")]
    [InlineData(TweakCategory.PerformanceMultimedia, "Performance")]
    [InlineData(TweakCategory.NetworkLatency, "Réseau & latence")]
    [InlineData(TweakCategory.Debloat, "Débloat")]
    [InlineData(TweakCategory.Services, "Services")]
    [InlineData(TweakCategory.UIQualityOfLife, "Confort d'usage")]
    [InlineData(TweakCategory.PowerBoot, "Alimentation & démarrage")]
    [InlineData(TweakCategory.Gaming, "Jeu")]
    [InlineData(TweakCategory.Security, "Sécurité")]
    [InlineData(TweakCategory.Advanced, "Avancé")]
    public void French_MapsEachCategory_ToItsLabel(TweakCategory category, string expected)
        => Assert.Equal(expected, TweakCategoryLabels.French(category));

    [Fact]
    public void French_TranslatesEveryDefinedCategory_NoRawEnumNameLeaks()
    {
        // If a new TweakCategory is added without a French case, French() falls through to the enum's ToString — an
        // English identifier in the FR UI. This guards that: every value yields a non-empty label that differs from
        // its raw enum name. « Services » is the one legitimate FR/EN collision (same word), so it's carved out.
        foreach (TweakCategory c in Enum.GetValues(typeof(TweakCategory)))
        {
            var label = TweakCategoryLabels.French(c);
            Assert.False(string.IsNullOrWhiteSpace(label));
            if (c != TweakCategory.Services)
                Assert.NotEqual(c.ToString(), label);
        }
    }
}
