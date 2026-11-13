using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using StealYourVoice.Audio;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>Volume of each program separately (Windows mixer, inside the panel).</summary>
public partial class MainWindow
{
    private sealed class AppRow
    {
        public AppAudio App = null!;
        public SlashSlider Slider = null!;
        public SlashToggle Mute = null!;
        public DateTime Touched = DateTime.MinValue;
    }

    private readonly Dictionary<string, AppRow> _appRows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every ~2 s while the ЗВУК tab is on screen.</summary>
    private void AppsTick()
    {
        if (!IsVisible || PageSound.Visibility != Visibility.Visible) return;

        var apps = AppMixer.List();
        bool same = apps.Count == _appRows.Count && apps.All(a => _appRows.ContainsKey(a.Process));
        if (!same)
        {
            _appRows.Clear();
            AppsPanel.Children.Clear();
            foreach (var a in apps)
            {
                var row = new AppRow { App = a };
                var name = a.Title.Length > 30 ? a.Title[..28] + "…" : a.Title;
                row.Slider = new SlashSlider
                {
                    Label = name.ToUpperInvariant(), Minimum = 0, Maximum = 100, Default = 100, Step = 2,
                    Unit = "%", Decimals = 0, Hint = $"Громкость {a.Title} ({a.Process}.exe). Двойной клик — 100%"
                };
                row.Mute = new SlashToggle
                {
                    Text = "MUTE", FontSize = 12, Height = 32, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(14, 0, 0, 2), Hint = "Выключить звук этой программы"
                };
                row.Slider.ValueChanged += v =>
                {
                    row.Touched = DateTime.Now;
                    row.App.Volume = (float)(v / 100.0);
                };
                row.Mute.Click += t =>
                {
                    row.Touched = DateTime.Now;
                    row.App.Mute = t.IsChecked;
                };

                var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                DockPanel.SetDock(row.Mute, Dock.Right);
                dock.Children.Add(row.Mute);
                dock.Children.Add(row.Slider);
                AppsPanel.Children.Add(dock);
                _appRows[a.Process] = row;
            }
            AppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var a in apps)
        {
            if (!_appRows.TryGetValue(a.Process, out var row)) continue;
            row.App = a; // fresh session handles
            // don't snap a slider back while it's being used or right after
            if (row.Slider.IsDragging || (DateTime.Now - row.Touched).TotalSeconds < 2) continue;
            row.Slider.Value = Math.Round(a.Volume * 100);
            row.Mute.IsChecked = a.Mute;
        }
    }
}
