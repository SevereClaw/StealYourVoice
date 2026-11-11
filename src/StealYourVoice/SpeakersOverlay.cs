using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>Click-through list of who is talking in Discord right now, shown over the game.</summary>
public sealed class SpeakersOverlay : Window
{
    private readonly StackPanel _stack = new() { Margin = new Thickness(8, 2, 8, 2) };
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
        Opacity = 0.92;
        Content = _stack;
        SourceInitialized += (_, _) => MiniOverlay.MakeClickThrough(this);
        SizeChanged += (_, _) => Place();
    }

    /// <param name="corner">1 = top-left, 2 = top-right, 3 = bottom-left, 4 = bottom-right</param>
    /// <param name="offset">vertical gap from the screen edge (to sit next to the mic indicator)</param>
    public void Set(IReadOnlyList<string> names, int corner, double offset)
    {
        _corner = corner;
        _offset = offset;
        _stack.Children.Clear();
        if (names.Count == 0) { Hide(); return; }

        bool right = corner is 2 or 4;
        foreach (var n in names)
        {
            var name = n.Length > 22 ? n[..20] + "…" : n;
            var text = new TextBlock
            {
                Text = name.ToUpperInvariant(),
                FontFamily = new FontFamily("Arial Black"),
                FontStyle = FontStyles.Italic,
                FontSize = 14,
                Foreground = P5.White,
                Margin = new Thickness(10, 2, 14, 3)
            };
            var row = new DockPanel();
            var bar = new Border { Width = 8, Background = P5.Red };
            DockPanel.SetDock(bar, Dock.Left);
            row.Children.Add(bar);
            row.Children.Add(text);
            _stack.Children.Add(new Border
            {
                Child = row,
                Background = P5.Black,
                BorderBrush = P5.White,
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                RenderTransform = new SkewTransform(-12, 0)
            });
        }

        if (!IsVisible) Show();
        UpdateLayout();
        Place();
    }

    private void Place()
    {
        var a = SystemParameters.WorkArea;
        const double m = 10;
        Left = _corner is 1 or 3 ? a.Left + m : a.Right - ActualWidth - m;
        Top = _corner is 1 or 2 ? a.Top + m + _offset : a.Bottom - ActualHeight - m - _offset;
    }
}
