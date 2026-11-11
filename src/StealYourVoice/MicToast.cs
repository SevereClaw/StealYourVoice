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
        _plate = new Border
        {
            Child = _text,
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

    public void Flash(string message, bool red)
    {
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
