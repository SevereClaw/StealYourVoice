using System;
using System.Collections.Generic;
using System.IO;
using System.Media;

namespace StealYourVoice.Audio;

/// <summary>
/// UI sound effects. All built-in sounds are synthesized here (original, no game assets).
/// Users can override any of them by dropping a .wav with the same name into
/// %AppData%\StealYourVoice\sounds\ (hover.wav, click.wav, confirm.wav, cancel.wav, whoosh.wav).
/// </summary>
public static class Sfx
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StealYourVoice", "sounds");

    public static readonly string[] Names = { "hover", "click", "confirm", "cancel", "whoosh", "tab", "preset" };

    private const int Rate = 44100;
    private static readonly Dictionary<string, SoundPlayer> Players = new();
    private static DateTime _lastHover = DateTime.MinValue;
    private static bool _loaded;

    public static bool Enabled { get; set; } = true;

    public static void Load()
    {
        foreach (var p in Players.Values) p.Dispose();
        Players.Clear();
        try { Directory.CreateDirectory(Folder); } catch { /* ignore */ }

        foreach (var name in Names)
        {
            try
            {
                Stream stream;
                var custom = Path.Combine(Folder, name + ".wav");
                if (File.Exists(custom)) stream = new MemoryStream(File.ReadAllBytes(custom));
                else stream = new MemoryStream(Wav(Synth(name)));
                var player = new SoundPlayer(stream);
                player.Load();
                Players[name] = player;
            }
            catch { /* ignore broken file */ }
        }
        _loaded = true;
    }

    public static void Play(string name)
    {
        if (!Enabled) return;
        if (!_loaded) Load();
        if (name == "hover")
        {
            // hovering across a row of buttons shouldn't machine-gun
            if ((DateTime.Now - _lastHover).TotalMilliseconds < 70) return;
            _lastHover = DateTime.Now;
        }
        try { if (Players.TryGetValue(name, out var p)) p.Play(); } catch { /* ignore */ }
    }

    // ───────────────────────── synthesis ─────────────────────────

    private static float[] Synth(string name) => name switch
    {
        "hover" => Blip(2400, 0.018, 0.18, square: false),
        "click" => Mix(Blip(1500, 0.035, 0.35, square: true), Noise(0.012, 0.25, 0)),
        "confirm" => Normalize(Concat(Bell(784, 0.06, 0.7), Bell(1175, 0.16, 1.0)), 0.18),
        "cancel" => Normalize(Concat(Bell(659, 0.06, 0.7), Bell(440, 0.16, 1.0)), 0.16),
        "preset" => Chime(),
        "whoosh" => Whoosh(0.26),
        "tab" => SoftPluck(),
        _ => Blip(1000, 0.03, 0.3, false)
    };

    private static float[] Blip(double freq, double dur, double vol, bool square)
    {
        int n = (int)(dur * Rate);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Exp(-t * 90);
            double ph = Math.Sin(2 * Math.PI * freq * t);
            double v = square ? Math.Sign(ph) * 0.6 + ph * 0.4 : ph;
            s[i] = (float)(v * env * vol);
        }
        return s;
    }

    /// <summary>Bright detuned saw "stab" with a quick pitch drop — punchy menu confirm.</summary>
    private static float[] Stab(double freq, double dur)
    {
        int n = (int)(dur * Rate);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double f = freq * (1 + 0.08 * Math.Exp(-t * 60));
            double saw1 = 2 * ((f * t) % 1.0) - 1;
            double saw2 = 2 * ((f * 1.007 * t) % 1.0) - 1;
            double sub = Math.Sin(2 * Math.PI * f * 0.5 * t);
            double env = Math.Min(1, t * 800) * Math.Exp(-t * 28);
            s[i] = (float)((saw1 * 0.35 + saw2 * 0.35 + sub * 0.3) * env * 0.32);
        }
        return s;
    }

    private static float[] Noise(double dur, double vol, int seed)
    {
        var rnd = new Random(seed);
        int n = (int)(dur * Rate);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            s[i] = (float)((rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 300) * vol);
        }
        return s;
    }

    /// <summary>
    /// Soft, dark air swish for the slash transitions. Two low-pass stages keep the hiss out,
    /// the envelope rises and falls smoothly, and it stays quiet — it should be felt more than heard.
    /// </summary>
    private static float[] Whoosh(double dur)
    {
        var rnd = new Random(7);
        int n = (int)(dur * Rate);
        var s = new float[n];
        double lp1 = 0, lp2 = 0, hp = 0, prev = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n;
            double cutoff = 0.008 + 0.05 * Math.Sin(Math.PI * Math.Pow(x, 0.8)); // gentle sweep, never bright
            double noise = rnd.NextDouble() * 2 - 1;
            lp1 += cutoff * (noise - lp1);
            lp2 += cutoff * (lp1 - lp2);                  // second stage: no fizz on top
            hp = 0.995 * (hp + lp2 - prev); prev = lp2;   // drop sub-rumble
            double env = Math.Pow(Math.Sin(Math.PI * x), 2); // smooth in, smooth out — no click at the edges
            s[i] = (float)(hp * env * 2.6);
        }
        return Normalize(s, 0.22);
    }

    /// <summary>Round sine note with a soft attack — mellow replacement for the old saw stabs.</summary>
    private static float[] Bell(double freq, double dur, double vol)
    {
        int n = (int)(dur * Rate);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double v = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 2 * t) * Math.Exp(-t * 40);
            double env = Math.Min(1, t * 500) * Math.Exp(-t * 18) * Math.Min(1, (dur - t) * 200); // fade the tail, no click
            s[i] = (float)(v * env * vol);
        }
        return s;
    }

    /// <summary>Preset switched: three soft overlapping notes rising, like a quiet music-box arpeggio.</summary>
    private static float[] Chime()
    {
        double[] notes = { 523.25, 659.25, 783.99 };
        int step = (int)(0.07 * Rate), len = (int)(0.42 * Rate);
        var s = new float[step * (notes.Length - 1) + len];
        for (int k = 0; k < notes.Length; k++)
        {
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / Rate;
                double v = Math.Sin(2 * Math.PI * notes[k] * t) + 0.12 * Math.Sin(2 * Math.PI * notes[k] * 3 * t) * Math.Exp(-t * 30);
                double env = Math.Min(1, t * 300) * Math.Exp(-t * 7.5) * Math.Min(1, (len - i) / (0.03 * Rate));
                s[k * step + i] += (float)(v * env);
            }
        }
        return Normalize(s, 0.14);
    }

    /// <summary>Muted wooden "tock" for tabs: a warm sine with a soft attack and a quick, round decay.</summary>
    private static float[] SoftPluck()
    {
        int n = (int)(0.11 * Rate);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double f = 440 * (1 + 0.04 * Math.Exp(-t * 80));          // tiny pitch settle
            double v = Math.Sin(2 * Math.PI * f * t) * 0.8
                     + Math.Sin(2 * Math.PI * f * 2 * t) * 0.15 * Math.Exp(-t * 60);
            double env = Math.Min(1, t * 400) * Math.Exp(-t * 32);    // 2.5 ms fade-in, so no click
            s[i] = (float)(v * env);
        }
        return Normalize(s, 0.16);
    }

    private static float[] Normalize(float[] s, double peak)
    {
        float max = 0;
        foreach (var v in s) max = Math.Max(max, Math.Abs(v));
        if (max <= 0) return s;
        var k = (float)(peak / max);
        for (int i = 0; i < s.Length; i++) s[i] *= k;
        return s;
    }

    private static float[] Concat(params float[][] parts)
    {
        var list = new List<float>();
        foreach (var p in parts) list.AddRange(p);
        return list.ToArray();
    }

    private static float[] Mix(float[] a, float[] b)
    {
        var r = new float[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < r.Length; i++)
            r[i] = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
        return r;
    }

    private static byte[] Wav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataLen);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataLen);
        foreach (var f in samples)
            w.Write((short)Math.Clamp(f * 32767, -32768, 32767));
        w.Flush();
        return ms.ToArray();
    }
}
