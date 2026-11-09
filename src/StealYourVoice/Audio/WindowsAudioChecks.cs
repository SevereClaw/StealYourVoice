using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace StealYourVoice.Audio;

public record EndpointFormat(string Name, bool IsCapture, int SampleRate, int Channels);

/// <summary>Read-only peeks into Windows audio settings (plus a couple of safe per-user fixes).</summary>
public static class WindowsAudioChecks
{
    private const string MmDevices = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string PkeyDeviceFormat = "{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0";
    private const string PkeyEndpointName = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string PkeyInterfaceName = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    /// <summary>Shared-mode format of every active playback and recording device.</summary>
    public static List<EndpointFormat> ActiveEndpointFormats()
    {
        var list = new List<EndpointFormat>();
        foreach (var (sub, capture) in new[] { ("Render", false), ("Capture", true) })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var root = hklm.OpenSubKey($@"{MmDevices}\{sub}");
                if (root == null) continue;
                foreach (var id in root.GetSubKeyNames())
                {
                    try
                    {
                        using var dev = root.OpenSubKey(id);
                        if (dev?.GetValue("DeviceState") is not int state || state != 1) continue;
                        using var props = dev.OpenSubKey("Properties");
                        if (props == null) continue;

                        var endpoint = props.GetValue(PkeyEndpointName) as string ?? "?";
                        var iface = props.GetValue(PkeyInterfaceName) as string ?? "";
                        var name = string.IsNullOrEmpty(iface) ? endpoint : $"{endpoint} ({iface})";

                        if (props.GetValue(PkeyDeviceFormat) is byte[] blob && TryParseFormat(blob, out var sr, out var ch))
                            list.Add(new EndpointFormat(name, capture, sr, ch));
                    }
                    catch { /* skip device */ }
                }
            }
            catch { /* no access */ }
        }
        return list;
    }

    /// <summary>The value is a serialized PROPVARIANT blob wrapping a WAVEFORMATEX(TENSIBLE).</summary>
    private static bool TryParseFormat(byte[] b, out int sampleRate, out int channels)
    {
        sampleRate = 0; channels = 0;
        // usual layout: 8 bytes VT header + 4 bytes size, WAVEFORMATEX starts at 12
        foreach (var start in Candidates(b.Length))
        {
            if (start + 8 > b.Length) continue;
            int tag = BitConverter.ToUInt16(b, start);
            int ch = BitConverter.ToUInt16(b, start + 2);
            int sr = BitConverter.ToInt32(b, start + 4);
            if ((tag == 1 || tag == 3 || tag == 0xFFFE) && ch is >= 1 and <= 16 && sr is >= 8000 and <= 768000)
            {
                sampleRate = sr; channels = ch;
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<int> Candidates(int len)
    {
        yield return 12;
        yield return 8;
        yield return 16;
        for (int i = 0; i + 8 <= len && i < 64; i += 2) yield return i;
    }

    // ── microphone privacy ──

    public static bool MicrophoneBlocked()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(root);
            if (string.Equals(k?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)) return true;
            using var np = Registry.CurrentUser.OpenSubKey(root + @"\NonPackaged");
            if (string.Equals(np?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { /* ignore */ }
        return false;
    }

    // ── "Communications" ducking (Windows lowers other sounds during calls) ──

    private const string AudioKey = @"Software\Microsoft\Multimedia\Audio";

    /// <summary>True when Windows will lower game/music volume while you talk in Discord.</summary>
    public static bool DuckingEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(AudioKey);
            var v = k?.GetValue("UserDuckingPreference");
            return v is not int i || i != 3; // 3 = "Do nothing", missing = default (reduce by 80%)
        }
        catch { return false; }
    }

    public static void DisableDucking()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(AudioKey);
            k.SetValue("UserDuckingPreference", 3, RegistryValueKind.DWord);
        }
        catch { /* ignore */ }
    }

    public static void OpenSoundPanel() => Run("control.exe", "mmsys.cpl");
    public static void OpenMicPrivacy() => Run("explorer.exe", "ms-settings:privacy-microphone");

    private static void Run(string exe, string args)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
