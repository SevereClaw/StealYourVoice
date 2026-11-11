using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace StealYourVoice.Audio;

/// <summary>
/// Ducks individual programs through the Windows volume mixer, so chosen ones can be left alone.
/// </summary>
public sealed class AppDucker
{
    /// <summary>Never touched: ourselves, the mixer, and Discord (friends must stay audible).</summary>
    private static readonly HashSet<string> Always = new(StringComparer.OrdinalIgnoreCase)
    {
        "StealYourVoice", "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment",
        "voicemeeter", "voicemeeterpro", "voicemeeter8", "voicemeeter8x64", "audiodg"
    };

    private static readonly string BackupFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StealYourVoice", "ducked-apps.json");

    private readonly List<(SimpleAudioVolume Volume, float Base, string Name)> _held = new();

    /// <summary>Process names (no .exe) that must not be ducked.</summary>
    public ICollection<string> Exclude { get; set; } = new List<string>();

    public static bool IsAlwaysSkipped(string name) => Always.Contains(name);

    private static IEnumerable<(SimpleAudioVolume Volume, string Name)> Sessions()
    {
        var result = new List<(SimpleAudioVolume, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var mgr = dev.AudioSessionManager;
                    mgr.RefreshSessions();
                    var sessions = mgr.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var s = sessions[i];
                            uint pid = s.GetProcessID;
                            if (pid == 0) continue; // system sounds
                            string name;
                            using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName;
                            result.Add((s.SimpleAudioVolume, name));
                        }
                        catch { /* session or process already gone */ }
                    }
                }
                catch { /* device without session support */ }
            }
        }
        catch { /* audio service unavailable */ }
        return result;
    }

    /// <summary>Programs that have a sound session right now (for the "don't duck" menu).</summary>
    public static List<string> AppsWithSound() =>
        Sessions().Select(s => s.Name)
            .Where(n => !Always.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Capture()
    {
        _held.Clear();
        foreach (var (vol, name) in Sessions())
        {
            if (Always.Contains(name) || Exclude.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            try { _held.Add((vol, vol.Volume, name)); } catch { /* ignore */ }
        }

        // the mixer remembers app volumes forever, so leave a note in case we die while ducked
        try
        {
            var backup = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in _held) backup[h.Name] = Math.Max(h.Base, backup.GetValueOrDefault(h.Name));
            Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
            File.WriteAllText(BackupFile, JsonSerializer.Serialize(backup));
        }
        catch { /* ignore */ }
    }

    public void Apply(double offsetDb)
    {
        double k = Math.Pow(10, offsetDb / 20.0);
        foreach (var h in _held)
        {
            try { h.Volume.Volume = (float)Math.Clamp(h.Base * k, 0, 1); } catch { /* app closed */ }
        }
    }

    public void Release()
    {
        Apply(0);
        _held.Clear();
        try { if (File.Exists(BackupFile)) File.Delete(BackupFile); } catch { /* ignore */ }
    }

    /// <summary>If the previous run ended while apps were ducked, give them their volume back.</summary>
    public static void RestoreAfterCrash()
    {
        try
        {
            if (!File.Exists(BackupFile)) return;
            var backup = JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(BackupFile));
            if (backup != null)
            {
                var byName = new Dictionary<string, float>(backup, StringComparer.OrdinalIgnoreCase);
                foreach (var (vol, name) in Sessions())
                {
                    if (!byName.TryGetValue(name, out var v)) continue;
                    try { vol.Volume = Math.Clamp(v, 0, 1); } catch { /* ignore */ }
                }
            }
            File.Delete(BackupFile);
        }
        catch { /* ignore */ }
    }
}
