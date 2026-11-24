using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using StealYourVoice.Voicemeeter;

namespace StealYourVoice;

/// <summary>
/// MidnightClips records every program's sound by itself (Discord, music, the game — each on its own track)
/// and takes only the microphone from a Voicemeeter bus. If that bus also carries the computer sound
/// (as ShadowPlay needs, see «ИГРА → КЛИПЫ (B2)»), music lands in the clip's mic track too and can't be
/// switched off there. In this mode the bus MidnightClips listens to carries only your voice.
/// </summary>
public partial class MainWindow
{
    private string? _midnightBus;          // "B1" / "B2" / "B3" — the bus MidnightClips uses as its microphone
    private bool _midnightInstalled;
    private string _midnightMic = "";

    /// <summary>MidnightClips settings: which Voicemeeter bus it takes as the microphone (null — not a Voicemeeter bus).</summary>
    private static (bool installed, string mic, string? bus) ReadMidnightClips()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MidnightClips", "settings.json");
            if (!File.Exists(path)) return (false, "", null);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var mic = doc.RootElement.TryGetProperty("mic", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            return (true, mic, BusOfDevice(mic));
        }
        catch
        {
            return (false, "", null);
        }
    }

    /// <summary>Voicemeeter's recording devices are its virtual buses: «Voicemeeter Out B2» (new driver names)
    /// or «VoiceMeeter Output / Aux Output / VAIO3 Output» (old ones).</summary>
    internal static string? BusOfDevice(string device)
    {
        var d = device.ToLowerInvariant();
        if (!d.Contains("voicemeeter")) return null;
        var m = Regex.Match(d, @"out\s*b\s*([1-3])");
        if (m.Success) return "B" + m.Groups[1].Value;
        if (d.Contains("vaio3 output")) return "B3";
        if (d.Contains("aux output")) return "B2";
        if (d.Contains("voicemeeter output")) return "B1";
        return null;
    }

    private void InitMidnight()
    {
        MidnightT.Click += t =>
        {
            _settings.MidnightClips = t.IsChecked;
            _settings.Save();
            if (t.IsChecked) ApplyMidnight(true);
            else
            {
                StatusText.Text = "MIDNIGHT CLIPS: ШИНА " + (_midnightBus ?? "B2") + " БОЛЬШЕ НЕ ТРОГАЮ";
                _paramsDirty = true;   // вернуть кнопки режимов B2 для ShadowPlay
                RefreshMidnightUi();
            }
        };
        MidnightTick();
    }

    /// <summary>Every couple of seconds: MidnightClips could have been installed, or its microphone changed;
    /// Voicemeeter could have been restarted or switched by hand — put the routing back.</summary>
    private void MidnightTick()
    {
        var (installed, mic, bus) = ReadMidnightClips();
        _midnightInstalled = installed;
        _midnightMic = mic;
        _midnightBus = bus;
        // first time we see MidnightClips listening to a Voicemeeter bus — switch the mode on by ourselves
        if (_settings.MidnightClips == null && bus != null)
        {
            _settings.MidnightClips = true;
            _settings.Save();
            if (_vm.IsConnected) ApplyMidnight(true);
        }
        else if (_settings.MidnightClips == true && bus != null && _vm.IsConnected && MidnightBroken(bus))
        {
            ApplyMidnight(false);
        }
        RefreshMidnightUi();
    }

    /// <summary>Virtual inputs Voicemeeter has: «Voicemeeter Input», «AUX» (Banana, Potato) and «VAIO3» (Potato).</summary>
    private int[] VirtualStrips()
    {
        int vin = _vm.HardwareStrips;
        return _vm.Type switch
        {
            VmType.Potato => new[] { vin, vin + 1, vin + 2 },
            VmType.Banana => new[] { vin, vin + 1 },
            _ => new[] { vin },
        };
    }

    private bool BusExists(string bus) => bus switch
    {
        "B1" => true,
        "B2" => _vm.Type is VmType.Banana or VmType.Potato,
        "B3" => _vm.Type == VmType.Potato,
        _ => false,
    };

    private bool MidnightBroken(string bus)
    {
        if (!BusExists(bus)) return false;
        if (_vm.Get(S(bus)) < 0.5f) return true;
        foreach (int s in VirtualStrips())
            if (_vm.Get($"Strip[{s}].{bus}") >= 0.5f) return true;
        return false;
    }

    private void ApplyMidnight(bool announce)
    {
        var bus = _midnightBus;
        if (!_vm.IsConnected || bus == null || !BusExists(bus)) { RefreshMidnightUi(); return; }

        // modes that put other things onto the same bus can't run together with this one
        if (bus == "B2" && _settings.StreamsNoVoices) SetStreamRec(false, false);
        if (_settings.CaptionsBus && CaptionsBusName == bus)
        {
            _settings.CaptionsBus = false;
            _settings.Save();
            ApplyCaptionsBus(false);
            CaptionsT.IsChecked = false;
        }

        // only your voice (with the soundboard and voice effects — they live in the mic strip) on that bus;
        // computer sound, music and Discord are recorded by MidnightClips from the programs themselves
        var script = $"{S(bus)}=1;";
        foreach (int s in VirtualStrips()) script += $"Strip[{s}].{bus}=0;";
        _vm.Script(script);
        if (announce)
            StatusText.Text = $"MIDNIGHT CLIPS: В {bus} ТОЛЬКО ГОЛОС — МУЗЫКУ, ИГРУ И DISCORD ОН ПИШЕТ САМ, КАЖДОЕ НА СВОЮ ДОРОЖКУ";
        _paramsDirty = true;
        RefreshMidnightUi();
    }

    private void RefreshMidnightUi()
    {
        bool on = _settings.MidnightClips == true && _midnightBus != null;
        MidnightT.Visibility = _midnightInstalled ? Visibility.Visible : Visibility.Collapsed;
        MidnightT.IsChecked = _settings.MidnightClips == true;
        var text = _midnightBus != null ? $"MIDNIGHT CLIPS ({_midnightBus})" : "MIDNIGHT CLIPS";
        if (MidnightT.Text != text) MidnightT.Text = text;
        var hint = _midnightBus != null
            ? $"MidnightClips берёт микрофон с шины {_midnightBus}. Включено — на {_midnightBus} только твой голос (с саундбордом и эффектами), а музыку, игру и Discord MidnightClips пишет сам, каждое на свою дорожку: в клипе их можно выключать по отдельности"
            : "В MidnightClips микрофоном выбрано не устройство Voicemeeter" + (_midnightMic.Length > 0 ? $" ({_midnightMic})" : "") +
              ". Выбери там «Voicemeeter Out B1» или «B2» — тогда голос пойдёт с эффектами и саундбордом";
        if (!Equals(MidnightT.ToolTip, hint)) MidnightT.ToolTip = hint;
        // the B2 clip modes for ShadowPlay would mix the computer sound into the mic track again
        bool b2 = on && _midnightBus == "B2";
        if (b2)
        {
            GameToB2T.Visibility = Visibility.Collapsed;
            FriendsClipsT.Visibility = Visibility.Collapsed;
        }
        StreamRecT.Visibility = b2 ? Visibility.Collapsed : Visibility.Visible;
        CaptionsT.Visibility = on && CaptionsBusName == _midnightBus ? Visibility.Collapsed : Visibility.Visible;
    }
}
