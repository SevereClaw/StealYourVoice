using System;
using System.Windows;
using System.Windows.Media;

namespace StealYourVoice.Controls;

/// <summary>Horizontal level meter made of slanted blades, with peak hold.</summary>
public class JaggedMeter : FrameworkElement
{
    private const double MinDb = -60, MaxDb = 6;
    private double _db = -90, _peakDb = -90;
    private DateTime _peakTime;

    public int Blades { get; set; } = 40;

    public JaggedMeter() { Height = 34; IsHitTestVisible = false; }

    /// <summary>Feed a linear peak value (0..1+). Call ~30 times per second.</summary>
    public void Push(float linear)
    {
        var db = P5.ToDb(linear);
        // fast attack, smooth release
        _db = db > _db ? db : Math.Max(db, _db - 1.5);
        if (_db >= _peakDb || (DateTime.Now - _peakTime).TotalMilliseconds > 1200)
        {
            _peakDb = _db;
            _peakTime = DateTime.Now;
        }
        InvalidateVisual();
    }

    public double CurrentDb => _db;

    private static double Norm(double db) => Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w < 20) return;

        dc.DrawGeometry(P5.Black, P5.Pen(P5.White, 2), P5.Slant(new Rect(0, 0, w, h), 12));

        double inner = w - 24;
        double bladeW = inner / Blades;
        int lit = (int)Math.Round(Norm(_db) * Blades);
        int peak = (int)Math.Round(Norm(_peakDb) * Blades) - 1;
        int clipFrom = (int)Math.Round(Norm(0) * Blades);
        int hotFrom = (int)Math.Round(Norm(-12) * Blades);

        for (int i = 0; i < Blades; i++)
        {
            bool on = i < lit || i == peak;
            Brush b;
            if (!on) b = P5.Coal;
            else if (i >= clipFrom) b = P5.Red;
            else if (i >= hotFrom) b = P5.DarkRed;
            else b = P5.White;
            if (i == peak && i >= lit) b = P5.Red;

            double x = 12 + i * bladeW;
            // each blade gets a slightly different height for the jagged look
            double jag = (i * 7 % 5) * 1.2;
            var g = P5.Poly(
                new Point(x + 6, 5 + jag),
                new Point(x + bladeW - 1 + 6, 5 + jag),
                new Point(x + bladeW - 1 - 2, h - 5),
                new Point(x - 2, h - 5));
            dc.DrawGeometry(b, null, g);
        }

        var txt = _db < MinDb ? "-∞" : _db.ToString("0") + " dB";
        var ft = P5.Text(this, txt, P5.Impact, 13, P5.White);
        dc.DrawRectangle(P5.Black, null, new Rect(w - ft.Width - 22, h / 2 - ft.Height / 2, ft.Width + 8, ft.Height));
        dc.DrawText(ft, new Point(w - ft.Width - 18, h / 2 - ft.Height / 2));
    }
}
