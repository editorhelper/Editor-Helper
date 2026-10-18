using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EditorHelper.Tests;

/// <summary>
/// Фейковый GitHub API на loopback: отдаёт заранее заданные JSON-ответы
/// releases. Тесты обновлений не ходят в настоящий GitHub и не зависят
/// от сети, лимитов и содержимого чужого репозитория.
///
/// Сервер отвечает на любой GET по пути, начинающемуся с /releases,
/// найдя первый зарегистрированный ответ. Прочие пути дают 404.
/// </summary>
public sealed class GitHubApiServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private List<(string Json, int Status)> _responses = new();

    public string BaseUrl { get; }

    public GitHubApiServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}";

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var stream = client.GetStream();
                        await RespondAsync(stream);
                    }
                    catch { }
                    finally { client.Dispose(); }
                });
            }
        });
    }

    /// <summary>Очередь ответов: каждый GET получает следующий.</summary>
    public void RespondWith(string json, int status = 200) =>
        _responses.Add((json, status));

    /// <summary>Единственный ответ на все запросы.</summary>
    public void AlwaysRespond(string json, int status = 200)
    {
        _responses.Clear();
        _responses.Add((json, status));
        _repeat = true;
    }

    private bool _repeat;

    private async Task RespondAsync(NetworkStream stream)
    {
        // Читаем заголовки запроса до пустой строки
        var buffer = new byte[4096];
        var head = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (head.ToString().Contains("\r\n\r\n")) break;
        }

        (string Json, int Status) response = ("{}", 404);
        if (_responses.Count > 0)
        {
            response = _responses[0];
            if (!_repeat) _responses.RemoveAt(0);
        }

        var body = Encoding.UTF8.GetBytes(response.Json);
        var header = $"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n" +
                     "Content-Type: application/json\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(body);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        _ => "Status"
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }

    /// <summary>JSON одного релиза GitHub в минимальном виде, который читает клиент.</summary>
    public static string ReleaseJson(
        string tag, string name, string body, bool prerelease,
        DateTime published, params (string assetName, long size)[] assets)
    {
        var assetText = string.Join(",", assets.Select(a =>
            $"{{\"name\":\"{a.assetName}\",\"browser_download_url\":\"http://127.0.0.1:9/{a.assetName}\"," +
            $"\"size\":{a.size}}}"));

        return "{\"tag_name\":\"" + tag + "\"," +
               "\"name\":\"" + name + "\"," +
               "\"body\":" + (body == null ? "null" : "\"" + body + "\"") + "," +
               "\"html_url\":\"https://github.com/test/editorhelper/releases/tag/" + tag + "\"," +
               "\"prerelease\":" + (prerelease ? "true" : "false") + "," +
               "\"draft\":false," +
               "\"published_at\":\"" + published.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"," +
               "\"assets\":[" + assetText + "]}";
    }
}
