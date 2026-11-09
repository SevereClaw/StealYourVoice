using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using StealYourVoice.Controls;
using StealYourVoice.Voicemeeter;

namespace StealYourVoice;

public partial class MainWindow : Window
{
    private readonly VmClient _vm = new();
    private readonly DispatcherTimer _timer;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _trayMute;
    private int _strip;
    private int _tick;
    private bool _exiting;
    private bool _balloonShown;
    private readonly Updater _updater = new();
    private readonly DispatcherTimer _updateTimer;
    private bool _updating;

    private readonly Settings _settings = Settings.Load();
    private MicToast? _toast;
    private Audio.Ducker? _ducker;
    private bool _capturingHotkey;
    private IntPtr _hwnd;
    private const int HotkeyId = 0x5359;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private string S(string p) => $"Strip[{_strip}].{p}";
    private string B1(string p) => $"Bus[{_vm.B1Index}].{p}";

    public MainWindow()
    {
        InitializeComponent();
        _strip = Math.Max(0, _settings.Strip);

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };

        MinBtn.Click += _ => HideToTray();
        CloseBtn.Click += _ => ExitApp();
        OfflineCloseBtn.Click += _ => ExitApp();
        LaunchBtn.Click += _ => { OfflineText.Text = "Запускаю Voicemeeter..."; _vm.Launch(); };

        // mic strip
        GainS.ValueChanged += v => _vm.Set(S("Gain"), (float)v);
        GateS.ValueChanged += v => _vm.Set(S("Gate"), (float)v);
        CompS.ValueChanged += v => _vm.Set(S("Comp"), (float)v);
        ToneS.ValueChanged += v => _vm.Set(S("Color_x"), (float)(v / 10.0));
        MonoT.Click += t => _vm.Set(S("Mono"), t.IsChecked ? 1 : 0);
        MuteT.Click += t => { _vm.Set(S("Mute"), t.IsChecked ? 1 : 0); UpdateTrayMute(); };
        A1T.Click += t =>
        {
            _vm.Set(S("A1"), t.IsChecked ? 1 : 0);
            // A1 without a hardware device = silence. Ask where the headphones are.
            if (t.IsChecked && string.IsNullOrWhiteSpace(_vm.GetString("Bus[0].device.name")))
                ShowHeadphonesMenu("Выбери наушники — без них A1 молчит");
        };
        HeadBtn.Click += _ => ShowHeadphonesMenu(null);
        B1T.Click += t => _vm.Set(S("B1"), t.IsChecked ? 1 : 0);
        DeviceBtn.Click += _ => ShowDeviceMenu();

        // output bus
        OutGainS.ValueChanged += v => _vm.Set(B1("Gain"), (float)v);
        OutMuteT.Click += t => _vm.Set(B1("Mute"), t.IsChecked ? 1 : 0);

        InitTools();
        InitDucking();

        // presets
        BuildPresets();
        SaveOk.Click += _ => SaveNewPreset();
        SaveCancel.Click += _ => SaveOverlay.Visibility = Visibility.Collapsed;
        PresetNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) SaveNewPreset();
            else if (e.Key == Key.Escape) SaveOverlay.Visibility = Visibility.Collapsed;
        };

        // bottom bar: hotkey / toast / autostart
        var cur = Updater.Current;
        VersionText.Text = $"v{cur.Major}.{cur.Minor}.{Math.Max(cur.Build, 0)}";
        ToastT.IsChecked = _settings.ShowToast;
        ToastT.Click += t => { _settings.ShowToast = t.IsChecked; _settings.Save(); };
        AutostartT.IsChecked = IsAutostart();
        AutostartT.Click += t => { SetAutostart(t.IsChecked); t.IsChecked = IsAutostart(); };
        HotkeyBtn.Click += _ => StartHotkeyCapture();
        PreviewKeyDown += OnPreviewKeyDown;

        ShowVmBtn.Click += _ => _vm.Script("Command.Show=1;");
        RestartBtn.Click += _ => _vm.Script("Command.Restart=1;");

        SetupTray();

        // auto-update: first check shortly after start, then every 6 hours
        UpdateBtn.Click += _ => ApplyUpdate();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            await CheckForUpdates();
        };
        _updateTimer.Start();

        _timer =new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        _timer.Start();

        // window handle exists even when we start hidden in the tray
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterMuteHotkey();
    }

    // ───────────────────────── polling ─────────────────────────

    private void OnTick(object? sender, EventArgs e)
    {
        _tick++;

        if (!_vm.IsConnected)
        {
            if (_tick % 30 == 1)
            {
                if (_vm.Connect()) OnConnected();
                else ShowOffline(_vm.LastError);
            }
            return;
        }

        if (_vm.IsDirty()) RefreshParams();
        if (!_vm.IsConnected) { ShowOffline(_vm.LastError); return; }

        if (IsVisible)
        {
            MicMeter.Push(_vm.StripLevel(_strip));
            OutMeter.Push(_vm.BusLevel(_vm.B1Index));
        }

        ToolTick();
        DuckTick();
    }

    private void OnConnected()
    {
        if (_strip >= _vm.HardwareStrips) _strip = 0;
        BuildStripButtons();
        RefreshParams();
        Offline.Visibility = Visibility.Collapsed;
        StatusText.Text = $"VOICEMEETER {_vm.Type.ToString().ToUpperInvariant()} // НА СВЯЗИ";
    }

    private void ShowOffline(string reason)
    {
        Offline.Visibility = Visibility.Visible;
        OfflineText.Text = string.IsNullOrEmpty(reason) ? "Voicemeeter не запущен." : reason;
        StatusText.Text = "НЕТ СВЯЗИ С VOICEMEETER";
    }

    private void RefreshParams()
    {
        if (!_vm.IsConnected) return;

        SetSlider(GainS, _vm.Get(S("Gain")));
        SetSlider(GateS, _vm.Get(S("Gate")));
        SetSlider(CompS, _vm.Get(S("Comp")));
        SetSlider(ToneS, _vm.Get(S("Color_x")) * 10.0);
        MonoT.IsChecked = _vm.Get(S("Mono")) >= 0.5f;
        MuteT.IsChecked = _vm.Get(S("Mute")) >= 0.5f;
        A1T.IsChecked = _vm.Get(S("A1")) >= 0.5f;
        B1T.IsChecked = _vm.Get(S("B1")) >= 0.5f;

        SetSlider(OutGainS, _vm.Get(B1("Gain")));
        OutMuteT.IsChecked = _vm.Get(B1("Mute")) >= 0.5f;
        GameToB1T.IsChecked = _vm.Get($"Strip[{_vm.HardwareStrips}].B1") >= 0.5f;
        DuckAuxT.Visibility = _vm.Type == VmType.Standard ? Visibility.Collapsed : Visibility.Visible;

        var dev = _vm.GetString(S("device.name"));
        if (dev.Length > 42) dev = dev[..40] + "…";
        DeviceBtn.Text = "УСТРОЙСТВО: " + (string.IsNullOrWhiteSpace(dev) ? "НЕ ВЫБРАНО" : dev.ToUpperInvariant());

        var head = _vm.GetString("Bus[0].device.name");
        if (head.Length > 40) head = head[..38] + "…";
        HeadBtn.Text = "НАУШНИКИ (A1): " + (string.IsNullOrWhiteSpace(head) ? "НЕ ВЫБРАНЫ ⚠" : head.ToUpperInvariant());

        UpdateTrayMute();
    }

    private static void SetSlider(SlashSlider s, double v)
    {
        if (!s.IsDragging) s.Value = v;
    }

    // ───────────────────────── strips / devices ─────────────────────────

    private void BuildStripButtons()
    {
        StripPanel.Children.Clear();
        StripPanel.Children.Add(new TextBlock
        {
            Text = "ВХОД:",
            Foreground = P5.Grey,
            FontFamily = new System.Windows.Media.FontFamily("Arial Black"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 8)
        });

        for (int i = 0; i < _vm.HardwareStrips; i++)
        {
            int idx = i;
            var label = _vm.GetString($"Strip[{i}].Label");
            var t = new SlashToggle
            {
                Text = $"{i + 1}" + (string.IsNullOrWhiteSpace(label) ? "" : $" {label.ToUpperInvariant()}"),
                IsChecked = i == _strip,
                Height = 32,
                FontSize = 13,
                Hint = $"Hardware Input {i + 1} в Voicemeeter"
            };
            t.Click += _ => SelectStrip(idx);
            StripPanel.Children.Add(t);
        }
    }

    private void SelectStrip(int idx)
    {
        _strip = idx;
        SaveStrip();
        foreach (var t in StripPanel.Children.OfType<SlashToggle>()) t.IsChecked = false;
        var list = StripPanel.Children.OfType<SlashToggle>().ToList();
        if (idx < list.Count) list[idx].IsChecked = true;
        RefreshParams();
    }

    private void ShowDeviceMenu()
    {
        var menu = new ContextMenu { PlacementTarget = DeviceBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var devices = _vm.InputDevices()
            .OrderBy(d => d.Kind == "wdm" ? 0 : d.Kind == "mme" ? 1 : 2)
            .ThenBy(d => d.Name)
            .ToList();

        if (devices.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Устройства не найдены", IsEnabled = false });

        foreach (var d in devices)
        {
            var dev = d;
            var mi = new MenuItem { Header = dev.ToString() };
            mi.Click += (_, _) => { _vm.SetStripDevice(_strip, dev); };
            menu.Items.Add(mi);
        }

        menu.Items.Add(new Separator());
        var hint = new MenuItem { Header = "WDM — лучший вариант для микрофона", IsEnabled = false };
        menu.Items.Add(hint);
        menu.IsOpen = true;
    }

    private void ShowHeadphonesMenu(string? title)
    {
        var menu = new ContextMenu { PlacementTarget = HeadBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (title != null)
        {
            menu.Items.Add(new MenuItem { Header = title, IsEnabled = false });
            menu.Items.Add(new Separator());
        }

        var devices = _vm.OutputDevices()
            .Where(d => !d.Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Kind == "wdm" ? 0 : d.Kind == "mme" ? 1 : 2)
            .ThenBy(d => d.Name)
            .ToList();

        if (devices.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Устройства не найдены", IsEnabled = false });

        foreach (var d in devices)
        {
            var dev = d;
            var mi = new MenuItem { Header = dev.ToString() };
            mi.Click += (_, _) => _vm.SetBusDevice(0, dev);
            menu.Items.Add(mi);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "WDM — меньше задержка. Если трещит — бери MME", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void SaveStrip()
    {
        _settings.Strip = _strip;
        _settings.Save();
    }

    // ───────────────────────── presets ─────────────────────────

    private void BuildPresets()
    {
        PresetPanel.Children.Clear();

        AddPresetButton("ЧИСТЫЙ", "Шумодав 3, компрессор 4, моно", -1.5,
            () => _vm.Script($"{S("Gate")}=3;{S("Comp")}=4;{S("Mono")}=1;"));
        AddPresetButton("СТРИМ", "Шумодав 4.5, компрессор 6.5, моно — ровный голос поверх игры", 1,
            () => _vm.Script($"{S("Gate")}=4.5;{S("Comp")}=6.5;{S("Mono")}=1;"));
        AddPresetButton("СЫРОЙ", "Без обработки", -0.5,
            () => _vm.Script($"{S("Gate")}=0;{S("Comp")}=0;"));

        var rnd = new Random(42);
        foreach (var p in _settings.Presets)
        {
            var preset = p;
            var btn = AddPresetButton("★ " + preset.Name.ToUpperInvariant(),
                $"Громкость {preset.Gain:0.#} dB, шумодав {preset.Gate:0.#}, компрессор {preset.Comp:0.#}",
                rnd.Next(-20, 21) / 10.0, () => ApplyUserPreset(preset));

            var menu = new ContextMenu();
            var overwrite = new MenuItem { Header = "Перезаписать текущими настройками" };
            overwrite.Click += (_, _) =>
            {
                var fresh = CaptureCurrent(preset.Name);
                var i = _settings.Presets.IndexOf(preset);
                if (i >= 0) _settings.Presets[i] = fresh;
                _settings.Save();
                BuildPresets();
            };
            var delete = new MenuItem { Header = "Удалить" };
            delete.Click += (_, _) =>
            {
                _settings.Presets.Remove(preset);
                _settings.Save();
                BuildPresets();
            };
            menu.Items.Add(overwrite);
            menu.Items.Add(delete);
            btn.ContextMenu = menu;
        }

        var add = AddPresetButton("+ СВОЙ", "Сохранить текущие настройки как пресет", 2, OpenSaveOverlay);
        add.IsToggle = false;
    }

    private SlashToggle AddPresetButton(string text, string hint, double tilt, Action onClick)
    {
        var b = new SlashToggle { Text = text, IsToggle = false, Tilt = tilt, Hint = hint, Height = 34, FontSize = 14 };
        b.Click += _ => onClick();
        PresetPanel.Children.Add(b);
        return b;
    }

    private void OpenSaveOverlay()
    {
        if (!_vm.IsConnected) return;
        PresetNameBox.Text = $"Пресет {_settings.Presets.Count + 1}";
        SaveOverlay.Visibility = Visibility.Visible;
        PresetNameBox.Focus();
        PresetNameBox.SelectAll();
    }

    private void SaveNewPreset()
    {
        var name = PresetNameBox.Text.Trim();
        if (name.Length == 0) return;
        var existing = _settings.Presets.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        var p = CaptureCurrent(name);
        if (existing >= 0) _settings.Presets[existing] = p;
        else _settings.Presets.Add(p);
        _settings.Save();
        SaveOverlay.Visibility = Visibility.Collapsed;
        BuildPresets();
    }

    private UserPreset CaptureCurrent(string name) => new()
    {
        Name = name,
        Gain = _vm.Get(S("Gain")),
        Gate = _vm.Get(S("Gate")),
        Comp = _vm.Get(S("Comp")),
        Tone = _vm.Get(S("Color_x")),
        Mono = _vm.Get(S("Mono")) >= 0.5f,
        A1 = _vm.Get(S("A1")) >= 0.5f,
        B1 = _vm.Get(S("B1")) >= 0.5f,
        OutGain = _vm.Get(B1("Gain")),
    };

    private void ApplyUserPreset(UserPreset p)
    {
        _vm.Script(
            $"{S("Gain")}={VmClient.F(p.Gain)};" +
            $"{S("Gate")}={VmClient.F(p.Gate)};" +
            $"{S("Comp")}={VmClient.F(p.Comp)};" +
            $"{S("Color_x")}={VmClient.F(p.Tone)};" +
            $"{S("Mono")}={(p.Mono ? 1 : 0)};" +
            $"{S("A1")}={(p.A1 ? 1 : 0)};" +
            $"{S("B1")}={(p.B1 ? 1 : 0)};" +
            $"{B1("Gain")}={VmClient.F(p.OutGain)};");
    }

    // ───────────────────────── mute hotkey ─────────────────────────

    private void RegisterMuteHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, HotkeyId);
        if (_settings.HotkeyVk == 0) { HotkeyBtn.Text = "MUTE НА КЛАВИШЕ: НЕТ"; return; }

        bool ok = RegisterHotKey(_hwnd, HotkeyId, (uint)_settings.HotkeyMods | 0x4000 /* NOREPEAT */, (uint)_settings.HotkeyVk);
        HotkeyBtn.Text = ok
            ? $"MUTE НА КЛАВИШЕ: {HotkeyName()}"
            : $"{HotkeyName()} ЗАНЯТА — ВЫБЕРИ ДРУГУЮ";
    }

    private string HotkeyName()
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((_settings.HotkeyMods & 2) != 0) parts.Add("CTRL");
        if ((_settings.HotkeyMods & 1) != 0) parts.Add("ALT");
        if ((_settings.HotkeyMods & 4) != 0) parts.Add("SHIFT");
        if ((_settings.HotkeyMods & 8) != 0) parts.Add("WIN");
        parts.Add(KeyInterop.KeyFromVirtualKey(_settings.HotkeyVk).ToString().ToUpperInvariant());
        return string.Join("+", parts);
    }

    private void StartHotkeyCapture()
    {
        _capturingHotkey = true;
        if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId);
        HotkeyBtn.Text = "НАЖМИ КЛАВИШУ...  (ESC — ОТМЕНА, BACKSPACE — УБРАТЬ)";
        Activate();
        Keyboard.Focus(this);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.LeftCtrl: case Key.RightCtrl: case Key.LeftAlt: case Key.RightAlt:
            case Key.LeftShift: case Key.RightShift: case Key.LWin: case Key.RWin:
                return; // wait for the real key
            case Key.Escape:
                _capturingHotkey = false;
                RegisterMuteHotkey();
                return;
            case Key.Back:
                _capturingHotkey = false;
                _settings.HotkeyVk = 0;
                _settings.HotkeyMods = 0;
                _settings.Save();
                RegisterMuteHotkey();
                return;
        }

        int mods = 0;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= 1;
        if (m.HasFlag(ModifierKeys.Control)) mods |= 2;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= 4;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= 8;

        _capturingHotkey = false;
        _settings.HotkeyVk = KeyInterop.VirtualKeyFromKey(key);
        _settings.HotkeyMods = mods;
        _settings.Save();
        RegisterMuteHotkey();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleMute();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ───────────────────────── autostart ─────────────────────────

    private static bool IsAutostart()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("StealYourVoice") != null;
        }
        catch { return false; }
    }

    private static void SetAutostart(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (on && Environment.ProcessPath != null)
                k.SetValue("StealYourVoice", $"\"{Environment.ProcessPath}\" --tray");
            else
                k.DeleteValue("StealYourVoice", false);
        }
        catch { /* ignore */ }
    }

    // ───────────────────────── auto-ducking ─────────────────────────

    private void InitDucking()
    {
        _ducker = new Audio.Ducker(_vm);
        DuckT.IsChecked = _settings.DuckEnabled;
        DuckMainT.IsChecked = _settings.DuckMain;
        DuckAuxT.IsChecked = _settings.DuckAux;
        DuckAmountS.Value = _settings.DuckAmount;
        DuckThreshS.Value = _settings.DuckThreshold;

        DuckT.Click += t =>
        {
            _settings.DuckEnabled = t.IsChecked;
            _settings.Save();
            if (!t.IsChecked) _ducker.Restore();
        };
        DuckMainT.Click += t => { _settings.DuckMain = t.IsChecked; _settings.Save(); };
        DuckAuxT.Click += t => { _settings.DuckAux = t.IsChecked; _settings.Save(); };
        DuckAmountS.ValueChanged += v => { _settings.DuckAmount = v; _settings.Save(); };
        DuckThreshS.ValueChanged += v => { _settings.DuckThreshold = v; _settings.Save(); };
        GameToB1T.Click += t => _vm.Set($"Strip[{_vm.HardwareStrips}].B1", t.IsChecked ? 1 : 0);
    }

    private void DuckTick()
    {
        if (_ducker == null) return;
        if (!_vm.IsConnected || !_settings.DuckEnabled)
        {
            if (_ducker.Active) _ducker.Restore();
            DuckState.Text = "ВЫКЛЮЧЕНО";
            DuckState.Foreground = P5.Grey;
            return;
        }

        var targets = new System.Collections.Generic.List<int>();
        int hw = _vm.HardwareStrips;
        if (_settings.DuckMain) targets.Add(hw);
        if (_settings.DuckAux && _vm.Type != VmType.Standard) targets.Add(hw + 1);

        // post-mute level: a muted mic never ducks the game
        var (l, r) = _vm.StripChannels(_strip, 2);
        var micDb = P5.ToDb(Math.Max(l, r));
        _ducker.Tick(micDb, targets, _settings.DuckAmount, _settings.DuckThreshold);

        if (targets.Count == 0)
        {
            DuckState.Text = "ВЫБЕРИ, ЧТО ПРИГЛУШАТЬ";
            DuckState.Foreground = P5.Grey;
        }
        else if (_ducker.Active)
        {
            DuckState.Text = $"▼ ИГРА {_ducker.Offset:0} dB";
            DuckState.Foreground = P5.Red;
        }
        else
        {
            DuckState.Text = "ЖДУ ГОЛОС";
            DuckState.Foreground = P5.White;
        }
    }

    // ───────────────────────── auto-update ─────────────────────────

    private async System.Threading.Tasks.Task CheckForUpdates()
    {
        if (_updating || _updater.Ready) return;
        _updating = true;
        try
        {
            var progress = new Progress<int>(p =>
            {
                UpdateBtn.Visibility = Visibility.Visible;
                UpdateBtn.IsChecked = false;
                UpdateBtn.Text = $"КАЧАЮ ОБНОВЛЕНИЕ {p}%";
            });

            if (await _updater.CheckAndDownloadAsync(progress))
            {
                var v = _updater.Latest;
                UpdateBtn.Text = $"ОБНОВИТЬ ДО v{v?.Major}.{v?.Minor}.{Math.Max(v?.Build ?? 0, 0)}";
                UpdateBtn.IsChecked = true;
                UpdateBtn.Visibility = Visibility.Visible;
                if (!IsVisible)
                    _tray?.ShowBalloonTip(3000, "Steal Your Voice", "Скачано обновление — открой окно и нажми «Обновить».",
                        System.Windows.Forms.ToolTipIcon.Info);
            }
            else
            {
                UpdateBtn.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void ApplyUpdate()
    {
        if (!_updater.Ready) { UpdateBtn.IsChecked = false; return; }
        UpdateBtn.IsChecked = true;
        UpdateBtn.Text = "ПЕРЕЗАПУСК...";
        if (_updater.ApplyAndRestart())
        {
            ExitApp();
            return;
        }

        UpdateBtn.Text = "НЕ ВЫШЛО — ОТКРОЙ РЕЛИЗ";
        try
        {
            if (_updater.ReleaseUrl != null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updater.ReleaseUrl) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    // ───────────────────────── tray ─────────────────────────

    private void SetupTray()
    {
        System.Drawing.Icon? icon = null;
        try
        {
            var path = Environment.ProcessPath;
            if (path != null) icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        }
        catch { /* ignore */ }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Показать", null, (_, _) => ShowFromTray());
        _trayMute = new System.Windows.Forms.ToolStripMenuItem("Выключить микрофон", null, (_, _) => ToggleMute());
        menu.Items.Add(_trayMute);
        menu.Items.Add("Открыть Voicemeeter", null, (_, _) => _vm.Script("Command.Show=1;"));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = $"Steal Your Voice v{Updater.Current.Major}.{Updater.Current.Minor}.{Math.Max(Updater.Current.Build, 0)}",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray();
        };
    }

    private void ToggleMute()
    {
        if (!_vm.IsConnected) return;
        var muted = _vm.Get(S("Mute")) >= 0.5f;
        _vm.Set(S("Mute"), muted ? 0 : 1);
        MuteT.IsChecked = !muted;
        UpdateTrayMute();
        if (_settings.ShowToast)
        {
            _toast ??= new MicToast();
            _toast.Flash(!muted);
        }
    }

    private void UpdateTrayMute()
    {
        if (_trayMute == null) return;
        _trayMute.Text = MuteT.IsChecked ? "Включить микрофон" : "Выключить микрофон";
    }

    private void HideToTray()
    {
        Hide();
        if (_balloonShown) return;
        _balloonShown = true;
        _tray?.ShowBalloonTip(1500, "Steal Your Voice", "Я в трее. Клик по иконке — вернуть окно.",
            System.Windows.Forms.ToolTipIcon.None);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        _exiting = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _ducker?.Restore();
        if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId);
        _toast?.Close();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _vm.Dispose();
        base.OnClosed(e);
        if (_exiting) Application.Current.Shutdown();
    }
}
