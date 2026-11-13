using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace StealYourVoice;

/// <summary>Which monitor the pop-up notifications (banners, MIC OFF, volume) appear on.</summary>
public static class NotifyArea
{
    /// <summary>0 = main monitor; 1, 2… = the other monitors, left to right.</summary>
    public static int Screen { get; set; }

    /// <summary>Main monitor first, then the rest ordered left to right.</summary>
    public static System.Windows.Forms.Screen[] Ordered()
    {
        var all = System.Windows.Forms.Screen.AllScreens;
        return all.Where(s => s.Primary).Concat(all.Where(s => !s.Primary).OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)).ToArray();
    }

    public static int Count => System.Windows.Forms.Screen.AllScreens.Length;

    /// <summary>Work area of the chosen monitor in WPF units (falls back to the main one if it was unplugged).</summary>
    public static Rect Get(Visual v)
    {
        try
        {
            var list = Ordered();
            var s = Screen >= 0 && Screen < list.Length ? list[Screen] : list[0];
            var wa = s.WorkingArea;
            double k = 1;
            var src = PresentationSource.FromVisual(v);
            if (src?.CompositionTarget != null) k = src.CompositionTarget.TransformFromDevice.M11;
            return new Rect(wa.Left * k, wa.Top * k, wa.Width * k, wa.Height * k);
        }
        catch { return SystemParameters.WorkArea; }
    }
}
