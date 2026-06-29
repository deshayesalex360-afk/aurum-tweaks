using AurumTweaks.Models;

namespace AurumTweaks.Services;

/// <summary>The single source of the French label for each <see cref="TweakCategory"/>, shared by the dashboard's
/// per-category bars (<c>TweakCategoryToLabelConverter</c>) and the shareable text report (<see cref="SystemReport"/>'s
/// optimization breakdown). Centralised so the two surfaces can never drift to different words for the same category —
/// the same single-source pattern as <see cref="OptimizationScore.GradeLabel"/>.</summary>
public static class TweakCategoryLabels
{
    public static string French(TweakCategory category) => category switch
    {
        TweakCategory.PrivacyTelemetry => "Confidentialité",
        TweakCategory.PerformanceMultimedia => "Performance",
        TweakCategory.NetworkLatency => "Réseau & latence",
        TweakCategory.Debloat => "Débloat",
        TweakCategory.Services => "Services",
        TweakCategory.UIQualityOfLife => "Confort d'usage",
        TweakCategory.PowerBoot => "Alimentation & démarrage",
        TweakCategory.Gaming => "Jeu",
        TweakCategory.Security => "Sécurité",
        TweakCategory.Advanced => "Avancé",
        _ => category.ToString()
    };
}
