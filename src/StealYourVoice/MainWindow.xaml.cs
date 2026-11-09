using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StealYourVoice");
    private static readonly string StripFile = Path.Combine(SettingsDir, "strip.txt");

    private string S(string p) => $"Strip[{_strip}].{p}";
    private string B1(string p) => $"Bus[{_vm.B1Index}].{p}";

    public MainWindow()
    {
        InitializeComponent();
        _strip = LoadStrip();

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

        // presets
        PresetClean.Click += _ => ApplyPreset(3, 4, true);
        PresetStream.Click += _ => ApplyPreset(4.5f, 6.5f, true);
        PresetRaw.Click += _ => ApplyPreset(0, 0, null);

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

    private void ApplyPreset(float gate, float comp, bool? mono)
    {
        var s = $"{S("Gate")}={VmClient.F(gate)};{S("Comp")}={VmClient.F(comp)};";
        if (mono.HasValue) s += $"{S("Mono")}={(mono.Value ? 1 : 0)};";
        _vm.Script(s);
    }

    private int LoadStrip()
    {
        try { return int.TryParse(File.ReadAllText(StripFile).Trim(), out var v) ? Math.Max(0, v) : 0; }
        catch { return 0; }
    }

    private void SaveStrip()
    {
        try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(StripFile, _strip.ToString()); }
        catch { /* ignore */ }
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
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _vm.Dispose();
        base.OnClosed(e);
        if (_exiting) Application.Current.Shutdown();
    }
}
