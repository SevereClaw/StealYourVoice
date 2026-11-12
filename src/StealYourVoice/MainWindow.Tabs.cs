using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>Tabs of the main window, and presets that follow the microphone.</summary>
public partial class MainWindow
{
    private SlashToggle[] _tabs = Array.Empty<SlashToggle>();
    private FrameworkElement[] _pages = Array.Empty<FrameworkElement>();

    private void InitTabs()
    {
        _tabs = new[] { TabVoice, TabSound, TabDiscord, TabSettings };
        _pages = new FrameworkElement[] { PageVoice, PageSound, PageDiscord, PageSettings };
        for (int i = 0; i < _tabs.Length; i++)
        {
            int idx = i;
            _tabs[i].Click += _ => SwitchTab(idx, true);
        }
        SwitchTab(Math.Clamp(_settings.Tab, 0, _tabs.Length - 1), false);

        // Ctrl+Tab / Ctrl+1..4 between tabs
        PreviewKeyDown += (_, e) =>
        {
            if (_capturingHotkey || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
            int n = e.Key switch
            {
                System.Windows.Input.Key.D1 => 0, System.Windows.Input.Key.D2 => 1,
                System.Windows.Input.Key.D3 => 2, System.Windows.Input.Key.D4 => 3,
                System.Windows.Input.Key.Tab => (_settings.Tab + 1) % _tabs.Length,
                _ => -1
            };
            if (n < 0) return;
            SwitchTab(n, true);
            e.Handled = true;
        };

        MicPresetT.IsChecked = _settings.MicPresetAuto;
        MicPresetT.Click += t => { _settings.MicPresetAuto = t.IsChecked; _settings.Save(); };
    }

    private void SwitchTab(int idx, bool animate)
    {
        bool changed = idx != _settings.Tab;
        _settings.Tab = idx;
        if (changed) _settings.Save();

        for (int i = 0; i < _tabs.Length; i++)
        {
            _tabs[i].IsChecked = i == idx;
            // Hidden, not Collapsed: the window keeps the height of the tallest page and doesn't jump
            _pages[i].Visibility = i == idx ? Visibility.Visible : Visibility.Hidden;
        }
        if (!animate || !changed) return;

        Audio.Sfx.Play("whoosh");
        var page = _pages[idx];
        var move = new TranslateTransform();
        page.RenderTransform = move;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    // ───────────────────────── presets that follow the microphone ─────────────────────────

    private string? _seenDevice;          // mic on the input at the last refresh
    private string? _presetDevice;        // mic a preset just switched to (so we don't re-apply in a loop)
    private HashSet<string>? _knownMics;  // capture devices Windows had at the last scan

    /// <summary>The preset saved with this mic; with several, the one used most recently.</summary>
    private UserPreset? PresetForMic(string device) =>
        _settings.Presets
            .Where(p => !string.IsNullOrWhiteSpace(p.DeviceName) && string.Equals(p.DeviceName, device, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.LastUsed)
            .FirstOrDefault();

    /// <summary>Called from RefreshParams: the mic on the input changed (in the panel or in Voicemeeter itself).</summary>
    private void CheckMicChanged()
    {
        var dev = _vm.GetString(S("device.name"));
        if (string.IsNullOrWhiteSpace(dev)) return;
        var before = _seenDevice;
        _seenDevice = dev;
        if (before == null || string.Equals(before, dev, StringComparison.OrdinalIgnoreCase)) return; // first look / no change

        if (string.Equals(_presetDevice, dev, StringComparison.OrdinalIgnoreCase)) { _presetDevice = null; return; } // our own switch
        if (!_settings.MicPresetAuto) return;
        var p = PresetForMic(dev);
        if (p != null) ApplyUserPreset(p, "МИКРОФОН СМЕНИЛСЯ");
    }

    /// <summary>Every ~2 s: a mic that has a preset was just plugged in → switch to it with that preset.</summary>
    private async void CheckMicPlugged()
    {
        if (!_vm.IsConnected) return;
        var names = await System.Threading.Tasks.Task.Run(() =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                foreach (var d in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
                {
                    try { set.Add(d.FriendlyName); } catch { /* ignore */ }
                    finally { d.Dispose(); }
                }
            }
            catch { /* audio service busy */ }
            return set;
        });
        if (names.Count == 0) return;

        var before = _knownMics;
        _knownMics = names;
        if (before == null || !_settings.MicPresetAuto) return;

        foreach (var added in names.Where(n => !before.Contains(n)))
        {
            // Voicemeeter's WDM names match Windows' endpoint names; MME ones are cut to 31 characters
            var p = _settings.Presets
                .Where(x => !string.IsNullOrWhiteSpace(x.DeviceName) &&
                            (string.Equals(x.DeviceName, added, StringComparison.OrdinalIgnoreCase) ||
                             added.StartsWith(x.DeviceName, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.LastUsed)
                .FirstOrDefault();
            if (p == null) continue;
            if (string.Equals(_vm.GetString(S("device.name")), p.DeviceName, StringComparison.OrdinalIgnoreCase)) continue;

            // Voicemeeter needs a moment to notice the new device
            for (int i = 0; i < 6; i++)
            {
                if (_vm.InputDevices().Any(d => string.Equals(d.Name, p.DeviceName, StringComparison.OrdinalIgnoreCase))) break;
                await System.Threading.Tasks.Task.Delay(700);
            }
            ApplyUserPreset(p, "МИКРОФОН ПОДКЛЮЧЁН");
            return;
        }
    }
}
