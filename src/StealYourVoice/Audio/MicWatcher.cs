using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace StealYourVoice.Audio;

/// <summary>
/// Listens (only measures the level, records nothing) to microphones that are NOT on the Voicemeeter input,
/// so we can notice when you start talking into one of them.
/// </summary>
public sealed class MicWatcher : IDisposable
{
    private sealed class Tap
    {
        public string Name = "";
        public WasapiCapture Capture = null!;
        public MMDevice Device = null!;
        public volatile float Peak;      // linear, decays between reads
        public DateTime LoudSince = DateTime.MinValue;
    }

    private readonly Dictionary<string, Tap> _taps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names currently being listened to.</summary>
    public IEnumerable<string> Names => _taps.Keys;

    /// <summary>Listen to exactly these Windows capture devices (by endpoint name); stop the rest.</summary>
    public void Sync(ICollection<string> wanted)
    {
        foreach (var gone in _taps.Keys.Where(k => !wanted.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            Close(_taps[gone]);
            _taps.Remove(gone);
        }
        if (wanted.All(w => _taps.ContainsKey(w))) return;

        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                string name;
                try { name = dev.FriendlyName; } catch { dev.Dispose(); continue; }
                if (!wanted.Contains(name, StringComparer.OrdinalIgnoreCase) || _taps.ContainsKey(name)) { dev.Dispose(); continue; }

                var tap = new Tap { Name = name, Device = dev };
                try
                {
                    tap.Capture = new WasapiCapture(dev, true, 50);
                    var fmt = tap.Capture.WaveFormat;
                    tap.Capture.DataAvailable += (_, e) => tap.Peak = Math.Max(tap.Peak, PeakOf(e.Buffer, e.BytesRecorded, fmt));
                    tap.Capture.StartRecording();
                    _taps[name] = tap;
                }
                catch
                {
                    // busy in exclusive mode, unplugged meanwhile… just skip it
                    try { tap.Capture?.Dispose(); } catch { /* ignore */ }
                    dev.Dispose();
                }
            }
        }
        catch { /* audio service busy */ }
    }

    /// <summary>Read and decay each mic's peak (linear).</summary>
    public List<(string Name, float Peak)> Read()
    {
        var list = new List<(string, float)>(_taps.Count);
        foreach (var t in _taps.Values)
        {
            var p = t.Peak;
            t.Peak = p * 0.5f;
            list.Add((t.Name, p));
        }
        return list;
    }

    public DateTime LoudSince(string name) => _taps.TryGetValue(name, out var t) ? t.LoudSince : DateTime.MinValue;
    public void SetLoudSince(string name, DateTime v) { if (_taps.TryGetValue(name, out var t)) t.LoudSince = v; }

    private static float PeakOf(byte[] buf, int bytes, WaveFormat f)
    {
        float peak = 0;
        try
        {
            if (f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32))
            {
                for (int i = 0; i + 4 <= bytes; i += 4)
                {
                    float v = Math.Abs(BitConverter.ToSingle(buf, i));
                    if (v > peak) peak = v;
                }
            }
            else if (f.BitsPerSample == 16)
            {
                for (int i = 0; i + 2 <= bytes; i += 2)
                {
                    float v = Math.Abs(BitConverter.ToInt16(buf, i) / 32768f);
                    if (v > peak) peak = v;
                }
            }
            else if (f.BitsPerSample == 24)
            {
                for (int i = 0; i + 3 <= bytes; i += 3)
                {
                    int s = (buf[i] << 8 | buf[i + 1] << 16 | buf[i + 2] << 24) >> 8;
                    float v = Math.Abs(s / 8388608f);
                    if (v > peak) peak = v;
                }
            }
        }
        catch { /* odd format */ }
        return Math.Min(peak, 1.5f);
    }

    private static void Close(Tap t)
    {
        try { t.Capture.StopRecording(); } catch { /* ignore */ }
        try { t.Capture.Dispose(); } catch { /* ignore */ }
        try { t.Device.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        foreach (var t in _taps.Values) Close(t);
        _taps.Clear();
    }
}
