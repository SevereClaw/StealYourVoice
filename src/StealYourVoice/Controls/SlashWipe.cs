using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace StealYourVoice.Controls;

/// <summary>Full-window transition: red / black / white shards slash across the screen.</summary>
public class SlashWipe : Canvas
{
    public SlashWipe()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public void Play()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        Children.Clear();

        var brushes = new[] { P5.Red, P5.Black, P5.White, P5.Red };
        double lean = h * 0.45;
        double[] widths = { w * 0.30, w * 0.22, w * 0.05, w * 0.12 };
        var last = TimeSpan.Zero;

        for (int i = 0; i < brushes.Length; i++)
        {
            double band = widths[i];
            var poly = new Polygon
            {
                Fill = brushes[i],
                Points = new PointCollection
                {
                    new Point(lean, 0), new Point(lean + band, 0),
                    new Point(band, h), new Point(0, h)
                }
            };
            var tt = new TranslateTransform(-(lean + band), 0);
            poly.RenderTransform = tt;
            Children.Add(poly);

            var begin = TimeSpan.FromMilliseconds(i * 45);
            var anim = new DoubleAnimation(-(lean + band), w + lean, TimeSpan.FromMilliseconds(360))
            {
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            if (i == brushes.Length - 1) anim.Completed += (_, _) => Children.Clear();
            tt.BeginAnimation(TranslateTransform.XProperty, anim);
            last = begin;
        }
    }
}
