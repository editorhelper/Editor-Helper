using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EditorHelper.Services;

namespace EditorHelper.Tests;

/// <summary>
/// Фейковый сервер обновлений на loopback: отдаёт заранее заданные JSON-ответы
/// /api/v1/updates/check и файлы по /api/v1/updates/download. Тесты не ходят
/// в настоящий GitHub и не зависят от сети.
///
/// Логика каналов (какие релизы видит пользователь) живёт на сервере, поэтому
/// клиентские тесты проверяют: разбор ответа, сравнение версий, поведение
/// при отказах и честность контрольных сумм. Поведение самого сервера —
/// (какие секции попадают в ответ без прав) — покрыто серверными тестами.
/// </summary>
internal static class TestUpdateService
{
    private static Action<string, bool, string?>? _check;
    private static Action<string>? _section;

    public static async Task RunAsync(Action<string, bool, string?> check, Action<string> section)
    {
        _check = check;
        _section = section;

        TestVersionComparison();
        await TestCheckParsingAsync();
        await TestNoUpdateWhenSameVersionAsync();
        await TestNewerVersionFoundAsync();
        await TestBetaGateAsync();
        await TestErrorHandlingAsync();
        await TestChecksumVerificationAsync();
    }

    private static void Section(string name) => _section!(name);
    private static void Check(string name, bool condition, string? detail = null) =>
        _check!(name, condition, detail);

    // ------------------------------------------------------------- версии

    private static void TestVersionComparison()
    {
        Check("version parsed from tag with v prefix",
            UpdateService.TryParseVersion("v1.2.3", out var v) && v.Major == 1 && v.Minor == 2 && v.Patch == 3);

        Check("version parsed with prerelease",
            UpdateService.TryParseVersion("1.1.0-beta.2", out var pre) && pre.Pre == "beta.2");

        Check("garbage rejected", !UpdateService.TryParseVersion("latest", out _));

        Check("1.1.0 is newer than 1.0.0", UpdateService.IsNewer("1.1.0", "1.0.0"));
        Check("same version is not newer", !UpdateService.IsNewer("1.0.0", "1.0.0"));
        Check("older version is not newer", !UpdateService.IsNewer("1.0.0", "1.1.0"));
        Check("v-prefix compared correctly", UpdateService.IsNewer("v1.1.0", "1.0.0"));
        Check("two-digit minor ordering", UpdateService.IsNewer("0.10.0", "0.9.0"));

        // Одинаковый номер: финальная версия новее prerelease
        Check("1.1.0 final is newer than 1.1.0-beta", UpdateService.IsNewer("1.1.0", "1.1.0-beta"));
        Check("1.1.0-beta is not newer than 1.1.0", !UpdateService.IsNewer("1.1.0-beta", "1.1.0"));
        Check("prerelease ordering by suffix", UpdateService.IsNewer("1.1.0-beta.2", "1.1.0-beta.1"));
    }

    // ------------------------------------------------------------- разбор ответа

    private static async Task TestCheckParsingAsync()
    {
        using var api = new FakeUpdateServer();
        var session = new SessionService(api.BaseUrl);
        var service = new UpdateService(api.BaseUrl, session);

        api.CheckResponse = """
        {
          "stable": {
            "tag": "v1.0.5", "title": "Stable 1.0.5", "notes": "Fix A",
            "publishedAt": "2026-01-01T00:00:00Z", "prerelease": false,
            "installerUrl": "/api/v1/updates/download?channel=stable&tag=v1.0.5",
            "installerName": "EditorHelper-Setup-1.0.5.exe", "sizeBytes": 49800000,
            "htmlUrl": "https://github.com/test/editorhelper/releases/tag/v1.0.5"
          },
          "beta": null
        }
        """;

        var result = await service.CheckAsync(requestBetaChannel: false);

        Check("stable release parsed", result.Stable != null, result.Stable?.Tag);
        Check("version extracted without v", result.Stable?.Version == "1.0.5");
        Check("notes carried", result.Stable?.Notes == "Fix A");
        Check("installer url carried", result.Stable?.InstallerUrl?.Contains("channel=stable") == true);
        Check("size carried", result.Stable?.SizeBytes == 49800000);
        Check("beta section absent for plain user", result.Beta == null && !result.BetaAllowed);
    }

    // ------------------------------------------------------------- нет обновлений

    private static async Task TestNoUpdateWhenSameVersionAsync()
    {
        using var api = new FakeUpdateServer();
        var session = new SessionService(api.BaseUrl);
        var service = new UpdateService(api.BaseUrl, session);

        api.CheckResponse = CheckJson(tag: $"v{UpdateService.CurrentVersion}", prerelease: false, beta: false);
        var result = await service.CheckAsync(false);
        Check("same version is not offered",
            result.Stable != null &&
            !UpdateService.IsNewer(result.Stable.Version, UpdateService.CurrentVersion));
    }

    // ------------------------------------------------------------- новая версия

    private static async Task TestNewerVersionFoundAsync()
    {
        using var api = new FakeUpdateServer();
        var session = new SessionService(api.BaseUrl);
        var service = new UpdateService(api.BaseUrl, session);

        var newer = BumpVersion(UpdateService.CurrentVersion);
        api.CheckResponse = CheckJson(tag: $"v{newer}", prerelease: false, beta: false);

        var result = await service.CheckAsync(false);
        Check("newer version offered",
            result.Stable != null && UpdateService.IsNewer(result.Stable.Version, UpdateService.CurrentVersion),
            result.Stable?.Version);

        // Бета-канал у пользователя со включённым флагом, но без права:
        // сервер отдал только stable — клиент не может «увидеть» бету,
        // какой бы флаг он ни выставил локально
        api.CheckResponse = CheckJson(tag: $"v{newer}", prerelease: false, beta: false);
        var noRight = await service.CheckAsync(requestBetaChannel: true);
        Check("local beta flag cannot conjure beta release",
            noRight.Beta == null && noRight.BetaAllowed == false);
    }

    // ------------------------------------------------------------- бета-гейт

    private static async Task TestBetaGateAsync()
    {
        using var api = new FakeUpdateServer();
        var session = new SessionService(api.BaseUrl);
        var service = new UpdateService(api.BaseUrl, session);

        var beta = BumpVersion(UpdateService.CurrentVersion);
        // Серверный ответ для авторизованного тестировщика: бета-секция есть
        api.CheckResponse = CheckJson(tag: $"v{UpdateService.CurrentVersion}", prerelease: false, beta: true, betaTag: $"v{beta}-beta.1");

        var result = await service.CheckAsync(requestBetaChannel: true);
        Check("beta release parsed when server allows", result.Beta != null, result.Beta?.Tag);
        Check("beta allowed flag carried", result.BetaAllowed);
        Check("beta version comparison works",
            result.Beta != null && UpdateService.IsNewer(result.Beta.Version, UpdateService.CurrentVersion));
        Check("beta release marked prerelease", result.Beta?.Prerelease == true);

        // Стабильная секция при этом не подменяется бетой
        Check("stable section stays stable",
            result.Stable != null && !result.Stable.Prerelease);
    }

    // ------------------------------------------------------------- ошибки

    private static async Task TestErrorHandlingAsync()
    {
        // 502 от сервера: внятное исключение, не «нет обновлений»
        using var api = new FakeUpdateServer { CheckStatus = 502, CheckResponse = """{"message":"Хранилище обновлений недоступно."}""" };
        var session = new SessionService(api.BaseUrl);
        var service = new UpdateService(api.BaseUrl, session);

        var failed = false;
        string message = "";
        try { await service.CheckAsync(false); }
        catch (InvalidOperationException ex) { failed = true; message = ex.Message; }
        Check("server failure raises with message", failed && message.Contains("Хранилище"), message);

        // Битый JSON — исключение, не пустой результат
        using var garbage = new FakeUpdateServer { CheckResponse = "<html>not json</html>" };
        var garbageService = new UpdateService(garbage.BaseUrl, new SessionService(garbage.BaseUrl));
        var badJson = false;
        try { await garbageService.CheckAsync(false); }
        catch (System.Text.Json.JsonException) { badJson = true; }
        catch (InvalidDataException) { badJson = true; }
        Check("malformed response raises", badJson);
    }

    // ------------------------------------------------------------- контрольная сумма

    private static async Task TestChecksumVerificationAsync()
    {
        // Полный цикл: сервер отдаёт релиз, артефакт суммы и установщик;
        // сумма совпадает — файл сохраняется
        var payload = new byte[4096];
        new Random(7).NextBytes(payload);
        var sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        var folder = Path.Combine(Path.GetTempPath(), "eh-update-test-" + Guid.NewGuid().ToString("N"));

        using var api = new FakeUpdateServer();
        api.CheckResponse = CheckJson(tag: "v9.9.9", prerelease: false, beta: false);
        api.File("/api/v1/updates/download?channel=stable&tag=v9.9.9", payload);
        api.File("/api/v1/updates/download?channel=stable&tag=v9.9.9.sha256", Encoding.ASCII.GetBytes(sha + "  EditorHelper-Setup-9.9.9.exe"));

        var session = new SessionService(api.BaseUrl);
        var service = new UpdateService(api.BaseUrl, session);

        var result = await service.CheckAsync(false);
        Check("release offered", result.Stable != null);

        var path = await service.DownloadInstallerAsync(result.Stable!, folder);
        Check("verified installer saved", File.Exists(path));

        if (Directory.Exists(folder)) Directory.Delete(folder, true);

        // Подменённая сумма: файл удаляется, обновление отклоняется
        using var tampered = new FakeUpdateServer();
        tampered.CheckResponse = CheckJson(tag: "v9.9.9", prerelease: false, beta: false);
        tampered.File("/api/v1/updates/download?channel=stable&tag=v9.9.9", payload);
        tampered.File("/api/v1/updates/download?channel=stable&tag=v9.9.9.sha256",
            Encoding.ASCII.GetBytes(new string('0', 64)));

        var tamperedService = new UpdateService(tampered.BaseUrl, new SessionService(tampered.BaseUrl));
        var tamperedResult = await tamperedService.CheckAsync(false);
        var folder2 = Path.Combine(Path.GetTempPath(), "eh-update-test-" + Guid.NewGuid().ToString("N"));

        var rejected = false;
        try { await tamperedService.DownloadInstallerAsync(tamperedResult.Stable!, folder2); }
        catch (InvalidDataException) { rejected = true; }
        // Папка может существовать (создана до проверки), но файла в ней быть
        // не должно: несовпавшая сумма всегда удаляет загруженный файл
        var leftover = Directory.Exists(folder2)
            ? Directory.GetFiles(folder2, "*.exe")
            : Array.Empty<string>();
        Check("tampered checksum rejected and file removed",
            rejected && leftover.Length == 0,
            leftover.Length > 0 ? "installer file left on disk" : null);
        if (Directory.Exists(folder2)) Directory.Delete(folder2, true);

        // Релиз без артефакта суммы: загрузка не начинается
        using var noSum = new FakeUpdateServer();
        noSum.CheckResponse = CheckJson(tag: "v9.9.9", prerelease: false, beta: false);
        noSum.File("/api/v1/updates/download?channel=stable&tag=v9.9.9", payload);

        var noSumService = new UpdateService(noSum.BaseUrl, new SessionService(noSum.BaseUrl));
        var noSumResult = await noSumService.CheckAsync(false);
        var folder3 = Path.Combine(Path.GetTempPath(), "eh-update-test-" + Guid.NewGuid().ToString("N"));

        var refused = false;
        try { await noSumService.DownloadInstallerAsync(noSumResult.Stable!, folder3); }
        catch (InvalidDataException ex) when (ex.Message.Contains("контрольн")) { refused = true; }
        Check("release without checksum artifact refused", refused);
        if (Directory.Exists(folder3)) Directory.Delete(folder3, true);
    }

    /// <summary>Версия на патч выше заданной.</summary>
    private static string BumpVersion(string version)
    {
        var parts = version.Split('.');
        parts[^1] = (int.Parse(parts[^1]) + 1).ToString();
        return string.Join(".", parts);
    }

    private static string CheckJson(string tag, bool prerelease, bool beta, string? betaTag = null)
    {
        var stable = $$"""
        {
            "tag": "{{tag}}", "title": "{{tag}}", "notes": "notes",
            "publishedAt": "2026-01-01T00:00:00Z", "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
            "installerUrl": "/api/v1/updates/download?channel=stable&tag={{tag}}",
            "installerName": "EditorHelper-Setup-{{tag.TrimStart('v')}}.exe", "sizeBytes": 1000,
            "htmlUrl": "https://github.com/test/editorhelper/releases/tag/{{tag}}"
        }
        """;

        string betaSection;
        if (beta && betaTag != null)
        {
            betaSection = $$"""
        {
            "allowed": true,
            "release": {
              "tag": "{{betaTag}}", "title": "{{betaTag}}", "notes": "beta notes",
              "publishedAt": "2026-01-02T00:00:00Z", "prerelease": true,
              "installerUrl": "/api/v1/updates/download?channel=beta&tag={{betaTag}}",
              "installerName": "EditorHelper-Setup-{{betaTag.TrimStart('v')}}.exe", "sizeBytes": 1000,
              "htmlUrl": ""
            }
        }
        """;
        }
        else if (beta)
        {
            betaSection = """{ "allowed": true, "release": null }""";
        }
        else
        {
            betaSection = "null";
        }

        return $$"""{ "stable": {{stable}}, "beta": {{betaSection}} }""";
    }
}

/// <summary>
/// Локальный HTTP-сервер, имитирующий маршруты обновлений: /check (JSON) и
/// произвольные файлы. Минимальный HTTP поверх TcpListener — тот же приём,
/// что в LoopbackHttpServer.
/// </summary>
internal sealed class FakeUpdateServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public string BaseUrl { get; }

    public string CheckResponse { get; set; } = """{ "stable": null, "beta": null }""";
    public int CheckStatus { get; set; } = 200;

    public FakeUpdateServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

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

    public void File(string path, byte[] content) => _files[path] = content;

    private async Task RespondAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var head = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (head.ToString().Contains("\r\n\r\n")) break;
        }

        var requestLine = head.ToString().Split('\r')[0];
        // "GET /path?query HTTP/1.1": путь — второй элемент
        var path = requestLine.Split(' ')[1];

        if (path.StartsWith("/api/v1/updates/check"))
        {
            var body = Encoding.UTF8.GetBytes(CheckResponse);
            await WriteAsync(stream, CheckStatus, body, "application/json");
            return;
        }

        if (_files.TryGetValue(path, out var file))
        {
            await WriteAsync(stream, 200, file, "application/octet-stream");
            return;
        }

        await WriteAsync(stream, 404, Encoding.UTF8.GetBytes("""{"message":"not found"}"""), "application/json");
    }

    private static async Task WriteAsync(NetworkStream stream, int status, byte[] body, string contentType)
    {
        var header = $"HTTP/1.1 {status} {Reason(status)}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(body);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 502 => "Bad Gateway", _ => "Status"
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
