using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StealYourVoice.Audio;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>Soundboard: sounds mixed into your mic (everyone hears them) and into AUX (you hear them).</summary>
public partial class MainWindow
{
    private const int SoundHotkeyBase = 0x5400, MaxSounds = 40, SoundStopHotkeyId = 0x53FF;
    private string? _playingPath;

    private void InitBoard()
    {
        BoardFriendsS.Value = Math.Round(_settings.SoundToFriends * 100);
        BoardMeS.Value = Math.Round(_settings.SoundToMe * 100);
        BoardFriendsS.ValueChanged += v =>
        {
            _settings.SoundToFriends = (float)(v / 100);
            if (_fx.Clip is { } c) c.ToFriends = _settings.SoundToFriends;
            _settings.Save();
        };
        BoardMeS.ValueChanged += v =>
        {
            _settings.SoundToMe = (float)(v / 100);
            if (_fx.Clip is { } c) c.ToMe = _settings.SoundToMe;
            _settings.Save();
        };
        BoardAddBtn.Click += _ => AddSounds();
        BoardStopBtn.Click += _ => StopSound();
        BoardStopKeyBtn.Click += _ => StartSlotCapture(-2);
        BuildBoard();
    }

    private void BuildBoard()
    {
        BoardPanel.Children.Clear();
        BoardEmpty.Visibility = _settings.Sounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _settings.Sounds.Count; i++)
        {
            var slot = _settings.Sounds[i];
            int idx = i;
            var key = slot.HotkeyVk != 0 ? $"  [{KeyName(slot.HotkeyVk, slot.HotkeyMods)}]" : "";
            var name = slot.Name.Length > 18 ? slot.Name[..17] + "…" : slot.Name;
            var b = new SlashToggle
            {
                Text = name.ToUpperInvariant() + key, IsToggle = false, Sound = "", Height = 38, FontSize = 13,
                Tilt = (i % 3 - 1) * 1.1, Hint = slot.Path
            };
            b.Click += _ => PlaySound(idx);

            var menu = new ContextMenu();
            var hk = new MenuItem { Header = "Назначить клавишу…" };
            hk.Click += (_, _) => StartSlotCapture(idx);
            var del = new MenuItem { Header = "Удалить" };
            del.Click += (_, _) =>
            {
                _settings.Sounds.RemoveAt(idx);
                _settings.Save();
                RegisterSoundHotkeys();
                BuildBoard();
            };
            menu.Items.Add(hk);
            menu.Items.Add(del);
            b.ContextMenu = menu;
            BoardPanel.Children.Add(b);
        }
        UpdateStopKeyBtn();
    }

    private void AddSounds()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Звуки для саундборда",
            Filter = "Звук (*.mp3;*.wav;*.m4a;*.aac;*.flac;*.wma;*.ogg)|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.wma;*.ogg|Все файлы|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames)
        {
            if (_settings.Sounds.Count >= MaxSounds) break;
            if (_settings.Sounds.Any(s => string.Equals(s.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
            _settings.Sounds.Add(new SoundSlot { Name = Path.GetFileNameWithoutExtension(f), Path = f });
        }
        _settings.Save();
        BuildBoard();
    }

    private async void PlaySound(int idx)
    {
        if (idx < 0 || idx >= _settings.Sounds.Count) return;
        if (!_vm.IsConnected) { StatusText.Text = "НЕТ СВЯЗИ С VOICEMEETER"; return; }
        var slot = _settings.Sounds[idx];

        // same sound again while it's playing → stop it (handy on a hotkey)
        if (_fx.ClipPlaying && string.Equals(_playingPath, slot.Path, StringComparison.OrdinalIgnoreCase)) { StopSound(); return; }

        int sr = _fx.SampleRate;
        var data = await Task.Run(() => SoundDecoder.Load(slot.Path, sr));
        if (data == null || data.Length < 4)
        {
            StatusText.Text = $"НЕ УДАЛОСЬ ПРОЧИТАТЬ «{slot.Name.ToUpperInvariant()}»";
            return;
        }

        if (_fx.Clip is { } old) old.Stop = true;
        _fx.Clip = new Clip { Data = data, ToFriends = _settings.SoundToFriends, ToMe = _settings.SoundToMe };
        _playingPath = slot.Path;
        PushFxParams();
        SyncFxEngine(); // the insert is registered on demand
        if (!_fx.Registered) return;
        BoardState.Text = "▶ " + slot.Name.ToUpperInvariant();
        BoardState.Foreground = P5.Red;
    }

    private void StopSound()
    {
        if (_fx.Clip is { } c) c.Stop = true;
        _playingPath = null;
    }

    /// <summary>~once a second: update the label and let go of the insert when nothing needs it.</summary>
    private void BoardTick()
    {
        if (_fx.ClipPlaying) return;
        if (_playingPath != null) _playingPath = null;
        if (BoardState.Text != "ТИШИНА")
        {
            BoardState.Text = "ТИШИНА";
            BoardState.Foreground = P5.Grey;
        }
        if (_fx.Registered && !_settings.FxOn) _fx.Stop();
    }

    // ── hotkeys ──

    private void RegisterSoundHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        for (int i = 0; i < MaxSounds; i++) UnregisterHotKey(_hwnd, SoundHotkeyBase + i);
        UnregisterHotKey(_hwnd, SoundStopHotkeyId);
        for (int i = 0; i < _settings.Sounds.Count && i < MaxSounds; i++)
        {
            var s = _settings.Sounds[i];
            if (s.HotkeyVk != 0) RegisterHotKey(_hwnd, SoundHotkeyBase + i, (uint)s.HotkeyMods | 0x4000, (uint)s.HotkeyVk);
        }
        if (_settings.SoundStopVk != 0)
            RegisterHotKey(_hwnd, SoundStopHotkeyId, (uint)_settings.SoundStopMods | 0x4000, (uint)_settings.SoundStopVk);
        UpdateStopKeyBtn();
    }

    private void UpdateStopKeyBtn() =>
        BoardStopKeyBtn.Text = _settings.SoundStopVk == 0 ? "СТОП НА КЛАВИШЕ: НЕТ" : $"СТОП НА КЛАВИШЕ: {KeyName(_settings.SoundStopVk, _settings.SoundStopMods)}";

    private bool OnSoundHotkey(int id)
    {
        if (id == SoundStopHotkeyId) { StopSound(); return true; }
        if (id < SoundHotkeyBase || id >= SoundHotkeyBase + MaxSounds) return false;
        PlaySound(id - SoundHotkeyBase);
        return true;
    }

    private void StartSlotCapture(int slot)
    {
        _capturingHotkey = true;
        _captureFx = false;
        _captureSlot = slot;
        if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, slot == -2 ? SoundStopHotkeyId : SoundHotkeyBase + slot);
        var text = "НАЖМИ КЛАВИШУ...  (ESC — ОТМЕНА, BACKSPACE — УБРАТЬ)";
        if (slot == -2) BoardStopKeyBtn.Text = text;
        else StatusText.Text = $"КЛАВИША ДЛЯ «{_settings.Sounds[slot].Name.ToUpperInvariant()}»: {text}";
        Activate();
        Keyboard.Focus(this);
    }

    /// <summary>Called by the shared key-capture code when a soundboard key was being set. Returns true if it handled it.</summary>
    private bool FinishSlotCapture(int vk, int mods)
    {
        if (_captureSlot == -1) return false;
        if (_captureSlot == -2) { _settings.SoundStopVk = vk; _settings.SoundStopMods = mods; }
        else if (_captureSlot < _settings.Sounds.Count) { _settings.Sounds[_captureSlot].HotkeyVk = vk; _settings.Sounds[_captureSlot].HotkeyMods = mods; }
        _captureSlot = -1;
        _settings.Save();
        RegisterSoundHotkeys();
        BuildBoard();
        StatusText.Text = vk == 0 ? "КЛАВИША УБРАНА" : $"КЛАВИША {KeyName(vk, mods)} НАЗНАЧЕНА";
        return true;
    }
}
