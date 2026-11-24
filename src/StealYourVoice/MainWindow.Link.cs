using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace StealYourVoice;

/// <summary>
/// Связь с MidnightClips: его меню поверх игры (Alt+Z) показывает мьют микрофона, эффект голоса и пресеты,
/// а на время записи MidnightClips может включить пресет и потом вернуть прежние настройки.
/// </summary>
public partial class MainWindow
{
    private LinkServer? _link;
    private string _activePreset = "";
    private UserPreset? _beforeLink;   // настройки до пресета, включённого на время записи

    private static readonly string[] BuiltinPresets = { "ЧИСТЫЙ", "СТРИМ", "СЫРОЙ" };

    private void InitLink()
    {
        _link = new LinkServer(json => Dispatcher.Invoke(() => HandleLink(json)));
        _link.Start();
    }

    private void ApplyBuiltinPreset(string name)
    {
        switch (name)
        {
            case "ЧИСТЫЙ": _vm.Script($"{S("Gate")}=3;{S("Comp")}=4;{S("Mono")}=1;"); break;
            case "СТРИМ": _vm.Script($"{S("Gate")}=4.5;{S("Comp")}=6.5;{S("Mono")}=1;"); break;
            case "СЫРОЙ": _vm.Script($"{S("Gate")}=0;{S("Comp")}=0;"); break;
            default: return;
        }
        _activePreset = name;
        ShowPresetBanner("ПРЕСЕТ ВКЛЮЧЁН", name);
    }

    /// <summary>Включить пресет по имени (встроенный или свой). false — такого нет.</summary>
    private bool ApplyPresetByName(string name)
    {
        if (BuiltinPresets.Contains(name)) { ApplyBuiltinPreset(name); return true; }
        var p = _settings.Presets.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p == null) return false;
        ApplyUserPreset(p);
        return true;
    }

    private string HandleLink(string json)
    {
        string cmd = "";
        JsonElement root = default;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            root = doc.RootElement.Clone();
            cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        }
        catch (JsonException) { return Reply(new { ok = false, error = "bad json" }); }

        bool? flag = root.TryGetProperty("on", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False ? o.GetBoolean() : null;
        switch (cmd)
        {
            case "state":
                break;
            case "mute":   // on = true — микрофон выключен
                if (!_vm.IsConnected) return Reply(new { ok = false, error = "Voicemeeter не запущен" });
                bool muted = _vm.Get(S("Mute")) >= 0.5f;
                if (flag == null || flag.Value != muted) ToggleMute();
                break;
            case "fx":
                SetFx(flag ?? !_settings.FxOn, true);
                break;
            case "preset":
                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                bool temp = root.TryGetProperty("temp", out var t) && t.ValueKind == JsonValueKind.True;
                if (!_vm.IsConnected) return Reply(new { ok = false, error = "Voicemeeter не запущен" });
                if (temp && _beforeLink == null) _beforeLink = CaptureCurrent("");   // вернуть потом как было
                if (!ApplyPresetByName(name)) return Reply(new { ok = false, error = "нет пресета " + name });
                StatusText.Text = temp ? $"MIDNIGHT CLIPS: НА ВРЕМЯ ЗАПИСИ — ПРЕСЕТ «{name.ToUpperInvariant()}»" : $"ПРЕСЕТ «{name.ToUpperInvariant()}» ИЗ MIDNIGHT CLIPS";
                break;
            case "voices":   // кто говорил в Discord в промежутке [since, until] — для клипа MidnightClips
                double since = root.TryGetProperty("since", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : 0;
                double until = root.TryGetProperty("until", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : double.MaxValue;
                return Reply(VoicesBetween(since, until));
            case "restore":
                if (_beforeLink != null && _vm.IsConnected)
                {
                    ApplyUserPreset(_beforeLink);
                    ShowPresetBanner("ЗАПИСЬ ЗАКОНЧЕНА", "ПРЕЖНИЕ НАСТРОЙКИ", true);
                    StatusText.Text = "MIDNIGHT CLIPS: ЗАПИСЬ ЗАКОНЧЕНА, НАСТРОЙКИ ВОЗВРАЩЕНЫ";
                }
                _beforeLink = null;
                break;
            default:
                return Reply(new { ok = false, error = "unknown command" });
        }
        return State();
    }

    private string State()
    {
        bool connected = _vm.IsConnected;
        var presets = new List<string>(BuiltinPresets);
        presets.AddRange(_settings.Presets.Select(p => p.Name).Where(x => !string.IsNullOrWhiteSpace(x)));
        return Reply(new
        {
            ok = true,
            app = "StealYourVoice",
            version = Updater.Current.ToString(3),
            connected,
            muted = connected && _vm.Get(S("Mute")) >= 0.5f,
            fx = _settings.FxOn,
            fxVoice = _settings.FxVoice,
            presets,
            active = _activePreset,
            temp = _beforeLink != null,
        });
    }

    private static string Reply(object value) => JsonSerializer.Serialize(value);
}
