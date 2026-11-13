using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>
/// Volume pop-up for the keyboard volume keys, Persona-menu style:
/// red shard + black slanted plate, speaker with waves, a number that counts, a bar of blades that slides.
/// </summary>
public sealed class VolumeToast : Window
{
    private const double MinDb = -60, MaxDb = 12;

    private readonly Face _face = new();
    private readonly TranslateTransform _move = new();
    private readonly ScaleTransform _kick = new(1, 1);
    private readonly RotateTransform _tilt = new(-3);
    private readonly DispatcherTimer _leave;
    private bool _rendering;

    public VolumeToast()
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
        Width = 470;
        Height = 150;
        _face.RenderTransformOrigin = new Point(0.5, 0.5);
        _face.RenderTransform = new TransformGroup { Children = { _kick, _tilt, _move } };
        Content = _face;
        SourceInitialized += (_, _) => MiniOverlay.MakeClickThrough(this);

        _leave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _leave.Tick += (_, _) =>
        {
            _leave.Stop();
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            _move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -26, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease };
            fade.Completed += (_, _) => { if (!_leave.IsEnabled) { Hide(); StopRendering(); } };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    /// <param name="db">new headphones level</param>
    /// <param name="muted">sound off</param>
    /// <param name="up">direction of the change (for the arrow and the kick)</param>
    public void ShowLevel(double db, bool muted, bool up)
    {
        bool fresh = !IsVisible || Opacity < 0.99;
        _face.Target = Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0, 1);
        _face.TargetDb = db;
        _face.Muted = muted;
        _face.Up = up;
        _face.ArrowAt = DateTime.Now;
        if (fresh) _face.SnapTo(_face.Target, db);

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        var area = NotifyArea.Get(this);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 24;

        var back = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
        if (fresh)
        {
            // slide in from the left with the tilt settling
            _move.BeginAnimation(TranslateTransform.YProperty, null);
            _move.Y = 0;
            _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-70, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = back });
            _tilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-9, -3, TimeSpan.FromMilliseconds(380)) { EasingFunction = back });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }
        else
        {
            // every further step: a small kick in the direction of the change
            _move.BeginAnimation(TranslateTransform.YProperty, null);
            _move.Y = 0;
            var k = new DoubleAnimation(muted ? 0.94 : up ? 1.045 : 0.965, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = back };
            _kick.BeginAnimation(ScaleTransform.ScaleXProperty, k);
            _kick.BeginAnimation(ScaleTransform.ScaleYProperty, k);
        }
        if (muted)
        {
            var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260) };
            double[] xs = { -8, 7, -5, 3, -1, 0 };
            for (int i = 0; i < xs.Length; i++)
                shake.KeyFrames.Add(new LinearDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40 * (i + 1)))));
            _move.BeginAnimation(TranslateTransform.XProperty, shake);
        }

        StartRendering();
        _leave.Stop();
        _leave.Start();
    }

    private void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e) => _face.Step();

    private sealed class Face : FrameworkElement
    {
        private const int Blades = 36;
        private static readonly Typeface Small = new(new FontFamily("Arial Black"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

        public double Target, TargetDb;
        public bool Muted, Up;
        public DateTime ArrowAt;
        private double _shown, _shownDb, _muteMix;

        public void SnapTo(double frac, double db) { _shown = frac; _shownDb = db; }

        public void Step()
        {
            // ease the bar and the number towards the new value
            _shown += (Target - _shown) * 0.22;
            _shownDb += (TargetDb - _shownDb) * 0.22;
            _muteMix += ((Muted ? 1 : 0) - _muteMix) * 0.25;
            InvalidateVisual();
        }

        private FormattedText T(string s, Typeface tf, double size, Brush b) =>
            new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 50) return;
            var plate = new Rect(34, 22, w - 80, h - 50);

            // red shard behind, offset like a Persona menu card; turns white when muted
            var shard = new Rect(plate.Left + 12, plate.Top + 10, plate.Width, plate.Height);
            dc.DrawGeometry(_muteMix > 0.5 ? P5.White : P5.Red, null, P5.Slant(shard, 22));
            dc.DrawGeometry(_muteMix > 0.5 ? P5.Red : P5.Black, P5.Pen(P5.White, 3), P5.Slant(plate, 22));

            // speaker
            double sx = plate.Left + 34, sy = plate.Top + plate.Height / 2 - 4;
            dc.DrawGeometry(P5.White, null, P5.Poly(
                new Point(sx - 10, sy - 7), new Point(sx - 3, sy - 7), new Point(sx + 7, sy - 16),
                new Point(sx + 7, sy + 16), new Point(sx - 3, sy + 7), new Point(sx - 10, sy + 7)));
            if (_muteMix > 0.5)
            {
                var x = P5.Pen(P5.White, 4);
                dc.DrawLine(x, new Point(sx + 14, sy - 8), new Point(sx + 28, sy + 8));
                dc.DrawLine(x, new Point(sx + 28, sy - 8), new Point(sx + 14, sy + 8));
            }
            else
            {
                int waves = _shown < 0.02 ? 0 : _shown < 0.45 ? 1 : _shown < 0.75 ? 2 : 3;
                for (int i = 1; i <= waves; i++)
                {
                    double r = 7 + i * 7;
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(sx + 7 + r * Math.Cos(-0.75), sy + r * Math.Sin(-0.75)), false, false);
                        c.ArcTo(new Point(sx + 7 + r * Math.Cos(0.75), sy + r * Math.Sin(0.75)), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, P5.Pen(i == 3 && TargetDb > 0 ? P5.Red : P5.White, 3), g);
                }
            }

            // label + number
            double tx = plate.Left + 92;
            dc.DrawText(T(_muteMix > 0.5 ? "ЗВУК" : "ГРОМКОСТЬ", Small, 11, _muteMix > 0.5 ? P5.Black : P5.Grey), new Point(tx, plate.Top + 9));
            string num = _muteMix > 0.5 ? "ВЫКЛ" : $"{Math.Round(_shownDb):+0;-0;0}";
            var big = T(num, P5.HeavyItalic, 34, _muteMix > 0.5 ? P5.White : TargetDb > 0.5 ? P5.Red : P5.White);
            dc.DrawText(big, new Point(tx - 2, plate.Top + 20));
            if (_muteMix <= 0.5)
            {
                dc.DrawText(T("dB", Small, 15, P5.Grey), new Point(tx + big.Width + 4, plate.Top + 38));
                // direction arrow, fades out
                double a = 1 - Math.Clamp((DateTime.Now - ArrowAt).TotalMilliseconds / 500.0, 0, 1);
                if (a > 0)
                {
                    var arrow = T(Up ? "▲" : "▼", Small, 14, Up ? P5.Red : P5.White);
                    dc.PushOpacity(a);
                    dc.DrawText(arrow, new Point(tx + big.Width + 30, plate.Top + 22 + (Up ? -6 : 6) * (1 - a)));
                    dc.Pop();
                }
            }

            // blade bar
            double bx0 = plate.Left + 92, bx1 = plate.Right - 30, by0 = plate.Bottom - 26, by1 = plate.Bottom - 12;
            double bw = (bx1 - bx0) / Blades;
            int zeroAt = (int)Math.Round((0 - MinDb) / (MaxDb - MinDb) * Blades);
            double litF = _muteMix > 0.5 ? 0 : _shown * Blades;
            for (int i = 0; i < Blades; i++)
            {
                double fill = Math.Clamp(litF - i, 0, 1);
                double x = bx0 + i * bw;
                double grow = i == (int)litF ? 2 : 0; // the leading blade sticks out
                var blade = P5.Poly(
                    new Point(x + 5, by0 - grow), new Point(x + bw - 2 + 5, by0 - grow),
                    new Point(x + bw - 2, by1 + grow), new Point(x, by1 + grow));
                dc.DrawGeometry(_muteMix > 0.5 ? P5.DarkRed : P5.Coal, null, blade);
                if (fill > 0)
                {
                    dc.PushOpacity(fill);
                    dc.DrawGeometry(i >= zeroAt ? P5.Red : P5.White, null, blade);
                    dc.Pop();
                }
            }
            // 0 dB mark
            double zx = bx0 + zeroAt * bw;
            dc.DrawLine(P5.Pen(P5.Red, 2), new Point(zx + 4, by0 - 7), new Point(zx - 1, by1 + 5));
            dc.DrawText(T("0", Small, 9, P5.Grey), new Point(zx - 2, by1 + 5));
        }
    }
}
