using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StealYourVoice.Voicemeeter;

namespace StealYourVoice.Audio;

/// <summary>
/// Sidechain "ducking": while a trigger is active, lowers either the chosen Voicemeeter
/// virtual inputs or individual apps (see <see cref="Apps"/>), then smoothly brings them back.
/// </summary>
public sealed class Ducker
{
    private readonly VmClient _vm;
    private readonly Dictionary<int, float> _base = new();
    private readonly List<int> _targets = new();
    private DateTime _lastVoice = DateTime.MinValue;
    private bool _held;

    // Voicemeeter saves its faders, so a crash / shutdown mid-duck would leave the game quiet forever.
    private static readonly string BackupFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StealYourVoice", "ducked-strips.json");

    /// <summary>If the previous run ended while inputs were ducked, give them their gain back.</summary>
    public static void RestoreAfterCrash(VmClient vm)
    {
        try
        {
            if (!File.Exists(BackupFile)) return;
            var backup = JsonSerializer.Deserialize<Dictionary<int, float>>(File.ReadAllText(BackupFile));
            if (backup != null)
                foreach (var kv in backup) vm.Set($"Strip[{kv.Key}].Gain", kv.Value);
            File.Delete(BackupFile);
        }
        catch { /* ignore */ }
    }

    private const double HoldMs = 450;        // keep ducked a bit after the last word
    private const double AttackPerTick = 4.0; // dB per 33 ms tick
    private const double ReleasePerTick = 0.6;

    public double Offset { get; private set; } // 0 .. amount (negative)
    public bool Active => Offset < -0.05;

    /// <summary>When set, apps are ducked one by one through the Windows mixer (with exceptions).</summary>
    public AppDucker? Apps { get; set; }

    public Ducker(VmClient vm) { _vm = vm; }

    /// <param name="voice">is the trigger (you / friends) talking right now</param>
    /// <param name="targets">Voicemeeter strip indexes to duck</param>
    /// <param name="amount">negative dB, e.g. -12</param>
    public void Tick(bool voice, IReadOnlyList<int> targets, double amount)
    {
        if (!SameTargets(targets))
        {
            Restore();
            _targets.Clear();
            _targets.AddRange(targets);
        }
        if (_targets.Count == 0 && Apps == null) return;

        var now = DateTime.Now;
        if (voice) _lastVoice = now;
        bool talking = (now - _lastVoice).TotalMilliseconds < HoldMs;
        double goal = talking ? Math.Min(0, amount) : 0;

        if (Math.Abs(goal - Offset) < 0.01) return;

        // starting to duck from rest: remember the user's own levels
        if (!_held && goal < 0) Capture();

        Offset = goal < Offset
            ? Math.Max(goal, Offset - AttackPerTick)
            : Math.Min(goal, Offset + ReleasePerTick);
        if (Math.Abs(Offset) < 0.05) Offset = 0;

        Apply();
        if (Offset == 0) Release();
    }

    /// <summary>Put everything back immediately (disable / exit / mode change).</summary>
    public void Restore()
    {
        if (!_held && Offset == 0) return;
        Offset = 0;
        Apply();
        Release();
    }

    private void Capture()
    {
        _held = true;
        _base.Clear();
        foreach (var t in _targets) _base[t] = _vm.Get($"Strip[{t}].Gain");
        if (_base.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
                File.WriteAllText(BackupFile, JsonSerializer.Serialize(_base));
            }
            catch { /* ignore */ }
        }
        Apps?.Capture();
    }

    private void Apply()
    {
        foreach (var kv in _base)
            _vm.Set($"Strip[{kv.Key}].Gain", (float)Math.Clamp(kv.Value + Offset, -60, 12));
        Apps?.Apply(Offset);
    }

    private void Release()
    {
        _held = false;
        _base.Clear();
        try { if (File.Exists(BackupFile)) File.Delete(BackupFile); } catch { /* ignore */ }
        Apps?.Release();
    }

    private bool SameTargets(IReadOnlyList<int> t)
    {
        if (t.Count != _targets.Count) return false;
        for (int i = 0; i < t.Count; i++) if (t[i] != _targets[i]) return false;
        return true;
    }
}
