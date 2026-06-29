using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AurumTweaks.Models;
using AurumTweaks.Services;

namespace AurumTweaks.Converters;

/// <summary>Converts bool → Visibility (true=Visible, false=Collapsed). Inverse with parameter "Invert".</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Converts non-null/non-empty string → Visibility.</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a TweakTier to its accent brush.</summary>
public sealed class TierToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TweakTier t) return Brushes.Gray;
        return t switch
        {
            TweakTier.Tranquille => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
            TweakTier.Avance => new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)),
            TweakTier.Extreme => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            _ => Brushes.Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a RiskLevel to a human readable label.</summary>
public sealed class RiskToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RiskLevel r) return string.Empty;
        return r switch
        {
            RiskLevel.None => "Aucun",
            RiskLevel.Low => "Faible",
            RiskLevel.Medium => "Moyen",
            RiskLevel.High => "Élevé",
            RiskLevel.HardwareDamage => "DANGER HARDWARE",
            _ => r.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns a fill colour for tweak tier — used directly on badges + accents.
/// Tier passed as the value.
/// </summary>
public sealed class TierToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TweakTier t) return Brushes.Transparent;
        var color = t switch
        {
            TweakTier.Tranquille => Color.FromRgb(0x22, 0xC5, 0x5E),
            TweakTier.Avance => Color.FromRgb(0xEA, 0xB3, 0x08),
            TweakTier.Extreme => Color.FromRgb(0xEF, 0x44, 0x44),
            _ => Colors.Transparent
        };
        // Return a very faint background tint (alpha 0x18)
        return new SolidColorBrush(Color.FromArgb(0x18, color.R, color.G, color.B));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps an <see cref="ScoreGrade"/> to its accent brush — a green→red band so the optimization-score ring
/// reads at a glance. NoData is muted grey (no verdict), matching the « en analyse » label.</summary>
public sealed class ScoreGradeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ScoreGrade g) return new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x76));
        return new SolidColorBrush(g switch
        {
            ScoreGrade.Excellent => Color.FromRgb(0x22, 0xC5, 0x5E),  // green
            ScoreGrade.VeryGood => Color.FromRgb(0x84, 0xCC, 0x16),   // lime
            ScoreGrade.Good => Color.FromRgb(0xEA, 0xB3, 0x08),       // gold
            ScoreGrade.Partial => Color.FromRgb(0xF5, 0x9E, 0x0B),    // amber
            ScoreGrade.Poor => Color.FromRgb(0xEF, 0x44, 0x44),       // red
            _ => Color.FromRgb(0x6B, 0x6B, 0x76)                      // muted (no data)
        });
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="TweakCategory"/> to its French label for the score breakdown bars. Delegates to the
/// shared <see cref="TweakCategoryLabels.French"/> so the dashboard bars and the text report can't drift.</summary>
public sealed class TweakCategoryToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is TweakCategory c ? TweakCategoryLabels.French(c) : string.Empty;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps an InsightSeverity to its accent brush (solid).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not InsightSeverity s) return Brushes.Gray;
        return s switch
        {
            InsightSeverity.Opportunity => new SolidColorBrush(Color.FromRgb(0xD4, 0xAF, 0x37)), // gold
            InsightSeverity.Warning => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),     // amber
            _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x76))                            // muted (info)
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps an InsightSeverity to a faint background tint.</summary>
public sealed class SeverityToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not InsightSeverity s) return Brushes.Transparent;
        var c = s switch
        {
            InsightSeverity.Opportunity => Color.FromRgb(0xD4, 0xAF, 0x37),
            InsightSeverity.Warning => Color.FromRgb(0xF5, 0x9E, 0x0B),
            _ => Color.FromRgb(0x6B, 0x6B, 0x76)
        };
        return new SolidColorBrush(Color.FromArgb(0x14, c.R, c.G, c.B));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns true if any anti-cheat in the matrix is non-Safe. For badge visibility.
/// </summary>
public sealed class AntiCheatHasConcernConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AntiCheatMatrix m) return m.HasAnyConcern;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Compact French "time since" labelling for a stored instant (e.g. a profile's last-applied stamp) — pure
/// (takes "now" as an argument) so the buckets are deterministically unit-testable. Buckets are coarse on
/// purpose: the label must never imply more precision than a single stored UTC instant warrants.
/// </summary>
public static class RelativeTime
{
    public static string Since(DateTime utc, DateTime nowUtc)
    {
        var span = nowUtc - utc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;          // a future stamp (clock skew) reads "à l'instant", never negative
        if (span.TotalMinutes < 1) return "à l'instant";
        if (span.TotalMinutes < 60) return $"il y a {(int)span.TotalMinutes} min";
        if (span.TotalHours < 24) return $"il y a {(int)span.TotalHours} h";
        if (span.TotalDays < 7) return $"il y a {(int)span.TotalDays} j";
        return utc.ToString("dd/MM/yyyy");
    }
}

/// <summary>Formats a profile's last-applied instant (UTC <see cref="DateTime"/>) into
/// "Dernière application : …". Empty string when never applied (null), so the bound row hides itself.</summary>
public sealed class LastAppliedToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime utc ? $"Dernière application : {RelativeTime.Since(utc, DateTime.UtcNow)}" : string.Empty;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Renders a live sensor reading for a UI tile, honouring the same "0 = capteur non lu" rule as the monitoring
/// paste (<see cref="Services.MonitoringSnapshotTextReport"/>): a value of 0 (or below) means the sensor wasn't
/// exposed — a running PC is never 0 °C, an active core never 0 MHz — so it shows "—" rather than a fabricated
/// "0". Pure (the format provider is an argument) so the threshold is deterministically unit-testable. A real 0 %
/// LOAD must NOT use this — idle is a legitimate reading; this is only for metrics where 0 means « non lu ».
/// </summary>
public static class SensorDisplay
{
    public static string OrDash(double value, string format, IFormatProvider? provider = null)
        => value > 0 ? value.ToString(format, provider ?? CultureInfo.CurrentCulture) : "—";
}

/// <summary>Thin converter over <see cref="SensorDisplay.OrDash"/> for live monitoring tiles: a numeric sensor
/// value renders via the format in ConverterParameter (default "F0"), but an unread 0 shows "—" instead of a
/// fabricated "0". Displayed (not copied), so it formats in <see cref="CultureInfo.CurrentCulture"/>. Use only for
/// temperature/clock tiles, never for a load % where 0 is a real idle reading.</summary>
public sealed class SensorValueOrDashConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IConvertible) return "—";   // null / unset → unread
        double d = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return SensorDisplay.OrDash(d, parameter as string ?? "F0", CultureInfo.CurrentCulture);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Repackages the pure <see cref="ScoreSparkline"/> projection (pixel-space <see cref="ScorePoint"/>s) into
/// a <see cref="PointCollection"/> for the dashboard's score Polyline. No geometry lives here — the honesty-bearing
/// math (fixed 0-100 scale, even chronological spacing) is in the tested core; this is only the WPF repackage.</summary>
public sealed class ScorePolylineConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var points = new PointCollection();
        if (value is IEnumerable<ScorePoint> projected)
            foreach (var p in projected)
                points.Add(new Point(p.X, p.Y));
        return points;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
