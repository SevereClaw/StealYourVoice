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

    public static readonly string[] Names = { "hover", "click", "confirm", "cancel", "whoosh" };

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
        "confirm" => Concat(Stab(988, 0.055), Stab(1480, 0.11)),
        "cancel" => Concat(Stab(740, 0.05), Stab(494, 0.10)),
        "whoosh" => Whoosh(0.32),
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

    /// <summary>Band-swept noise: the "slash" across the screen.</summary>
    private static float[] Whoosh(double dur)
    {
        var rnd = new Random(7);
        int n = (int)(dur * Rate);
        var s = new float[n];
        double lp = 0, hp = 0, prev = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n;
            double cutoff = 0.02 + 0.35 * Math.Sin(Math.PI * x);       // sweep up and back down
            double noise = rnd.NextDouble() * 2 - 1;
            lp += cutoff * (noise - lp);                                  // one-pole low-pass
            hp = 0.97 * (hp + lp - prev); prev = lp;                      // remove rumble
            double env = Math.Sin(Math.PI * Math.Pow(x, 0.7));
            s[i] = (float)(hp * env * 0.9);
        }
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
