using System;
using System.Collections.Generic;
using System.Linq;
using StealYourVoice.Controls;

namespace StealYourVoice.Audio;

/// <summary>Collects raw (pre-fader) mic levels and turns them into numbers we can reason about.</summary>
public sealed class MicAnalyzer
{
    private readonly List<double> _silence = new();
    private readonly List<(double max, double l, double r)> _speech = new();

    public void AddSilence(float l, float r) => _silence.Add(P5.ToDb(Math.Max(l, r)));
    public void AddSpeech(float l, float r) => _speech.Add((P5.ToDb(Math.Max(l, r)), P5.ToDb(l), P5.ToDb(r)));

    public void Reset() { _silence.Clear(); _speech.Clear(); }

    public static double Percentile(IReadOnlyList<double> v, double p)
    {
        if (v.Count == 0) return -90;
        var s = v.OrderBy(x => x).ToList();
        var idx = Math.Clamp((int)Math.Round(p * (s.Count - 1)), 0, s.Count - 1);
        return s[idx];
    }

    public MicReport Analyze()
    {
        var rep = new MicReport();
        rep.HasSilencePhase = _silence.Count > 0;
        rep.Noise = rep.HasSilencePhase ? Percentile(_silence, 0.5) : -90;
        var noiseHi = rep.HasSilencePhase ? Percentile(_silence, 0.9) : -70;

        var all = _speech.Select(x => x.max).ToList();
        rep.MaxPeak = all.Count > 0 ? all.Max() : -90;

        // frames that clearly contain voice
        var voiced = _speech.Where(x => x.max > Math.Max(noiseHi + 8, -65)).ToList();
        rep.VoicedFrames = voiced.Count;
        if (voiced.Count == 0) return rep;

        var vmax = voiced.Select(x => x.max).ToList();
        rep.VoicePeak = Percentile(vmax, 0.95);
        rep.VoiceMedian = Percentile(vmax, 0.5);
        rep.LeftMedian = Percentile(voiced.Select(x => x.l).ToList(), 0.5);
        rep.RightMedian = Percentile(voiced.Select(x => x.r).ToList(), 0.5);
        return rep;
    }
}

public sealed class MicReport
{
    public bool HasSilencePhase { get; set; }
    public double Noise { get; set; } = -90;
    public double MaxPeak { get; set; } = -90;
    public int VoicedFrames { get; set; }
    public double VoicePeak { get; set; } = -90;
    public double VoiceMedian { get; set; } = -90;
    public double LeftMedian { get; set; } = -90;
    public double RightMedian { get; set; } = -90;

    /// <summary>~0.6 s of voice at 30 fps.</summary>
    public bool HeardVoice => VoicedFrames >= 18;
    public bool Clipping => MaxPeak > -1.0;
    public bool TooQuiet => HeardVoice && VoicePeak < -30;
    public double Snr => VoiceMedian - Noise;

    /// <summary>"L" / "R" if the voice sits in one channel only, otherwise null.</summary>
    public string? OneSided =>
        !HeardVoice ? null :
        LeftMedian - RightMedian > 15 ? "L" :
        RightMedian - LeftMedian > 15 ? "R" : null;

    // ── suggested Voicemeeter settings ──

    public float SuggestGate()
    {
        var g = Math.Clamp((Noise + 75) / 5.0, 0, 8);
        if (Snr < 20) g = Math.Min(g, 3); // don't let the gate eat the voice in a noisy room
        return (float)(Math.Round(g * 2) / 2);
    }

    public float SuggestComp()
    {
        var dyn = VoicePeak - VoiceMedian;
        var c = Math.Clamp(2 + dyn / 3.0, 2, 7);
        return (float)(Math.Round(c * 2) / 2);
    }

    /// <summary>Strip gain so that voice peaks land around -9 dB after processing.</summary>
    public float SuggestGain(float comp)
    {
        var g = -9 - VoicePeak - comp * 0.5;
        g = Math.Clamp(g, -12, 12);
        return (float)(Math.Round(g * 2) / 2);
    }
}
