using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StealYourVoice.Voicemeeter;

public enum VmType { None = 0, Standard = 1, Banana = 2, Potato = 3 }

public record VmDevice(string Kind, string Name)
{
    public override string ToString() => $"{Kind.ToUpperInvariant()}: {Name}";
}

/// <summary>High-level, exception-free wrapper over the Voicemeeter Remote API.</summary>
public sealed class VmClient : IDisposable
{
    private bool _loggedIn;

    public VmType Type { get; private set; }
    public string LastError { get; private set; } = "";
    public bool IsConnected => _loggedIn && Type != VmType.None;

    /// <summary>Hardware input strips == hardware A buses for every Voicemeeter edition.</summary>
    public int HardwareStrips => Type switch
    {
        VmType.Standard => 2,
        VmType.Banana => 3,
        VmType.Potato => 5,
        _ => 0
    };

    /// <summary>Bus index of B1 (virtual output used for recording).</summary>
    public int B1Index => HardwareStrips;

    public bool Connect()
    {
        try
        {
            if (!VmNative.TryInit(out var err)) { LastError = err; return false; }

            if (!_loggedIn)
            {
                var r = VmNative.Login();
                if (r < 0) { LastError = $"Не удалось подключиться к Voicemeeter (код {r})."; return false; }
                _loggedIn = true;
            }

            if (VmNative.GetVoicemeeterType(out var t) != 0 || t < 1 || t > 3)
            {
                Type = VmType.None;
                LastError = "Voicemeeter не запущен.";
                return false;
            }

            Type = (VmType)t;
            VmNative.IsParametersDirty(); // first call primes the cache
            LastError = "";
            return true;
        }
        catch (DllNotFoundException) { LastError = "Не удалось загрузить VoicemeeterRemote64.dll."; }
        catch (EntryPointNotFoundException) { LastError = "Старая версия Voicemeeter, обнови её."; }
        catch (Exception ex) { LastError = ex.Message; }
        return false;
    }

    /// <summary>Launch Voicemeeter Banana (falls back to Standard). Requires login first.</summary>
    public void Launch()
    {
        try
        {
            if (!VmNative.TryInit(out _)) return;
            if (!_loggedIn && VmNative.Login() >= 0) _loggedIn = true;
            if (VmNative.RunVoicemeeter(2) != 0) VmNative.RunVoicemeeter(1);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Returns true when parameters changed since the last call.
    /// Drops the connection if Voicemeeter was closed.
    /// </summary>
    public bool IsDirty()
    {
        if (!IsConnected) return false;
        try
        {
            var r = VmNative.IsParametersDirty();
            if (r < 0) { Type = VmType.None; LastError = "Voicemeeter закрыт."; return false; }
            return r == 1;
        }
        catch { return false; }
    }

    public float Get(string param, float fallback = 0f)
    {
        try { return IsConnected && VmNative.GetParameterFloat(param, out var v) == 0 ? v : fallback; }
        catch { return fallback; }
    }

    public string GetString(string param)
    {
        try
        {
            if (!IsConnected) return "";
            var sb = new StringBuilder(512);
            return VmNative.GetParameterStringA(param, sb) == 0 ? sb.ToString() : "";
        }
        catch { return ""; }
    }

    public void Set(string param, float value)
    {
        try { if (IsConnected) VmNative.SetParameterFloat(param, value); } catch { /* ignore */ }
    }

    public void Script(string script)
    {
        try { if (IsConnected) VmNative.SetParameters(script); } catch { /* ignore */ }
    }

    public static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Peak level (linear) of an input strip, post-fader.</summary>
    public float StripLevel(int strip)
    {
        if (!IsConnected) return 0;
        int hw = HardwareStrips;
        int first, count;
        if (strip < hw) { first = strip * 2; count = 2; }
        else { first = hw * 2 + (strip - hw) * 8; count = 2; }
        return PeakOf(1, first, count);
    }

    /// <summary>Peak level (linear) of an output bus.</summary>
    public float BusLevel(int bus) => IsConnected ? PeakOf(3, bus * 8, 2) : 0;

    private static float PeakOf(int type, int first, int count)
    {
        float peak = 0;
        try
        {
            for (int c = first; c < first + count; c++)
                if (VmNative.GetLevel(type, c, out var v) == 0 && v > peak) peak = v;
        }
        catch { /* ignore */ }
        return peak;
    }

    public List<VmDevice> InputDevices() => Devices(false);
    public List<VmDevice> OutputDevices() => Devices(true);

    private List<VmDevice> Devices(bool output)
    {
        var list = new List<VmDevice>();
        if (!IsConnected) return list;
        try
        {
            int n = output ? VmNative.OutputGetDeviceNumber() : VmNative.InputGetDeviceNumber();
            for (int i = 0; i < n; i++)
            {
                var name = new StringBuilder(512);
                var hw = new StringBuilder(512);
                int t;
                int r = output
                    ? VmNative.OutputGetDeviceDescA(i, out t, name, hw)
                    : VmNative.InputGetDeviceDescA(i, out t, name, hw);
                if (r != 0) continue;
                var kind = t switch { 1 => "mme", 3 => "wdm", 4 => "ks", 5 => "asio", _ => "" };
                if (kind == "" || kind == "asio") continue;
                list.Add(new VmDevice(kind, name.ToString()));
            }
        }
        catch { /* ignore */ }
        return list;
    }

    public void SetStripDevice(int strip, VmDevice d) =>
        Script($"Strip[{strip}].device.{d.Kind}=\"{d.Name.Replace("\"", "")}\";");

    public void SetBusDevice(int bus, VmDevice d) =>
        Script($"Bus[{bus}].device.{d.Kind}=\"{d.Name.Replace("\"", "")}\";");

    public void Dispose()
    {
        try { if (_loggedIn) VmNative.Logout(); } catch { /* ignore */ }
        _loggedIn = false;
    }
}
