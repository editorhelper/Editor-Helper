using System.Net.Sockets;
using System.Net;
using System.Text;

namespace EditorHelper.Server.Tests;

/// <summary>
/// Фейковый Bot API на Kestrel: отвечает на методы, которые использует сервер
/// (getMe, getChat, getChatMember, getUpdates, getFile, sendDocument), отдаёт
/// файл по file_path и заодно изображает внешний файловый источник.
/// HttpListener не подходит — под http.sys нужны резервации URL.
/// </summary>
public sealed class FakeTelegram : IAsyncDisposable
{
    public const string Token = "test-token-123";
    public const long ChatId = -1001234567;
    public const string ChannelUsername = "testchannel";
    public const long MessageId = 77;
    public const string FileName = "archive.zip";
    public const string FileId = "fid-archive-1";
    public const string FileUniqueId = "uniq-archive-1";

    /// <summary>Файл, который «больше лимита бота»: getFile вернёт для него 21 МБ.</summary>
    public const string BigFileId = "fid-archive-big";

    /// <summary>user_id, на который фейк отвечает ошибкой доступа к списку участников.</summary>
    public const long NotAdminUserId = 2;

    /// <summary>user_id, на который фейк отвечает «user not found» (человек не в канале).</summary>
    public const long UnknownUserId = 3;

    /// <summary>Содержимое «архива», которое бот отдаёт при скачивании.</summary>
    public byte[] FileBytes { get; }

    private readonly WebApplication _app;
    private int _updatesServed;

    public string BaseUrl { get; }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private FakeTelegram(WebApplication app, string baseUrl, byte[] fileBytes)
    {
        _app = app;
        BaseUrl = baseUrl;
        FileBytes = fileBytes;
    }

    public static async Task<FakeTelegram> StartAsync(byte[] fileBytes)
    {
        var port = GetFreePort();
        return await StartAsync(fileBytes, port);
    }

    public static async Task<FakeTelegram> StartAsync(byte[] fileBytes, int port)
    {
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(baseUrl);
        var app = builder.Build();

        var fake = new FakeTelegram(app, baseUrl, fileBytes);

        // Все методы Bot API приходят POST на /bot<token>/<method>
        app.MapPost("/bot{token}/{method}", async (HttpContext context, string token, string method) =>
        {
            if (token != Token)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("{\"ok\":false,\"description\":\"Unauthorized\"}");
                return;
            }

            switch (method)
            {
                case "getMe":
                    await Write(context, "{\"ok\":true,\"result\":{\"id\":1,\"is_bot\":true,\"first_name\":\"TestBot\",\"username\":\"testbot\"}}");
                    break;

                case "getChat":
                    await Write(context,
                        $"{{\"ok\":true,\"result\":{{\"id\":{ChatId},\"type\":\"channel\",\"title\":\"Test Channel\",\"username\":\"{ChannelUsername}\"}}}}");
                    break;

                case "getChatMember":
                    var memberBody = await context.Request.ReadFromJsonAsync<GetMemberRequest>();
                    // Реальный Telegram на getChatMember для самого себя в канале,
                    // куда бот не добавлен админом, отвечает 400 «member list is
                    // inaccessible» — этот случай проверяет, что сервер не глотает
                    // ошибку API, а несёт её наверх с точной причиной
                    if (memberBody?.user_id == NotAdminUserId)
                        await Write(context, "{\"ok\":false,\"description\":\"Bad Request: member list is inaccessible\"}", 400);
                    else if (memberBody?.user_id == UnknownUserId)
                        await Write(context, "{\"ok\":false,\"description\":\"Bad Request: user not found\"}", 400);
                    else
                        await Write(context, "{\"ok\":true,\"result\":{\"user\":{\"id\":1},\"status\":\"administrator\"}}");
                    break;

                case "getUpdates":
                    // Первый запрос отдаёт пост с документом, остальные — пусто:
                    // так же ведёт себя настоящий Telegram после подтверждения offset
                    var first = Interlocked.Exchange(ref fake._updatesServed, 1) == 0;
                    await Write(context, first ? fake.BuildUpdate() : "{\"ok\":true,\"result\":[]}");
                    break;

                case "getFile":
                    var body = await context.Request.ReadFromJsonAsync<GetFileRequest>();
                    var fileId = body?.file_id ?? "";
                    if (fileId == BigFileId)
                    {
                        await Write(context,
                            $"{{\"ok\":true,\"result\":{{\"file_id\":\"{BigFileId}\",\"file_unique_id\":\"uniq-big\",\"file_size\":{21 * 1024 * 1024},\"file_path\":\"documents/big.zip\"}}}}");
                    }
                    else if (fileId == FileId)
                    {
                        await Write(context,
                            $"{{\"ok\":true,\"result\":{{\"file_id\":\"{FileId}\",\"file_unique_id\":\"{FileUniqueId}\",\"file_size\":{fileBytes.Length},\"file_path\":\"documents/{FileName}\"}}}}");
                    }
                    else
                    {
                        await Write(context, "{\"ok\":false,\"description\":\"wrong file identifier specified\"}", 400);
                    }
                    break;

                case "sendDocument":
                    var form = await context.Request.ReadFormAsync();
                    var file = form.Files["document"];
                    var sentName = file?.FileName ?? FileName;
                    var sentSize = file?.Length ?? 0;
                    await Write(context, $$"""
                        {"ok":true,"result":{
                          "message_id": 101,
                          "chat": {"id": {{ChatId}}, "username": "{{ChannelUsername}}"},
                          "document": {
                            "file_id": "fid-uploaded-1",
                            "file_unique_id": "uniq-uploaded-1",
                            "file_name": "{{Escape(sentName)}}",
                            "file_size": {{sentSize}}
                          }
                        }
                        }
                        """);
                    break;

                default:
                    await Write(context, $"{{\"ok\":false,\"description\":\"Unknown method {Escape(method)}\"}}", 400);
                    break;
            }
        });

        // Скачивание файла по file_path
        app.MapGet("/file/bot{token}/{**path}", (HttpContext context, string token, string path) =>
        {
            if (token != Token || path != $"documents/{FileName}")
                return Results.NotFound();

            return Results.Bytes(fileBytes, "application/octet-stream");
        });

        // «Внешний» источник файлов
        app.MapGet("/external/{name}", (string name) =>
            name == FileName
                ? Results.Bytes(fileBytes, "application/octet-stream")
                : Results.NotFound());

        await app.StartAsync();
        return fake;
    }

    private string BuildUpdate()
    {
        return $$"""
        {"ok":true,"result":[{
          "update_id": 5001,
          "channel_post": {
            "message_id": {{MessageId}},
            "chat": {"id": {{ChatId}}, "type": "channel", "username": "{{ChannelUsername}}"},
            "document": {
              "file_id": "{{FileId}}",
              "file_unique_id": "{{FileUniqueId}}",
              "file_name": "{{FileName}}",
              "file_size": {{FileBytes.Length}},
              "mime_type": "application/zip"
            }
          }
        }]}
        """;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private record GetFileRequest(string? file_id);
    private record GetMemberRequest(long? user_id);

    private static Task Write(HttpContext context, string json, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.Body.WriteAsync(bytes).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
