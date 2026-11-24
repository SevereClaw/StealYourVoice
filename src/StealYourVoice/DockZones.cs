using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>
/// Drop zones over the game while a person is dragged with Ctrl: two columns at the sides and two rows
/// at the top and bottom. Drop the plate into one and the person docks there; everyone in the same zone
/// lines up neatly, like in the ready-made arrangements. Click-through, never takes focus.
/// </summary>
public sealed class DockZones : Window
{
    public static readonly (string Key, string Text)[] Zones =
    {
        ("left", "СТОЛБЕЦ СЛЕВА"),
        ("right", "СТОЛБЕЦ СПРАВА"),
        ("top", "РЯД СВЕРХУ"),
        ("bottom", "РЯД СНИЗУ"),
    };

    private readonly Face _face = new();

    public DockZones()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        Content = _face;
        SourceInitialized += (_, _) => MiniOverlay.MakeClickThrough(this);
    }

    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Cover the work area of the main screen and appear.</summary>
    public void ShowZones()
    {
        var a = SystemParameters.WorkArea;
        Left = a.Left;
        Top = a.Top;
        Width = a.Width;
        Height = a.Height;
        _face.Active = null;
        _face.InvalidateVisual();
        if (!IsVisible) Show();
    }

    public void SetActive(string? zone)
    {
        if (_face.Active == zone) return;
        _face.Active = zone;
        _face.InvalidateVisual();
    }

    /// <summary>Which zone is under this physical screen point (null = none).</summary>
    public string? ZoneAt(int x, int y)
    {
        if (!IsVisible) return null;
        double k = DpiScale;
        var p = new Point(x / k - Left, y / k - Top);
        foreach (var z in Zones)
            if (ZoneRect(z.Key, Width, Height).Contains(p)) return z.Key;
        return null;
    }

    /// <summary>Zone rectangle inside the work area (window coordinates).</summary>
    public static Rect ZoneRect(string zone, double w, double h)
    {
        const double m = 12, col = 270, row = 78;
        return zone switch
        {
            "left" => new Rect(m, row + 2 * m, col, Math.Max(40, h - 2 * (row + 2 * m))),
            "right" => new Rect(w - col - m, row + 2 * m, col, Math.Max(40, h - 2 * (row + 2 * m))),
            "top" => new Rect(w * 0.22, m, w * 0.56, row),
            _ => new Rect(w * 0.22, h - row - m, w * 0.56, row),
        };
    }

    private sealed class Face : FrameworkElement
    {
        public string? Active;
        private static readonly Brush Fill = P5.Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x0A, 0x0A, 0x0A)));
        private static readonly Brush FillOn = P5.Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xE6, 0x00, 0x12)));
        private static readonly Brush Stripe = P5.Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xE6, 0x00, 0x12)));

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            foreach (var (key, text) in Zones)
            {
                var r = ZoneRect(key, w, h);
                bool on = key == Active;
                dc.DrawGeometry(on ? FillOn : Fill, P5.Pen(on ? P5.White : P5.Red, on ? 3 : 2), P5.Slant(r, 14));
                // a dashed strip along the zone, like a slot to drop into
                var inner = new Rect(r.Left + 18, r.Top + 10, Math.Max(0, r.Width - 36), Math.Max(0, r.Height - 20));
                if (!on) dc.DrawGeometry(null, new Pen(Stripe, 2) { DashStyle = DashStyles.Dash }, P5.Slant(inner, 10));
                var ft = P5.Text(this, text, P5.HeavyItalic, 15, P5.White);
                double tx = r.Left + (r.Width - ft.Width) / 2, ty = r.Top + (r.Height - ft.Height) / 2;
                if (key is "left" or "right") ty = r.Top + 18;
                var plate = new Rect(tx - 12, ty - 3, ft.Width + 24, ft.Height + 6);
                dc.DrawGeometry(on ? P5.Black : P5.Red, null, P5.Slant(plate, 8));
                dc.DrawText(ft, new Point(tx, ty));
            }
            var hint = P5.Text(this, "ОТПУСТИ В ЗОНЕ — ЧЕЛОВЕК ВСТАНЕТ ТУДА  ·  МИМО ЗОН — ОСТАНЕТСЯ, ГДЕ ОТПУСТИЛ",
                P5.Heavy, 12, P5.White);
            var hr = new Rect((w - hint.Width) / 2 - 14, h / 2 - hint.Height / 2 - 4, hint.Width + 28, hint.Height + 8);
            dc.DrawGeometry(Fill, null, P5.Slant(hr, 8));
            dc.DrawText(hint, new Point(hr.Left + 14, hr.Top + 4));
        }
    }
}
