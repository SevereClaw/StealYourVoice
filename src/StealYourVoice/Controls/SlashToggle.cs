using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace StealYourVoice.Controls;

/// <summary>Persona 5 style slanted toggle / button.</summary>
public class SlashToggle : FrameworkElement
{
    private bool _checked;
    private bool _hover;
    private bool _pressed;
    private string _text = "";

    public string Text { get => _text; set { _text = value ?? ""; InvalidateMeasure(); InvalidateVisual(); } }

    /// <summary>When false the control behaves as a plain button.</summary>
    public bool IsToggle { get; set; } = true;

    /// <summary>Rotation in degrees, for that hand-cut look.</summary>
    public double Tilt { get; set; }

    public double FontSize { get; set; } = 15;
    public string Hint { get; set; } = "";

    public bool IsChecked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; InvalidateVisual(); }
    }

    public event Action<SlashToggle>? Click;

    public SlashToggle()
    {
        Cursor = Cursors.Hand;
        Height = 36;
        Margin = new Thickness(0, 0, 8, 8);
        Loaded += (_, _) =>
        {
            if (!string.IsNullOrEmpty(Hint)) ToolTip = Hint;
            if (Math.Abs(Tilt) > 0.01) RenderTransform = new RotateTransform(Tilt);
            RenderTransformOrigin = new Point(0.5, 0.5);
        };
    }

    protected override Size MeasureOverride(Size available)
    {
        var ft = P5.Text(this, _text, P5.HeavyItalic, FontSize, P5.White);
        var h = double.IsNaN(Height) ? 36 : Height;
        return new Size(ft.Width + 40, h);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _pressed = true;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        ReleaseMouseCapture();
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight)
        {
            if (IsToggle) IsChecked = !IsChecked;
            Click?.Invoke(this);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { _hover = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = false; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        var r = new Rect(0, 0, ActualWidth, ActualHeight);
        if (r.Width < 4) return;
        dc.DrawRectangle(Brushes.Transparent, null, r);

        bool on = IsToggle ? _checked : _pressed;
        var shift = _pressed ? 2.0 : 0.0;

        // Drop "shadow" plate (offset), Persona menu style
        var shadow = new Rect(r.Left + 5, r.Top + 4, r.Width - 6, r.Height - 5);
        dc.DrawGeometry(on ? P5.White : P5.Red, null, P5.Slant(shadow, 12));

        var face = new Rect(r.Left + shift, r.Top + shift, r.Width - 6, r.Height - 5);
        Brush fill = on ? P5.Red : (_hover ? P5.White : P5.Black);
        Brush fg = on ? P5.White : (_hover ? P5.Black : P5.White);
        dc.DrawGeometry(fill, P5.Pen(P5.White, 2), P5.Slant(face, 12));

        var ft = P5.Text(this, _text, P5.HeavyItalic, FontSize, fg);
        dc.DrawText(ft, new Point(face.Left + (face.Width - ft.Width) / 2, face.Top + (face.Height - ft.Height) / 2));
    }
}
