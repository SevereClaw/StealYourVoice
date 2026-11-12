using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StealYourVoice.Controls;

namespace StealYourVoice;

public record SpeakerInfo(string Id, string Name, string? Avatar, bool MicOff, bool Deaf, bool Live = false, bool Camera = false);

public record SpeakerStyle(bool OnlyTalking, double SilentOpacity, double Scale, bool AvatarsOnly);

/// <summary>
/// Click-through list of the Discord voice channel, shown over the game:
/// avatar + name + mute/deaf badges; see-through while silent, solid while talking.
/// Hovering a plate spreads the list and shows the full nickname.
/// </summary>
public sealed class SpeakersOverlay : Window
{
    private sealed class Row
    {
        public Border Border = null!;
        public ScaleTransform Scale = null!;
        public TranslateTransform Move = null!;
        public TextBlock? Name;          // short name (absent in avatars-only mode)
        public TextBlock Full = null!;   // full nickname, shown on hover
        public StackPanel VolPanel = null!;
        public ScaleTransform VolFill = null!;
        public ScaleTransform VolPop = null!;
        public TextBlock VolText = null!;
        public TextBlock Tag = null!;
        public int LastVol = -1;
    }

    private static readonly FontFamily Font = new("Arial Black, Segoe UI, Segoe UI Symbol");
    private static readonly Thickness Rest = new(0, 2, 0, 2);
    private static readonly Thickness Spread = new(0, 10, 0, 10);

    private readonly StackPanel _stack = new() { Margin = new Thickness(8, 2, 8, 2) };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Dictionary<string, Row> _rows = new();
    private readonly Dictionary<string, int> _lit = new();
    private readonly Dictionary<string, bool> _talk = new();
    private readonly Dictionary<string, int> _loudApplied = new();
    private static readonly Dictionary<string, BitmapImage> AvatarCache = new();
    private readonly DispatcherTimer _volTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private HashSet<string> _known = new();
    private HashSet<string> _speaking = new();
    private SpeakerStyle _style = new(false, 0.28, 1, false);
    private string _signature = "";
    private string? _volShownFor;
    private string? _hover;
    private int _corner = 1;
    private double _offset;
    private Point? _custom;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>Free position chosen by dragging (overrides the corner).</summary>
    public Point? CustomPos => _custom;

    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    public SpeakersOverlay()
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
        SizeToContent = SizeToContent.WidthAndHeight;
        _stack.LayoutTransform = _scale;
        Content = _stack;
        SourceInitialized += (_, _) => MiniOverlay.MakeClickThrough(this);
        SizeChanged += (_, _) => Place();
        _volTimer.Tick += (_, _) =>
        {
            _volTimer.Stop();
            if (_volShownFor != null && _rows.TryGetValue(_volShownFor, out var r)) r.VolPanel.Visibility = Visibility.Collapsed;
            _volShownFor = null;
        };
    }

    // ───────────────────────── geometry ─────────────────────────

    /// <summary>Is this physical screen point over the list?</summary>
    public bool Contains(int x, int y)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return h != IntPtr.Zero && GetWindowRect(h, out var r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    /// <summary>Which person's plate is under this physical screen point?</summary>
    public string? RowAt(int x, int y)
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return null;
            double k = DpiScale;
            var p = new Point((x - r.Left) / k, (y - r.Top) / k);
            foreach (var kv in _rows)
            {
                var b = kv.Value.Border;
                if (!b.IsVisible) continue;
                var box = b.TransformToAncestor(this).TransformBounds(new Rect(b.RenderSize));
                box.Inflate(0, 3); // no dead gaps between plates
                if (box.Contains(p)) return kv.Key;
            }
        }
        catch { /* layout not ready */ }
        return null;
    }

    public void MoveTo(double left, double top)
    {
        _custom = new Point(left, top);
        Left = left;
        Top = top;
    }

    private void Place()
    {
        if (_custom is { } p)
        {
            // keep at least a corner of it on screen
            Left = Math.Clamp(p.X, SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40);
            Top = Math.Clamp(p.Y, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 30);
            return;
        }
        var a = SystemParameters.WorkArea;
        const double m = 10;
        Left = _corner is 1 or 3 ? a.Left + m : a.Right - ActualWidth - m;
        Top = _corner is 1 or 2 ? a.Top + m + _offset : a.Bottom - ActualHeight - m - _offset;
    }

    // ───────────────────────── content ─────────────────────────

    /// <param name="corner">1 = top-left, 2 = top-right, 3 = bottom-left, 4 = bottom-right</param>
    /// <param name="offset">vertical gap from the screen edge (to sit next to the mic indicator)</param>
    public void SetMembers(IReadOnlyList<SpeakerInfo> members, int corner, double offset, SpeakerStyle style, Point? custom)
    {
        var signature = $"{corner}|{custom.HasValue}|{style}|" + string.Join("|", members.Select(m => m.ToString()));
        bool moved = corner != _corner || Math.Abs(offset - _offset) > 0.1 || custom != _custom;
        _custom = custom;
        _corner = corner;
        _offset = offset;
        _style = style;
        _scale.ScaleX = _scale.ScaleY = style.Scale;

        if (signature != _signature)
        {
            _signature = signature;
            _stack.Children.Clear();
            _rows.Clear();
            _lit.Clear();
            _loudApplied.Clear();
            _volShownFor = null;
            var before = _known;
            _known = new HashSet<string>(members.Select(m => m.Id));
            foreach (var gone in _talk.Keys.Where(k => !_known.Contains(k)).ToList()) _talk.Remove(gone);
            if (_hover != null && !_known.Contains(_hover)) _hover = null;

            bool right = corner is 2 or 4 && !custom.HasValue;
            foreach (var m in members)
            {
                var row = BuildRow(m, right, style);
                _rows[m.Id] = row;
                _stack.Children.Add(row.Border);
                if (m.Id == _hover) ApplyHover(row, true, false);

                // someone new joined → their plate flies in from the screen edge
                if (before.Count > 0 && !before.Contains(m.Id))
                    row.Move.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(right ? 120 : -120, 0, TimeSpan.FromMilliseconds(380))
                        {
                            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
                        });
            }
        }
        else if (!moved) return;

        if (members.Count == 0) { Hide(); return; }
        if (!IsVisible) Show();
        UpdateLayout();
        Place();
    }

    public void SetSpeaking(ICollection<string> speaking)
    {
        _speaking = new HashSet<string>(speaking);
        foreach (var kv in _rows)
        {
            var b = kv.Value.Border;
            bool on = _speaking.Contains(kv.Key);
            bool hover = kv.Key == _hover;

            bool talkedBefore = _talk.TryGetValue(kv.Key, out var pt) && pt;
            _talk[kv.Key] = on;
            if (on && !talkedBefore) Bounce(kv.Value);

            int state = (on ? 1 : 0) | (hover ? 2 : 0);
            if (_lit.TryGetValue(kv.Key, out var was) && was == state) continue;
            _lit[kv.Key] = state;
            b.BorderBrush = on ? P5.Red : P5.White;

            if (_style.OnlyTalking)
            {
                b.BeginAnimation(OpacityProperty, null);
                b.Opacity = 1;
                b.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                bool solid = on || hover; // a plate you point at is always fully visible
                b.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(solid ? 1.0 : _style.SilentOpacity, TimeSpan.FromMilliseconds(solid ? 70 : 260)));
            }
        }
    }

    /// <summary>Mouse is over this person's plate (null = over nobody): spread the list, show the full nickname.</summary>
    public void SetHover(string? id)
    {
        if (id == _hover) return;
        _hover = id;
        foreach (var kv in _rows) ApplyHover(kv.Value, kv.Key == id, true);
        SetSpeaking(_speaking);
    }

    private static void ApplyHover(Row row, bool on, bool animate)
    {
        var target = on ? Spread : Rest;
        if (animate)
            row.Border.BeginAnimation(MarginProperty, new ThicknessAnimation(target, TimeSpan.FromMilliseconds(170))
                { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
        else
        {
            row.Border.BeginAnimation(MarginProperty, null);
            row.Border.Margin = target;
        }
        if (row.Name != null) row.Name.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        row.Full.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && animate)
        {
            row.Full.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            var pop = new DoubleAnimation(1.07, 1.0, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            row.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            row.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    /// <summary>Animated volume read-out on a person's plate: arrow, number that pops, a bar that slides.</summary>
    public void ShowVolume(string id, int percent)
    {
        if (_volShownFor != null && _volShownFor != id && _rows.TryGetValue(_volShownFor, out var old))
            old.VolPanel.Visibility = Visibility.Collapsed;
        if (!_rows.TryGetValue(id, out var row)) return;

        bool up = row.LastVol < 0 || percent >= row.LastVol;
        row.LastVol = percent;
        row.VolText.Text = (up ? "▲ " : "▼ ") + percent + "%";
        row.VolText.Foreground = up ? P5.Red : P5.White;
        row.VolPanel.Visibility = Visibility.Visible;

        row.VolFill.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(Math.Clamp(percent / 200.0, 0, 1), TimeSpan.FromMilliseconds(160))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        var pop = new DoubleAnimation(up ? 1.35 : 0.75, 1.0, TimeSpan.FromMilliseconds(190))
            { EasingFunction = new BackEase { Amplitude = 0.7, EasingMode = EasingMode.EaseOut } };
        row.VolPop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        row.VolPop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        _volShownFor = id;
        _volTimer.Stop();
        _volTimer.Start();
    }

    /// <summary>
    /// Flag people who are much quieter / louder than normal while they talk:
    /// −1 → blinking "ТИХО", +1 → red "ГРОМКО" and the plate shakes.
    /// </summary>
    public void SetLoudness(IReadOnlyDictionary<string, int> states, ICollection<string> speaking)
    {
        foreach (var kv in _rows)
        {
            var id = kv.Key;
            var row = kv.Value;
            int s = speaking.Contains(id) && states.TryGetValue(id, out var v) ? v : 0;
            if (_loudApplied.TryGetValue(id, out var was) && was == s) continue;
            _loudApplied[id] = s;

            row.Tag.BeginAnimation(OpacityProperty, null);
            row.Move.BeginAnimation(TranslateTransform.XProperty, null);
            if (s == 0) { row.Tag.Visibility = Visibility.Collapsed; continue; }

            row.Tag.Visibility = Visibility.Visible;
            if (s < 0)
            {
                row.Tag.Text = "▼ ТИХО";
                row.Tag.Foreground = P5.White;
                row.Tag.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(450))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                row.Tag.Text = "▲ ГРОМКО";
                row.Tag.Foreground = P5.Red;
                var shake = new DoubleAnimationUsingKeyFrames
                    { Duration = TimeSpan.FromMilliseconds(320), RepeatBehavior = RepeatBehavior.Forever };
                double[] xs = { -4, 4, -3, 3, -1, 0 };
                for (int i = 0; i < xs.Length; i++)
                    shake.KeyFrames.Add(new LinearDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45 * (i + 1)))));
                row.Move.BeginAnimation(TranslateTransform.XProperty, shake);
            }
        }
    }

    private static void Bounce(Row row)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(240) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)),
            new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut }));
        row.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        row.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ───────────────────────── building a plate ─────────────────────────

    private static TextBlock Label(string text, double size, Brush brush, Thickness margin) => new()
    {
        Text = text,
        FontFamily = Font,
        FontSize = size,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = margin
    };

    private static Border TextBadge(string text, Brush background, Brush foreground) => new()
    {
        Background = background,
        Margin = new Thickness(2, 0, 2, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text, FontFamily = Font, FontSize = 9, Foreground = foreground,
            Margin = new Thickness(4, 0, 4, 1)
        }
    };

    private static Row BuildRow(SpeakerInfo m, bool right, SpeakerStyle style)
    {
        var row = new Row();
        var shortName = m.Name.Length > 22 ? m.Name[..20] + "…" : m.Name;

        // avatar, with a letter tile underneath in case the picture can't be downloaded
        var avatar = new Grid { Width = 30, Height = 30 };
        var letter = Label(shortName.Length > 0 ? shortName[..1].ToUpperInvariant() : "?", 16, P5.White, new Thickness(0));
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        avatar.Children.Add(new Border { Background = P5.Red, Child = letter });
        var bmp = LoadAvatar(m.Avatar);
        if (bmp != null)
        {
            var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
            img.ImageFailed += (_, _) => img.Visibility = Visibility.Collapsed;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            avatar.Children.Add(img);
        }

        StackPanel? badges = null;
        if (m.MicOff || m.Deaf)
        {
            badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (m.MicOff) badges.Children.Add(new StatusIcon(false));
            if (m.Deaf) badges.Children.Add(new StatusIcon(true));
        }

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(avatar);

        if (!style.AvatarsOnly)
        {
            row.Name = Label(shortName.ToUpperInvariant(), 14, P5.White, new Thickness(10, 0, 12, 1));
            row.Name.FontStyle = FontStyles.Italic;
            line.Children.Add(row.Name);
        }

        row.Full = Label(m.Name.ToUpperInvariant(), 14, P5.White, new Thickness(10, 0, 12, 1));
        row.Full.FontStyle = FontStyles.Italic;
        row.Full.Visibility = Visibility.Collapsed;
        line.Children.Add(row.Full);

        if (badges != null)
        {
            if (style.AvatarsOnly)
            {
                // tiny badges tucked into the avatar's corner
                badges.LayoutTransform = new ScaleTransform(0.8, 0.8);
                avatar.Children.Add(new Border
                {
                    Child = badges, Background = P5.Black,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
                });
            }
            else
            {
                badges.Margin = new Thickness(0, 0, 8, 0);
                line.Children.Add(badges);
            }
        }

        row.Tag = Label("", 11, P5.White, new Thickness(2, 0, 10, 0));
        row.Tag.Visibility = Visibility.Collapsed;
        line.Children.Add(row.Tag);

        // volume read-out: sliding bar + popping number
        row.VolFill = new ScaleTransform(0.5, 1);
        row.VolPop = new ScaleTransform(1, 1);
        row.VolText = Label("", 13, P5.Red, new Thickness(8, 0, 10, 1));
        row.VolText.RenderTransformOrigin = new Point(0.5, 0.5);
        row.VolText.RenderTransform = row.VolPop;
        var bar = new Grid { Width = 70, Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        bar.Children.Add(new Border { Background = P5.Coal, BorderBrush = P5.White, BorderThickness = new Thickness(1) });
        bar.Children.Add(new Border
        {
            Background = P5.Red, Margin = new Thickness(1),
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = row.VolFill
        });
        // the 100% mark
        bar.Children.Add(new Border { Width = 2, Background = P5.White, HorizontalAlignment = HorizontalAlignment.Center });
        row.VolPanel = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        row.VolPanel.Children.Add(bar);
        row.VolPanel.Children.Add(row.VolText);
        line.Children.Add(row.VolPanel);

        row.Scale = new ScaleTransform(1, 1);
        row.Move = new TranslateTransform();
        row.Border = new Border
        {
            Child = line,
            Background = P5.Black,
            BorderBrush = P5.White,
            BorderThickness = new Thickness(2),
            Margin = Rest,
            Opacity = style.OnlyTalking ? 1 : style.SilentOpacity,
            Visibility = style.OnlyTalking ? Visibility.Collapsed : Visibility.Visible,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { row.Scale, row.Move } }
        };
        return row;
    }

    internal static BitmapImage? LoadAvatar(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (AvatarCache.TryGetValue(url, out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url);
            bmp.DecodePixelWidth = 64;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit(); // downloads in the background; the letter tile shows until then (or forever on failure)
            bmp.DownloadFailed += (_, _) => AvatarCache.Remove(url);
            AvatarCache[url] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>16×16 hand-drawn badge: crossed-out microphone or crossed-out headphones.</summary>
    private sealed class StatusIcon : FrameworkElement
    {
        private readonly bool _headphones;

        public StatusIcon(bool headphones)
        {
            _headphones = headphones;
            Width = 16;
            Height = 16;
            Margin = new Thickness(2, 0, 2, 0);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var white = P5.Pen(P5.White, 1.6);
            if (_headphones)
            {
                var band = new StreamGeometry();
                using (var c = band.Open())
                {
                    c.BeginFigure(new Point(3, 10), false, false);
                    c.ArcTo(new Point(13, 10), new Size(5, 6), 0, false, SweepDirection.Clockwise, true, false);
                }
                band.Freeze();
                dc.DrawGeometry(null, white, band);
                dc.DrawRectangle(P5.White, null, new Rect(1.5, 9, 3.5, 5.5));
                dc.DrawRectangle(P5.White, null, new Rect(11, 9, 3.5, 5.5));
            }
            else
            {
                dc.DrawRoundedRectangle(P5.White, null, new Rect(5.5, 1, 5, 8.5), 2.5, 2.5);
                var cradle = new StreamGeometry();
                using (var c = cradle.Open())
                {
                    c.BeginFigure(new Point(3.5, 7), false, false);
                    c.ArcTo(new Point(12.5, 7), new Size(4.5, 4.5), 0, false, SweepDirection.Counterclockwise, true, false);
                }
                cradle.Freeze();
                dc.DrawGeometry(null, white, cradle);
                dc.DrawLine(white, new Point(8, 11.5), new Point(8, 15));
            }
            dc.DrawLine(P5.Pen(P5.Black, 4.2), new Point(2, 14.5), new Point(14, 1.5));
            dc.DrawLine(P5.Pen(P5.Red, 2.2), new Point(2, 14.5), new Point(14, 1.5));
        }
    }
}
