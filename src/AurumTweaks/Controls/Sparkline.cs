using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AurumTweaks.Controls;

/// <summary>
/// Minimalist sparkline for live metrics. Renders a thin gold line over the
/// available area, scaled to the visible data window. Linear-style — no axes,
/// no labels, just the trend.
/// </summary>
public class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IEnumerable<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gold, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Samples
    {
        get => (IEnumerable<double>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Samples is null) return;
        var data = Samples.ToList();
        if (data.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;

        double min = Minimum;
        double max = Maximum;
        if (max - min < 0.0001) max = min + 1;

        double stepX = ActualWidth / (data.Count - 1);
        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var start = new Point(0, MapY(data[0], min, max));
            ctx.BeginFigure(start, Fill is not null, false);
            for (int i = 1; i < data.Count; i++)
            {
                ctx.LineTo(new Point(i * stepX, MapY(data[i], min, max)), true, false);
            }
            if (Fill is not null)
            {
                ctx.LineTo(new Point((data.Count - 1) * stepX, ActualHeight), false, false);
                ctx.LineTo(new Point(0, ActualHeight), false, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(Fill, pen, geo);
    }

    private double MapY(double value, double min, double max)
    {
        double n = (value - min) / (max - min);
        if (n < 0) n = 0;
        if (n > 1) n = 1;
        return ActualHeight - n * ActualHeight;
    }
}
