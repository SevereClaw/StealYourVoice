using System;
using System.Collections.Concurrent;
using System.IO;
using NAudio.Wave;

namespace StealYourVoice.Audio;

/// <summary>Turns mp3 / wav / ogg* / m4a / flac files into ready-to-mix stereo floats (*whatever Windows can decode).</summary>
public static class SoundDecoder
{
    public const int MaxSeconds = 60;
    private static readonly ConcurrentDictionary<string, float[]> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static float[]? Load(string path, int sampleRate)
    {
        var key = path + "|" + sampleRate + "|" + SafeStamp(path);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        try
        {
            using var reader = new MediaFoundationReader(path);
            using var rs = new MediaFoundationResampler(reader, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)) { ResamplerQuality = 60 };
            var max = sampleRate * 2 * 4 * MaxSeconds;
            using var ms = new MemoryStream();
            var buf = new byte[65536];
            int n;
            while (ms.Length < max && (n = rs.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
            var bytes = ms.GetBuffer();
            int count = (int)Math.Min(ms.Length, max) / 4;
            var data = new float[count - count % 2];
            Buffer.BlockCopy(bytes, 0, data, 0, data.Length * 4);
            Cache[key] = data;
            return data;
        }
        catch { return null; }
    }

    private static long SafeStamp(string p)
    {
        try { return File.GetLastWriteTimeUtc(p).Ticks; } catch { return 0; }
    }
}
