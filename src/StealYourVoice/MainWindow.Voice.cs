using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    private int _captureSlot = -1; // soundboard slot being given a key; -2 = the stop key
    private string? _captureVoice;  // voice (built-in or own set) being given a key
    private const int VoiceHotkeyBase = 0x5500, MaxVoiceKeys = 64, MaxVoiceSets = 30;
    private const string Tweaked = "СВОЙ";   // the sliders were moved by hand and the result isn't saved as a set
    private readonly List<VoicePreset> _voiceKeyOrder = new();   // hotkey id - base → voice

    /// <summary>Built-in voices first, then the user's own sets.</summary>
    private IEnumerable<VoicePreset> AllVoices() =>
        Voices.Concat(_settings.FxSets.Select(x => new VoicePreset(x.Name, x.Pitch, x.Robot, x.Radio, x.Echo, x.Cave, "")));

    private bool IsOwnSet(string name) => _settings.FxSets.Any(x => SameName(x.Name, name));
    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void InitVoiceFx()
    {
        BuildVoices();

        FxOnT.Click += t => SetFx(t.IsChecked, true);
        FxListenT.Click += t => _vm.Set(S("A1"), t.IsChecked ? 1 : 0);
        FxHotkeyBtn.Click += _ => StartHotkeyCapture(true);

        void Hook(SlashSlider s, Action<float> set)
        {
            s.ValueChanged += v =>
            {
                if (_fxApplying) return;
                set((float)v);
                _settings.FxVoice = Tweaked;
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
        MarkVoice();
        UpdateFxState();
    }

    private void MarkVoice()
    {
        foreach (var t in FxVoicePanel.Children.OfType<SlashToggle>())
            if (t.Tag is string name) t.IsChecked = SameName(name, _settings.FxVoice);
    }

    // ── voices: built-in ones and the user's own sets, each can have its own key ──

    private void BuildVoices()
    {
        FxVoicePanel.Children.Clear();
        int i = 0;
        foreach (var v in AllVoices())
        {
            var voice = v;
            bool own = IsOwnSet(v.Name);
            var key = KeyOfVoice(v.Name);
            var hint = own ? $"Свой набор: высота {v.Pitch:+0;-0;0} пт, робот {v.Robot * 100:0}%, рация {v.Radio * 100:0}%, эхо {v.Echo * 100:0}%, пещера {v.Cave * 100:0}%"
                           : v.Hint;
            hint += key != null ? $"\nКлавиша: {KeyName(key.Vk, key.Mods)}" : "";
            hint += "\nПКМ — назначить клавишу" + (own ? ", перезаписать или удалить" : "");
            var b = new SlashToggle
            {
                Text = v.Name + (key != null ? $"  [{KeyName(key.Vk, key.Mods)}]" : ""), Tag = v.Name, Hint = hint,
                Height = 36, FontSize = 14, Sound = "", Tilt = (i++ % 3 - 1) * 1.2,
            };
            b.Click += _ => PickVoice(voice);

            var menu = new ContextMenu();
            var bind = new MenuItem { Header = key != null ? "Другая клавиша…" : "Назначить клавишу…" };
            bind.Click += (_, _) => StartVoiceCapture(voice.Name);
            menu.Items.Add(bind);
            if (key != null)
            {
                var unbind = new MenuItem { Header = $"Убрать клавишу {KeyName(key.Vk, key.Mods)}" };
                unbind.Click += (_, _) => SetVoiceKey(voice.Name, 0, 0);
                menu.Items.Add(unbind);
            }
            if (own)
            {
                var overwrite = new MenuItem { Header = "Перезаписать текущими ползунками" };
                overwrite.Click += (_, _) => SaveVoiceSet(voice.Name);
                var delete = new MenuItem { Header = "Удалить набор" };
                delete.Click += (_, _) => DeleteVoiceSet(voice.Name);
                menu.Items.Add(overwrite);
                menu.Items.Add(delete);
            }
            b.ContextMenu = menu;
            FxVoicePanel.Children.Add(b);
        }

        var add = new SlashToggle
        {
            Text = "+ СВОЙ НАБОР", IsToggle = false, Height = 36, FontSize = 14, Tilt = 1.5,
            Hint = "Сохранить то, что сейчас на ползунках «НАСТРОЙКА», своим голосом — с именем и своей клавишей",
        };
        add.Click += _ => OpenVoiceSetOverlay();
        FxVoicePanel.Children.Add(add);
        MarkVoice();
    }

    private VoiceKey? KeyOfVoice(string name) => _settings.FxKeys.FirstOrDefault(k => SameName(k.Name, name) && k.Vk != 0);

    private void OpenVoiceSetOverlay()
    {
        if (_settings.FxSets.Count >= MaxVoiceSets) { StatusText.Text = $"СВОИХ НАБОРОВ УЖЕ {MaxVoiceSets} — УДАЛИ ЛИШНИЙ (ПКМ)"; return; }
        _savingVoiceSet = true;
        SaveTitle.Text = "НОВЫЙ ГОЛОС";
        SaveHint.Text = $"Сохранит ползунки «НАСТРОЙКА»: высота {_settings.FxPitch:+0;-0;0} пт, робот {_settings.FxRobot * 100:0}%, " +
                        $"рация {_settings.FxRadio * 100:0}%, эхо {_settings.FxEcho * 100:0}%, пещера {_settings.FxCave * 100:0}%";
        PresetNameBox.Text = $"ГОЛОС {_settings.FxSets.Count + 1}";
        ShowOverlay(SaveOverlay);
        PresetNameBox.Focus();
        PresetNameBox.SelectAll();
    }

    /// <summary>Save the sliders as an own set (a new one, or over the set with this name).</summary>
    private bool SaveVoiceSet(string name)
    {
        name = name.Trim().ToUpperInvariant();
        if (name.Length == 0) return false;
        if (Voices.Any(v => SameName(v.Name, name)) || SameName(name, Tweaked))
        {
            StatusText.Text = $"«{name}» — ЭТО ВСТРОЕННЫЙ ГОЛОС, ПРИДУМАЙ ДРУГОЕ ИМЯ";
            Audio.Sfx.Play("cancel");
            return false;
        }
        var set = new VoiceSet
        {
            Name = name, Pitch = _settings.FxPitch, Robot = _settings.FxRobot, Radio = _settings.FxRadio,
            Echo = _settings.FxEcho, Cave = _settings.FxCave,
        };
        int i = _settings.FxSets.FindIndex(x => SameName(x.Name, name));
        if (i >= 0) _settings.FxSets[i] = set;
        else _settings.FxSets.Add(set);
        _settings.FxVoice = name;   // it's what's on the sliders right now
        _settings.Save();
        BuildVoices();
        RegisterVoiceKeys();
        UpdateFxState();
        ShowPresetBanner(i >= 0 ? "ГОЛОС ПЕРЕЗАПИСАН" : "ГОЛОС СОХРАНЁН", name);
        return true;
    }

    private void DeleteVoiceSet(string name)
    {
        _settings.FxSets.RemoveAll(x => SameName(x.Name, name));
        _settings.FxKeys.RemoveAll(k => SameName(k.Name, name));
        if (SameName(_settings.FxVoice, name)) _settings.FxVoice = Tweaked;   // the sliders keep the sound, it just has no name now
        _settings.Save();
        BuildVoices();
        RegisterVoiceKeys();
        UpdateFxState();
        StatusText.Text = $"НАБОР «{name}» УДАЛЁН";
    }

    private void StartVoiceCapture(string name)
    {
        _capturingHotkey = true;
        _captureFx = false;
        _captureSlot = -1;
        _captureVoice = name;
        if (_hwnd != IntPtr.Zero)
            for (int i = 0; i < MaxVoiceKeys; i++) UnregisterHotKey(_hwnd, VoiceHotkeyBase + i);   // the old key of this voice must not fire
        StatusText.Text = $"КЛАВИША ДЛЯ ГОЛОСА «{name}»: НАЖМИ КЛАВИШУ...  (ESC — ОТМЕНА, BACKSPACE — УБРАТЬ)";
        Activate();
        Keyboard.Focus(this);
    }

    /// <summary>Called by the shared key-capture code. Returns true if a voice key was being set.</summary>
    private bool FinishVoiceCapture(int vk, int mods)
    {
        if (_captureVoice == null) return false;
        var name = _captureVoice;
        _captureVoice = null;
        SetVoiceKey(name, vk, mods);
        return true;
    }

    private void SetVoiceKey(string name, int vk, int mods)
    {
        _settings.FxKeys.RemoveAll(k => SameName(k.Name, name));
        string? taken = null;
        if (vk != 0)
        {
            // one key — one voice: the key moves here from whatever voice had it
            foreach (var k in _settings.FxKeys.Where(k => k.Vk == vk && k.Mods == mods).ToList())
            {
                taken = k.Name;
                _settings.FxKeys.Remove(k);
            }
            _settings.FxKeys.Add(new VoiceKey { Name = name, Vk = vk, Mods = mods });
        }
        _settings.Save();
        BuildVoices();
        var failed = RegisterVoiceKeys();
        StatusText.Text = vk == 0 ? $"У ГОЛОСА «{name}» БОЛЬШЕ НЕТ КЛАВИШИ"
            : failed.Contains(name) ? $"{KeyName(vk, mods)} ЗАНЯТА ДРУГОЙ ПРОГРАММОЙ — ВЫБЕРИ ДРУГУЮ (ПКМ ПО ГОЛОСУ)"
            : $"ГОЛОС «{name}» НА КЛАВИШЕ {KeyName(vk, mods)}" + (taken != null ? $" (У «{taken}» ОНА СНЯТА)" : "");
    }

    /// <summary>Register every voice key. Returns the voices whose key Windows refused (taken by another program).</summary>
    private HashSet<string> RegisterVoiceKeys()
    {
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_hwnd == IntPtr.Zero) return failed;
        for (int i = 0; i < MaxVoiceKeys; i++) UnregisterHotKey(_hwnd, VoiceHotkeyBase + i);
        _voiceKeyOrder.Clear();
        _settings.FxKeys.RemoveAll(k => !AllVoices().Any(v => SameName(v.Name, k.Name)));   // voice was deleted
        foreach (var k in _settings.FxKeys.Where(k => k.Vk != 0))
        {
            if (_voiceKeyOrder.Count >= MaxVoiceKeys) break;
            var voice = AllVoices().First(v => SameName(v.Name, k.Name));
            if (RegisterHotKey(_hwnd, VoiceHotkeyBase + _voiceKeyOrder.Count, (uint)k.Mods | 0x4000, (uint)k.Vk))
                _voiceKeyOrder.Add(voice);
            else
            {
                failed.Add(k.Name);
                _voiceKeyOrder.Add(voice);   // keep ids in step; this one just never fires
            }
        }
        return failed;
    }

    /// <summary>A voice key: switch to that voice; the same voice again while it's on — effect off.</summary>
    private bool OnVoiceHotkey(int id)
    {
        if (id < VoiceHotkeyBase || id >= VoiceHotkeyBase + MaxVoiceKeys) return false;
        int i = id - VoiceHotkeyBase;
        if (i >= _voiceKeyOrder.Count) return true;
        var voice = _voiceKeyOrder[i];
        if (_settings.FxOn && SameName(_settings.FxVoice, voice.Name)) SetFx(false, true);
        else PickVoice(voice);
        return true;
    }

    private void SaveFxParams()
    {
        PushFxParams();
        MarkVoice();
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
        _fx.P.AuxChannel = _vm.IsConnected && _vm.Type != Voicemeeter.VmType.Standard ? _vm.HardwareStrips * 2 + 8 : -1;
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
        if ((_settings.FxOn || _fx.ClipPlaying) && _vm.IsConnected)
        {
            if (!_fx.Start())
            {
                _settings.FxOn = false;
                FxOnT.IsChecked = false;
                _fx.Clip = null;
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
