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
    private readonly Dictionary<string, (bool MicOff, bool Deaf, bool Live, bool Cam)> _dcFlags = new();
    private readonly Dictionary<string, SpeakersOverlay> _detached = new();
    private SpeakersOverlay? _dragOverlay;
    private string? _dragPerson;
    private bool _dcSelfInChannel;
    private MouseHook? _mouseHook;
    private BannerToast? _joinToast;
    private string? _dcOutputName;
    private readonly Dictionary<string, int> _dcLoud = new();
    private readonly Dictionary<string, double> _lvlAvg = new();
    private readonly Dictionary<string, int> _lvlCount = new();
    private static readonly Dictionary<string, int> NoLoudness = new();
    private int _dcPollCount;
    private HashSet<string>? _dcPrevIds;
    private string? _dcPrevChannel;
    private bool _spkDragging;
    private bool _dragMic;
    private long _hoverCheck;
    private bool _swallowUp;
    private readonly Dictionary<string, bool> _dcLocalMute = new();
    private double _micGainLocal;
    private DateTime _micGainLast = DateTime.MinValue;
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
        DcDebugBtn.Click += _ =>
        {
            try
            {
                if (System.IO.File.Exists(DcDebugFile))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{DcDebugFile}\"") { UseShellExecute = true });
                else _dcStatus = "ЛОГ ПОЯВИТСЯ, КОГДА ТЫ ЗАЙДЁШЬ В ГОЛОСОВОЙ КАНАЛ";
                DcRender();
            }
            catch { /* ignore */ }
        };
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
            _settings.SpeakerPositions.Clear();
            _settings.Save();
            SpeakersBtn.Text = "КТО ГОВОРИТ: " + CornerNames[_settings.SpeakersCorner];
            UpdateSpeakers();
        };
        _settings.SpeakersSilentOpacity = Math.Clamp(_settings.SpeakersSilentOpacity, 0.05, 1);
        _settings.SpeakersScale = Math.Clamp(_settings.SpeakersScale, 0.5, 2.5);
        SpkTalkOnlyT.IsChecked = _settings.SpeakersOnlyTalking;
        SpkNoNamesT.IsChecked = _settings.SpeakersAvatarsOnly;
        SpkSelfT.IsChecked = _settings.SpeakersShowSelf;
        SpkLoudT.IsChecked = _settings.SpeakersLoudness;
        SpkLoudT.Click += t => { _settings.SpeakersLoudness = t.IsChecked; _settings.Save(); UpdateSpeakers(); };
        SpkGatherBtn.Click += _ =>
        {
            _settings.SpeakerPositions.Clear();
            _settings.Save();
            UpdateSpeakers();
        };
        SpkJoinT.IsChecked = _settings.JoinLeaveToasts;
        SpkJoinT.Click += t => { _settings.JoinLeaveToasts = t.IsChecked; _settings.Save(); };
        SpkOpacityS.Value = Math.Round(_settings.SpeakersSilentOpacity * 100);
        SpkScaleS.Value = Math.Round(_settings.SpeakersScale * 100);
        SpkTalkOnlyT.Click += t => { _settings.SpeakersOnlyTalking = t.IsChecked; _settings.Save(); UpdateSpeakers(); };
        SpkNoNamesT.Click += t => { _settings.SpeakersAvatarsOnly = t.IsChecked; _settings.Save(); UpdateSpeakers(); };
        SpkSelfT.Click += t => { _settings.SpeakersShowSelf = t.IsChecked; _settings.Save(); UpdateSpeakers(); };
        SpkOpacityS.ValueChanged += v => { _settings.SpeakersSilentOpacity = v / 100.0; _settings.Save(); UpdateSpeakers(); ApplyOverlay(); };
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
        _dcLoud.Clear();
        _lvlAvg.Clear();
        _lvlCount.Clear();
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

            // keep the last raw answer on disk: shows exactly which fields Discord gives us (e.g. stream status)
            if (_dcPollCount % 5 == 1 && data.ValueKind == JsonValueKind.Object)
            {
                var raw = data.GetRawText();
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try { System.IO.File.WriteAllText(DcDebugFile, raw); } catch { /* ignore */ }
                });
            }
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
                    var name = CleanName(DiscordRpc.Str(vs, "nick")) ?? CleanName(DiscordRpc.Str(user, "global_name"))
                               ?? CleanName(DiscordRpc.Str(user, "username")) ?? id;

                    bool micOff = false, deaf = false, live = false, cam = false;
                    if (vs.TryGetProperty("voice_state", out var st) && st.ValueKind == JsonValueKind.Object)
                    {
                        micOff = DcFlag(st, "self_mute") || DcFlag(st, "mute") || DcFlag(st, "suppress");
                        deaf = DcFlag(st, "self_deaf") || DcFlag(st, "deaf");

                    }
                    // Discord's local RPC has no stream / camera fields (checked on a live answer), so these stay false
                    _dcFlags[id] = (micOff, deaf, live, cam);

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
                    // don't let a poll that raced with an Alt-click flip the button back
                    if ((DateTime.Now - _dcLastTouch).TotalSeconds > 2.5) _dcLocalMute[id] = mute;

                    // Discord forgets RPC volumes when we disconnect — put the saved one back
                    if (_dcApplied.Add(id) && _settings.FriendVolumes.TryGetValue(id, out var saved) && saved != vol)
                    {
                        DcSetUser(id, saved, null);
                        vol = saved;
                    }
                    if (mute) _dcFlags[id] = (true, deaf, live, cam); // muted by you
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

    /// <summary>The global mouse hook lives only while one of the over-the-game overlays is on.</summary>
    private void SyncMouseHook()
    {
        bool want = _settings.SpeakersCorner != 0 || _settings.OverlayCorner != 0;
        if (want) _mouseHook ??= new MouseHook { Handler = HookFilter, EveryMove = () => _spkDragging };
        else
        {
            _mouseHook?.Dispose();
            _mouseHook = null;
            _spkDragging = false;
            _dragMic = false;
            _dragOverlay = null;
            _dragPerson = null;
        }
    }

    private int _movePosted, _moveX, _moveY;

    /// <summary>
    /// Runs on the hook thread for every mouse event of the system. Plain movement and clicks without
    /// Ctrl/Shift/Alt go straight back to the game; only the rare modified clicks wait for the UI to decide.
    /// </summary>
    private bool HookFilter(int msg, int x, int y, int delta)
    {
        if (msg == MouseHook.WM_MOUSEMOVE)
        {
            // hover / drag: hand the latest position to the UI, never more than one pending
            _moveX = x;
            _moveY = y;
            if (System.Threading.Interlocked.Exchange(ref _movePosted, 1) == 0)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref _movePosted, 0);
                    OnGlobalMouse(MouseHook.WM_MOUSEMOVE, _moveX, _moveY, 0);
                }), System.Windows.Threading.DispatcherPriority.Input);
            return false;
        }

        if (!_spkDragging && !_swallowUp && !MouseHook.CtrlDown && !MouseHook.ShiftDown && !MouseHook.AltDown)
            return false;

        try
        {
            return Dispatcher.Invoke(() => OnGlobalMouse(msg, x, y, delta),
                System.Windows.Threading.DispatcherPriority.Send, System.Threading.CancellationToken.None,
                TimeSpan.FromMilliseconds(120));
        }
        catch { return false; } // UI busy — let the click through rather than freeze the mouse
    }

    /// <summary>The shared list plus every plate that was pulled out of it.</summary>
    private IEnumerable<SpeakersOverlay> AllLists()
    {
        if (_speakers != null) yield return _speakers;
        foreach (var d in _detached.Values) yield return d;
    }

    private SpeakersOverlay? ListAt(int x, int y)
    {
        foreach (var l in AllLists())
            if (l.IsVisible && l.Contains(x, y)) return l;
        return null;
    }

    /// <summary>
    /// Mouse over the click-through overlays. Hover spreads the list; Shift + drag / wheel moves and resizes;
    /// Ctrl + wheel is volume; Ctrl + drag pulls one person out; Ctrl + Shift + click opens them in Discord.
    /// Returns true to swallow the event.
    /// </summary>
    private bool OnGlobalMouse(int msg, int x, int y, int delta)
    {
        // plain mouse movement: hovering a plate spreads the list and shows the full nickname
        if (msg == MouseHook.WM_MOUSEMOVE && !_spkDragging)
        {
            long now = Environment.TickCount64;
            if (now - _hoverCheck < 40) return false;
            _hoverCheck = now;
            bool alt = MouseHook.AltDown;
            foreach (var l in AllLists())
            {
                if (!l.IsVisible) continue;
                l.SetHover(l.Contains(x, y) ? l.RowAt(x, y) : null);
                l.SetAlt(alt);
            }
            return false;
        }

        if (_spkDragging && _dragMic)
        {
            var m = _overlay;
            if (msg == MouseHook.WM_MOUSEMOVE && m != null)
            {
                var km = m.DpiScale;
                m.MoveTo(_spkStartLeft + (x - _spkDragX) / km, _spkStartTop + (y - _spkDragY) / km);
                return false;
            }
            if (msg == MouseHook.WM_LBUTTONUP)
            {
                _spkDragging = false;
                _dragMic = false;
                if (m != null)
                {
                    _settings.OverlayX = m.Left;
                    _settings.OverlayY = m.Top;
                    _settings.Save();
                }
                return true;
            }
            return false;
        }

        if (_spkDragging)
        {
            if (msg == MouseHook.WM_MOUSEMOVE)
            {
                if (_dragPerson != null && _dragOverlay == null)
                {
                    // Ctrl + drag from the shared list: the person pops out once the mouse really moves
                    if (Math.Abs(x - _spkDragX) + Math.Abs(y - _spkDragY) < 6) return false;
                    double k0 = _speakers?.DpiScale ?? 1;
                    _spkStartLeft = _spkDragX / k0 - 24;
                    _spkStartTop = _spkDragY / k0 - 20;
                    _settings.SpeakerPositions[_dragPerson] = new[] { _spkStartLeft, _spkStartTop };
                    UpdateSpeakers();
                    _detached.TryGetValue(_dragPerson, out _dragOverlay);
                    if (_dragOverlay == null) { _spkDragging = false; _dragPerson = null; return false; }
                    Audio.Sfx.Play("whoosh");
                }
                var d = _dragOverlay;
                if (d != null)
                {
                    var k = d.DpiScale;
                    d.MoveTo(_spkStartLeft + (x - _spkDragX) / k, _spkStartTop + (y - _spkDragY) / k);
                }
                return false;
            }
            if (msg == MouseHook.WM_LBUTTONUP)
            {
                var d = _dragOverlay;
                var person = _dragPerson;
                _spkDragging = false;
                _dragOverlay = null;
                _dragPerson = null;
                if (d?.CustomPos is { } p)
                {
                    if (person != null) _settings.SpeakerPositions[person] = new[] { p.X, p.Y };
                    else
                    {
                        _settings.SpeakersX = p.X;
                        _settings.SpeakersY = p.Y;
                    }
                    _settings.Save();
                }
                return true;
            }
            return false;
        }

        // the release that belongs to a click we already took
        if (msg == MouseHook.WM_LBUTTONUP && _swallowUp)
        {
            _swallowUp = false;
            return true;
        }

        if (msg != MouseHook.WM_LBUTTONDOWN && msg != MouseHook.WM_MOUSEWHEEL) return false;

        bool ctrl = MouseHook.CtrlDown, shift = MouseHook.ShiftDown;

        // Alt + click on the mic button of a plate → mute / unmute that person only for you
        if (msg == MouseHook.WM_LBUTTONDOWN && MouseHook.AltDown && !ctrl && !shift)
        {
            var hit = ListAt(x, y);
            var who = hit?.MuteButtonAt(x, y);
            if (hit != null && who != null && who != _dcUserId && _dcAuthed)
            {
                bool now = !(_dcLocalMute.TryGetValue(who, out var was) && was);
                _dcLocalMute[who] = now;
                _dcLastTouch = DateTime.Now;
                DcSetUser(who, null, now);
                if (_dcRows.TryGetValue(who, out var fr)) fr.Mute.IsChecked = now;
                var f = _dcFlags.GetValueOrDefault(who);
                _dcFlags[who] = (now || f.MicOff && !was, f.Deaf, f.Live, f.Cam);
                hit.FlashMute(who, now);
                Audio.Sfx.Play(now ? "cancel" : "confirm");
                _swallowUp = true;
                Dispatcher.BeginInvoke(new Action(UpdateSpeakers));
                return true;
            }
        }

        if (!ctrl && !shift) return false; // without a modifier everything passes straight through to the game

        var list = ListAt(x, y);

        // Ctrl + Shift + click on a person → open them in Discord (profile with the message box)
        if (msg == MouseHook.WM_LBUTTONDOWN && list != null && ctrl && shift)
        {
            var who = list.RowAt(x, y);
            if (who != null && who != _dcUserId && who.All(char.IsDigit))
            {
                _swallowUp = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                            $"discord://-/users/{who}") { UseShellExecute = true });
                        Audio.Sfx.Play("confirm");
                    }
                    catch { /* Discord link handler missing */ }
                }));
                return true;
            }
        }

        // the mic indicator: Shift + drag / Shift + wheel / Ctrl + wheel (your own mic volume)
        var mic = _overlay;
        if (mic != null && mic.IsVisible && mic.Contains(x, y))
        {
            if (shift)
            {
                if (msg == MouseHook.WM_LBUTTONDOWN)
                {
                    _spkDragging = true;
                    _dragMic = true;
                    _spkDragX = x;
                    _spkDragY = y;
                    _spkStartLeft = mic.Left;
                    _spkStartTop = mic.Top;
                    return true;
                }
                var s = Math.Round((_settings.OverlayScale + Math.Sign(delta) * 0.1) * 10) / 10;
                _settings.OverlayScale = Math.Clamp(s, 0.5, 2.5);
                _settings.Save();
                ApplyOverlay();
                return true;
            }

            if (msg == MouseHook.WM_MOUSEWHEEL && _vm.IsConnected)
            {
                // Voicemeeter reports new values with a delay, so count from our own number while rolling
                bool rolling = (DateTime.Now - _micGainLast).TotalSeconds < 1.5;
                double from = rolling ? _micGainLocal : Math.Round(_vm.Get(S("Gain")));
                double g = Math.Clamp(from + Math.Sign(delta), -60, 12);
                _micGainLocal = g;
                _micGainLast = DateTime.Now;
                _vm.Set(S("Gain"), (float)g);
                if (!GainS.IsDragging) GainS.Value = g;
                mic.ShowNote($"МИКРОФОН {g:+0;-0;0} dB", (g + 60) / 72.0, delta > 0);
                return true;
            }
            return false;
        }

        if (list == null) return false;

        if (ctrl && !shift)
        {
            var id = list.RowAt(x, y);
            if (id == null) return false;

            if (msg == MouseHook.WM_MOUSEWHEEL)
            {
                // Ctrl + wheel over a person → that person's volume
                if (id == _dcUserId || !_dcAuthed) return true;
                double current = _dcRows.TryGetValue(id, out var fr) ? fr.Slider.Value : _settings.FriendVolumes.GetValueOrDefault(id, 100);
                int vol = (int)Math.Clamp(Math.Round(current / 5) * 5 + Math.Sign(delta) * 5, 0, 200);
                _dcLastTouch = DateTime.Now;
                _settings.FriendVolumes[id] = vol;
                _dcSettingsDirty = true;
                if (fr != null) fr.Slider.Value = vol;
                DcSetUser(id, vol, null);
                list.ShowVolume(id, vol);
                return true;
            }

            // Ctrl + drag → this one person, on their own
            _spkDragging = true;
            _dragPerson = id;
            _spkDragX = x;
            _spkDragY = y;
            if (ReferenceEquals(list, _speakers)) _dragOverlay = null; // pops out on the first real movement
            else
            {
                _dragOverlay = list;
                _spkStartLeft = list.Left;
                _spkStartTop = list.Top;
            }
            return true;
        }

        if (!shift) return false;

        if (msg == MouseHook.WM_LBUTTONDOWN)
        {
            // Shift + drag → the whole list, or a parked plate
            _spkDragging = true;
            _dragOverlay = list;
            _dragPerson = null;
            if (!ReferenceEquals(list, _speakers))
                foreach (var kv in _detached)
                    if (ReferenceEquals(kv.Value, list)) _dragPerson = kv.Key;
            _spkDragX = x;
            _spkDragY = y;
            _spkStartLeft = list.Left;
            _spkStartTop = list.Top;
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
        if (name.Length > 20) name = name[..19] + "…";
        var title = name.ToUpperInvariant() + (ids.Count > 1 ? $"  +{ids.Count - 1}" : "");

        _joinToast ??= new BannerToast(30);
        _joinToast.Pop(isJoin ? "ЗАШЁЛ В КАНАЛ" : "ВЫШЕЛ ИЗ КАНАЛА", title, _dcAvatars.GetValueOrDefault(ids[0]), !isJoin, true);
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

    /// <summary>
    /// "Fancy" nicknames (𝐎𝐥𝐚𝐯, Ｏｌａｖ, emoji, zalgo) have no glyphs in our font and came out as boxes.
    /// Fold look-alike letters into plain ones and drop what can't be drawn.
    /// </summary>
    private static string? CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s;
        try { s = raw.Normalize(System.Text.NormalizationForm.FormKC); } catch { s = raw; }
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsSurrogate(ch)) continue; // emoji and other astral symbols
            switch (char.GetUnicodeCategory(ch))
            {
                case System.Globalization.UnicodeCategory.Format:
                case System.Globalization.UnicodeCategory.Control:
                case System.Globalization.UnicodeCategory.PrivateUse:
                case System.Globalization.UnicodeCategory.OtherNotAssigned:
                case System.Globalization.UnicodeCategory.NonSpacingMark:
                case System.Globalization.UnicodeCategory.EnclosingMark:
                case System.Globalization.UnicodeCategory.OtherSymbol:
                    continue;
            }
            sb.Append(ch);
        }
        var clean = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>
    /// Discord doesn't tell us how loud each person is, but while exactly one friend is talking
    /// the level on the AUX input is theirs. Average it and flag the very quiet / very loud ones.
    /// </summary>
    private void TrackLoudness(double auxDb)
    {
        if (!_dcAuthed || !_settings.SpeakersLoudness || _settings.SpeakersCorner == 0 || auxDb < -55) return;

        string? only = null;
        int talking = 0;
        foreach (var id in _dcSpeaking)
        {
            if (id == _dcUserId) continue;
            talking++;
            only = id;
        }
        if (talking != 1 || only == null) return;

        double avg = _lvlAvg.TryGetValue(only, out var a) ? a + (auxDb - a) * 0.05 : auxDb;
        _lvlAvg[only] = avg;
        int n = _lvlCount[only] = _lvlCount.GetValueOrDefault(only) + 1;
        if (n < 25) return; // ~0.8 s of speech before judging

        int cur = _dcLoud.GetValueOrDefault(only);
        int next = cur;
        if (cur == 0) next = avg < -36 ? -1 : avg > -9 ? 1 : 0;
        else if (cur < 0 && avg > -33) next = 0;
        else if (cur > 0 && avg < -12) next = 0;
        if (next == cur) return;

        _dcLoud[only] = next;
        UpdateSpeakers();
    }

    private static readonly string DcDebugFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StealYourVoice", "discord-channel.json");

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
        SyncMouseHook();
        if (_settings.SpeakersCorner == 0)
        {
            _speakers?.Hide();
            foreach (var d in _detached.Values) d.Hide();
            return;
        }

        var members = new List<SpeakerInfo>();
        if (_settings.SpeakersShowSelf && _dcSelfInChannel && _dcUserId != null)
        {
            var f = _dcFlags.GetValueOrDefault(_dcUserId);
            // your real mute is the one in Voicemeeter, so it counts too
            members.Add(new SpeakerInfo(_dcUserId, _dcNames.GetValueOrDefault(_dcUserId, "ТЫ"),
                _dcAvatars.GetValueOrDefault(_dcUserId), f.MicOff || MuteT.IsChecked, f.Deaf, f.Live, f.Cam));
        }
        foreach (var id in _dcCurrent)
        {
            var f = _dcFlags.GetValueOrDefault(id);
            members.Add(new SpeakerInfo(id, _dcNames.GetValueOrDefault(id, id), _dcAvatars.GetValueOrDefault(id),
                f.MicOff, f.Deaf, f.Live, f.Cam));
        }

        var style = new SpeakerStyle(_settings.SpeakersOnlyTalking, _settings.SpeakersSilentOpacity,
            _settings.SpeakersScale, _settings.SpeakersAvatarsOnly);
        var loud = _settings.SpeakersLoudness ? _dcLoud : NoLoudness;

        // people parked on their own get a little window each; the rest stay in the shared list
        var attached = new List<SpeakerInfo>();
        var parked = new HashSet<string>();
        foreach (var m in members)
        {
            if (!_settings.SpeakerPositions.TryGetValue(m.Id, out var pos) || pos.Length != 2)
            {
                attached.Add(m);
                continue;
            }
            parked.Add(m.Id);
            if (!_detached.TryGetValue(m.Id, out var d)) _detached[m.Id] = d = new SpeakersOverlay();
            // while it is being dragged the live position wins over the saved one
            var at = ReferenceEquals(d, _dragOverlay) && d.CustomPos is { } live ? live : new Point(pos[0], pos[1]);
            d.SetMembers(new[] { m }, 1, 0, style, at);
            d.SetSpeaking(_dcSpeaking);
            d.SetLoudness(loud, _dcSpeaking);
            d.SetLocalMutes(_dcLocalMute, _dcUserId);
        }
        foreach (var gone in _detached.Keys.Where(k => !parked.Contains(k)).ToList())
        {
            _detached[gone].Close();
            _detached.Remove(gone);
        }

        if (attached.Count == 0 && _speakers == null) return;
        _speakers ??= new SpeakersOverlay();
        // same corner as the mic indicator → sit right next to it instead of on top
        bool shared = _settings.OverlayCorner == _settings.SpeakersCorner;
        _speakers.SetMembers(attached, _settings.SpeakersCorner, shared ? 36 : 0, style,
            _settings.SpeakersX.HasValue && _settings.SpeakersY.HasValue
                ? new Point(_settings.SpeakersX.Value, _settings.SpeakersY.Value) : null);
        _speakers.SetSpeaking(_dcSpeaking);
        _speakers.SetLoudness(loud, _dcSpeaking);
        _speakers.SetLocalMutes(_dcLocalMute, _dcUserId);
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
