using System;
using System.Collections.Generic;
using StealYourVoice.Voicemeeter;

namespace StealYourVoice.Audio;

/// <summary>
/// Sidechain "ducking": while you talk, lowers the gain of the chosen Voicemeeter
/// virtual inputs (game / music), then smoothly brings it back.
/// </summary>
public sealed class Ducker
{
    private readonly VmClient _vm;
    private readonly Dictionary<int, float> _base = new();
    private readonly List<int> _targets = new();
    private DateTime _lastVoice = DateTime.MinValue;

    private const double HoldMs = 450;       // keep ducked a bit after the last word
    private const double AttackPerTick = 4.0; // dB per 33 ms tick
    private const double ReleasePerTick = 0.6;

    public double Offset { get; private set; } // 0 .. Amount (negative)
    public bool Active => Offset < -0.05;

    public Ducker(VmClient vm) { _vm = vm; }

    /// <param name="micDb">mic level after mute (dB)</param>
    /// <param name="targets">strip indexes to duck</param>
    /// <param name="amount">negative dB, e.g. -12</param>
    /// <param name="threshold">voice threshold in dB</param>
    public void Tick(bool voice, IReadOnlyList<int> targets, double amount)
    {
        if (!SameTargets(targets))
        {
            Restore();
            _targets.Clear();
            _targets.AddRange(targets);
        }
        if (_targets.Count == 0) return;

        var now = DateTime.Now;
        if (voice) _lastVoice = now;
        bool talking = (now - _lastVoice).TotalMilliseconds < HoldMs;
        double goal = talking ? Math.Min(0, amount) : 0;

        if (Math.Abs(goal - Offset) < 0.01) return;

        // starting to duck from rest: remember the user's gains
        if (!Active && goal < 0) CaptureBase();

        Offset = goal < Offset
            ? Math.Max(goal, Offset - AttackPerTick)
            : Math.Min(goal, Offset + ReleasePerTick);
        if (Math.Abs(Offset) < 0.05) Offset = 0;

        Apply();
    }

    /// <summary>Put the gains back immediately (disable / exit / target change).</summary>
    public void Restore()
    {
        if (Offset == 0 && _base.Count == 0) return;
        Offset = 0;
        Apply();
        _base.Clear();
    }

    private void CaptureBase()
    {
        _base.Clear();
        foreach (var t in _targets) _base[t] = _vm.Get($"Strip[{t}].Gain");
    }

    private void Apply()
    {
        foreach (var kv in _base)
            _vm.Set($"Strip[{kv.Key}].Gain", (float)Math.Clamp(kv.Value + Offset, -60, 12));
    }

    private bool SameTargets(IReadOnlyList<int> t)
    {
        if (t.Count != _targets.Count) return false;
        for (int i = 0; i < t.Count; i++) if (t[i] != _targets[i]) return false;
        return true;
    }
}
