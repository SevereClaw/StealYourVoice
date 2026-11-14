using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace StealYourVoice;

public sealed class UserPreset
{
    public string Name { get; set; } = "";
    public float Gain { get; set; }
    public float Gate { get; set; }
    public float Comp { get; set; }
    public float Tone { get; set; }   // Strip Color_x
    public bool Mono { get; set; }
    public bool A1 { get; set; }
    public bool B1 { get; set; } = true;
    public float OutGain { get; set; } // Bus B1 gain

    /// <summary>Microphone device that was selected when the preset was saved (empty = don't touch the device).</summary>
    public string DeviceName { get; set; } = "";
    public string DeviceKind { get; set; } = ""; // wdm / mme / ks

    /// <summary>When this preset was last switched on (picks between several presets of the same mic).</summary>
    public DateTime LastUsed { get; set; }

    /// <summary>Process name (without .exe) that auto-applies this preset while it runs.</summary>
    public string? Process { get; set; }
}

public sealed class SoundSlot
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int HotkeyVk { get; set; }
    public int HotkeyMods { get; set; }
}

public sealed class Settings
{
    public int Strip { get; set; }

    /// <summary>Virtual-key code of the mute hotkey (0 = none). Default: Pause.</summary>
    public int HotkeyVk { get; set; } = 0x13;

    /// <summary>MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8.</summary>
    public int HotkeyMods { get; set; }

    public bool ShowToast { get; set; } = true;
    public bool SoundsEnabled { get; set; } = true;

    /// <summary>Last open tab of the main window.</summary>
    public int Tab { get; set; }

    /// <summary>Switching to / plugging in a mic turns on the preset that was saved with it.</summary>
    public bool MicPresetAuto { get; set; } = true;

    /// <summary>Start talking into another mic that has a preset → switch the input (and the preset) to it.</summary>
    public bool FollowVoice { get; set; }

    /// <summary>Monitor for pop-up notifications: 0 = main, 1… = other monitors left to right.</summary>
    public int NotifyScreen { get; set; }

    /// <summary>Warn when two microphones are heard in Discord / the recording at the same time.</summary>
    public bool TwoMicWarn { get; set; } = true;

    // voice changer
    public bool FxOn { get; set; }
    public string FxVoice { get; set; } = "ДЕМОН";
    public float FxPitch { get; set; } = -7;
    public float FxRobot { get; set; }
    public float FxRadio { get; set; } = 0.15f;
    public float FxEcho { get; set; }
    public float FxCave { get; set; } = 0.3f;
    public int FxHotkeyVk { get; set; }

    // soundboard
    public List<SoundSlot> Sounds { get; set; } = new();
    public float SoundToFriends { get; set; } = 0.8f;
    public float SoundToMe { get; set; } = 0.6f;
    public int SoundStopVk { get; set; }
    public int SoundStopMods { get; set; }
    public int FxHotkeyMods { get; set; }

    /// <summary>
    /// A virtual bus (B2 on Banana, B3 on Potato) carries only Discord, so CyberCall can subtitle friends
    /// while recorders never see that bus.
    /// </summary>
    public bool CaptionsBus { get; set; }

    /// <summary>
    /// Record Discord (streams) through B2, but cut it out of B2 while a friend is talking —
    /// the recording keeps game + streams + you, without friends' voices.
    /// </summary>
    public bool StreamsNoVoices { get; set; }

    /// <summary>Keyboard volume keys drive the Voicemeeter headphones bus (A1) while Voicemeeter is the default device.</summary>
    public bool VolumeKeys { get; set; } = true;

    // auto-ducking
    public bool DuckEnabled { get; set; }
    public bool DuckMain { get; set; } = true;  // "Voicemeeter Input" virtual strip
    public bool DuckAux { get; set; }           // "Voicemeeter AUX" virtual strip
    public double DuckAmount { get; set; } = -12;
    public double DuckThreshold { get; set; } = -40;
    /// <summary>Programs that must NOT be ducked. Non-empty = duck app by app instead of whole Voicemeeter inputs.</summary>
    public List<string> DuckExclude { get; set; } = new();
    public bool DuckFriends { get; set; }       // duck the game while Discord (AUX) is talking

    /// <summary>Corner mic indicator: 0 = off, 1..4 = TL, TR, BL, BR.</summary>
    public int OverlayCorner { get; set; }
    public double OverlayScale { get; set; } = 1.0;

    /// <summary>Free position of the mic indicator (set by Shift+drag); null = snapped to OverlayCorner.</summary>
    public double? OverlayX { get; set; }
    public double? OverlayY { get; set; }

    public List<UserPreset> Presets { get; set; } = new();

    // Discord per-friend volume (secret and tokens are DPAPI-encrypted)
    public string DiscordClientId { get; set; } = "";
    public string DiscordSecretEnc { get; set; } = "";
    public string DiscordAccessEnc { get; set; } = "";
    public string DiscordRefreshEnc { get; set; } = "";
    public DateTime DiscordTokenExpires { get; set; }
    public Dictionary<string, int> FriendVolumes { get; set; } = new();

    /// <summary>"Who is talking" overlay: 0 = off, 1..4 = TL, TR, BL, BR.</summary>
    public int SpeakersCorner { get; set; }
    public bool SpeakersOnlyTalking { get; set; }
    public bool SpeakersAvatarsOnly { get; set; }
    public bool SpeakersShowSelf { get; set; } = true;
    public bool JoinLeaveToasts { get; set; } = true;
    public bool SpeakersLoudness { get; set; } = true;
    public double SpeakersSilentOpacity { get; set; } = 0.3;
    public double SpeakersScale { get; set; } = 1.0;

    /// <summary>Free position of the list (set by Shift+drag); null = snapped to SpeakersCorner.</summary>
    public double? SpeakersX { get; set; }
    public double? SpeakersY { get; set; }

    /// <summary>People pulled out of the list and parked on their own: user id → [x, y].</summary>
    public Dictionary<string, double[]> SpeakerPositions { get; set; } = new();

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StealYourVoice");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();

            // migrate from v0.1 strip.txt
            var s = new Settings();
            var old = Path.Combine(Dir, "strip.txt");
            if (File.Exists(old) && int.TryParse(File.ReadAllText(old).Trim(), out var strip)) s.Strip = strip;
            return s;
        }
        catch
        {
            return new Settings();
        }
    }

    private static System.Windows.Threading.DispatcherTimer? _saveTimer;
    private static Settings? _pending;

    /// <summary>Queue a save: a burst of changes (dragging a slider, rolling the wheel) becomes one write.</summary>
    public void Save()
    {
        _pending = this;
        try
        {
            if (_saveTimer == null)
            {
                _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (_, _) => { _saveTimer!.Stop(); _pending?.Flush(); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        catch { Flush(); }
    }

    /// <summary>Write to disk right now (called on exit).</summary>
    public void Flush()
    {
        _pending = null;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* ignore */ }
    }
}
