using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StealYourVoice.Controls;

/// <summary>"Calling card" ransom-note lettering: every letter cut from a different magazine.</summary>
public class RansomTitle : WrapPanel
{
    private static readonly string[] Fonts =
        { "Arial Black", "Impact", "Times New Roman", "Georgia", "Courier New", "Segoe UI Black", "Trebuchet MS" };

    private string _text = "";
    public int Seed { get; set; } = 5;
    public double LetterSize { get; set; } = 34;

    public string Text
    {
        get => _text;
        set { _text = value ?? ""; Build(); }
    }

    public RansomTitle()
    {
        Orientation = Orientation.Horizontal;
        IsHitTestVisible = false;
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        Children.Clear();
        var rnd = new Random(Seed);
        foreach (var ch in _text)
        {
            if (ch == ' ')
            {
                Children.Add(new Border { Width = LetterSize * 0.4 });
                continue;
            }

            int style = rnd.Next(3);
            Brush bg = style switch { 0 => P5.White, 1 => P5.Black, _ => P5.Red };
            Brush fg = style == 0 ? P5.Black : P5.White;
            double size = LetterSize * (0.8 + rnd.NextDouble() * 0.45);

            var tb = new TextBlock
            {
                Text = (rnd.Next(4) == 0 ? char.ToLowerInvariant(ch) : ch).ToString(),
                FontFamily = new FontFamily(Fonts[rnd.Next(Fonts.Length)]),
                FontWeight = rnd.Next(2) == 0 ? FontWeights.Black : FontWeights.Bold,
                FontSize = size,
                Foreground = fg,
                Margin = new Thickness(5, -2, 5, -2),
            };

            var b = new Border
            {
                Background = bg,
                BorderBrush = style == 1 ? P5.White : null,
                BorderThickness = new Thickness(style == 1 ? 2 : 0),
                Child = tb,
                Margin = new Thickness(-2, rnd.Next(0, 9), -2, 0),
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(rnd.Next(-12, 13)),
            };
            Panel.SetZIndex(b, rnd.Next(10));
            Children.Add(b);
        }
    }
}
