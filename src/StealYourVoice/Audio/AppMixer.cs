using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace StealYourVoice.Audio;

/// <summary>One program with sound, as the Windows volume mixer sees it (all its sessions on all devices).</summary>
public sealed class AppAudio
{
    public string Process = "";
    public string Title = "";
    public readonly List<AudioSessionControl> Sessions = new();

    public float Volume
    {
        get { try { return Sessions.Count == 0 ? 1 : Sessions.Max(s => s.SimpleAudioVolume.Volume); } catch { return 1; } }
        set { foreach (var s in Sessions) try { s.SimpleAudioVolume.Volume = Math.Clamp(value, 0, 1); } catch { /* gone */ } }
    }

    public bool Mute
    {
        get { try { return Sessions.Count > 0 && Sessions.All(s => s.SimpleAudioVolume.Mute); } catch { return false; } }
        set { foreach (var s in Sessions) try { s.SimpleAudioVolume.Mute = value; } catch { /* gone */ } }
    }

    /// <summary>Current output level 0..1 (what the mixer's green bar shows).</summary>
    public float Peak
    {
        get { try { return Sessions.Count == 0 ? 0 : Sessions.Max(s => s.AudioMeterInformation.MasterPeakValue); } catch { return 0; } }
    }
}

/// <summary>Per-program volume, like the Windows mixer, for the panel.</summary>
public static class AppMixer
{
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "audiodg" };
    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase);

    public static List<AppAudio> List()
    {
        var byName = new Dictionary<string, AppAudio>(StringComparer.OrdinalIgnoreCase);
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
                            if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                            uint pid = s.GetProcessID;
                            if (pid == 0) continue; // Windows system sounds
                            string name;
                            using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) name = p.ProcessName;
                            // Voicemeeter's own session carries everything — that's what the master sliders are for
                            if (Hidden.Contains(name) || name.StartsWith("voicemeeter", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!byName.TryGetValue(name, out var app))
                                byName[name] = app = new AppAudio { Process = name, Title = TitleOf((int)pid, name) };
                            app.Sessions.Add(s);
                        }
                        catch { /* session or process gone */ }
                    }
                }
                catch { /* device without sessions */ }
            }
        }
        catch { /* audio service busy */ }
        return byName.Values.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>"Google Chrome" instead of "chrome" where Windows knows the nicer name.</summary>
    private static string TitleOf(int pid, string process)
    {
        if (Titles.TryGetValue(process, out var t)) return t;
        string title = process;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            var d = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(d) && d.Length <= 40) title = d.Trim();
        }
        catch { /* protected or 32/64-bit mismatch — keep the process name */ }
        if (string.Equals(process, "StealYourVoice", StringComparison.OrdinalIgnoreCase)) title = "Steal Your Voice (звуки панели)";
        Titles[process] = title;
        return title;
    }
}
