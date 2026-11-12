using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace StealYourVoice.Controls;

/// <summary>Shared Persona 5 palette and drawing helpers.</summary>
public static class P5
{
    public static readonly Color RedColor = Color.FromRgb(0xE6, 0x00, 0x12);
    public static readonly Color DarkRedColor = Color.FromRgb(0x8A, 0x00, 0x0B);

    public static readonly Brush Red = Freeze(new SolidColorBrush(RedColor));
    public static readonly Brush DarkRed = Freeze(new SolidColorBrush(DarkRedColor));
    public static readonly Brush Black = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)));
    public static readonly Brush Coal = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    public static readonly Brush White = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)));
    public static readonly Brush Grey = Freeze(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));

    public static readonly Typeface Heavy = new(new FontFamily("Arial Black, Impact, Segoe UI Black"),
        FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
    public static readonly Typeface HeavyItalic = new(new FontFamily("Arial Black, Impact, Segoe UI Black"),
        FontStyles.Italic, FontWeights.Black, FontStretches.Normal);
    public static readonly Typeface Impact = new(new FontFamily("Impact, Arial Black"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static readonly System.Collections.Generic.Dictionary<(Brush, double), Pen> PenCache = new();

    /// <summary>Frozen pens are cached: controls ask for the same few on every repaint.</summary>
    public static Pen Pen(Brush b, double t)
    {
        if (PenCache.TryGetValue((b, t), out var cached)) return cached;
        var p = new Pen(b, t) { LineJoin = PenLineJoin.Miter };
        p.Freeze();
        if (b.IsFrozen) PenCache[(b, t)] = p;
        return p;
    }

    /// <summary>Parallelogram slanted to the right by <paramref name="slant"/> pixels.</summary>
    public static Geometry Slant(Rect r, double slant)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(r.Left + slant, r.Top), true, true);
            c.LineTo(new Point(r.Right, r.Top), true, false);
            c.LineTo(new Point(r.Right - slant, r.Bottom), true, false);
            c.LineTo(new Point(r.Left, r.Bottom), true, false);
        }
        g.Freeze();
        return g;
    }

    public static Geometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], true, true);
            for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, false);
        }
        g.Freeze();
        return g;
    }

    public static FormattedText Text(Visual v, string s, Typeface tf, double size, Brush b)
    {
        double ppd = 1.0;
        try { ppd = VisualTreeHelper.GetDpi(v).PixelsPerDip; } catch { /* design time */ }
        return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, b, ppd);
    }

    public static double ToDb(float linear) => linear <= 0.000001f ? -90 : 20 * Math.Log10(linear);
}
