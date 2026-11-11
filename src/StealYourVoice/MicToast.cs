using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>Small always-on-top "MIC OFF / MIC ON" plate, Persona 5 style.</summary>
public sealed class MicToast : Window
{
    private readonly Border _plate;
    private readonly TextBlock _text;
    private readonly DispatcherTimer _hide;
    private const double BarWidth = 260;
    private readonly Border _bar;
    private readonly ScaleTransform _barFill = new(0, 1);
    private readonly ScaleTransform _pop = new(1, 1);
    private double _lastLevel = -1;

    public MicToast()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;

        _text = new TextBlock
        {
            FontFamily = new FontFamily("Arial Black"),
            FontStyle = FontStyles.Italic,
            FontSize = 30,
            Margin = new Thickness(28, 6, 34, 8)
        };
        // level bar for volume changes (hidden for plain messages)
        var barGrid = new Grid();
        barGrid.Children.Add(new Border { Background = P5.Coal });
        barGrid.Children.Add(new Border
        {
            Background = P5.Red, RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _barFill
        });
        // the 0 dB mark sits at 60/72 of the range
        barGrid.Children.Add(new Border
        {
            Width = 3, Background = P5.Black, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(BarWidth * 60 / 72, 0, 0, 0)
        });
        _bar = new Border
        {
            Child = barGrid, Width = BarWidth, Height = 12, BorderBrush = P5.Black, BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(28, 0, 34, 12),
            Visibility = Visibility.Collapsed
        };
        _text.RenderTransformOrigin = new Point(0.5, 0.5);
        _text.RenderTransform = _pop;
        var panel = new StackPanel();
        panel.Children.Add(_text);
        panel.Children.Add(_bar);

        _plate = new Border
        {
            Child = panel,
            BorderThickness = new Thickness(4),
            Margin = new Thickness(30, 16, 30, 16),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup
            {
                Children = { new SkewTransform(-14, 0), new RotateTransform(-3) }
            }
        };
        Content = _plate;

        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
            fade.Completed += (_, _) => Hide();
            BeginAnimation(OpacityProperty, fade);
        };
    }

    public void Flash(bool muted) => Flash(muted ? "MIC OFF" : "MIC ON", muted);

    /// <summary>Volume change: the bar slides to the new level and the text kicks up or down.</summary>
    public void FlashLevel(string message, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        bool up = _lastLevel < 0 || fraction >= _lastLevel;
        _lastLevel = fraction;
        Flash((up ? "▲ " : "▼ ") + message, false);
        _bar.Visibility = Visibility.Visible;
        _barFill.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fraction, TimeSpan.FromMilliseconds(140))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var pop = new DoubleAnimation(up ? 1.18 : 0.86, 1.0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
        _pop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _pop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    public void Flash(string message, bool red)
    {
        _bar.Visibility = Visibility.Collapsed;
        _text.Text = message;
        _text.Foreground = red ? P5.White : P5.Black;
        _plate.Background = red ? P5.Red : P5.White;
        _plate.BorderBrush = P5.Black;

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        UpdateLayout();

        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 40;

        _hide.Stop();
        _hide.Start();
    }
}
