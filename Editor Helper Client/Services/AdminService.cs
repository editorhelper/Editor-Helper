using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EditorHelper.Models;

namespace EditorHelper.Services;

/// <summary>
/// Карточка в том виде, в каком её отдаёт и принимает админ-API сервера:
/// поля клиента плюс служебные (статус, источник файла, заметка).
/// Один контракт — и на чтение списка, и на сохранение формы.
/// </summary>
public sealed class AdminResource
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? LongDescription { get; set; }
    public string Type { get; set; } = "Script";
    public string Application { get; set; } = "After Effects";
    public string? Category { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> SupportedVersions { get; set; } = new();
    public string InstallationType { get; set; } = "Manual";
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public string? Checksum { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsFree { get; set; } = true;
    public string Status { get; set; } = "draft";
    public string? Note { get; set; }
    public string? DocumentationUrl { get; set; }
    public List<ResourceLink> Links { get; set; } = new();
    public DateTime PublishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>
    /// Источник файла. null означает «не менять»: сервер обновляет поле только
    /// если источник передан, поэтому пустой объект был бы отвергнут как неполный.
    /// </summary>
    public AdminFileSource? Source { get; set; } = new();
    public AdminFileSource? Banner { get; set; }
}

/// <summary>Источник файла карточки: локальный, внешний HTTP или документ Telegram.</summary>
public sealed class AdminFileSource
{
    public string Kind { get; set; } = "local";
    public string? TelegramFileId { get; set; }
    public string? TelegramFileUniqueId { get; set; }
    public long? TelegramMessageId { get; set; }
    public string? TelegramChatId { get; set; }
    public string? ExternalUrl { get; set; }
    public string? LocalPath { get; set; }
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
}

/// <summary>Ошибка админ-API с внятным сообщением от сервера.</summary>
public sealed class AdminApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Состояние интеграции сервера с Telegram.</summary>
public sealed class AdminTelegramStatus
{
    public bool Enabled { get; set; }
    public bool Polling { get; set; }
    public string? BotUsername { get; set; }
    public string? ChannelTitle { get; set; }
    public bool BotIsAdmin { get; set; }
    public string? Message { get; set; }
}

/// <summary>Документ, замеченный ботом в канале: кандидат в источники файла.</summary>
public sealed class AdminTelegramDocument
{
    public string FileId { get; set; } = string.Empty;
    public string? FileUniqueId { get; set; }
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }
    public long MessageId { get; set; }
    public string? MessageLink { get; set; }
    public DateTime SeenAt { get; set; }
    public string? BoundResourceId { get; set; }
    public bool WithinBotLimit { get; set; }
}

/// <summary>Найденный по ссылке пост: тот же документ, но без истории.</summary>
public sealed class AdminTelegramLookup
{
    public string FileId { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }
    public long MessageId { get; set; }
    public bool WithinBotLimit { get; set; }
}

/// <summary>
/// Клиент админ-API. Работает только через удалённый сервер и только
/// с Bearer-сессией текущего пользователя: сервер проверяет роль Owner на
/// каждом запросе, поэтому панель в клиенте — удобство, а не защита.
/// </summary>
public sealed class AdminService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _baseUrl;
    private readonly SessionService _session;

    public AdminService(string baseUrl, SessionService session)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _session = session;
    }

    /// <summary>Адрес сервера — нужен клиенту, чтобы открыть баннер или скачать файл.</summary>
    public string BaseUrl => _baseUrl;

    public bool CanUse => _session.IsAuthenticated;

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _baseUrl + path);
        _session.ApplyAuthorization(request);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new AdminApiException(await MessageAsync(response), (int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(Json)
               ?? throw new AdminApiException("Сервер вернул пустой ответ.", (int)response.StatusCode);
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.TryGetProperty("message", out var message) &&
                message.GetString() is { Length: > 0 } text)
                return text;
        }
        catch { }
        return $"Сервер ответил ошибкой (HTTP {(int)response.StatusCode}).";
    }

    public async Task<List<AdminResource>> ListAsync(CancellationToken ct = default)
    {
        using var response = await Http.SendAsync(Request(HttpMethod.Get, "/admin/api/resources"), ct);
        return await ReadAsync<List<AdminResource>>(response);
    }

    public async Task<AdminResource> GetAsync(string id, CancellationToken ct = default)
    {
        using var response = await Http.SendAsync(Request(HttpMethod.Get, $"/admin/api/resources/{Uri.EscapeDataString(id)}"), ct);
        return await ReadAsync<AdminResource>(response);
    }

    public async Task<AdminResource> CreateAsync(AdminResource input, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, "/admin/api/resources");
        request.Content = JsonContent.Create(input, options: Json);
        using var response = await Http.SendAsync(request, ct);
        return await ReadAsync<AdminResource>(response);
    }

    public async Task<AdminResource> UpdateAsync(string id, AdminResource input, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Put, $"/admin/api/resources/{Uri.EscapeDataString(id)}");
        request.Content = JsonContent.Create(input, options: Json);
        using var response = await Http.SendAsync(request, ct);
        return await ReadAsync<AdminResource>(response);
    }

    public async Task<AdminResource> DuplicateAsync(string id, CancellationToken ct = default)
    {
        using var response = await Http.SendAsync(
            Request(HttpMethod.Post, $"/admin/api/resources/{Uri.EscapeDataString(id)}/duplicate"), ct);
        return await ReadAsync<AdminResource>(response);
    }

    public async Task<AdminResource> SetStatusAsync(string id, string action, CancellationToken ct = default)
    {
        using var response = await Http.SendAsync(
            Request(HttpMethod.Post, $"/admin/api/resources/{Uri.EscapeDataString(id)}/{action}"), ct);
        return await ReadAsync<AdminResource>(response);
    }

    public Task<AdminResource> PublishAsync(string id, CancellationToken ct = default) => SetStatusAsync(id, "publish", ct);
    public Task<AdminResource> UnpublishAsync(string id, CancellationToken ct = default) => SetStatusAsync(id, "unpublish", ct);
    public Task<AdminResource> HideAsync(string id, CancellationToken ct = default) => SetStatusAsync(id, "hide", ct);

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var response = await Http.SendAsync(
            Request(HttpMethod.Delete, $"/admin/api/resources/{Uri.EscapeDataString(id)}"), ct);
        if (!response.IsSuccessStatusCode)
            throw new AdminApiException(await MessageAsync(response), (int)response.StatusCode);
    }

    /// <summary>Загружает файл архива на сервер и возвращает готовый источник.</summary>
    public async Task<AdminFileSource> UploadFileAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, "/admin/api/files/upload");
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        request.Content = form;

        using var response = await Http.SendAsync(request, ct);
        var uploaded = await ReadAsync<UploadedFile>(response);
        return new AdminFileSource
        {
            Kind = uploaded.Kind,
            LocalPath = uploaded.LocalPath,
            FileName = uploaded.FileName,
            SizeBytes = uploaded.SizeBytes,
            Sha256 = uploaded.Sha256
        };
    }

    /// <summary>Загружает обложку для уже сохранённой карточки.</summary>
    public async Task UploadBannerAsync(string id, string fileName, Stream content, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, $"/admin/api/resources/{Uri.EscapeDataString(id)}/banner");
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        request.Content = form;

        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new AdminApiException(await MessageAsync(response), (int)response.StatusCode);
    }

    /// <summary>Проверяет внешнюю ссылку на скачивание: доступна ли и какого размера файл.</summary>
    public async Task<ExternalProbe> CheckExternalAsync(string url, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, "/admin/api/files/check-external");
        request.Content = JsonContent.Create(new { url }, options: Json);
        using var response = await Http.SendAsync(request, ct);
        return await ReadAsync<ExternalProbe>(response);
    }

    // ------------------------------------------------------- Telegram-источник

    public async Task<AdminTelegramStatus> TelegramStatusAsync(CancellationToken ct = default)
    {
        using var response = await Http.SendAsync(Request(HttpMethod.Get, "/admin/api/telegram/status"), ct);
        return await ReadAsync<AdminTelegramStatus>(response);
    }

    public async Task<List<AdminTelegramDocument>> TelegramDocumentsAsync(CancellationToken ct = default)
    {
        using var response = await Http.SendAsync(Request(HttpMethod.Get, "/admin/api/telegram/documents"), ct);
        return await ReadAsync<List<AdminTelegramDocument>>(response);
    }

    /// <summary>Ищет документ по ссылке на пост канала. 404 — бот его не видел.</summary>
    public async Task<AdminTelegramLookup> CheckTelegramLinkAsync(string url, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, "/admin/api/telegram/check-link");
        request.Content = JsonContent.Create(new { url }, options: Json);
        using var response = await Http.SendAsync(request, ct);
        return await ReadAsync<AdminTelegramLookup>(response);
    }

    /// <summary>Отправляет файл в канал через бота и возвращает file_id нового документа.</summary>
    public async Task<AdminTelegramLookup> UploadToTelegramAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Post, "/admin/api/telegram/upload");
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        request.Content = form;

        using var response = await Http.SendAsync(request, ct);
        return await ReadAsync<AdminTelegramLookup>(response);
    }

    /// <summary>
    /// Загружает байты обложки карточки через админ-маршрут (работает и для
    /// черновика). Картинка в WPF не умеет слать Bearer-заголовок, поэтому
    /// изображение выкачиваем сами и собираем BitmapImage из памяти.
    /// </summary>
    public async Task<byte[]?> FetchBannerAsync(string id, CancellationToken ct = default)
    {
        using var response = await Http.SendAsync(
            Request(HttpMethod.Get, $"/admin/api/resources/{Uri.EscapeDataString(id)}/banner"), ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private sealed class UploadedFile
    {
        public string Kind { get; set; } = "local";
        public string? LocalPath { get; set; }
        public string? FileName { get; set; }
        public long SizeBytes { get; set; }
        public string? Sha256 { get; set; }
    }

    public sealed class ExternalProbe
    {
        public bool Available { get; set; }
        public string? FileName { get; set; }
        public long SizeBytes { get; set; }
        public string? Message { get; set; }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
}
