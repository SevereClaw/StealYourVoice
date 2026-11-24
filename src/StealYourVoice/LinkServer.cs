using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace StealYourVoice;

/// <summary>
/// Канал для MidnightClips (\\.\pipe\StealYourVoice.Link): одна строка JSON на запрос, одна строка JSON в ответ.
/// Только для программ того же пользователя (CurrentUserOnly), по сети недоступен.
/// </summary>
public sealed class LinkServer : IDisposable
{
    public const string PipeName = "StealYourVoice.Link";
    private readonly Func<string, string> _handle;
    private readonly CancellationTokenSource _stop = new();

    public LinkServer(Func<string, string> handle) => _handle = handle;

    /// <summary>Сколько входов в канал ждут одновременно. С одним входом, пока панель отвечала на запрос,
    /// следующий запрос не находил канала, и MidnightClips считал, что панель не запущена (строка в меню Alt+Z
    /// появлялась через раз).</summary>
    private const int Listeners = 3;

    public void Start()
    {
        for (int i = 0; i < Listeners; i++)
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "link" + i };
            thread.Start();
        }
    }

    private void Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
                string request;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    wait.CancelAfter(3000);   // клиент подключился и молчит — не держать канал
                    request = ReadLine(pipe, wait.Token);
                }
                string reply;
                try { reply = _handle(request); }
                catch (Exception ex) { reply = "{\"ok\":false,\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}"; }
                var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                try { pipe.WaitForPipeDrain(); } catch { /* клиент уже прочитал и ушёл */ }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { /* клиент промолчал 3 секунды — ждём следующего */ }
            catch (Exception) { Thread.Sleep(200); } // клиент оборвал связь — ждём следующего
        }
    }

    private static string ReadLine(Stream s, CancellationToken token)
    {
        var buf = new MemoryStream();
        var chunk = new byte[1024];
        while (buf.Length < 64 * 1024)
        {
            int n = s.ReadAsync(chunk, 0, chunk.Length, token).GetAwaiter().GetResult();
            if (n == 0) break;
            int end = Array.IndexOf(chunk, (byte)'\n', 0, n);
            buf.Write(chunk, 0, end >= 0 ? end : n);
            if (end >= 0) break;
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    public void Dispose() => _stop.Cancel();
}
