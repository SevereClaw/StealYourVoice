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

    /// <summary>Process name (without .exe) that auto-applies this preset while it runs.</summary>
    public string? Process { get; set; }
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

    // auto-ducking
    public bool DuckEnabled { get; set; }
    public bool DuckMain { get; set; } = true;  // "Voicemeeter Input" virtual strip
    public bool DuckAux { get; set; }           // "Voicemeeter AUX" virtual strip
    public double DuckAmount { get; set; } = -12;
    public double DuckThreshold { get; set; } = -40;
    public bool DuckFriends { get; set; }       // duck the game while Discord (AUX) is talking

    /// <summary>Corner mic indicator: 0 = off, 1..4 = TL, TR, BL, BR.</summary>
    public int OverlayCorner { get; set; }

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
    public double SpeakersSilentOpacity { get; set; } = 0.3;
    public double SpeakersScale { get; set; } = 1.0;

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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* ignore */ }
    }
}
