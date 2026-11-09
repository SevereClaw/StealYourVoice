using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using StealYourVoice.Audio;

namespace StealYourVoice;

/// <summary>Persona-style transitions: slash wipes, cards flying in, popping overlays, screen shake.</summary>
public partial class MainWindow
{
    private void InitFx()
    {
        Sfx.Enabled = _settings.SoundsEnabled;
        SfxT.IsChecked = _settings.SoundsEnabled;
        SfxT.Click += t =>
        {
            _settings.SoundsEnabled = t.IsChecked;
            _settings.Save();
            Sfx.Enabled = t.IsChecked;
            if (t.IsChecked) { Sfx.Load(); Sfx.Play("confirm"); }
        };
        Sfx.Load();
        Loaded += (_, _) => PlayEntrance();
    }

    /// <summary>Full intro: slash across the screen, letters slap on, cards fly in from the sides.</summary>
    private void PlayEntrance()
    {
        Wipe.Play();
        Sfx.Play("whoosh");
        TitleText.AnimateIn();
        SlideIn(MicCard, -1, 90);
        SlideIn(OutCard, +1, 170);
    }

    private static void SlideIn(FrameworkElement card, int dir, int delayMs)
    {
        // remember the card's resting tilt once
        if (card.Tag is not double angle)
        {
            angle = card.RenderTransform is RotateTransform r ? r.Angle : 0;
            card.Tag = angle;
        }

        var rot = new RotateTransform(angle);
        var move = new TranslateTransform();
        card.RenderTransform = new TransformGroup { Children = { rot, move } };

        var begin = TimeSpan.FromMilliseconds(delayMs);
        var fly = new DoubleAnimation(dir * 1100, 0, TimeSpan.FromMilliseconds(460))
        {
            BeginTime = begin,
            EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
        };
        move.X = dir * 1100;
        move.BeginAnimation(TranslateTransform.XProperty, fly);

        var tilt = new DoubleAnimation(angle - dir * 9, angle, TimeSpan.FromMilliseconds(520))
        {
            BeginTime = begin,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        rot.BeginAnimation(RotateTransform.AngleProperty, tilt);
    }

    /// <summary>Show an overlay with a slash wipe and a "slapped onto the screen" pop.</summary>
    private void ShowOverlay(Grid overlay)
    {
        overlay.Visibility = Visibility.Visible;
        Wipe.Play();
        Sfx.Play("whoosh");

        if (overlay.Children.Count == 0 || overlay.Children[0] is not FrameworkElement panel) return;
        if (panel.Tag is not double angle)
        {
            angle = panel.RenderTransform is RotateTransform r ? r.Angle : 0;
            panel.Tag = angle;
        }

        var scale = new ScaleTransform(0.6, 0.6);
        var rot = new RotateTransform(angle - 8);
        panel.RenderTransformOrigin = new Point(0.5, 0.5);
        panel.RenderTransform = new TransformGroup { Children = { scale, rot } };

        var begin = TimeSpan.FromMilliseconds(120);
        var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(300))
        {
            BeginTime = begin,
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        rot.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(angle - 8, angle, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { BeginTime = begin };
        panel.Opacity = 0;
        panel.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Short screen shake (mic toggled, big action).</summary>
    private void Shake()
    {
        if (Stage.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            Stage.RenderTransform = tt;
        }
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(220) };
        double[] xs = { -10, 8, -6, 4, -2, 0 };
        for (int i = 0; i < xs.Length; i++)
            a.KeyFrames.Add(new LinearDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(35 * (i + 1)))));
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }
}
