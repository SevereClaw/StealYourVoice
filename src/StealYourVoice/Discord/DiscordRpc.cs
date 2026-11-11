using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StealYourVoice.Discord;

/// <summary>Minimal client for Discord's local RPC server (named pipe discord-ipc-N).</summary>
public sealed class DiscordRpc : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _ready;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>Raised on a background thread: (event name, data).</summary>
    public event Action<string, JsonElement>? Event;
    public event Action? Disconnected;

    public string LastError { get; private set; } = "";
    public bool IsOpen => _pipe is { IsConnected: true };

    public async Task<bool> OpenAsync(string clientId)
    {
        for (int i = 0; i < 10 && _pipe == null; i++)
        {
            var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await p.ConnectAsync(200); _pipe = p; }
            catch { p.Dispose(); }
        }
        if (_pipe == null) { LastError = "Discord не запущен"; return false; }

        _cts = new CancellationTokenSource();
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipe = _pipe;
        var token = _cts.Token;
        _ = Task.Run(() => ReadLoop(pipe, token));

        try
        {
            await WriteFrame(0, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new Dictionary<string, object> { ["v"] = 1, ["client_id"] = clientId })));
        }
        catch { LastError = "Discord не принял подключение"; return false; }

        var done = await Task.WhenAny(_ready.Task, Task.Delay(5000));
        if (done == _ready.Task && _ready.Task.Result) return true;
        if (LastError == "") LastError = "Discord не ответил";
        return false;
    }

    public async Task<JsonElement> Command(string cmd, object args, string? evt = null, int timeoutMs = 8000)
    {
        var nonce = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;

        var msg = new Dictionary<string, object?> { ["cmd"] = cmd, ["args"] = args, ["nonce"] = nonce };
        if (evt != null) msg["evt"] = evt;
        await WriteFrame(1, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg)));

        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (done != tcs.Task)
        {
            _pending.TryRemove(nonce, out _);
            throw new TimeoutException("Discord не ответил");
        }
        return await tcs.Task;
    }

    private async Task WriteFrame(int op, byte[] payload)
    {
        var pipe = _pipe ?? throw new IOException("нет соединения");
        var frame = new byte[8 + payload.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(frame, 4);
        payload.CopyTo(frame, 8);
        await _write.WaitAsync();
        try
        {
            await pipe.WriteAsync(frame);
            await pipe.FlushAsync();
        }
        finally { _write.Release(); }
    }

    private async Task ReadLoop(Stream s, CancellationToken ct)
    {
        var header = new byte[8];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadExact(s, header, ct);
                int op = BitConverter.ToInt32(header, 0);
                int len = BitConverter.ToInt32(header, 4);
                if (len < 0 || len > 8 * 1024 * 1024) break;
                var buf = new byte[len];
                await ReadExact(s, buf, ct);

                if (op == 3) { await WriteFrame(4, buf); continue; }   // ping → pong
                if (op == 2)                                              // close
                {
                    try
                    {
                        using var d = JsonDocument.Parse(buf);
                        if (d.RootElement.TryGetProperty("message", out var m)) LastError = m.GetString() ?? "";
                    }
                    catch { /* ignore */ }
                    break;
                }
                if (op == 1) Handle(buf);
            }
        }
        catch { /* pipe closed */ }
        finally
        {
            _ready?.TrySetResult(false);
            foreach (var kv in _pending) kv.Value.TrySetException(new IOException("Discord отключился"));
            _pending.Clear();
            if (!ct.IsCancellationRequested) Disconnected?.Invoke();
        }
    }

    private void Handle(byte[] buf)
    {
        using var doc = JsonDocument.Parse(buf);
        var root = doc.RootElement;
        string? cmd = Str(root, "cmd"), evt = Str(root, "evt"), nonce = Str(root, "nonce");
        JsonElement data = root.TryGetProperty("data", out var d) ? d.Clone() : default;

        if (cmd == "DISPATCH")
        {
            if (evt == "READY") _ready?.TrySetResult(true);
            else if (evt != null) Event?.Invoke(evt, data);
            return;
        }

        if (nonce != null && _pending.TryRemove(nonce, out var tcs))
        {
            if (evt == "ERROR")
                tcs.TrySetException(new InvalidOperationException(
                    (data.ValueKind == JsonValueKind.Object ? Str(data, "message") : null) ?? "ошибка Discord"));
            else tcs.TrySetResult(data);
        }
    }

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrEmpty(v.GetString()) ? null : v.GetString())
            : null;

    private static async Task ReadExact(Stream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, buf.Length - off), ct);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _pipe = null;
    }
}

/// <summary>Keeps the Discord secret and tokens encrypted for the current Windows user (DPAPI).</summary>
public static class Secrets
{
    public static string Protect(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public static string Unprotect(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
