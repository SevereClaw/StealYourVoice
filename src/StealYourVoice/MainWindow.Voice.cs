using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using StealYourVoice.Audio;
using StealYourVoice.Controls;

namespace StealYourVoice;

/// <summary>Voice changer tab.</summary>
public partial class MainWindow
{
    private sealed record VoicePreset(string Name, float Pitch, float Robot, float Radio, float Echo, float Cave, string Hint);

    private static readonly VoicePreset[] Voices =
    {
        new("ДЕМОН", -7, 0, 0.15f, 0, 0.3f, "Низкий рычащий голос в гулком зале"),
        new("ГИГАНТ", -4, 0, 0, 0, 0.25f, "Ниже и объёмнее"),
        new("ТЕНЬ", -3, 0.3f, 0, 0, 0.4f, "Твоя тёмная сторона: ниже, с металлом и эхом зала"),
        new("БУРУНДУК", 8, 0, 0, 0, 0, "Высокий мультяшный голос"),
        new("РЕБЁНОК", 4, 0, 0, 0, 0, "Чуть выше обычного"),
        new("РОБОТ", 0, 0.85f, 0, 0, 0.1f, "Металлический голос машины"),
        new("РАЦИЯ", 0, 0, 0.9f, 0, 0, "Узкая полоса и хрип, как в рации"),
        new("МЕГАФОН", 0, 0, 0.7f, 0.15f, 0.15f, "Громкоговоритель на улице"),
        new("ПЕЩЕРА", 0, 0, 0, 0.2f, 0.9f, "Огромное гулкое пространство"),
        new("ПРИЗРАК", -2, 0, 0, 0.45f, 0.75f, "Шёпот из пустоты с длинным эхом"),
    };

    private readonly VoiceFx _fx = new();
    private bool _fxApplying;
    private const int FxHotkeyId = 0x535A;
    private bool _captureFx;

    private void InitVoiceFx()
    {
        foreach (var v in Voices)
        {
            var voice = v;
            var b = new SlashToggle { Text = v.Name, Hint = v.Hint, Height = 36, FontSize = 14, Sound = "", Tilt = (v.Name.Length % 3 - 1) * 1.2 };
            b.Click += _ => PickVoice(voice);
            FxVoicePanel.Children.Add(b);
        }

        FxOnT.Click += t => SetFx(t.IsChecked, true);
        FxListenT.Click += t => _vm.Set(S("A1"), t.IsChecked ? 1 : 0);
        FxHotkeyBtn.Click += _ => StartHotkeyCapture(true);

        void Hook(SlashSlider s, Action<float> set)
        {
            s.ValueChanged += v =>
            {
                if (_fxApplying) return;
                set((float)v);
                _settings.FxVoice = "СВОЙ";
                SaveFxParams();
            };
        }
        Hook(FxPitchS, v => _settings.FxPitch = v);
        Hook(FxRobotS, v => _settings.FxRobot = v / 100f);
        Hook(FxRadioS, v => _settings.FxRadio = v / 100f);
        Hook(FxEchoS, v => _settings.FxEcho = v / 100f);
        Hook(FxCaveS, v => _settings.FxCave = v / 100f);

        LoadFxParams();
        FxOnT.IsChecked = _settings.FxOn;
    }

    private void PickVoice(VoicePreset v)
    {
        _settings.FxVoice = v.Name;
        _settings.FxPitch = v.Pitch;
        _settings.FxRobot = v.Robot;
        _settings.FxRadio = v.Radio;
        _settings.FxEcho = v.Echo;
        _settings.FxCave = v.Cave;
        LoadFxParams();
        _settings.Save();
        if (!_settings.FxOn) SetFx(true, false);
        ShowPresetBanner("ГОЛОС", v.Name);
    }

    /// <summary>Settings → sliders, buttons and the audio thread.</summary>
    private void LoadFxParams()
    {
        _fxApplying = true;
        FxPitchS.Value = _settings.FxPitch;
        FxRobotS.Value = Math.Round(_settings.FxRobot * 100);
        FxRadioS.Value = Math.Round(_settings.FxRadio * 100);
        FxEchoS.Value = Math.Round(_settings.FxEcho * 100);
        FxCaveS.Value = Math.Round(_settings.FxCave * 100);
        _fxApplying = false;
        PushFxParams();
        foreach (var t in FxVoicePanel.Children.OfType<SlashToggle>()) t.IsChecked = t.Text == _settings.FxVoice;
        UpdateFxState();
    }

    private void SaveFxParams()
    {
        PushFxParams();
        foreach (var t in FxVoicePanel.Children.OfType<SlashToggle>()) t.IsChecked = false;
        UpdateFxState();
        _settings.Save();
    }

    private void PushFxParams()
    {
        _fx.P.Pitch = _settings.FxPitch;
        _fx.P.Robot = _settings.FxRobot;
        _fx.P.Radio = _settings.FxRadio;
        _fx.P.Echo = _settings.FxEcho;
        _fx.P.Cave = _settings.FxCave;
        _fx.P.Strip = _strip;
        _fx.P.On = _settings.FxOn;
    }

    private void SetFx(bool on, bool banner)
    {
        _settings.FxOn = on;
        _settings.Save();
        FxOnT.IsChecked = on;
        SyncFxEngine();
        PushFxParams();
        UpdateFxState();
        if (banner) ShowPresetBanner(on ? "ГОЛОС ИЗМЕНЁН" : "ЭФФЕКТ ВЫКЛЮЧЕН", on ? _settings.FxVoice : "ОБЫЧНЫЙ ГОЛОС", !on);
    }

    /// <summary>Keep the insert registered only while the effect is on — no processing at all otherwise.</summary>
    private void SyncFxEngine()
    {
        if (_settings.FxOn && _vm.IsConnected)
        {
            if (!_fx.Start())
            {
                _settings.FxOn = false;
                FxOnT.IsChecked = false;
                StatusText.Text = _fx.LastError.ToUpperInvariant();
            }
        }
        else _fx.Stop();
    }

    private void UpdateFxState()
    {
        FxState.Text = _settings.FxOn ? $"● {_settings.FxVoice}" : "ВЫКЛЮЧЕНО";
        FxState.Foreground = _settings.FxOn ? P5.Red : P5.Grey;
        if (_vm.IsConnected) FxListenT.IsChecked = _vm.Get(S("A1")) >= 0.5f;
    }

    // ── hotkey ──

    private void RegisterFxHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, FxHotkeyId);
        if (_settings.FxHotkeyVk == 0) { FxHotkeyBtn.Text = "ЭФФЕКТ НА КЛАВИШЕ: НЕТ"; return; }
        bool ok = RegisterHotKey(_hwnd, FxHotkeyId, (uint)_settings.FxHotkeyMods | 0x4000, (uint)_settings.FxHotkeyVk);
        var name = KeyName(_settings.FxHotkeyVk, _settings.FxHotkeyMods);
        FxHotkeyBtn.Text = ok ? $"ЭФФЕКТ НА КЛАВИШЕ: {name}" : $"{name} ЗАНЯТА — ВЫБЕРИ ДРУГУЮ";
    }

    private static string KeyName(int vk, int mods)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((mods & 2) != 0) parts.Add("CTRL");
        if ((mods & 1) != 0) parts.Add("ALT");
        if ((mods & 4) != 0) parts.Add("SHIFT");
        if ((mods & 8) != 0) parts.Add("WIN");
        parts.Add(KeyInterop.KeyFromVirtualKey(vk).ToString().ToUpperInvariant());
        return string.Join("+", parts);
    }
}
