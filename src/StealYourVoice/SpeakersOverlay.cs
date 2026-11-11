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

/// <summary>
/// Click-through list of everyone in the Discord voice channel, shown over the game:
/// avatar + name, see-through while silent, solid while talking.
/// </summary>
public sealed class SpeakersOverlay : Window
{
    private const double SilentOpacity = 0.28;

    private readonly StackPanel _stack = new() { Margin = new Thickness(8, 2, 8, 2) };
    private readonly Dictionary<string, Border> _rows = new();
    private readonly Dictionary<string, bool> _lit = new();
    private static readonly Dictionary<string, BitmapImage> AvatarCache = new();
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
        Content = _stack;
        SourceInitialized += (_, _) => MiniOverlay.MakeClickThrough(this);
        SizeChanged += (_, _) => Place();
    }

    /// <param name="corner">1 = top-left, 2 = top-right, 3 = bottom-left, 4 = bottom-right</param>
    /// <param name="offset">vertical gap from the screen edge (to sit next to the mic indicator)</param>
    public void SetMembers(IReadOnlyList<(string Id, string Name, string? Avatar)> members, int corner, double offset)
    {
        var signature = corner + "|" + string.Join("|", members.Select(m => m.Id + ":" + m.Name + ":" + m.Avatar));
        bool moved = corner != _corner || Math.Abs(offset - _offset) > 0.1;
        _corner = corner;
        _offset = offset;

        if (signature != _signature)
        {
            _signature = signature;
            _stack.Children.Clear();
            _rows.Clear();
            _lit.Clear();
            bool right = corner is 2 or 4;
            foreach (var m in members)
            {
                var row = BuildRow(m.Id, m.Name, m.Avatar, right);
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
            kv.Value.BeginAnimation(OpacityProperty,
                new DoubleAnimation(on ? 1.0 : SilentOpacity, TimeSpan.FromMilliseconds(on ? 70 : 260)));
        }
    }

    private static Border BuildRow(string id, string fullName, string? avatarUrl, bool right)
    {
        var name = fullName.Length > 22 ? fullName[..20] + "…" : fullName;

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
        var bmp = LoadAvatar(avatarUrl);
        if (bmp != null)
        {
            var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
            img.ImageFailed += (_, _) => img.Visibility = Visibility.Collapsed;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            avatar.Children.Add(img);
        }

        var text = new TextBlock
        {
            Text = name.ToUpperInvariant(),
            FontFamily = new FontFamily("Arial Black"),
            FontStyle = FontStyles.Italic,
            FontSize = 14,
            Foreground = P5.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 14, 1)
        };

        var row = new DockPanel();
        DockPanel.SetDock(avatar, Dock.Left);
        row.Children.Add(avatar);
        row.Children.Add(text);

        return new Border
        {
            Child = row,
            Background = P5.Black,
            BorderBrush = P5.White,
            BorderThickness = new Thickness(2),
            Margin = new Thickness(0, 2, 0, 2),
            Opacity = SilentOpacity,
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
}
