using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>
/// Drop zones over the game while a person is dragged with Ctrl: a column in every corner, a column in the
/// middle of each side and a row at the top and bottom. Drop the plate into one and the person docks there;
/// everyone in the same zone lines up neatly. Every zone shows its slots — where the people already standing
/// there are and where the new one will go. Click-through, never takes focus.
/// </summary>
public sealed class DockZones : Window
{
    public static readonly (string Key, string Text)[] Zones =
    {
        ("tl", "УГОЛ ↖"),
        ("tr", "УГОЛ ↗"),
        ("bl", "УГОЛ ↙"),
        ("br", "УГОЛ ↘"),
        ("left", "СЛЕВА"),
        ("right", "СПРАВА"),
        ("top", "РЯД СВЕРХУ"),
        ("bottom", "РЯД СНИЗУ"),
    };

    /// <summary>Columns stand people one under another; rows — side by side.</summary>
    public static bool IsColumn(string zone) => zone is not ("top" or "bottom");

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

    /// <summary>Cover the work area of the main screen and appear. counts — how many already stand in each zone.</summary>
    public void ShowZones(IReadOnlyDictionary<string, int> counts)
    {
        var a = SystemParameters.WorkArea;
        Left = a.Left;
        Top = a.Top;
        Width = a.Width;
        Height = a.Height;
        _face.Counts = counts;
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

    /// <summary>
    /// Draw the zones into a PNG without showing anything (build check: <c>--render-zones file.png</c>).
    /// </summary>
    public static void RenderPreview(string path, double w, double h, string? active, IReadOnlyDictionary<string, int> counts)
    {
        var face = new Face { Active = active, Counts = counts };
        face.Measure(new Size(w, h));
        face.Arrange(new Rect(0, 0, w, h));
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            // something game-like behind, to judge how the zones read over a picture
            var g = new LinearGradientBrush(Color.FromRgb(0x2A, 0x4A, 0x6A), Color.FromRgb(0x6A, 0x5A, 0x3A), 30);
            dc.DrawRectangle(g, null, new Rect(0, 0, w, h));
        }
        bmp.Render(bg);
        bmp.Render(face);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var f = System.IO.File.Create(path);
        enc.Save(f);
    }

    /// <summary>Zone rectangle inside the work area (window coordinates).</summary>
    public static Rect ZoneRect(string zone, double w, double h)
    {
        const double m = 12, col = 280, row = 84;
        double corner = Math.Max(170, h * 0.34);
        double middle = Math.Max(60, h - 2 * (corner + 2 * m));
        double rowX = col + 3 * m, rowW = Math.Max(120, w - 2 * rowX);
        return zone switch
        {
            "tl" => new Rect(m, m, col, corner),
            "tr" => new Rect(w - col - m, m, col, corner),
            "bl" => new Rect(m, h - corner - m, col, corner),
            "br" => new Rect(w - col - m, h - corner - m, col, corner),
            "left" => new Rect(m, corner + 2 * m, col, middle),
            "right" => new Rect(w - col - m, corner + 2 * m, col, middle),
            "top" => new Rect(rowX, m, rowW, row),
            _ => new Rect(rowX, h - row - m, rowW, row),
        };
    }

    private sealed class Face : FrameworkElement
    {
        public string? Active;
        public IReadOnlyDictionary<string, int> Counts = new Dictionary<string, int>();

        private static readonly Brush Card = P5.Freeze(new SolidColorBrush(Color.FromArgb(0x9C, 0x0A, 0x0A, 0x0A)));
        private static readonly Brush CardOn = P5.Freeze(new SolidColorBrush(Color.FromArgb(0xC8, 0xC4, 0x00, 0x10)));
        private static readonly Brush Hatch = P5.Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xE6, 0x00, 0x12)));
        private static readonly Brush HatchOn = P5.Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush Slot = P5.Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0xF5, 0xF5, 0xF5)));
        private static readonly Brush SlotNew = P5.Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0xF5, 0xF5, 0xF5)));
        private static readonly Brush Shadow = P5.Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0xE6, 0x00, 0x12)));
        private static readonly Pen Dash = MakeDash();

        private static Pen MakeDash()
        {
            var p = new Pen(P5.Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xF5, 0xF5, 0xF5))), 1.5)
                { DashStyle = DashStyles.Dash };
            p.Freeze();
            return p;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            foreach (var (key, text) in Zones)
                DrawZone(dc, key, text, ZoneRect(key, w, h), key == Active, Counts.TryGetValue(key, out var n) ? n : 0);

            var hint = P5.Text(this, "ОТПУСТИ В ЗОНЕ — ВСТАНЕТ ТУДА И ЗАКРЕПИТСЯ  ·  МИМО ЗОН — ОСТАНЕТСЯ, ГДЕ ОТПУСТИЛ",
                P5.HeavyItalic, 13, P5.White);
            var hr = new Rect((w - hint.Width) / 2 - 18, h / 2 - hint.Height / 2 - 6, hint.Width + 36, hint.Height + 12);
            dc.DrawGeometry(Shadow, null, P5.Slant(new Rect(hr.X + 5, hr.Y + 4, hr.Width, hr.Height), 10));
            dc.DrawGeometry(P5.Black, null, P5.Slant(hr, 10));
            dc.DrawText(hint, new Point(hr.Left + 18, hr.Top + 6));
        }

        private void DrawZone(DrawingContext dc, string key, string text, Rect r, bool on, int count)
        {
            const double slant = 16;
            var card = P5.Slant(r, slant);

            // the card: black glass, red rim; the one under the cursor turns red
            dc.DrawGeometry(on ? CardOn : Card, P5.Pen(on ? P5.White : P5.Red, on ? 3 : 2), card);
            dc.PushClip(card);
            var hp = P5.Pen(on ? HatchOn : Hatch, 6);
            for (double x = r.Left - r.Height; x < r.Right; x += 22)            // diagonal hatching, P5 style
                dc.DrawLine(hp, new Point(x, r.Bottom), new Point(x + r.Height, r.Top));
            dc.Pop();

            // slots: who already stands here (grey) and where the new one goes (dashed / white)
            bool column = IsColumn(key);
            bool fromBottom = key is "bl" or "br";
            int slots = count + 1;
            double pw = column ? Math.Min(r.Width - 56, 200) : 150, ph = 30, gap = 8;
            const double titleH = 40;
            var area = new Rect(r.Left + 22, r.Top + titleH, Math.Max(0, r.Width - 44), Math.Max(0, r.Height - titleH - 10));
            int fit = column
                ? Math.Max(1, (int)((area.Height + gap) / (ph + gap)))
                : Math.Max(1, (int)((area.Width + gap) / (pw + gap)));
            int shown = Math.Min(slots, fit);
            for (int i = 0; i < shown; i++)
            {
                bool isNew = i == shown - 1;            // when not everyone fits, the new slot is still the last one
                Rect s;
                if (column)
                {
                    double y = fromBottom ? area.Bottom - (shown - i) * (ph + gap) + gap : area.Top + i * (ph + gap);
                    double x = key is "tr" or "br" or "right" ? area.Right - pw : area.Left;
                    s = new Rect(x, y, pw, ph);
                }
                else
                {
                    double total = shown * pw + (shown - 1) * gap;
                    s = new Rect(area.Left + (area.Width - total) / 2 + i * (pw + gap), area.Top + (area.Height - ph) / 2, pw, ph);
                }
                var g = P5.Slant(s, 9);
                if (isNew)
                {
                    dc.DrawGeometry(on ? SlotNew : null, Dash, g);
                    var plus = P5.Text(this, on ? "СЮДА" : "+", P5.HeavyItalic, 13, on ? P5.Black : P5.White);
                    dc.DrawText(plus, new Point(s.Left + (s.Width - plus.Width) / 2, s.Top + (s.Height - plus.Height) / 2));
                }
                else
                {
                    dc.DrawGeometry(Slot, null, g);
                    dc.DrawEllipse(P5.Black, null, new Point(s.Left + 22, s.Top + ph / 2), 10, 10);   // avatar spot
                    dc.DrawRectangle(P5.Black, null, new Rect(s.Left + 40, s.Top + ph / 2 - 3, Math.Max(10, s.Width * 0.45), 6));
                }
            }

            // title: white tag with a red shadow, like the P5 menu; how many people already stand there
            var ft = P5.Text(this, text, P5.HeavyItalic, 15, on ? P5.White : P5.Black);
            var tag = new Rect(r.Left + 18, r.Top + 8, ft.Width + 26, ft.Height + 6);
            dc.DrawGeometry(Shadow, null, P5.Slant(new Rect(tag.X + 4, tag.Y + 3, tag.Width, tag.Height), 8));
            dc.DrawGeometry(on ? P5.Black : P5.White, null, P5.Slant(tag, 8));
            dc.DrawText(ft, new Point(tag.Left + 13, tag.Top + 3));
            if (count > 0)
            {
                var c = P5.Text(this, count.ToString(), P5.Heavy, 13, P5.White);
                var cr = new Rect(tag.Right + 8, tag.Top + 1, c.Width + 16, tag.Height - 2);
                dc.DrawGeometry(P5.Red, P5.Pen(P5.White, 1.5), P5.Slant(cr, 6));
                dc.DrawText(c, new Point(cr.Left + 8, cr.Top + (cr.Height - c.Height) / 2));
            }
        }
    }
}
