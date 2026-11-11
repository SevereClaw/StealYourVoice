using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    }

    /// <param name="corner">1 = top-left, 2 = top-right, 3 = bottom-left, 4 = bottom-right</param>
    /// <param name="offset">vertical gap from the screen edge (to sit next to the mic indicator)</param>
    public void SetMembers(IReadOnlyList<SpeakerInfo> members, int corner, double offset, SpeakerStyle style)
    {
        var signature = $"{corner}|{style}|" + string.Join("|", members.Select(m => m.ToString()));
        bool moved = corner != _corner || Math.Abs(offset - _offset) > 0.1;
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
            bool right = corner is 2 or 4;
            foreach (var m in members)
            {
                var row = BuildRow(m, right, style);
                _rows[m.Id] = row;
                _stack.Children.Add(row);
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

    private static Border BuildRow(SpeakerInfo m, bool right, SpeakerStyle style)
    {
        var name = m.Name.Length > 22 ? m.Name[..20] + "…" : m.Name;

        // fallback tile with the first letter, in case the avatar can't be downloaded
        var avatar = new Grid { Width = 30, Height = 30 };
        avatar.Children.Add(new Border
        {
            Background = P5.Red,
            Child = new TextBlock
            {
                Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
                FontFamily = new FontFamily("Arial Black"),
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

        var row = new DockPanel();
        DockPanel.SetDock(avatar, Dock.Left);
        row.Children.Add(avatar);

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
                FontFamily = new FontFamily("Arial Black"),
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
