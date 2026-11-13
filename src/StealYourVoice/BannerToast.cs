using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>
/// Animated over-the-game banner (preset switched, someone joined / left):
/// a coloured shard slashes in, the black plate snaps over it, the text kicks into place,
/// then the whole thing is swept off the screen.
/// </summary>
public sealed class BannerToast : Window
{
    private readonly double _topOffset;
    private readonly Border _shard, _plate, _avatarBox;
    private readonly Image _avatar = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _letter, _label, _title;
    private readonly ScaleTransform _shardScale = new(0, 1), _plateScale = new(0, 1), _avatarScale = new(1, 1);
    private readonly TranslateTransform _slide = new(), _titleMove = new(), _labelMove = new();
    private readonly StackPanel _texts;
    private readonly DispatcherTimer _leave;
    private bool _exitLeft;

    public BannerToast(double topOffset)
    {
        _topOffset = topOffset;
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
        SourceInitialized += (_, _) => MiniOverlay.MakeClickThrough(this);

        var font = new FontFamily("Arial Black, Segoe UI, Segoe UI Symbol");

        _letter = new TextBlock
        {
            FontFamily = font, FontSize = 24, Foreground = P5.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var avatarGrid = new Grid();
        avatarGrid.Children.Add(new Border { Background = P5.Red, Child = _letter });
        avatarGrid.Children.Add(_avatar);
        RenderOptions.SetBitmapScalingMode(_avatar, BitmapScalingMode.HighQuality);
        _avatar.ImageFailed += (_, _) => _avatar.Visibility = Visibility.Collapsed;
        _avatarBox = new Border
        {
            Width = 52, Height = 52, Child = avatarGrid, BorderBrush = P5.White, BorderThickness = new Thickness(3),
            Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { _avatarScale, new RotateTransform(-6) } }
        };

        _label = new TextBlock { FontFamily = font, FontSize = 12, Foreground = P5.Red, RenderTransform = _labelMove };
        _title = new TextBlock
        {
            FontFamily = font, FontStyle = FontStyles.Italic, FontSize = 28, Foreground = P5.White,
            Margin = new Thickness(0, -3, 0, 0), RenderTransform = _titleMove
        };
        _texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _texts.Children.Add(_label);
        _texts.Children.Add(_title);

        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 9, 34, 11) };
        line.Children.Add(_avatarBox);
        line.Children.Add(_texts);

        _shard = new Border
        {
            Margin = new Thickness(16, 12, -16, -12),
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _shardScale
        };
        _plate = new Border
        {
            Background = P5.Black, BorderBrush = P5.White, BorderThickness = new Thickness(3), Child = line,
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _plateScale
        };

        var root = new Grid
        {
            Margin = new Thickness(90, 22, 110, 30),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { new SkewTransform(-13, 0), new RotateTransform(-2), _slide } }
        };
        root.Children.Add(_shard);
        root.Children.Add(_plate);
        Content = root;

        _leave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2100) };
        _leave.Tick += (_, _) =>
        {
            _leave.Stop();
            // swept off the screen
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            _slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, _exitLeft ? -140 : 140, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
            fade.Completed += (_, _) => { if (!_leave.IsEnabled) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    /// <param name="label">small red line, e.g. "ПРЕСЕТ ВКЛЮЧЁН"</param>
    /// <param name="title">the big line</param>
    /// <param name="avatarUrl">picture on the left (null = no picture)</param>
    /// <param name="leaving">true → white shard and exit to the left (someone left); false → red shard, exit to the right</param>
    public void Pop(string label, string title, string? avatarUrl, bool leaving, bool showAvatar)
    {
        _exitLeft = leaving;
        _label.Text = label;
        _label.Foreground = leaving ? P5.Grey : P5.Red;
        _title.Text = title;
        _shard.Background = leaving ? P5.White : P5.Red;

        _avatarBox.Visibility = showAvatar ? Visibility.Visible : Visibility.Collapsed;
        if (showAvatar)
        {
            _letter.Text = title.Length > 0 ? title[..1] : "?";
            var bmp = SpeakersOverlay.LoadAvatar(avatarUrl);
            _avatar.Source = bmp;
            _avatar.Visibility = bmp != null ? Visibility.Visible : Visibility.Collapsed;
        }

        // place first, then animate
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        UpdateLayout();
        var area = NotifyArea.Get(this);
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + _topOffset;

        TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);
        var back = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
        var cubic = new CubicEase { EasingMode = EasingMode.EaseOut };

        _slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(leaving ? 70 : -70, 0, Ms(260)) { EasingFunction = cubic });

        // 1) the shard slashes in
        _shardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, Ms(190)) { EasingFunction = cubic });
        // 2) the black plate snaps over it
        _plateScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, Ms(230)) { BeginTime = Ms(70), EasingFunction = back });

        // 3) text kicks in from the side, picture pops
        _texts.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(140)) { BeginTime = Ms(200) });
        _titleMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-46, 0, Ms(300)) { BeginTime = Ms(200), EasingFunction = back });
        _labelMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(40, 0, Ms(300)) { BeginTime = Ms(200), EasingFunction = back });
        var pop = new DoubleAnimation(0, 1, Ms(320))
            { BeginTime = Ms(160), EasingFunction = new BackEase { Amplitude = 0.9, EasingMode = EasingMode.EaseOut } };
        _avatarScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _avatarScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        _leave.Stop();
        _leave.Start();
    }
}
