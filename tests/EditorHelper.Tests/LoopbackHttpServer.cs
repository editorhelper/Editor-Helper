using System.Net;
using System.Net.Sockets;

namespace EditorHelper.Tests;

/// <summary>
/// Крошечный HTTP-сервер на loopback для проверки сетевого пути загрузки
/// без выхода наружу. На каждый запрос запускает обработчик, который сам
/// пишет ответ в сокет. Используется тестами отмены и повторов.
/// </summary>
public sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public string Url { get; }

    public LoopbackHttpServer(Func<NetworkStream, Task> onRequest)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/file";

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var stream = client.GetStream();
                        // Читаем заголовки запроса до пустой строки — серверу они
                        // не нужны, но сокет надо освободить от входящих данных
                        await DrainRequestHeadAsync(stream);
                        await onRequest(stream);
                    }
                    catch
                    {
                        // Обрыв соединения — штатная ситуация для теста повторов
                    }
                    finally
                    {
                        client.Dispose();
                    }
                });
            }
        });
    }

    private static async Task DrainRequestHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var data = new System.Text.StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            data.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
            if (data.ToString().Contains("\r\n\r\n")) break;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
