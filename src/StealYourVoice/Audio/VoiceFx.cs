using System;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using StealYourVoice.Voicemeeter;

namespace StealYourVoice.Audio;

/// <summary>What the voice changer does right now. Written by the UI, read by the audio thread.</summary>
public sealed class VoiceParams
{
    public volatile bool On;
    public volatile float Pitch;    // semitones, -12..+12
    public volatile float Robot;    // 0..1
    public volatile float Radio;    // 0..1  (band-limited + drive: radio / phone / megaphone)
    public volatile float Echo;     // 0..1
    public volatile float Cave;     // 0..1  (reverb)
    public volatile int Strip;      // which hardware input is the mic
    public volatile int AuxChannel = -1; // first channel of the "Voicemeeter AUX" input (your headphones path), -1 = none
}

/// <summary>A decoded sound being played into the mic (friends hear it) and into AUX (you hear it).</summary>
public sealed class Clip
{
    public float[] Data = Array.Empty<float>(); // interleaved stereo at Voicemeeter's sample rate
    public int Pos;                             // in frames
    public volatile float ToFriends = 1, ToMe = 0.8f;
    public volatile bool Stop;
    public int Frames => Data.Length / 2;
}

/// <summary>
/// Voice changer inserted straight into Voicemeeter's input strip through the official audio callback:
/// Voicemeeter hands us each block of the mic, we change it in place and it goes on to Discord / recording.
/// Runs on Voicemeeter's audio thread: no allocations, no locks in the hot path.
/// </summary>
public sealed unsafe class VoiceFx : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioInfo { public int SampleRate, Samples; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBuffer
    {
        public int SampleRate, Samples, Inputs, Outputs;
        public fixed long Read[128];
        public fixed long Write[128];
    }

    public readonly VoiceParams P = new();
    public bool Registered { get; private set; }
    public int SampleRate => _sr;

    /// <summary>Set by the UI thread; the audio thread plays it and clears it when done.</summary>
    public volatile Clip? Clip;
    public bool ClipPlaying => Clip != null;
    public string LastError { get; private set; } = "";

    private readonly VmNative.AudioCallback _cb; // keep the delegate alive while Voicemeeter holds its pointer
    private Channel _l = new(48000), _r = new(48000);
    private int _sr = 48000;

    public VoiceFx() { _cb = OnAudio; }

    public bool Start()
    {
        if (Registered) return true;
        try
        {
            var name = new byte[64];
            Encoding.ASCII.GetBytes("Steal Your Voice").CopyTo(name, 0);
            int r = VmNative.AudioCallbackRegister(VmNative.ModeInputInsert, _cb, IntPtr.Zero, name);
            if (r == 1)
            {
                var other = Encoding.ASCII.GetString(name).TrimEnd('\0');
                LastError = $"Обработку Voicemeeter уже заняла программа «{other}».";
                return false;
            }
            if (r != 0) { LastError = $"Voicemeeter не дал подключить обработку (код {r})."; return false; }
            VmNative.AudioCallbackStart();
            Registered = true;
            LastError = "";
            return true;
        }
        catch (EntryPointNotFoundException) { LastError = "Эта версия Voicemeeter не умеет подключать обработку — обнови Voicemeeter."; }
        catch (Exception ex) { LastError = ex.Message; }
        return false;
    }

    public void Stop()
    {
        if (!Registered) return;
        try { VmNative.AudioCallbackStop(); } catch { /* ignore */ }
        try { VmNative.AudioCallbackUnregister(); } catch { /* ignore */ }
        Registered = false;
    }

    public void Dispose() => Stop();

    private int OnAudio(IntPtr user, int command, IntPtr data, int nnn)
    {
        try
        {
            switch (command)
            {
                case VmNative.CbStarting:
                case VmNative.CbChange:
                    var info = (AudioInfo*)data;
                    if (info != null && info->SampleRate > 0 && info->SampleRate != _sr) Reset(info->SampleRate);
                    break;

                case VmNative.CbBufferIn:
                    Process((AudioBuffer*)data);
                    break;
            }
        }
        catch { /* never take Voicemeeter's audio thread down */ }
        return 0;
    }

    private void Reset(int sr)
    {
        _sr = sr;
        _l = new Channel(sr);
        _r = new Channel(sr);
    }

    private void Process(AudioBuffer* b)
    {
        if (b == null) return;
        int n = b->Samples, ins = Math.Min(b->Inputs, 128), outs = Math.Min(b->Outputs, 128);
        if (b->SampleRate > 0 && b->SampleRate != _sr) Reset(b->SampleRate);

        // insert mode: every channel must be passed through, or it goes silent
        for (int i = 0; i < ins && i < outs; i++)
        {
            float* src = (float*)b->Read[i], dst = (float*)b->Write[i];
            if (src == null || dst == null || src == dst) continue;
            Buffer.MemoryCopy(src, dst, n * 4L, n * 4L);
        }

        int ch = P.Strip * 2; // hardware strips are stereo: L, R
        if (ch + 1 >= outs) return;
        if (P.On)
        {
            _l.Run((float*)b->Write[ch], n, P);
            _r.Run((float*)b->Write[ch + 1], n, P);
        }

        // soundboard: after the effects, so the sound itself isn't turned into a demon
        var clip = Clip;
        if (clip == null) return;
        int aux = P.AuxChannel;
        float* ml = (float*)b->Write[ch], mr = (float*)b->Write[ch + 1];
        float* al = aux >= 0 && aux + 1 < outs ? (float*)b->Write[aux] : null;
        float* ar = aux >= 0 && aux + 1 < outs ? (float*)b->Write[aux + 1] : null;
        float vf = clip.ToFriends, vm = clip.ToMe;
        var d = clip.Data;
        int frames = clip.Frames, pos = clip.Pos;
        bool stop = clip.Stop;
        for (int i = 0; i < n && pos < frames; i++, pos++)
        {
            float fade = stop ? 1f - (float)i / n : 1f; // stopping fades out over one block instead of clicking
            float l = d[pos * 2] * fade, r = d[pos * 2 + 1] * fade;
            ml[i] += l * vf;
            mr[i] += r * vf;
            if (al != null) { al[i] += l * vm; ar[i] += r * vm; }
        }
        clip.Pos = pos;
        if (stop || pos >= frames) Clip = null;
    }

    // ───────────────────────── DSP, one instance per channel ─────────────────────────

    private sealed class Channel
    {
        private readonly int _sr;

        // pitch: two read heads sliding through a delay line, crossfaded (classic delay-line pitch shifter)
        private readonly float[] _pBuf = new float[8192];
        private int _pW;
        private double _phase;
        private readonly double _win;
        private double _ratio = 1;

        // robot: ring modulator + short resonant comb
        private double _ringPh;
        private readonly float[] _comb = new float[4096];
        private int _cW;
        private readonly int _combLen;

        // radio: band-pass + drive
        private Biquad _hp, _lp;

        // echo
        private readonly float[] _eBuf;
        private int _eW;
        private readonly int _eLen;

        // reverb (small Freeverb)
        private readonly float[][] _rc;
        private readonly int[] _rcI = new int[4];
        private readonly float[] _rcLp = new float[4];
        private readonly float[][] _ra;
        private readonly int[] _raI = new int[2];

        public Channel(int sr)
        {
            _sr = sr;
            _win = sr * 0.045;
            _combLen = Math.Max(16, (int)(sr * 0.0075));
            _hp = Biquad.HighPass(sr, 450, 0.8);
            _lp = Biquad.LowPass(sr, 3000, 0.8);
            _eLen = (int)(sr * 0.29);
            _eBuf = new float[_eLen + 1];
            double k = sr / 44100.0;
            _rc = new[] { 1116, 1188, 1277, 1356 }.Select(l => new float[(int)(l * k)]).ToArray();
            _ra = new[] { 556, 441 }.Select(l => new float[(int)(l * k)]).ToArray();
        }

        public unsafe void Run(float* x, int n, VoiceParams p)
        {
            if (x == null) return;
            float pitch = p.Pitch, robot = p.Robot, radio = p.Radio, echo = p.Echo, cave = p.Cave;
            double target = Math.Pow(2, pitch / 12.0);
            double ringStep = 2 * Math.PI * 72 / _sr;
            double drive = 1 + radio * 7;
            double driveNorm = 1 / Math.Tanh(drive);

            for (int i = 0; i < n; i++)
            {
                double s = x[i];

                // ── pitch ──
                _ratio += (target - _ratio) * 0.002; // glide instead of clicking when the slider moves
                _pBuf[_pW] = (float)s; // always keep the delay line fresh, so switching pitch on doesn't replay old audio
                if (Math.Abs(_ratio - 1) > 0.002)
                {
                    _phase += (1 - _ratio) / _win;
                    _phase -= Math.Floor(_phase);
                    double ph2 = _phase + 0.5; if (ph2 >= 1) ph2 -= 1;
                    double a = Tap(_phase * _win), c = Tap(ph2 * _win);
                    double g1 = Math.Sin(Math.PI * _phase), g2 = Math.Sin(Math.PI * ph2);
                    s = a * g1 * g1 + c * g2 * g2;
                }
                _pW = (_pW + 1) & 8191;

                // ── robot ──
                if (robot > 0.001f)
                {
                    _ringPh += ringStep; if (_ringPh > 2 * Math.PI) _ringPh -= 2 * Math.PI;
                    double ring = s * Math.Sin(_ringPh) * 1.6;
                    int rd = _cW - _combLen; if (rd < 0) rd += _comb.Length;
                    double comb = ring + _comb[rd] * 0.62;
                    _comb[_cW] = (float)comb;
                    _cW = (_cW + 1) % _comb.Length;
                    s = s * (1 - robot) + comb * 0.7 * robot;
                }

                // ── radio / megaphone ──
                if (radio > 0.001f)
                {
                    double band = _lp.Run(_hp.Run(s));
                    double dirty = Math.Tanh(band * drive) * driveNorm * 0.8;
                    s = s * (1 - radio) + dirty * radio;
                }

                // ── echo ──
                if (echo > 0.001f)
                {
                    double d = _eBuf[_eW];
                    _eBuf[_eW] = (float)(s + d * (0.25 + 0.3 * echo) + 1e-20);
                    _eW = (_eW + 1) % _eLen;
                    s += d * 0.55 * echo;
                }

                // ── cave (reverb) ──
                if (cave > 0.001f)
                {
                    double input = s * 0.25, wet = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        var buf = _rc[k];
                        double o = buf[_rcI[k]];
                        _rcLp[k] = (float)(o * 0.75 + _rcLp[k] * 0.25);
                        buf[_rcI[k]] = (float)(input + _rcLp[k] * (0.78 + 0.08 * cave) + 1e-20);
                        if (++_rcI[k] >= buf.Length) _rcI[k] = 0;
                        wet += o;
                    }
                    for (int k = 0; k < 2; k++)
                    {
                        var buf = _ra[k];
                        double o = buf[_raI[k]];
                        buf[_raI[k]] = (float)(wet + o * 0.5);
                        wet = o - wet * 0.5;
                        if (++_raI[k] >= buf.Length) _raI[k] = 0;
                    }
                    s = s * (1 - 0.35 * cave) + wet * 0.9 * cave;
                }

                // soft safety limiter, so effects never clip harshly
                if (s > 0.85 || s < -0.85) s = Math.Tanh(s);
                x[i] = (float)s;
            }
        }

        private double Tap(double delay)
        {
            double pos = _pW - delay - 1;
            while (pos < 0) pos += 8192;
            int i0 = (int)pos;
            double f = pos - i0;
            return _pBuf[i0 & 8191] * (1 - f) + _pBuf[(i0 + 1) & 8191] * f;
        }
    }

    private struct Biquad
    {
        private double b0, b1, b2, a1, a2, z1, z2;

        public double Run(double x)
        {
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }

        private static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2) =>
            new() { b0 = b0 / a0, b1 = b1 / a0, b2 = b2 / a0, a1 = a1 / a0, a2 = a2 / a0 };

        public static Biquad LowPass(int sr, double f, double q)
        {
            double w = 2 * Math.PI * f / sr, c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
            return Make((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al);
        }

        public static Biquad HighPass(int sr, double f, double q)
        {
            double w = 2 * Math.PI * f / sr, c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
            return Make((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
        }
    }
}
