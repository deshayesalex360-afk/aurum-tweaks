using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AurumTweaks.Controls;

/// <summary>
/// The Aurum Tweaks signature surface. Renders a procedural black gold marble pattern
/// using simplex-style noise + sine-wave veining. This is the brand mark — when a user
/// sees this marble pattern, they know they're looking at Aurum Tweaks.
///
/// Each instance is deterministic via the <see cref="Seed"/> property so the same area of
/// the app always shows the same vein pattern across launches, but different sections of
/// the UI render distinct slabs.
/// </summary>
public class MarbleSurface : Image
{
    public static readonly DependencyProperty SeedProperty = DependencyProperty.Register(
        nameof(Seed), typeof(int), typeof(MarbleSurface),
        new PropertyMetadata(1337, OnRenderPropertyChanged));

    public static readonly DependencyProperty VeinDensityProperty = DependencyProperty.Register(
        nameof(VeinDensity), typeof(double), typeof(MarbleSurface),
        new PropertyMetadata(1.2, OnRenderPropertyChanged));

    public static readonly DependencyProperty VeinIntensityProperty = DependencyProperty.Register(
        nameof(VeinIntensity), typeof(double), typeof(MarbleSurface),
        new PropertyMetadata(0.85, OnRenderPropertyChanged));

    public static readonly DependencyProperty BaseDarknessProperty = DependencyProperty.Register(
        nameof(BaseDarkness), typeof(double), typeof(MarbleSurface),
        new PropertyMetadata(0.04, OnRenderPropertyChanged));

    public static readonly DependencyProperty GoldHueProperty = DependencyProperty.Register(
        nameof(GoldHue), typeof(Color), typeof(MarbleSurface),
        new PropertyMetadata(Color.FromRgb(0xD4, 0xAF, 0x37), OnRenderPropertyChanged));

    public static readonly DependencyProperty RenderWidthProperty = DependencyProperty.Register(
        nameof(RenderWidth), typeof(int), typeof(MarbleSurface),
        new PropertyMetadata(512, OnRenderPropertyChanged));

    public static readonly DependencyProperty RenderHeightProperty = DependencyProperty.Register(
        nameof(RenderHeight), typeof(int), typeof(MarbleSurface),
        new PropertyMetadata(512, OnRenderPropertyChanged));

    /// <summary>Deterministic seed — same seed always produces the same slab.</summary>
    public int Seed
    {
        get => (int)GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    /// <summary>How many veins per unit area. Higher = busier marble.</summary>
    public double VeinDensity
    {
        get => (double)GetValue(VeinDensityProperty);
        set => SetValue(VeinDensityProperty, value);
    }

    /// <summary>How sharp/bright the gold veins are. 0..1.</summary>
    public double VeinIntensity
    {
        get => (double)GetValue(VeinIntensityProperty);
        set => SetValue(VeinIntensityProperty, value);
    }

    /// <summary>How dark the marble base is. 0 = pure black, 0.1 = slightly lighter.</summary>
    public double BaseDarkness
    {
        get => (double)GetValue(BaseDarknessProperty);
        set => SetValue(BaseDarknessProperty, value);
    }

    /// <summary>The dominant gold hue used for the veins.</summary>
    public Color GoldHue
    {
        get => (Color)GetValue(GoldHueProperty);
        set => SetValue(GoldHueProperty, value);
    }

    public int RenderWidth
    {
        get => (int)GetValue(RenderWidthProperty);
        set => SetValue(RenderWidthProperty, value);
    }

    public int RenderHeight
    {
        get => (int)GetValue(RenderHeightProperty);
        set => SetValue(RenderHeightProperty, value);
    }

    public MarbleSurface()
    {
        Stretch = Stretch.UniformToFill;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => Render();
    }

    private static void OnRenderPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarbleSurface s) s.Render();
    }

    /// <summary>
    /// Generates the marble bitmap. The algorithm layers value-noise turbulence,
    /// sinusoidal vein bands, fine grain, and a soft directional highlight.
    /// </summary>
    private void Render()
    {
        int w = Math.Max(64, RenderWidth);
        int h = Math.Max(64, RenderHeight);

        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.Lock();
        try
        {
            unsafe
            {
                byte* p = (byte*)bmp.BackBuffer.ToPointer();
                int stride = bmp.BackBufferStride;

                var noise = new ValueNoise(Seed);

                // Pre-extracted parameters
                double goldR = GoldHue.R / 255.0;
                double goldG = GoldHue.G / 255.0;
                double goldB = GoldHue.B / 255.0;
                double density = VeinDensity;
                double intensity = VeinIntensity;
                double baseDark = BaseDarkness;

                // Random offsets so each seed yields a distinct slab.
                var rng = new Random(Seed);
                double offX = rng.NextDouble() * 1000.0;
                double offY = rng.NextDouble() * 1000.0;
                double rotation = rng.NextDouble() * Math.PI;
                double cosR = Math.Cos(rotation);
                double sinR = Math.Sin(rotation);

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        // Normalized coordinates 0..1
                        double nx = (double)x / w;
                        double ny = (double)y / h;

                        // Rotate so veins flow diagonally for premium feel.
                        double rx = nx * cosR - ny * sinR + offX;
                        double ry = nx * sinR + ny * cosR + offY;

                        // Layered turbulence — multi-octave noise simulates organic marble flow.
                        double turbulence =
                              noise.Sample(rx * 2.5, ry * 2.5) * 0.55
                            + noise.Sample(rx * 6.0, ry * 6.0) * 0.30
                            + noise.Sample(rx * 14.0, ry * 14.0) * 0.15;

                        // Vein function: sin combined with turbulence produces wavy bands.
                        double vein = Math.Sin((rx + turbulence * 3.0) * Math.PI * density);
                        // Sharpen the veins — only the tips are bright, rest stays dark.
                        double veinIntensity = Math.Pow(Math.Abs(vein), 4.5) * intensity;

                        // Fine grain — tiny shimmer texture.
                        double grain = noise.Sample(rx * 80.0, ry * 80.0) * 0.04;

                        // Subtle directional highlight (top-left → bottom-right).
                        double highlight = (nx * 0.5 + ny * 0.5) * 0.08;

                        // Base value — deep black with tiny grain.
                        double baseVal = baseDark + grain;

                        // Blend vein color over base.
                        double r = baseVal + (goldR - baseVal) * veinIntensity + highlight * 0.3;
                        double g = baseVal + (goldG - baseVal) * veinIntensity + highlight * 0.25;
                        double b = baseVal + (goldB - baseVal) * veinIntensity + highlight * 0.18;

                        // Clamp 0..1
                        r = Clamp01(r);
                        g = Clamp01(g);
                        b = Clamp01(b);

                        byte* px = p + y * stride + x * 4;
                        px[0] = (byte)(b * 255.0);
                        px[1] = (byte)(g * 255.0);
                        px[2] = (byte)(r * 255.0);
                        px[3] = 255;
                    }
                }
            }

            bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            bmp.Unlock();
        }

        bmp.Freeze();
        Source = bmp;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}

/// <summary>
/// Minimal seeded 2D value-noise (smoother than white noise, faster than perlin).
/// Deterministic per seed.
/// </summary>
internal sealed class ValueNoise
{
    private readonly int[] _perm;

    public ValueNoise(int seed)
    {
        var rng = new Random(seed);
        var p = new int[512];
        for (int i = 0; i < 256; i++) p[i] = i;
        // Fisher–Yates shuffle
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 256; i++) p[256 + i] = p[i];
        _perm = p;
    }

    public double Sample(double x, double y)
    {
        int xi = (int)Math.Floor(x) & 255;
        int yi = (int)Math.Floor(y) & 255;
        double xf = x - Math.Floor(x);
        double yf = y - Math.Floor(y);

        double u = Fade(xf);
        double v = Fade(yf);

        double v00 = Hash(xi, yi);
        double v10 = Hash(xi + 1, yi);
        double v01 = Hash(xi, yi + 1);
        double v11 = Hash(xi + 1, yi + 1);

        double lerpX1 = Lerp(v00, v10, u);
        double lerpX2 = Lerp(v01, v11, u);
        return Lerp(lerpX1, lerpX2, v);
    }

    private double Hash(int x, int y) => _perm[(_perm[x & 255] + y) & 255] / 255.0;
    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
