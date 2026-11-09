using System.Windows;
using System.Windows.Media;

namespace StealYourVoice.Controls;

/// <summary>Black backdrop with red shards and halftone dots.</summary>
public class ShardBackground : FrameworkElement
{
    public ShardBackground() { IsHitTestVisible = false; }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(P5.Black, null, new Rect(0, 0, w, h));

        // big red shard from the top-left
        dc.DrawGeometry(P5.Red, null, P5.Poly(
            new Point(0, 0), new Point(w * 0.46, 0), new Point(w * 0.30, h * 0.20),
            new Point(w * 0.36, h * 0.22), new Point(w * 0.12, h * 0.55), new Point(0, h * 0.62)));

        // dark red echo
        dc.DrawGeometry(P5.DarkRed, null, P5.Poly(
            new Point(w * 0.46, 0), new Point(w * 0.52, 0), new Point(w * 0.33, h * 0.21), new Point(w * 0.30, h * 0.20)));

        // white streaks
        var pen = P5.Pen(P5.White, 3);
        dc.DrawLine(pen, new Point(w * 0.62, h), new Point(w * 0.98, h * 0.35));
        dc.DrawLine(P5.Pen(P5.Red, 10), new Point(w * 0.70, h), new Point(w, h * 0.50));

        // bottom-right red shard
        dc.DrawGeometry(P5.Red, null, P5.Poly(
            new Point(w, h * 0.70), new Point(w, h), new Point(w * 0.80, h)));

        // halftone dots, bottom-left
        var dot = new SolidColorBrush(Color.FromArgb(70, 0xE6, 0x00, 0x12));
        dot.Freeze();
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 18; x++)
            {
                double r = 6 - (x + y) * 0.28;
                if (r < 0.8) continue;
                dc.DrawEllipse(dot, null, new Point(14 + x * 16 + (y % 2) * 8, h - 14 - y * 14), r, r);
            }
    }
}
