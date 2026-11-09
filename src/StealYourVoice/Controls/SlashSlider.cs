using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace StealYourVoice.Controls;

/// <summary>Persona 5 style slanted slider, fully custom drawn.</summary>
public class SlashSlider : FrameworkElement
{
    private const double Slant = 14;
    private const double BarTop = 26;
    private double _value;
    private bool _hover;

    public string Label { get; set; } = "";
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 10;
    public double Default { get; set; }
    public double Step { get; set; } = 0.5;
    public string Unit { get; set; } = "";
    public int Decimals { get; set; } = 1;
    public string Hint { get; set; } = "";

    public bool IsDragging { get; private set; }

    public double Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, Minimum, Maximum);
            if (Math.Abs(v - _value) < 0.0001) return;
            _value = v;
            InvalidateVisual();
        }
    }

    /// <summary>Raised only for user-initiated changes.</summary>
    public event Action<double>? ValueChanged;

    public SlashSlider()
    {
        Height = 60;
        Cursor = Cursors.Hand;
        Focusable = false;
        Loaded += (_, _) => { if (!string.IsNullOrEmpty(Hint)) ToolTip = Hint; };
    }

    private void SetFromUser(double v)
    {
        var old = _value;
        Value = v;
        if (Math.Abs(old - _value) > 0.0001) ValueChanged?.Invoke(_value);
    }

    private Rect BarRect => new(0, BarTop, Math.Max(ActualWidth, 1), Math.Max(ActualHeight - BarTop - 4, 8));

    private double ValueFromX(double x)
    {
        var r = BarRect;
        var t = Math.Clamp((x - r.Left - Slant / 2) / (r.Width - Slant), 0, 1);
        var v = Minimum + t * (Maximum - Minimum);
        return Step > 0 ? Math.Round(v / Step) * Step : v;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { SetFromUser(Default); e.Handled = true; return; }
        IsDragging = true;
        CaptureMouse();
        SetFromUser(ValueFromX(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsDragging) SetFromUser(ValueFromX(e.GetPosition(this).X));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        IsDragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => IsDragging = false;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        SetFromUser(_value + Math.Sign(e.Delta) * (Step > 0 ? Step : (Maximum - Minimum) / 50));
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { _hover = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = false; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        if (w < 20) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-test area

        // Label tag: white slanted plate with black text
        var label = P5.Text(this, Label, P5.HeavyItalic, 13, P5.Black);
        var tag = new Rect(0, 2, label.Width + 26, 20);
        dc.DrawGeometry(_hover ? P5.Red : P5.White, null, P5.Slant(tag, 8));
        if (_hover) label.SetForegroundBrush(P5.White);
        dc.DrawText(label, new Point(12, 3));

        // Value text on the right
        var valStr = _value.ToString("F" + Decimals) + Unit;
        var val = P5.Text(this, valStr, P5.Impact, 22, P5.White);
        dc.DrawText(val, new Point(w - val.Width - 4, -2));

        // Bar
        var r = BarRect;
        dc.DrawGeometry(P5.Coal, P5.Pen(P5.White, 2), P5.Slant(r, Slant));

        var t = (Maximum - Minimum) <= 0 ? 0 : (_value - Minimum) / (Maximum - Minimum);
        var fillW = Slant + (r.Width - Slant) * t;
        if (fillW > Slant + 1)
        {
            var inner = new Rect(r.Left + 3, r.Top + 3, Math.Max(fillW - 6, 1), r.Height - 6);
            dc.DrawGeometry(P5.Red, null, P5.Slant(inner, Slant - 2));
        }

        // Thumb: a white jagged slash
        var x = r.Left + fillW - Slant / 2;
        var thumb = P5.Poly(
            new Point(x + 4, r.Top - 6),
            new Point(x + 14, r.Top - 6),
            new Point(x - 2, r.Bottom + 6),
            new Point(x - 12, r.Bottom + 6));
        dc.DrawGeometry(P5.White, P5.Pen(P5.Black, 2), thumb);
    }
}
