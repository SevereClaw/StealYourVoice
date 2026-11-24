using System;
using System.Collections.Generic;
using System.Linq;

namespace StealYourVoice;

/// <summary>
/// Кто и когда говорил в голосовом канале Discord — для MidnightClips: в клипе под шкалой видно, где говорил
/// каждый друг, и звук Discord можно приглушить в моменты конкретного человека.
/// Сам звук Discord отдаёт одним потоком на всех, поэтому здесь только время: начало и конец каждой фразы.
/// </summary>
public partial class MainWindow
{
    private sealed record VoiceSpan(string Id, string Name, double Start, double End);

    private readonly List<VoiceSpan> _voiceLog = new();
    private readonly Dictionary<string, (string Name, double Start)> _voiceOpen = new();
    private const double VoiceKeepSeconds = 4 * 3600;   // дольше записи не бывают — старое не храним

    private static double NowEpoch() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>Каждый кадр таймера: сверить, кто говорит сейчас, с открытыми фразами.</summary>
    private void VoiceLogTick()
    {
        double now = NowEpoch();
        foreach (var id in _dcSpeaking)
        {
            if (id == _dcUserId || _voiceOpen.ContainsKey(id)) continue;   // свой голос — это дорожка микрофона
            _voiceOpen[id] = (_dcNames.GetValueOrDefault(id, "Друг"), now);
        }
        if (_voiceOpen.Count == 0) return;
        foreach (var id in _voiceOpen.Keys.Where(id => !_dcSpeaking.Contains(id)).ToList())
        {
            var (name, start) = _voiceOpen[id];
            _voiceOpen.Remove(id);
            _voiceLog.Add(new VoiceSpan(id, name, start, now));
        }
        if (_voiceLog.Count > 20000 || (_voiceLog.Count > 0 && now - _voiceLog[0].End > VoiceKeepSeconds))
            _voiceLog.RemoveAll(v => now - v.End > VoiceKeepSeconds);
    }

    /// <summary>Фразы, задевающие промежуток [since, until] (секунды Unix). Идущая сейчас — до текущего момента.</summary>
    private object VoicesBetween(double since, double until)
    {
        double now = NowEpoch();
        var list = _voiceLog.Where(v => v.End >= since && v.Start <= until)
            .Select(v => new { id = v.Id, name = v.Name, s = v.Start, e = v.End })
            .Concat(_voiceOpen.Where(o => o.Value.Start <= until)
                .Select(o => new { id = o.Key, name = o.Value.Name, s = o.Value.Start, e = now }))
            .OrderBy(v => v.s)
            .ToList();
        return new { ok = true, connected = _dcAuthed, voices = list };
    }
}
