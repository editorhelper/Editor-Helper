using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditorHelper.Services;

/// <summary>Релиз, найденный через сервер обновлений.</summary>
public sealed class UpdateRelease
{
    /// <summary>Тег релиза (v1.2.0 или v1.3.0-beta.1).</summary>
    public string Tag { get; init; } = "";

    /// <summary>Версия, разобранная из тега: v1.2.0 -> 1.2.0.</summary>
    public string Version { get; init; } = "";

    /// <summary>Название релиза.</summary>
    public string Title { get; init; } = "";

    /// <summary>Описание изменений из Release Notes.</summary>
    public string Notes { get; init; } = "";

    /// <summary>Страница релиза (для беты может отсутствовать — релиз приватный).</summary>
    public string HtmlUrl { get; init; } = "";

    /// <summary>Предрелиз (бета-канал).</summary>
    public bool Prerelease { get; init; }

    /// <summary>Относительный путь загрузки установщика на сервере обновлений.</summary>
    public string InstallerUrl { get; init; } = "";

    /// <summary>Имя файла установщика.</summary>
    public string InstallerName { get; init; } = "";

    /// <summary>Размер установщика в байтах.</summary>
    public long SizeBytes { get; init; }

    public DateTime PublishedAt { get; init; }
}

/// <summary>Один шаг прогресса загрузки установщика.</summary>
public sealed class UpdateProgress
{
    public long ReceivedBytes { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>
/// Проверка и установка обновлений. Клиент не ходит в GitHub API напрямую:
/// он спрашивает сервер обновлений со своей Bearer-сессией.
///
/// Это принципиально для каналов:
/// - Stable-пользователь получает метаданные только стабильных релизов;
/// - бета-секция ответа сервера присутствует исключительно при флаге
///   is_beta_tester у аккаунта — локальная настройка клиента этот флаг
///   не подделывает, право живёт в базе сервера;
/// - загрузка установщика тоже идёт через сервер, поэтому доступ к файлу
///   проверяется на каждый запрос, а GitHub-токен беты в клиент не попадает.
///
/// Целостность установщика подтверждается SHA-256 из отдельного артефакта
/// релиза (&lt;установщик&gt;.exe.sha256), который публикуется той же сборкой
/// CI из того же тега. Релиз без артефакта суммы отклоняется.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _baseUrl;
    private readonly SessionService _session;

    public UpdateService(string baseUrl, SessionService session)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _session = session;
    }

    /// <summary>Текущая установленная версия приложения.</summary>
    public static string CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Результат проверки: лучший релиз канала и признак бета-доступа.
    /// BetaAllowed приходит от сервера и отражает право аккаунта, а не
    /// желание клиента.
    /// </summary>
    public sealed record UpdateCheck(UpdateRelease? Stable, UpdateRelease? Beta, bool BetaAllowed);

    /// <summary>
    /// Проверяет обновления на сервере. Обычный пользователь видит только
    /// Stable; бета-секция присутствует лишь при серверном разрешении.
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(bool requestBetaChannel, CancellationToken ct = default)
    {
        var channel = requestBetaChannel ? "beta" : "stable";
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_baseUrl}/api/v1/updates/check?channel={channel}");
        _session.ApplyAuthorization(request);

        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw await ErrorAsync(response, "Не удалось проверить обновления.", ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var body = await JsonSerializer.DeserializeAsync<CheckResponse>(stream, Json, ct)
                   ?? throw new InvalidDataException("Сервер обновлений вернул пустой ответ.");

        return new UpdateCheck(
            ParseRelease(body.Stable),
            body.Beta?.Release == null ? null : ParseRelease(body.Beta.Release),
            body.Beta != null);
    }

    /// <summary>
    /// Скачивает установщик релиза через сервер обновлений и проверяет его
    /// контрольную сумму. Несовпавшая сумма — ошибка, файл удаляется.
    /// Релиз без опубликованной суммы не скачивается вовсе.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        UpdateRelease release, string destinationFolder,
        IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.InstallerUrl))
            throw new InvalidOperationException(
                "В релизе нет установщика для Windows (EditorHelper-Setup-*.exe).");

        if (string.IsNullOrWhiteSpace(release.InstallerName))
            throw new InvalidOperationException("Сервер не сообщил имя установщика.");

        Directory.CreateDirectory(destinationFolder);
        var path = Path.Combine(destinationFolder, release.InstallerName);

        // Сумма скачивается до установщика: её отсутствие означает, что
        // релиз собран без шага контрольной суммы, и доверять файлу нельзя
        var expected = await FetchChecksumAsync(release, ct);
        if (expected == null)
            throw new InvalidDataException(
                $"В релизе {release.Tag} нет контрольной суммы для {release.InstallerName}. " +
                "Обновление не устанавливается без подтверждения целостности файла.");

        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + release.InstallerUrl);
        _session.ApplyAuthorization(request);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw await ErrorAsync(response, "Не удалось скачать обновление.", ct);

        var total = response.Content.Headers.ContentLength ?? release.SizeBytes;
        long received = 0;

        await using (var content = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                progress?.Report(new UpdateProgress { ReceivedBytes = received, TotalBytes = total });
            }
        }

        if (total > 0 && received != total)
        {
            TryDelete(path);
            throw new IOException($"Загрузка прервана: получено {received} из {total} байт.");
        }

        var actual = await Sha256FileAsync(path, ct);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidDataException(
                "Контрольная сумма установщика не совпала. Файл удалён — " +
                "обновление не установлено. Повторите попытку позже.");
        }

        return path;
    }

    /// <summary>
    /// Сумма установщика из артефакта &lt;имя&gt;.sha256, опубликованного тем же
    /// CI-запуском. Артефакт лежит рядом с установщиком в релизе, сервер
    /// отдаёт его по тому же маршруту загрузки.
    /// </summary>
    private async Task<string?> FetchChecksumAsync(UpdateRelease release, CancellationToken ct)
    {
        var checksumUrl = release.InstallerUrl + ".sha256";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + checksumUrl);
            _session.ApplyAuthorization(request);
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (text.Length == 0) return null;

            // Формат shasum: "<hex>  <filename>" или просто "<hex>"
            var value = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
            return value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
                ? value.ToLowerInvariant()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static UpdateRelease? ParseRelease(ReleaseResponse? raw)
    {
        if (raw == null || string.IsNullOrWhiteSpace(raw.Tag)) return null;

        var version = raw.Tag.Trim();
        if (version.StartsWith('v') || version.StartsWith('V')) version = version[1..];

        return new UpdateRelease
        {
            Tag = raw.Tag,
            Version = version,
            Title = raw.Title ?? raw.Tag,
            Notes = raw.Notes ?? "",
            HtmlUrl = raw.HtmlUrl ?? "",
            Prerelease = raw.Prerelease,
            InstallerUrl = raw.InstallerUrl ?? "",
            InstallerName = raw.InstallerName ?? "",
            SizeBytes = raw.SizeBytes,
            PublishedAt = raw.PublishedAt ?? default
        };
    }

    private static async Task<InvalidOperationException> ErrorAsync(
        HttpResponseMessage response, string fallback, CancellationToken ct)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (json.RootElement.TryGetProperty("message", out var message) &&
                message.GetString() is { Length: > 0 } text)
                return new InvalidOperationException(text);
        }
        catch { }
        return new InvalidOperationException($"{fallback} (HTTP {(int)response.StatusCode})");
    }

    // ------------------------------------------------------------- сравнение версий

    /// <summary>Сравнение версий с поддержкой prerelease-метки (-beta.1).</summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!TryParseVersion(candidate, out var c)) return false;
        if (!TryParseVersion(current, out var cur)) return false;
        return CompareVersions(c, cur) > 0;
    }

    public static int CompareVersions((int Major, int Minor, int Patch, string? Pre) a, (int Major, int Minor, int Patch, string? Pre) b)
    {
        var numeric = a.Major.CompareTo(b.Major);
        if (numeric != 0) return numeric;
        numeric = a.Minor.CompareTo(b.Minor);
        if (numeric != 0) return numeric;
        numeric = a.Patch.CompareTo(b.Patch);
        if (numeric != 0) return numeric;

        // Одинаковые номера: обычная версия новее prerelease
        if (a.Pre == null && b.Pre == null) return 0;
        if (a.Pre == null) return 1;
        if (b.Pre == null) return -1;
        return string.CompareOrdinal(a.Pre, b.Pre);
    }

    public static bool TryParseVersion(string text, out (int Major, int Minor, int Patch, string? Pre) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        var dash = value.IndexOf('-');
        var pre = dash >= 0 ? value[(dash + 1)..] : null;
        if (dash >= 0) value = value[..dash];

        var parts = value.Split('.');
        if (parts.Length is < 1 or > 3) return false;

        var numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var n) || n < 0) return false;
            numbers[i] = n;
        }

        version = (numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* файл занят — сообщение об ошибке важнее */ }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ------------------------------------------------------------ ответы сервера

    private sealed class CheckResponse
    {
        [JsonPropertyName("stable")] public ReleaseResponse? Stable { get; set; }
        [JsonPropertyName("beta")] public BetaSection? Beta { get; set; }
    }

    private sealed class BetaSection
    {
        [JsonPropertyName("allowed")] public bool Allowed { get; set; }
        [JsonPropertyName("release")] public ReleaseResponse? Release { get; set; }
    }

    private sealed class ReleaseResponse
    {
        [JsonPropertyName("tag")] public string? Tag { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("publishedAt")] public DateTime? PublishedAt { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("installerUrl")] public string? InstallerUrl { get; set; }
        [JsonPropertyName("installerName")] public string? InstallerName { get; set; }
        [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
        [JsonPropertyName("htmlUrl")] public string? HtmlUrl { get; set; }
    }
}
