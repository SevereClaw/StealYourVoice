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

public record SpeakerInfo(string Id, string Name, string? Avatar, bool MicOff, bool Deaf);

public record SpeakerStyle(bool OnlyTalking, double SilentOpacity, double Scale, bool AvatarsOnly);

/// <summary>
/// Click-through list of the Discord voice channel, shown over the game:
/// avatar + name + mute/deaf badges; see-through while silent, solid while talking.
/// </summary>
public sealed class SpeakersOverlay : Window
{
    private readonly StackPanel _stack = new() { Margin = new Thickness(8, 2, 8, 2) };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Dictionary<string, Border> _rows = new();
    private readonly Dictionary<string, bool> _lit = new();
    private static readonly Dictionary<string, BitmapImage> AvatarCache = new();
    private SpeakerStyle _style = new(false, 0.28, 1, false);
    private string _signature = "";
    private int _corner = 1;
    private double _offset;
    private Point? _custom;
    private HashSet<string> _known = new();
    private readonly Dictionary<string, bool> _talk = new();
    private readonly Dictionary<string, ScaleTransform> _scales = new();
    private readonly Dictionary<string, TextBlock> _volLabels = new();
    private readonly DispatcherTimer _volTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };
    private string? _volShownFor;
    private readonly Dictionary<string, TextBlock> _tags = new();
    private readonly Dictionary<string, TranslateTransform> _moves = new();
    private readonly Dictionary<string, int> _loudApplied = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>Free position chosen by dragging (overrides the corner).</summary>
    public Point? CustomPos => _custom;

    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Is this physical screen point over the list?</summary>
    public bool Contains(int x, int y)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return h != IntPtr.Zero && GetWindowRect(h, out var r) && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    public void MoveTo(double left, double top)
    {
        _custom = new Point(left, top);
        Left = left;
        Top = top;
    }

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
            if (_volShownFor != null && _volLabels.TryGetValue(_volShownFor, out var l)) l.Visibility = Visibility.Collapsed;
            _volShownFor = null;
        };
    }

    /// <summary>Which person's row is under this physical screen point?</summary>
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
                if (!kv.Value.IsVisible) continue;
                var b = kv.Value.TransformToAncestor(this).TransformBounds(new Rect(kv.Value.RenderSize));
                if (b.Contains(p)) return kv.Key;
            }
        }
        catch { /* layout not ready */ }
        return null;
    }

    /// <summary>Briefly show "120%" on a person's row.</summary>
    public void ShowVolume(string id, int percent)
    {
        if (_volShownFor != null && _volShownFor != id && _volLabels.TryGetValue(_volShownFor, out var old))
            old.Visibility = Visibility.Collapsed;
        if (!_volLabels.TryGetValue(id, out var label)) return;
        label.Text = percent + "%";
        label.Visibility = Visibility.Visible;
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
            int s = speaking.Contains(id) && states.TryGetValue(id, out var v) ? v : 0;
            if (_loudApplied.TryGetValue(id, out var was) && was == s) continue;
            _loudApplied[id] = s;
            if (!_tags.TryGetValue(id, out var tag) || !_moves.TryGetValue(id, out var move)) continue;

            tag.BeginAnimation(OpacityProperty, null);
            move.BeginAnimation(TranslateTransform.XProperty, null);
            if (s == 0) { tag.Visibility = Visibility.Collapsed; continue; }

            tag.Visibility = Visibility.Visible;
            if (s < 0)
            {
                tag.Text = "▼ ТИХО";
                tag.Foreground = P5.White;
                tag.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(450))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                tag.Text = "▲ ГРОМКО";
                tag.Foreground = P5.Red;
                var shake = new DoubleAnimationUsingKeyFrames
                    { Duration = TimeSpan.FromMilliseconds(320), RepeatBehavior = RepeatBehavior.Forever };
                double[] xs = { -4, 4, -3, 3, -1, 0 };
                for (int i = 0; i < xs.Length; i++)
                    shake.KeyFrames.Add(new LinearDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45 * (i + 1)))));
                move.BeginAnimation(TranslateTransform.XProperty, shake);
            }
        }
    }

    private void Bounce(string id)
    {
        if (!_scales.TryGetValue(id, out var sc)) return;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(240) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)),
            new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut }));
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

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
            _scales.Clear();
            _volLabels.Clear();
            _tags.Clear();
            _moves.Clear();
            _loudApplied.Clear();
            _volShownFor = null;
            var before = _known;
            _known = new HashSet<string>(members.Select(m => m.Id));
            foreach (var gone in _talk.Keys.Where(k => !_known.Contains(k)).ToList()) _talk.Remove(gone);

            bool right = corner is 2 or 4 && !custom.HasValue;
            foreach (var m in members)
            {
                var row = BuildRow(m, right, style, out var vol, out var tag);
                var sc = new ScaleTransform(1, 1);
                var tr = new TranslateTransform();
                row.RenderTransformOrigin = new Point(0.5, 0.5);
                row.RenderTransform = new TransformGroup { Children = { sc, tr } };
                _rows[m.Id] = row;
                _scales[m.Id] = sc;
                _volLabels[m.Id] = vol;
                _tags[m.Id] = tag;
                _moves[m.Id] = tr;
                _stack.Children.Add(row);

                // someone new joined → their plate flies in from the screen edge
                if (before.Count > 0 && !before.Contains(m.Id))
                    tr.BeginAnimation(TranslateTransform.XProperty,
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
        foreach (var kv in _rows)
        {
            bool on = speaking.Contains(kv.Key);
            bool talkedBefore = _talk.TryGetValue(kv.Key, out var pt) && pt;
            _talk[kv.Key] = on;
            if (on && !talkedBefore) Bounce(kv.Key);
            if (_lit.TryGetValue(kv.Key, out var was) && was == on) continue;
            _lit[kv.Key] = on;
            kv.Value.BorderBrush = on ? P5.Red : P5.White;

            if (_style.OnlyTalking)
            {
                kv.Value.BeginAnimation(OpacityProperty, null);
                kv.Value.Opacity = 1;
                kv.Value.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                kv.Value.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(on ? 1.0 : _style.SilentOpacity, TimeSpan.FromMilliseconds(on ? 70 : 260)));
            }
        }
    }

    private static Border BuildRow(SpeakerInfo m, bool right, SpeakerStyle style, out TextBlock vol, out TextBlock tag)
    {
        tag = new TextBlock
        {
            FontFamily = new FontFamily("Arial Black, Segoe UI Symbol"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 10, 0),
            Visibility = Visibility.Collapsed
        };
        vol = new TextBlock
        {
            FontFamily = new FontFamily("Arial Black, Segoe UI, Segoe UI Symbol"),
            FontSize = 14,
            Foreground = P5.Red,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 10, 1),
            Visibility = Visibility.Collapsed
        };

        var name = m.Name.Length > 22 ? m.Name[..20] + "…" : m.Name;

        // fallback tile with the first letter, in case the avatar can't be downloaded
        var avatar = new Grid { Width = 30, Height = 30 };
        avatar.Children.Add(new Border
        {
            Background = P5.Red,
            Child = new TextBlock
            {
                Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
                FontFamily = new FontFamily("Arial Black, Segoe UI, Segoe UI Symbol"),
                FontSize = 16,
                Foreground = P5.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
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
            badges = new StackPanel { Orientation = Orientation.Horizontal };
            if (m.MicOff) badges.Children.Add(new StatusIcon(false));
            if (m.Deaf) badges.Children.Add(new StatusIcon(true));
        }

        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(avatar, Dock.Left);
        row.Children.Add(avatar);
        DockPanel.SetDock(vol, Dock.Right);
        row.Children.Add(vol);
        DockPanel.SetDock(tag, Dock.Right);
        row.Children.Add(tag);

        if (style.AvatarsOnly)
        {
            if (badges != null)
            {
                // tiny badges tucked into the avatar's corner
                badges.LayoutTransform = new ScaleTransform(0.8, 0.8);
                avatar.Children.Add(new Border
                {
                    Child = badges, Background = P5.Black,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
                });
            }
        }
        else
        {
            if (badges != null)
            {
                badges.Margin = new Thickness(0, 0, 8, 0);
                badges.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(badges, Dock.Right);
                row.Children.Add(badges);
            }
            row.Children.Add(new TextBlock
            {
                Text = name.ToUpperInvariant(),
                FontFamily = new FontFamily("Arial Black, Segoe UI, Segoe UI Symbol"),
                FontStyle = FontStyles.Italic,
                FontSize = 14,
                Foreground = P5.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, badges != null ? 8 : 14, 1)
            });
        }

        return new Border
        {
            Child = row,
            Background = P5.Black,
            BorderBrush = P5.White,
            BorderThickness = new Thickness(2),
            Margin = new Thickness(0, 2, 0, 2),
            Opacity = style.OnlyTalking ? 1 : style.SilentOpacity,
            Visibility = style.OnlyTalking ? Visibility.Collapsed : Visibility.Visible,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
    }

    private static BitmapImage? LoadAvatar(string? url)
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
                // headband
                var band = new StreamGeometry();
                using (var c = band.Open())
                {
                    c.BeginFigure(new Point(3, 10), false, false);
                    c.ArcTo(new Point(13, 10), new Size(5, 6), 0, false, SweepDirection.Clockwise, true, false);
                }
                band.Freeze();
                dc.DrawGeometry(null, white, band);
                // ear cups
                dc.DrawRectangle(P5.White, null, new Rect(1.5, 9, 3.5, 5.5));
                dc.DrawRectangle(P5.White, null, new Rect(11, 9, 3.5, 5.5));
            }
            else
            {
                // capsule
                dc.DrawRoundedRectangle(P5.White, null, new Rect(5.5, 1, 5, 8.5), 2.5, 2.5);
                // cradle
                var cradle = new StreamGeometry();
                using (var c = cradle.Open())
                {
                    c.BeginFigure(new Point(3.5, 7), false, false);
                    c.ArcTo(new Point(12.5, 7), new Size(4.5, 4.5), 0, false, SweepDirection.Counterclockwise, true, false);
                }
                cradle.Freeze();
                dc.DrawGeometry(null, white, cradle);
                // stem
                dc.DrawLine(white, new Point(8, 11.5), new Point(8, 15));
            }
            // the slash
            dc.DrawLine(P5.Pen(P5.Black, 4.2), new Point(2, 14.5), new Point(14, 1.5));
            dc.DrawLine(P5.Pen(P5.Red, 2.2), new Point(2, 14.5), new Point(14, 1.5));
        }
    }
}
