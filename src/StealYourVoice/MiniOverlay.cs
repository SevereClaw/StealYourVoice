using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>Tiny always-on-top, click-through mic indicator for the corner of the screen.</summary>
public sealed class MiniOverlay : Window
{
    private readonly Face _face = new();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    public MiniOverlay()
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
        Width = 178;
        Height = 30;
        Opacity = 0.9;
        Content = _face;

        SourceInitialized += (_, _) =>
        {
            // click-through, never takes focus, hidden from Alt+Tab
            const int GWL_EXSTYLE = -20;
            const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var h = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        };
    }

    /// <param name="corner">1 = top-left, 2 = top-right, 3 = bottom-left, 4 = bottom-right</param>
    public void Place(int corner)
    {
        var a = SystemParameters.WorkArea;
        const double m = 10;
        Left = corner is 1 or 3 ? a.Left + m : a.Right - Width - m;
        Top = corner is 1 or 2 ? a.Top + m : a.Bottom - Height - m;
    }

    public void Push(float linear, bool muted) => _face.Push(linear, muted);

    private sealed class Face : FrameworkElement
    {
        private const int Blades = 14;
        private double _db = -90;
        private bool _muted;

        public void Push(float linear, bool muted)
        {
            var db = P5.ToDb(linear);
            _db = db > _db ? db : Math.Max(db, _db - 2);
            _muted = muted;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 20) return;
            var plate = new Rect(1, 1, w - 2, h - 2);

            if (_muted)
            {
                dc.DrawGeometry(P5.Red, P5.Pen(P5.Black, 2), P5.Slant(plate, 9));
                var t = P5.Text(this, "MIC OFF", P5.HeavyItalic, 14, P5.White);
                dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
                return;
            }

            dc.DrawGeometry(P5.Black, P5.Pen(P5.White, 2), P5.Slant(plate, 9));
            var label = P5.Text(this, "MIC", P5.HeavyItalic, 12, P5.White);
            dc.DrawText(label, new Point(14, (h - label.Height) / 2));

            double x0 = 14 + label.Width + 8, x1 = w - 14;
            double bw = (x1 - x0) / Blades;
            int lit = (int)Math.Round(Math.Clamp((_db + 60) / 60.0, 0, 1) * Blades);
            int hot = (int)Math.Round((60 - 9) / 60.0 * Blades);
            for (int i = 0; i < Blades; i++)
            {
                Brush b = i >= lit ? P5.Coal : i >= hot ? P5.Red : P5.White;
                double x = x0 + i * bw;
                dc.DrawGeometry(b, null, P5.Poly(
                    new Point(x + 4, 7), new Point(x + bw - 1 + 4, 7),
                    new Point(x + bw - 1, h - 7), new Point(x, h - 7)));
            }
        }
    }
}
