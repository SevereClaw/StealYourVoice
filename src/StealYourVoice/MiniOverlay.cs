using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>
/// Tiny always-on-top, click-through mic indicator. See-through while you are silent,
/// solid while you talk or while the mic is muted.
/// </summary>
public sealed class MiniOverlay : Window
{
    private const double BaseW = 178, BaseH = 30, Solid = 0.92;

    private readonly Face _face = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private int _corner = 1;
    private Point? _custom;
    private double _silentOpacity = 0.3;
    private double _opacity = Solid;
    private DateTime _lastTalk = DateTime.MinValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

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
        Width = BaseW;
        Height = BaseH;
        Opacity = Solid;
        _face.LayoutTransform = _scale;
        Content = _face;
        SourceInitialized += (_, _) => MakeClickThrough(this);
    }

    /// <summary>Click-through, never takes focus, hidden from Alt+Tab. Call after the handle exists.</summary>
    public static void MakeClickThrough(Window w)
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        var h = new WindowInteropHelper(w).Handle;
        var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    /// <param name="corner">1 = top-left, 2 = top-right, 3 = bottom-left, 4 = bottom-right</param>
    /// <param name="custom">free position (overrides the corner)</param>
    public void Configure(int corner, double scale, Point? custom, double silentOpacity)
    {
        _corner = corner;
        _custom = custom;
        _silentOpacity = Math.Clamp(silentOpacity, 0.05, 1);
        _scale.ScaleX = _scale.ScaleY = scale;
        Width = BaseW * scale;
        Height = BaseH * scale;
        Place();
    }

    private void Place()
    {
        if (_custom is { } p)
        {
            Left = Math.Clamp(p.X, SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40);
            Top = Math.Clamp(p.Y, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20);
            return;
        }
        var a = SystemParameters.WorkArea;
        const double m = 10;
        Left = _corner is 1 or 3 ? a.Left + m : a.Right - Width - m;
        Top = _corner is 1 or 2 ? a.Top + m : a.Bottom - Height - m;
    }

    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Is this physical screen point over the indicator?</summary>
    public bool Contains(int x, int y)
    {
        var h = new WindowInteropHelper(this).Handle;
        return h != IntPtr.Zero && GetWindowRect(h, out var r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    public void MoveTo(double left, double top)
    {
        _custom = new Point(left, top);
        Left = left;
        Top = top;
    }

    /// <summary>Briefly show a text (e.g. "+3 dB") instead of the level bar.</summary>
    public void ShowNote(string text)
    {
        _face.Note = text;
        _face.NoteUntil = DateTime.Now.AddMilliseconds(1400);
        _lastTalk = DateTime.Now; // light it up while adjusting
    }

    /// <summary>Feed the post-mute mic level ~30 times per second.</summary>
    public void Push(float linear, bool muted)
    {
        _face.Push(linear, muted);

        var now = DateTime.Now;
        if (P5.ToDb(linear) > -45) _lastTalk = now;
        bool lit = muted || (now - _lastTalk).TotalMilliseconds < 500;
        double target = lit ? Solid : _silentOpacity * Solid;
        // snap up quickly, fade down gently
        _opacity += (target - _opacity) * (target > _opacity ? 0.5 : 0.1);
        if (Math.Abs(Opacity - _opacity) > 0.004) Opacity = _opacity;
    }

    private sealed class Face : FrameworkElement
    {
        private const int Blades = 14;
        private double _db = -90;
        private bool _muted;

        public string? Note;
        public DateTime NoteUntil = DateTime.MinValue;

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

            if (Note != null && DateTime.Now < NoteUntil)
            {
                dc.DrawGeometry(P5.Black, P5.Pen(P5.Red, 2), P5.Slant(plate, 9));
                var n = P5.Text(this, Note, P5.HeavyItalic, 14, P5.White);
                dc.DrawText(n, new Point((w - n.Width) / 2, (h - n.Height) / 2));
                return;
            }

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
