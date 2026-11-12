using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>Discord (AUX) volume, corner indicator, and presets bound to games.</summary>
public partial class MainWindow
{
    private MiniOverlay? _overlay;
    private bool _friendsTalking;
    private bool _gameScanBusy;
    private string? _activeGame;
    private UserPreset? _beforeGame;

    private static readonly string[] CornerNames = { "ВЫКЛ", "ЛЕВО-ВЕРХ", "ПРАВО-ВЕРХ", "ЛЕВО-НИЗ", "ПРАВО-НИЗ" };

    private int AuxStrip => _vm.HardwareStrips + 1;

    // ───────────────────────── keyboard volume keys → headphones bus ─────────────────────────

    private bool _volKeysOn;
    // Voicemeeter reports a changed value with a delay, so during a fast roll we count from our own number
    private double _volLocal;
    private bool _volMutedLocal;
    private DateTime _volLast = DateTime.MinValue;
    private MicToast? _volToast;
    private const int VolUpId = 0x5360, VolDownId = 0x5361, VolMuteId = 0x5362;

    private static bool DefaultOutputIsVoicemeeter()
    {
        try
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            return dev.FriendlyName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// With Voicemeeter as the default device, Windows' own volume barely does anything,
    /// so we take the volume keys over — but only then, and only while Voicemeeter is running.
    /// </summary>
    private void SyncVolumeKeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool want = _settings.VolumeKeys && _vm.IsConnected && DefaultOutputIsVoicemeeter();
        if (want == _volKeysOn) return;
        if (want)
        {
            RegisterHotKey(_hwnd, VolUpId, 0, 0xAF);
            RegisterHotKey(_hwnd, VolDownId, 0, 0xAE);
            RegisterHotKey(_hwnd, VolMuteId, 0, 0xAD);
        }
        else ReleaseVolumeKeys();
        _volKeysOn = want;
    }

    private void ReleaseVolumeKeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, VolUpId);
        UnregisterHotKey(_hwnd, VolDownId);
        UnregisterHotKey(_hwnd, VolMuteId);
        _volKeysOn = false;
    }

    private bool OnVolumeKey(int id)
    {
        if (id != VolUpId && id != VolDownId && id != VolMuteId) return false;
        if (!_vm.IsConnected) return true;

        bool rolling = (DateTime.Now - _volLast).TotalSeconds < 1.5;
        bool muted = rolling ? _volMutedLocal : _vm.Get("Bus[0].Mute") >= 0.5f;
        string text;
        double level = -1;
        if (id == VolMuteId)
        {
            _vm.Set("Bus[0].Mute", muted ? 0 : 1);
            text = muted ? "ЗВУК ВКЛ" : "ЗВУК ВЫКЛ";
            muted = !muted;
        }
        else
        {
            if (muted) { _vm.Set("Bus[0].Mute", 0); muted = false; }
            double from = rolling ? _volLocal : Math.Round(_vm.Get("Bus[0].Gain") / 2.0) * 2;
            double g = Math.Clamp(from + (id == VolUpId ? 2 : -2), -60, 12);
            _volLocal = g;
            _vm.Set("Bus[0].Gain", (float)g);
            if (!PhonesGainS.IsDragging) PhonesGainS.Value = g;
            text = $"ГРОМКОСТЬ {g:+0;-0;0} dB";
            level = (g + 60) / 72.0;
        }

        if (!rolling && id == VolMuteId) _volLocal = Math.Round(_vm.Get("Bus[0].Gain") / 2.0) * 2;
        _volMutedLocal = muted;
        _volLast = DateTime.Now;

        _volToast ??= new MicToast();
        if (level >= 0) _volToast.FlashLevel(text, level);
        else _volToast.Flash(text, muted);
        return true;
    }

    /// <summary>Which virtual bus is free for CyberCall: B3 exists only on Potato, otherwise B2.</summary>
    private string CaptionsBusName => _vm.Type == Voicemeeter.VmType.Potato ? "B3" : "B2";

    private void ApplyCaptionsBus(bool announce)
    {
        if (!_vm.IsConnected || _vm.Type == Voicemeeter.VmType.Standard) return;
        int vin = _vm.HardwareStrips;
        var bus = CaptionsBusName;
        if (_settings.CaptionsBus)
        {
            // only Discord on that bus: no game, no you; and Discord never into B1 (your mic)
            var script = $"Strip[{AuxStrip}].{bus}=1;Strip[{AuxStrip}].B1=0;Strip[{vin}].{bus}=0;";
            for (int i = 0; i < _vm.HardwareStrips; i++) script += $"Strip[{i}].{bus}=0;";
            _vm.Script(script);
            if (announce)
                StatusText.Text = bus == "B2"
                    ? "B2 = СУБТИТРЫ // SHADOWPLAY И DISCORD: МИКРОФОН = OUT B1"
                    : "B3 = СУБТИТРЫ // CYBERCALL САМ НАЙДЁТ OUT B3";
        }
        else
        {
            _vm.Set($"Strip[{AuxStrip}].{bus}", 0);
            if (announce) StatusText.Text = "ШИНА СУБТИТРОВ ВЫКЛЮЧЕНА";
        }
    }

    private void InitExtras()
    {
        VolKeysT.IsChecked = _settings.VolumeKeys;
        VolKeysT.Click += t => { _settings.VolumeKeys = t.IsChecked; _settings.Save(); SyncVolumeKeys(); };

        // overall loudness: computer sound (Voicemeeter Input) and the headphones bus (A1)
        GameGainS.ValueChanged += v =>
        {
            _ducker?.Restore(); // otherwise a duck in progress would put the old level back
            _vm.Set($"Strip[{_vm.HardwareStrips}].Gain", (float)v);
        };
        PhonesGainS.ValueChanged += v => _vm.Set("Bus[0].Gain", (float)v);

        // Discord / AUX
        AuxGainS.ValueChanged += v => _vm.Set($"Strip[{AuxStrip}].Gain", (float)v);
        DuckFriendsT.IsChecked = _settings.DuckFriends;
        DuckFriendsT.Click += t =>
        {
            _settings.DuckFriends = t.IsChecked;
            _settings.Save();
            if (!t.IsChecked && !_settings.DuckEnabled) _ducker?.Restore();
        };

        // friends' voices into clips (B2) — off by default so recordings stay friend-free
        FriendsClipsT.Click += t =>
        {
            int vin = _vm.HardwareStrips;
            if (t.IsChecked)
            {
                // clips are mixed on B2: game + you + friends; Discord (B1) still gets only your voice
                _vm.Script($"Strip[{AuxStrip}].B2=1;Strip[{AuxStrip}].B1=0;Strip[{vin}].B2=1;Strip[{vin}].B1=0;{S("B2")}=1;");
                StatusText.Text = "ДРУЗЬЯ В КЛИПАХ // SHADOWPLAY: МИКРОФОН = OUT B2, СИСТЕМНЫЕ ЗВУКИ = 0%";
            }
            else
            {
                _vm.Script($"Strip[{AuxStrip}].B2=0;");
                StatusText.Text = "ДРУЗЬЯ НЕ ПИШУТСЯ В КЛИПЫ";
            }
        };

        // a bus that carries only Discord → CyberCall subtitles, never recorded
        CaptionsT.IsChecked = _settings.CaptionsBus;
        CaptionsT.Click += t =>
        {
            _settings.CaptionsBus = t.IsChecked;
            _settings.Save();
            ApplyCaptionsBus(true);
        };

        // corner indicator
        _settings.OverlayCorner = Math.Clamp(_settings.OverlayCorner, 0, 4);
        OverlayBtn.Click += _ =>
        {
            _settings.OverlayCorner = (_settings.OverlayCorner + 1) % 5;
            _settings.OverlayX = _settings.OverlayY = null; // back to a corner
            _settings.Save();
            ApplyOverlay();
        };
        ApplyOverlay();
    }

    private void ApplyOverlay()
    {
        OverlayBtn.Text = "ИНДИКАТОР: " + CornerNames[_settings.OverlayCorner];
        SyncMouseHook();
        if (_settings.OverlayCorner == 0)
        {
            _overlay?.Hide();
            return;
        }
        _settings.OverlayScale = Math.Clamp(_settings.OverlayScale, 0.5, 2.5);
        _overlay ??= new MiniOverlay();
        _overlay.Configure(_settings.OverlayCorner, _settings.OverlayScale,
            _settings.OverlayX.HasValue && _settings.OverlayY.HasValue
                ? new System.Windows.Point(_settings.OverlayX.Value, _settings.OverlayY.Value) : null,
            _settings.SpeakersSilentOpacity);
        if (!_overlay.IsVisible) _overlay.Show();
    }

    /// <summary>Called from the 30 fps timer while connected.</summary>
    private void ExtrasTick()
    {
        bool hasAux = _vm.Type != Voicemeeter.VmType.Standard;

        if (hasAux)
        {
            var (fl, fr) = _vm.StripChannels(AuxStrip, 0); // pre-fader: what Discord sends us
            double auxDb = P5.ToDb(Math.Max(fl, fr));
            _friendsTalking = auxDb > -45;
            TrackLoudness(auxDb);
            if (IsVisible)
            {
                DiscordState.Text = _friendsTalking ? "● ГОВОРЯТ" : "ТИШИНА";
                DiscordState.Foreground = _friendsTalking ? P5.Red : P5.Grey;
            }
        }
        else _friendsTalking = false;

        if (_overlay is { IsVisible: true })
        {
            var (l, r) = _vm.StripChannels(_strip, 2);
            _overlay.Push(Math.Max(l, r), MuteT.IsChecked);
        }

        if (_tick % 15 == 0) FocusTick();
        if (_tick % 90 == 0) GameTick();
    }

    private void RefreshExtras()
    {
        // while ducking lowers the game, the gain we'd read is the temporary one
        bool gameDucked = _ducker is { Active: true, Apps: null };
        if (!gameDucked && !GameGainS.IsDragging) GameGainS.Value = _vm.Get($"Strip[{_vm.HardwareStrips}].Gain");
        // right after a volume key the value Voicemeeter reports is still the old one
        if (!PhonesGainS.IsDragging && (DateTime.Now - _volLast).TotalSeconds > 1)
            PhonesGainS.Value = _vm.Get("Bus[0].Gain");

        bool hasAux = _vm.Type != Voicemeeter.VmType.Standard;
        DiscordPanel.Visibility = hasAux ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (!hasAux) return;
        FriendsClipsT.IsChecked = _vm.Get($"Strip[{AuxStrip}].B2") >= 0.5f;

        // on Banana the subtitles bus is B2, so the B2 clip modes can't be used at the same time
        bool b2Taken = _settings.CaptionsBus && _vm.Type == Voicemeeter.VmType.Banana;
        FriendsClipsT.Visibility = b2Taken ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        if (b2Taken) GameToB2T.Visibility = System.Windows.Visibility.Collapsed;
        CaptionsT.IsChecked = _settings.CaptionsBus;
        // while ducking lowers AUX, the gain we'd read is the temporary one
        bool auxDucked = _ducker is { Active: true } && _settings.DuckEnabled && _settings.DuckAux && !_settings.DuckFriends;
        if (!auxDucked && !AuxGainS.IsDragging) AuxGainS.Value = _vm.Get($"Strip[{AuxStrip}].Gain");
    }

    // ───────────────────────── presets bound to games ─────────────────────────

    private BannerToast? _presetToast;

    /// <summary>Animated "preset switched" banner over whatever is on screen.</summary>
    private void ShowPresetBanner(string label, string title, bool off = false)
    {
        _presetToast ??= new BannerToast(118);
        _presetToast.Pop(label, title.Length > 22 ? title[..21] + "…" : title, null, off, false);
        Audio.Sfx.Play("preset");
    }

    private static string PresetTitle(UserPreset p) =>
        "★ " + p.Name.ToUpperInvariant() + (string.IsNullOrEmpty(p.Process) ? "" : $" [{p.Process.ToUpperInvariant()}]");

    private void AddGameMenuItems(ContextMenu menu, UserPreset preset, SlashToggle button)
    {
        var bind = new MenuItem { Header = "Привязать к программе (включается, когда она активна)..." };
        bind.Click += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ShowBindMenu(preset, button)));
        menu.Items.Add(bind);

        if (!string.IsNullOrEmpty(preset.Process))
        {
            var unbind = new MenuItem { Header = $"Отвязать от {preset.Process}" };
            unbind.Click += (_, _) =>
            {
                preset.Process = null;
                _settings.Save();
                BuildPresets();
            };
            menu.Items.Add(unbind);
        }
    }

    private void ShowBindMenu(UserPreset preset, SlashToggle button)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Открой программу и выбери её из списка:", IsEnabled = false });
        menu.Items.Add(new Separator());

        var apps = WindowedApps();
        if (apps.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Не вижу открытых программ", IsEnabled = false });

        foreach (var (name, title) in apps)
        {
            var proc = name;
            var shortTitle = title.Length > 46 ? title[..44] + "…" : title;
            var mi = new MenuItem { Header = $"{name}  —  {shortTitle}" };
            mi.Click += (_, _) =>
            {
                foreach (var other in _settings.Presets)
                    if (string.Equals(other.Process, proc, StringComparison.OrdinalIgnoreCase)) other.Process = null;
                preset.Process = proc;
                _settings.Save();
                BuildPresets();
                _activeGame = null; // re-evaluate on the next focus check
                StatusText.Text = $"ПРЕСЕТ «{preset.Name.ToUpperInvariant()}» ПРИВЯЗАН К {proc.ToUpperInvariant()}";
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private static List<(string Name, string Title)> WindowedApps()
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "StealYourVoice", "explorer", "TextInputHost", "ApplicationFrameHost", "SystemSettings",
            "voicemeeter", "voicemeeterpro", "voicemeeter8", "voicemeeter8x64", "ShellExperienceHost", "SearchHost"
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                var title = p.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title) || skip.Contains(p.ProcessName) || !seen.Add(p.ProcessName)) continue;
                list.Add((p.ProcessName, title));
            }
            catch { /* access denied */ }
            finally { p.Dispose(); }
        }
        return list.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static HashSet<string> RunningProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { set.Add(p.ProcessName); } catch { /* ignore */ }
            finally { p.Dispose(); }
        }
        return set;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static string? ForegroundProcessName()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    /// <summary>
    /// Twice a second: the program in focus has its own preset → switch to it.
    /// Windows without a preset (desktop, browser, this panel) keep whatever is on.
    /// </summary>
    private void FocusTick()
    {
        if (!_vm.IsConnected) return;
        if (!_settings.Presets.Any(p => !string.IsNullOrEmpty(p.Process))) return;

        var name = ForegroundProcessName();
        if (name == null || string.Equals(name, _activeGame, StringComparison.OrdinalIgnoreCase)) return;
        var hit = _settings.Presets.FirstOrDefault(p => string.Equals(p.Process, name, StringComparison.OrdinalIgnoreCase));
        if (hit == null) return;

        if (_activeGame == null) _beforeGame = CaptureCurrent(""); // what to return to when they all close
        ApplyUserPreset(hit, $"{hit.Process!.ToUpperInvariant()} В ФОКУСЕ");
        _activeGame = hit.Process;
        StatusText.Text = $"{hit.Process!.ToUpperInvariant()} В ФОКУСЕ // ПРЕСЕТ «{hit.Name.ToUpperInvariant()}»";
    }

    /// <summary>Every ~3 s: once every program with a preset is closed, put the old settings back.</summary>
    private async void GameTick()
    {
        if (_gameScanBusy || _activeGame == null) return;
        _gameScanBusy = true;
        try
        {
            var bound = _settings.Presets.Where(p => !string.IsNullOrEmpty(p.Process)).Select(p => p.Process!).ToList();
            var running = await Task.Run(RunningProcessNames);
            if (!_vm.IsConnected || _activeGame == null) return;
            if (bound.Any(running.Contains)) return;

            _activeGame = null;
            if (_beforeGame != null)
            {
                ApplyUserPreset(_beforeGame);
                _beforeGame = null;
                ShowPresetBanner("ПРОГРАММЫ С ПРЕСЕТАМИ ЗАКРЫТЫ", "ПРЕЖНИЕ НАСТРОЙКИ", true);
                StatusText.Text = "ПРОГРАММЫ С ПРЕСЕТАМИ ЗАКРЫТЫ // ВЕРНУЛ ПРЕЖНИЕ НАСТРОЙКИ";
            }
        }
        catch { /* ignore */ }
        finally { _gameScanBusy = false; }
    }
}
