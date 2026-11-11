using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StealYourVoice.Controls;
using StealYourVoice.Discord;

namespace StealYourVoice;

/// <summary>Per-friend volume through Discord's local RPC.</summary>
public partial class MainWindow
{
    private sealed class FriendRow
    {
        public SlashSlider Slider = null!;
        public SlashToggle Mute = null!;
        public Border Dot = null!;
    }

    private const string DcRedirect = "http://localhost";
    private static readonly string[] DcScopes = { "rpc", "rpc.voice.read", "rpc.voice.write" };
    private static readonly HttpClient DcHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private DiscordRpc? _dc;
    private bool _dcBusy, _dcAuthed, _dcNeedsManual, _dcPolling, _dcSettingsDirty;
    private string _dcStatus = "";
    private string? _dcUserId, _dcChannelId;
    private DateTime _dcNextTry = DateTime.MinValue, _dcLastTouch = DateTime.MinValue;
    private readonly Dictionary<string, FriendRow> _dcRows = new();
    private readonly HashSet<string> _dcApplied = new();
    private readonly HashSet<string> _dcSpeaking = new();
    private readonly Dictionary<string, string> _dcNames = new();
    private readonly Dictionary<string, string> _dcAvatars = new();
    private List<string> _dcCurrent = new();
    private readonly Dictionary<string, (bool MicOff, bool Deaf)> _dcFlags = new();
    private bool _dcSelfInChannel;
    private MouseHook? _mouseHook;
    private MicToast? _joinToast;
    private string? _dcOutputName;
    private int _dcPollCount;
    private HashSet<string>? _dcPrevIds;
    private string? _dcPrevChannel;
    private bool _spkDragging;
    private int _spkDragX, _spkDragY;
    private double _spkStartLeft, _spkStartTop;
    private SpeakersOverlay? _speakers;

    private bool DcConfigured =>
        !string.IsNullOrWhiteSpace(_settings.DiscordClientId) && !string.IsNullOrEmpty(_settings.DiscordSecretEnc);

    private void InitDiscord()
    {
        FriendsBtn.Click += _ =>
        {
            DcIdBox.Text = _settings.DiscordClientId;
            DcRender();
            ShowOverlay(FriendsOverlay);
        };
        DcCloseBtn.Click += _ => FriendsOverlay.Visibility = Visibility.Collapsed;
        DcPortalBtn.Click += _ =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://discord.com/developers/applications") { UseShellExecute = true });
            }
            catch { /* ignore */ }
        };
        DcConnectBtn.Click += async _ =>
        {
            var id = DcIdBox.Text.Trim();
            var secret = DcSecretBox.Password.Trim();
            if (id.Length == 0 || (secret.Length == 0 && string.IsNullOrEmpty(_settings.DiscordSecretEnc)))
            {
                _dcStatus = "ВСТАВЬ CLIENT ID И CLIENT SECRET";
                DcRender();
                return;
            }
            if (id != _settings.DiscordClientId) DcForgetTokens();
            _settings.DiscordClientId = id;
            if (secret.Length > 0) _settings.DiscordSecretEnc = Secrets.Protect(secret);
            _settings.Save();
            DcSecretBox.Password = "";
            await DcConnect(true);
        };
        DcForgetBtn.Click += _ =>
        {
            DcDrop();
            DcForgetTokens();
            _settings.DiscordClientId = "";
            _settings.DiscordSecretEnc = "";
            _settings.Save();
            DcIdBox.Text = "";
            _dcStatus = "КЛЮЧИ УДАЛЕНЫ";
            DcRender();
        };
        _settings.SpeakersCorner = Math.Clamp(_settings.SpeakersCorner, 0, 4);
        SpeakersBtn.Text = "КТО ГОВОРИТ: " + CornerNames[_settings.SpeakersCorner];
        SpeakersBtn.Click += _ =>
        {
            _settings.SpeakersCorner = (_settings.SpeakersCorner + 1) % 5;
            _settings.SpeakersX = _settings.SpeakersY = null; // back to a corner
            _settings.Save();
            SpeakersBtn.Text = "КТО ГОВОРИТ: " + CornerNames[_settings.SpeakersCorner];
            UpdateSpeakers();
        };
        _settings.SpeakersSilentOpacity = Math.Clamp(_settings.SpeakersSilentOpacity, 0.05, 1);
        _settings.SpeakersScale = Math.Clamp(_settings.SpeakersScale, 0.5, 2.5);
        SpkTalkOnlyT.IsChecked = _settings.SpeakersOnlyTalking;
        SpkNoNamesT.IsChecked = _settings.SpeakersAvatarsOnly;
        SpkSelfT.IsChecked = _settings.SpeakersShowSelf;
        SpkJoinT.IsChecked = _settings.JoinLeaveToasts;
        SpkJoinT.Click += t => { _settings.JoinLeaveToasts = t.IsChecked; _settings.Save(); };
        SpkOpacityS.Value = Math.Round(_settings.SpeakersSilentOpacity * 100);
        SpkScaleS.Value = Math.Round(_settings.SpeakersScale * 100);
        SpkTalkOnlyT.Click += t => { _settings.SpeakersOnlyTalking = t.IsChecked; _settings.Save(); UpdateSpeakers(); };
        SpkNoNamesT.Click += t => { _settings.SpeakersAvatarsOnly = t.IsChecked; _settings.Save(); UpdateSpeakers(); };
        SpkSelfT.Click += t => { _settings.SpeakersShowSelf = t.IsChecked; _settings.Save(); UpdateSpeakers(); };
        SpkOpacityS.ValueChanged += v => { _settings.SpeakersSilentOpacity = v / 100.0; _settings.Save(); UpdateSpeakers(); };
        SpkScaleS.ValueChanged += v => { _settings.SpeakersScale = v / 100.0; _settings.Save(); UpdateSpeakers(); };
        _dcStatus = DcConfigured ? "ПОДКЛЮЧАЮСЬ..." : "НЕ НАСТРОЕНО";
        DcRender();
    }

    private void DcForgetTokens()
    {
        _settings.DiscordAccessEnc = "";
        _settings.DiscordRefreshEnc = "";
        _settings.DiscordTokenExpires = DateTime.MinValue;
    }

    /// <summary>Called every tick from the main timer (independent of Voicemeeter).</summary>
    private void DiscordTick()
    {
        if (_tick % 60 != 0) return;
        if (_dcSettingsDirty && (DateTime.Now - _dcLastTouch).TotalSeconds > 1) { _dcSettingsDirty = false; _settings.Save(); }
        if (!DcConfigured || _dcBusy) return;

        if (_dcAuthed) { _ = DcPoll(); return; }
        if (!_dcNeedsManual && DateTime.Now > _dcNextTry) _ = DcConnect(false);
    }

    private void DcDrop()
    {
        var old = _dc;
        _dc = null;
        _dcAuthed = false;
        _dcChannelId = null;
        _dcUserId = null;
        _dcSelfInChannel = false;
        _dcOutputName = null;
        _dcPollCount = 0;
        _dcPrevIds = null;
        _dcPrevChannel = null;
        _dcApplied.Clear();
        _dcSpeaking.Clear();
        old?.Dispose();
        DcSetFriends(new List<(string, string, int, bool)>());
    }

    private async Task DcConnect(bool interactive)
    {
        if (_dcBusy) return;
        _dcBusy = true;
        try
        {
            DcDrop();
            _dcStatus = "ПОДКЛЮЧАЮСЬ...";
            DcRender();

            var dc = new DiscordRpc();
            dc.Event += (evt, data) => Dispatcher.BeginInvoke(new Action(() => DcOnEvent(dc, evt, data)));
            dc.Disconnected += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_dc != dc) return;
                DcDrop();
                _dcStatus = "DISCORD ЗАКРЫТ — ЖДУ ЕГО";
                _dcNextTry = DateTime.Now.AddSeconds(15);
                DcRender();
            }));

            if (!await dc.OpenAsync(_settings.DiscordClientId))
            {
                var err = dc.LastError;
                dc.Dispose();
                bool notRunning = err == "Discord не запущен";
                _dcStatus = notRunning ? "DISCORD НЕ ЗАПУЩЕН — ЖДУ ЕГО" : "DISCORD ОТКАЗАЛ: " + err.ToUpperInvariant();
                if (!notRunning) _dcNeedsManual = true; // e.g. wrong client id — retrying won't help
                _dcNextTry = DateTime.Now.AddSeconds(15);
                return;
            }
            _dc = dc;

            JsonElement auth = default;
            bool ok = false;

            var token = _settings.DiscordTokenExpires > DateTime.UtcNow.AddMinutes(5)
                ? Secrets.Unprotect(_settings.DiscordAccessEnc) : "";
            if (token.Length == 0 && !string.IsNullOrEmpty(_settings.DiscordRefreshEnc) && await DcRefresh() == null)
                token = Secrets.Unprotect(_settings.DiscordAccessEnc);

            if (token.Length > 0)
            {
                try { auth = await dc.Command("AUTHENTICATE", new Dictionary<string, object> { ["access_token"] = token }); ok = true; }
                catch { /* stale token */ }
            }

            if (!ok)
            {
                if (!interactive)
                {
                    _dcNeedsManual = true;
                    _dcStatus = "НУЖНО РАЗРЕШЕНИЕ — НАЖМИ «ПОДКЛЮЧИТЬ»";
                    DcDrop();
                    return;
                }

                _dcStatus = "ОТКРОЙ DISCORD И НАЖМИ «АВТОРИЗОВАТЬ»";
                DcRender();
                var res = await dc.Command("AUTHORIZE",
                    new Dictionary<string, object> { ["client_id"] = _settings.DiscordClientId, ["scopes"] = DcScopes },
                    timeoutMs: 120000);
                var code = DiscordRpc.Str(res, "code");
                if (code == null) throw new InvalidOperationException("Discord не вернул код");

                var err = await DcTokenRequest(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = DcRedirect
                });
                if (err != null) throw new InvalidOperationException(err);

                auth = await dc.Command("AUTHENTICATE", new Dictionary<string, object>
                    { ["access_token"] = Secrets.Unprotect(_settings.DiscordAccessEnc) });
            }

            if (auth.ValueKind == JsonValueKind.Object && auth.TryGetProperty("user", out var u))
                _dcUserId = DiscordRpc.Str(u, "id");
            _dcAuthed = true;
            _dcNeedsManual = false;
            _dcStatus = "ПОДКЛЮЧЕНО";
            Audio.Sfx.Play("confirm");
            _ = DcPoll();
        }
        catch (Exception ex)
        {
            DcDrop();
            _dcNeedsManual = true;
            _dcStatus = "НЕ ВЫШЛО: " + ex.Message.ToUpperInvariant();
        }
        finally
        {
            _dcBusy = false;
            DcRender();
        }
    }

    private Task<string?> DcRefresh() => DcTokenRequest(new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token", ["refresh_token"] = Secrets.Unprotect(_settings.DiscordRefreshEnc)
    });

    /// <summary>Returns null on success, otherwise an error text.</summary>
    private async Task<string?> DcTokenRequest(Dictionary<string, string> form)
    {
        form["client_id"] = _settings.DiscordClientId;
        form["client_secret"] = Secrets.Unprotect(_settings.DiscordSecretEnc);
        try
        {
            using var resp = await DcHttp.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(form));
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (!resp.IsSuccessStatusCode)
                return "discord: " + (DiscordRpc.Str(r, "error_description") ?? DiscordRpc.Str(r, "error") ?? $"HTTP {(int)resp.StatusCode}") +
                       " (проверь secret и redirect http://localhost)";

            _settings.DiscordAccessEnc = Secrets.Protect(DiscordRpc.Str(r, "access_token") ?? "");
            _settings.DiscordRefreshEnc = Secrets.Protect(DiscordRpc.Str(r, "refresh_token") ?? "");
            var secs = r.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 3600;
            _settings.DiscordTokenExpires = DateTime.UtcNow.AddSeconds(secs);
            _settings.Save();
            return null;
        }
        catch (Exception ex)
        {
            return "нет связи с discord.com (" + ex.Message + ")";
        }
    }

    private async Task DcPoll()
    {
        var dc = _dc;
        if (dc == null || !_dcAuthed || _dcPolling) return;
        _dcPolling = true;
        try
        {
            // now and then: which device does Discord play into? (diagnostics uses it)
            if (_dcPollCount++ % 5 == 0)
            {
                try { _dcOutputName = DcOutputDevice(await dc.Command("GET_VOICE_SETTINGS", new Dictionary<string, object>())); }
                catch { /* optional */ }
                if (_dc != dc) return;
            }

            var data = await dc.Command("GET_SELECTED_VOICE_CHANNEL", new Dictionary<string, object>());
            if (_dc != dc) return;

            if (data.ValueKind != JsonValueKind.Object)
            {
                _dcChannelId = null;
                _dcSelfInChannel = false;
                _dcPrevIds = null;
                _dcPrevChannel = null;
                _dcSpeaking.Clear();
                _dcStatus = "ПОДКЛЮЧЕНО // ЗАЙДИ В ГОЛОСОВОЙ КАНАЛ";
                DcSetFriends(new List<(string, string, int, bool)>());
                DcRender();
                return;
            }

            var channelId = DiscordRpc.Str(data, "id");
            if (channelId != null && channelId != _dcChannelId)
            {
                var old = _dcChannelId;
                _dcChannelId = channelId;
                _dcSpeaking.Clear();
                foreach (var evt in new[] { "SPEAKING_START", "SPEAKING_STOP" })
                {
                    try
                    {
                        if (old != null) await dc.Command("UNSUBSCRIBE", new Dictionary<string, object> { ["channel_id"] = old }, evt);
                        await dc.Command("SUBSCRIBE", new Dictionary<string, object> { ["channel_id"] = channelId }, evt);
                    }
                    catch { /* highlight is optional */ }
                }
            }

            bool selfSeen = false;
            var friends = new List<(string Id, string Name, int Volume, bool Mute)>();
            if (data.TryGetProperty("voice_states", out var states) && states.ValueKind == JsonValueKind.Array)
            {
                foreach (var vs in states.EnumerateArray())
                {
                    if (!vs.TryGetProperty("user", out var user)) continue;
                    var id = DiscordRpc.Str(user, "id");
                    if (id == null) continue;
                    var name = DiscordRpc.Str(vs, "nick") ?? DiscordRpc.Str(user, "global_name") ?? DiscordRpc.Str(user, "username") ?? id;

                    bool micOff = false, deaf = false;
                    if (vs.TryGetProperty("voice_state", out var st) && st.ValueKind == JsonValueKind.Object)
                    {
                        micOff = DcFlag(st, "self_mute") || DcFlag(st, "mute") || DcFlag(st, "suppress");
                        deaf = DcFlag(st, "self_deaf") || DcFlag(st, "deaf");
                    }
                    _dcFlags[id] = (micOff, deaf);

                    if (id == _dcUserId)
                    {
                        selfSeen = true;
                        _dcNames[id] = name;
                        _dcAvatars[id] = AvatarUrl(id, DiscordRpc.Str(user, "avatar"));
                        continue;
                    }
                    int vol = vs.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number
                        ? (int)Math.Round(v.GetDouble()) : 100;
                    bool mute = vs.TryGetProperty("mute", out var m) && m.ValueKind == JsonValueKind.True;

                    // Discord forgets RPC volumes when we disconnect — put the saved one back
                    if (_dcApplied.Add(id) && _settings.FriendVolumes.TryGetValue(id, out var saved) && saved != vol)
                    {
                        DcSetUser(id, saved, null);
                        vol = saved;
                    }
                    if (mute) _dcFlags[id] = (true, deaf); // muted by you
                    _dcNames[id] = name;
                    _dcAvatars[id] = AvatarUrl(id, DiscordRpc.Str(user, "avatar"));
                    friends.Add((id, name, vol, mute));
                }
            }

            _dcSelfInChannel = selfSeen;
            DcJoinLeave(channelId, friends.Select(f => f.Id).ToHashSet());
            var chName = DiscordRpc.Str(data, "name");
            _dcStatus = friends.Count == 0
                ? "В КАНАЛЕ ТОЛЬКО ТЫ"
                : $"{(chName ?? "КАНАЛ").ToUpperInvariant()} // ЛЮДЕЙ: {friends.Count}";
            DcSetFriends(friends);
            DcRender();
        }
        catch { /* next poll will retry; a dead pipe raises Disconnected */ }
        finally { _dcPolling = false; }
    }

    private async void DcSetUser(string id, int? volume, bool? mute)
    {
        var dc = _dc;
        if (dc == null || !_dcAuthed) return;
        try
        {
            var args = new Dictionary<string, object> { ["user_id"] = id };
            if (volume.HasValue) args["volume"] = volume.Value;
            if (mute.HasValue) args["mute"] = mute.Value;
            await dc.Command("SET_USER_VOICE_SETTINGS", args);
        }
        catch { /* ignore */ }
    }

    private void DcOnEvent(DiscordRpc source, string evt, JsonElement data)
    {
        if (source != _dc) return;
        var id = DiscordRpc.Str(data, "user_id");
        if (id == null) return;
        if (evt == "SPEAKING_START") _dcSpeaking.Add(id);
        else if (evt == "SPEAKING_STOP") _dcSpeaking.Remove(id);
        else return;
        if (_dcRows.TryGetValue(id, out var row)) row.Dot.Background = _dcSpeaking.Contains(id) ? P5.Red : P5.Coal;
        UpdateSpeakers();
    }

    /// <summary>Shift + drag moves the list, Shift + wheel over it resizes. Returns true to swallow the event.</summary>
    private bool OnGlobalMouse(int msg, int x, int y, int delta)
    {
        var o = _speakers;
        if (_spkDragging)
        {
            if (msg == MouseHook.WM_MOUSEMOVE && o != null)
            {
                var k = o.DpiScale;
                o.MoveTo(_spkStartLeft + (x - _spkDragX) / k, _spkStartTop + (y - _spkDragY) / k);
                return false;
            }
            if (msg == MouseHook.WM_LBUTTONUP)
            {
                _spkDragging = false;
                if (o?.CustomPos is { } p)
                {
                    _settings.SpeakersX = p.X;
                    _settings.SpeakersY = p.Y;
                    _settings.Save();
                }
                return true;
            }
            return false;
        }

        if (msg != MouseHook.WM_LBUTTONDOWN && msg != MouseHook.WM_MOUSEWHEEL) return false;

        // Ctrl + wheel over a person → that person's volume
        if (msg == MouseHook.WM_MOUSEWHEEL && o != null && o.IsVisible && MouseHook.CtrlDown && !MouseHook.ShiftDown && o.Contains(x, y))
        {
            var id = o.RowAt(x, y);
            if (id == null || id == _dcUserId || !_dcAuthed) return id != null;
            double current = _dcRows.TryGetValue(id, out var fr) ? fr.Slider.Value : _settings.FriendVolumes.GetValueOrDefault(id, 100);
            int vol = (int)Math.Clamp(Math.Round(current / 5) * 5 + Math.Sign(delta) * 5, 0, 200);
            _dcLastTouch = DateTime.Now;
            _settings.FriendVolumes[id] = vol;
            _dcSettingsDirty = true;
            if (fr != null) fr.Slider.Value = vol;
            DcSetUser(id, vol, null);
            o.ShowVolume(id, vol);
            return true;
        }
        if (o == null || !o.IsVisible || !MouseHook.ShiftDown || !o.Contains(x, y)) return false;

        if (msg == MouseHook.WM_LBUTTONDOWN)
        {
            _spkDragging = true;
            _spkDragX = x;
            _spkDragY = y;
            _spkStartLeft = o.Left;
            _spkStartTop = o.Top;
            return true;
        }

        var scale = Math.Round((_settings.SpeakersScale + Math.Sign(delta) * 0.1) * 10) / 10;
        _settings.SpeakersScale = Math.Clamp(scale, 0.5, 2.5);
        _settings.Save();
        SpkScaleS.Value = Math.Round(_settings.SpeakersScale * 100);
        UpdateSpeakers();
        return true;
    }

    /// <summary>Toast "NAME ЗАШЁЛ / ВЫШЕЛ" when the set of people in the same channel changes.</summary>
    private void DcJoinLeave(string? channelId, HashSet<string> now)
    {
        var prev = _dcPrevIds;
        bool sameChannel = channelId != null && channelId == _dcPrevChannel;
        _dcPrevIds = now;
        _dcPrevChannel = channelId;
        if (!sameChannel || prev == null || !_settings.JoinLeaveToasts) return;

        var joined = now.Where(id => !prev.Contains(id)).ToList();
        var left = prev.Where(id => !now.Contains(id)).ToList();
        if (joined.Count == 0 && left.Count == 0) return;

        bool isJoin = joined.Count > 0;
        var ids = isJoin ? joined : left;
        var name = _dcNames.GetValueOrDefault(ids[0], "КТО-ТО");
        if (name.Length > 16) name = name[..15] + "…";
        var text = name.ToUpperInvariant() + (ids.Count > 1 ? $" +{ids.Count - 1}" : "") + (isJoin ? " ЗАШЁЛ" : " ВЫШЕЛ");

        _joinToast ??= new MicToast();
        _joinToast.Flash(text, !isJoin);
        Audio.Sfx.Play(isJoin ? "confirm" : "cancel");
    }

    private static string? DcOutputDevice(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("output", out var o)) return null;
        var id = DiscordRpc.Str(o, "device_id");
        if (id == null) return null;
        if (o.TryGetProperty("available_devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
            foreach (var d in devs.EnumerateArray())
                if (DiscordRpc.Str(d, "id") == id) return DiscordRpc.Str(d, "name") ?? id;
        return id;
    }

    private static bool DcFlag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string AvatarUrl(string id, string? hash)
    {
        if (!string.IsNullOrEmpty(hash)) return $"https://cdn.discordapp.com/avatars/{id}/{hash}.png?size=64";
        // no custom avatar → one of Discord's default ones
        int index = ulong.TryParse(id, out var n) ? (int)((n >> 22) % 6) : 0;
        return $"https://cdn.discordapp.com/embed/avatars/{index}.png";
    }

    /// <summary>Refresh the over-the-game list: everyone in the channel, lit up while talking.</summary>
    private void UpdateSpeakers()
    {
        if (_settings.SpeakersCorner == 0)
        {
            _speakers?.Hide();
            _mouseHook?.Dispose();
            _mouseHook = null;
            _spkDragging = false;
            return;
        }
        _mouseHook ??= new MouseHook { Handler = OnGlobalMouse };
        var members = new List<SpeakerInfo>();
        if (_settings.SpeakersShowSelf && _dcSelfInChannel && _dcUserId != null)
        {
            var f = _dcFlags.GetValueOrDefault(_dcUserId);
            // your real mute is the one in Voicemeeter, so it counts too
            members.Add(new SpeakerInfo(_dcUserId, _dcNames.GetValueOrDefault(_dcUserId, "ТЫ"),
                _dcAvatars.GetValueOrDefault(_dcUserId), f.MicOff || MuteT.IsChecked, f.Deaf));
        }
        foreach (var id in _dcCurrent)
        {
            var f = _dcFlags.GetValueOrDefault(id);
            members.Add(new SpeakerInfo(id, _dcNames.GetValueOrDefault(id, id), _dcAvatars.GetValueOrDefault(id), f.MicOff, f.Deaf));
        }
        if (members.Count == 0 && _speakers == null) return;
        _speakers ??= new SpeakersOverlay();
        // same corner as the mic indicator → sit right next to it instead of on top
        bool shared = _settings.OverlayCorner == _settings.SpeakersCorner;
        _speakers.SetMembers(members, _settings.SpeakersCorner, shared ? 36 : 0,
            new SpeakerStyle(_settings.SpeakersOnlyTalking, _settings.SpeakersSilentOpacity,
                _settings.SpeakersScale, _settings.SpeakersAvatarsOnly),
            _settings.SpeakersX.HasValue && _settings.SpeakersY.HasValue
                ? new Point(_settings.SpeakersX.Value, _settings.SpeakersY.Value) : null);
        _speakers.SetSpeaking(_dcSpeaking);
    }

    private void DcSetFriends(List<(string Id, string Name, int Volume, bool Mute)> friends)
    {
        bool same = friends.Count == _dcRows.Count && friends.All(f => _dcRows.ContainsKey(f.Id));
        if (!same)
        {
            _dcRows.Clear();
            DcFriendsPanel.Children.Clear();
            foreach (var f in friends.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var id = f.Id;
                var label = f.Name.Length > 24 ? f.Name[..22] + "…" : f.Name;
                var row = new FriendRow
                {
                    Dot = new Border
                    {
                        Width = 12, Height = 34, Margin = new Thickness(0, 22, 12, 0), VerticalAlignment = VerticalAlignment.Top,
                        Background = P5.Coal, BorderBrush = P5.White, BorderThickness = new Thickness(2),
                        RenderTransform = new SkewTransform(-15, 0), ToolTip = "Горит красным, когда человек говорит"
                    },
                    Mute = new SlashToggle
                    {
                        Text = "MUTE", FontSize = 12, Height = 32, VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(16, 0, 0, 2), Hint = "Заглушить этого человека только у себя"
                    },
                    Slider = new SlashSlider
                    {
                        Label = label.ToUpperInvariant(), Minimum = 0, Maximum = 200, Default = 100, Step = 5,
                        Unit = "%", Decimals = 0, Hint = "Громкость этого человека. Двойной клик — 100%"
                    }
                };
                row.Slider.ValueChanged += v =>
                {
                    _dcLastTouch = DateTime.Now;
                    _settings.FriendVolumes[id] = (int)Math.Round(v);
                    _dcSettingsDirty = true;
                    DcSetUser(id, (int)Math.Round(v), null);
                };
                row.Mute.Click += t => { _dcLastTouch = DateTime.Now; DcSetUser(id, null, t.IsChecked); };

                var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
                DockPanel.SetDock(row.Dot, Dock.Left);
                DockPanel.SetDock(row.Mute, Dock.Right);
                dock.Children.Add(row.Dot);
                dock.Children.Add(row.Mute);
                dock.Children.Add(row.Slider);
                DcFriendsPanel.Children.Add(dock);
                _dcRows[id] = row;
            }
        }

        // don't snap a slider back from a poll that raced with the user's own change
        bool settle = (DateTime.Now - _dcLastTouch).TotalSeconds > 2.5;
        foreach (var f in friends)
        {
            if (!_dcRows.TryGetValue(f.Id, out var row)) continue;
            if (!same || (settle && !row.Slider.IsDragging)) row.Slider.Value = f.Volume;
            if (!same || settle) row.Mute.IsChecked = f.Mute;
            row.Dot.Background = _dcSpeaking.Contains(f.Id) ? P5.Red : P5.Coal;
        }
        FriendsBtn.Text = friends.Count > 0 ? $"ДРУЗЬЯ ({friends.Count})" : "ДРУЗЬЯ";

        // someone who left mid-word never sends SPEAKING_STOP
        _dcCurrent = friends.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Select(f => f.Id).ToList();
        _dcSpeaking.IntersectWith(friends.Select(f => f.Id).Append(_dcUserId ?? ""));
        UpdateSpeakers();
    }

    private void DcRender()
    {
        DcStatusText.Text = _dcStatus;
        var setup = _dcAuthed ? Visibility.Collapsed : Visibility.Visible;
        DcSetupPanel.Visibility = setup;
        DcConnectBtn.Visibility = setup;
        DcPortalBtn.Visibility = setup;
        DcForgetBtn.Visibility = DcConfigured ? Visibility.Visible : Visibility.Collapsed;
        DcViewPanel.Visibility = _dcAuthed ? Visibility.Visible : Visibility.Collapsed;
        DcSecretHint.Text = string.IsNullOrEmpty(_settings.DiscordSecretEnc)
            ? "CLIENT SECRET" : "CLIENT SECRET  (уже сохранён — оставь пустым, чтобы не менять)";
    }
}
