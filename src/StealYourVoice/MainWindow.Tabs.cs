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

        FollowVoiceT.IsChecked = _settings.FollowVoice;
        FollowVoiceT.Click += t =>
        {
            _settings.FollowVoice = t.IsChecked;
            _settings.Save();
            if (!t.IsChecked) { _micWatcher?.Dispose(); _micWatcher = null; }
            StatusText.Text = t.IsChecked
                ? "СЛУШАЮ МИКРОФОНЫ С ПРЕСЕТАМИ // ЗАГОВОРИШЬ В ДРУГОЙ — ПЕРЕКЛЮЧУСЬ"
                : "ПЕРЕКЛЮЧЕНИЕ ПО ГОЛОСУ ВЫКЛЮЧЕНО";
        };

        NotifyArea.Screen = Math.Max(0, _settings.NotifyScreen);
        UpdateNotifyScreenBtn();
        NotifyScreenBtn.Click += _ =>
        {
            int n = NotifyArea.Count;
            _settings.NotifyScreen = n <= 1 ? 0 : (_settings.NotifyScreen + 1) % n;
            _settings.Save();
            NotifyArea.Screen = _settings.NotifyScreen;
            UpdateNotifyScreenBtn();
            _warnToast ??= new BannerToast(200);
            _warnToast.Pop("УВЕДОМЛЕНИЯ БУДУТ ЗДЕСЬ", _settings.NotifyScreen == 0 ? "ОСНОВНОЙ МОНИТОР" : $"МОНИТОР {_settings.NotifyScreen + 1}", null, false, false);
        };

        TwoMicT.IsChecked = _settings.TwoMicWarn;
        TwoMicT.Click += t => { _settings.TwoMicWarn = t.IsChecked; _settings.Save(); };

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
        if (idx == 1) AppsTick(); // fill the program list right away
        if (!animate || !changed) return;

        Audio.Sfx.Play("tab");
        var page = _pages[idx];
        var move = new TranslateTransform();
        page.RenderTransform = move;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private BannerToast? _warnToast;

    private void UpdateNotifyScreenBtn()
    {
        int n = NotifyArea.Count;
        NotifyScreenBtn.Text = n <= 1 ? "УВЕДОМЛЕНИЯ: МОНИТОР ОДИН"
            : _settings.NotifyScreen <= 0 || _settings.NotifyScreen >= n ? "УВЕДОМЛЕНИЯ: ОСНОВНОЙ МОНИТОР"
            : $"УВЕДОМЛЕНИЯ: МОНИТОР {_settings.NotifyScreen + 1}";
    }

    // ───────────────────────── two microphones heard at once ─────────────────────────

    private DateTime _twoMicSince = DateTime.MinValue, _twoMicWarned = DateTime.MinValue;

    /// <summary>~10 times a second: two Voicemeeter inputs with a device both carry voice into Discord / recording.</summary>
    private void TwoMicTick()
    {
        if (!_settings.TwoMicWarn || !_vm.IsConnected) return;
        if (_routedStrips == null || _routedStrips.Length != _vm.HardwareStrips) RefreshRoutedStrips();
        int routed = 0;
        foreach (var r in _routedStrips!) if (r) routed++;
        if (routed < 2) { _twoMicSince = DateTime.MinValue; return; } // can't be two — skip the meters entirely

        var live = new List<int>();
        for (int i = 0; i < _routedStrips.Length; i++)
            if (_routedStrips[i] && Controls.P5.ToDb(_vm.StripLevel(i)) > -45) live.Add(i);

        var now = DateTime.Now;
        if (live.Count < 2) { _twoMicSince = DateTime.MinValue; return; }
        if (_twoMicSince == DateTime.MinValue) { _twoMicSince = now; return; }
        if ((now - _twoMicSince).TotalMilliseconds < 1200 || (now - _twoMicWarned).TotalSeconds < 60) return;

        _twoMicWarned = now;
        var names = live.Select(i =>
        {
            var d = _vm.GetString($"Strip[{i}].device.name");
            return string.IsNullOrWhiteSpace(d) ? $"ВХОД {i + 1}" : d;
        }).ToList();
        _warnToast ??= new BannerToast(200);
        _warnToast.Pop("ДВА МИКРОФОНА СЛЫШНО ОДНОВРЕМЕННО", "ВХОДЫ " + string.Join(" И ", live.Select(i => (i + 1).ToString())), null, false, false);
        Audio.Sfx.Play("cancel");
        StatusText.Text = "ДВА МИКРОФОНА В DISCORD / ЗАПИСИ: " + string.Join(" + ", names).ToUpperInvariant();
        _tray?.ShowBalloonTip(3000, "Steal Your Voice",
            "Одновременно слышно два микрофона: " + string.Join(" и ", names) + ". Выключи B1/B2 или MUTE на лишнем входе.",
            System.Windows.Forms.ToolTipIcon.Warning);
    }

    private bool[]? _routedStrips;

    /// <summary>Which hardware inputs have a device, aren't muted and go to B1/B2. Re-read only when Voicemeeter reports changes.</summary>
    private void RefreshRoutedStrips()
    {
        int n = _vm.HardwareStrips;
        var r = new bool[n];
        for (int i = 0; i < n; i++)
            r[i] = _vm.Get($"Strip[{i}].Mute") < 0.5f
                   && (_vm.Get($"Strip[{i}].B1") >= 0.5f || _vm.Get($"Strip[{i}].B2") >= 0.5f)
                   && !string.IsNullOrWhiteSpace(_vm.GetString($"Strip[{i}].device.name"));
        _routedStrips = r;
    }

    // ───────────────────────── switch to the mic you are talking into ─────────────────────────

    private Audio.MicWatcher? _micWatcher;
    private DateTime _lastVoiceSwitch = DateTime.MinValue;

    private static bool SameMic(string windowsName, string vmName) =>
        string.Equals(windowsName, vmName, StringComparison.OrdinalIgnoreCase) ||
        windowsName.StartsWith(vmName, StringComparison.OrdinalIgnoreCase); // MME names are cut to 31 characters

    /// <summary>~10 times a second.</summary>
    private void FollowVoiceTick()
    {
        if (!_settings.FollowVoice || !_vm.IsConnected) return;
        _micWatcher ??= new Audio.MicWatcher();

        var current = _vm.GetString(S("device.name"));

        // every ~3 s: listen to every plugged-in mic that has a preset, except the one Voicemeeter already uses
        if (_tick % 90 == 0)
        {
            var wanted = new List<string>();
            foreach (var n in _knownMics ?? new HashSet<string>())
            {
                if (!string.IsNullOrWhiteSpace(current) && SameMic(n, current)) continue;
                if (_settings.Presets.Any(p => !string.IsNullOrWhiteSpace(p.DeviceName) && SameMic(n, p.DeviceName))) wanted.Add(n);
            }
            _micWatcher.Sync(wanted);
        }

        if ((DateTime.Now - _lastVoiceSwitch).TotalSeconds < 3) return; // let the new mic settle

        var (cl, cr) = _vm.StripChannels(_strip, 0); // what the active mic hears right now
        double currentDb = Controls.P5.ToDb(Math.Max(cl, cr));
        var now = DateTime.Now;

        foreach (var (name, peak) in _micWatcher.Read())
        {
            double db = Controls.P5.ToDb(peak);
            // clearly talking into THIS mic: loud enough, and much louder than the active one (not just room bleed)
            bool loud = db > -38 && db - currentDb > 10;
            if (!loud) { _micWatcher.SetLoudSince(name, DateTime.MinValue); continue; }

            var since = _micWatcher.LoudSince(name);
            if (since == DateTime.MinValue) { _micWatcher.SetLoudSince(name, now); continue; }
            if ((now - since).TotalMilliseconds < 350) continue; // a word, not a bump on the desk

            var p = _settings.Presets
                .Where(x => !string.IsNullOrWhiteSpace(x.DeviceName) && SameMic(name, x.DeviceName))
                .OrderByDescending(x => x.LastUsed)
                .FirstOrDefault();
            if (p == null) continue;

            _lastVoiceSwitch = now;
            _micWatcher.SetLoudSince(name, DateTime.MinValue);
            ApplyUserPreset(p, "ГОВОРИШЬ В ДРУГОЙ МИКРОФОН");
            _micWatcher.Sync(Array.Empty<string>()); // re-pick the listened set right away next round
            return;
        }
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
        // Windows tells us when a device is plugged / unplugged — no need to scan otherwise
        if (_knownMics != null && !Audio.AudioWatch.DevicesDirty) return;
        Audio.AudioWatch.DevicesDirty = false;
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
