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

    private void InitExtras()
    {
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

        // corner indicator
        _settings.OverlayCorner = Math.Clamp(_settings.OverlayCorner, 0, 4);
        OverlayBtn.Click += _ =>
        {
            _settings.OverlayCorner = (_settings.OverlayCorner + 1) % 5;
            _settings.Save();
            ApplyOverlay();
        };
        ApplyOverlay();
    }

    private void ApplyOverlay()
    {
        OverlayBtn.Text = "ИНДИКАТОР: " + CornerNames[_settings.OverlayCorner];
        if (_settings.OverlayCorner == 0)
        {
            _overlay?.Hide();
            return;
        }
        _overlay ??= new MiniOverlay();
        _overlay.Place(_settings.OverlayCorner);
        if (!_overlay.IsVisible) _overlay.Show();
    }

    /// <summary>Called from the 30 fps timer while connected.</summary>
    private void ExtrasTick()
    {
        bool hasAux = _vm.Type != Voicemeeter.VmType.Standard;

        if (hasAux)
        {
            var (fl, fr) = _vm.StripChannels(AuxStrip, 0); // pre-fader: what Discord sends us
            _friendsTalking = P5.ToDb(Math.Max(fl, fr)) > -45;
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

        if (_tick % 90 == 0) GameTick();
    }

    private void RefreshExtras()
    {
        bool hasAux = _vm.Type != Voicemeeter.VmType.Standard;
        DiscordPanel.Visibility = hasAux ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        if (!hasAux) return;
        FriendsClipsT.IsChecked = _vm.Get($"Strip[{AuxStrip}].B2") >= 0.5f;
        // while ducking lowers AUX, the gain we'd read is the temporary one
        bool auxDucked = _ducker is { Active: true } && _settings.DuckEnabled && _settings.DuckAux && !_settings.DuckFriends;
        if (!auxDucked && !AuxGainS.IsDragging) AuxGainS.Value = _vm.Get($"Strip[{AuxStrip}].Gain");
    }

    // ───────────────────────── presets bound to games ─────────────────────────

    private static string PresetTitle(UserPreset p) =>
        "★ " + p.Name.ToUpperInvariant() + (string.IsNullOrEmpty(p.Process) ? "" : $" [{p.Process.ToUpperInvariant()}]");

    private void AddGameMenuItems(ContextMenu menu, UserPreset preset, SlashToggle button)
    {
        var bind = new MenuItem { Header = "Привязать к игре (включается сам при запуске)..." };
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
        menu.Items.Add(new MenuItem { Header = "Запусти игру и выбери её из списка:", IsEnabled = false });
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

    /// <summary>Every ~3 s: a bound game started → apply its preset; it closed → put the old settings back.</summary>
    private async void GameTick()
    {
        if (_gameScanBusy) return;
        var bound = _settings.Presets.Where(p => !string.IsNullOrEmpty(p.Process)).ToList();
        if (bound.Count == 0 && _activeGame == null) return;

        _gameScanBusy = true;
        try
        {
            var running = await Task.Run(RunningProcessNames);
            if (!_vm.IsConnected) return;

            if (_activeGame != null && !running.Contains(_activeGame))
            {
                var closed = _activeGame;
                _activeGame = null;
                if (_beforeGame != null)
                {
                    ApplyUserPreset(_beforeGame);
                    _beforeGame = null;
                    StatusText.Text = $"{closed.ToUpperInvariant()} ЗАКРЫТА // ВЕРНУЛ ПРЕЖНИЕ НАСТРОЙКИ";
                }
            }

            if (_activeGame == null)
            {
                var hit = bound.FirstOrDefault(p => running.Contains(p.Process!));
                if (hit != null)
                {
                    _beforeGame = CaptureCurrent("");
                    ApplyUserPreset(hit);
                    _activeGame = hit.Process;
                    StatusText.Text = $"{hit.Process!.ToUpperInvariant()} ЗАПУЩЕНА // ПРЕСЕТ «{hit.Name.ToUpperInvariant()}»";
                    Audio.Sfx.Play("confirm");
                }
            }
        }
        catch { /* ignore */ }
        finally { _gameScanBusy = false; }
    }
}
