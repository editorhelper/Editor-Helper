using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using EditorHelper.Models;

namespace EditorHelper.Services;

/// <summary>
/// Провайдер каталога, работающий через HTTP API будущего сервера.
///
/// Ожидаемый контракт API (версия v1):
///   GET {base}/api/v1/catalog
///       -> { "version": "1.0", "lastUpdated": "...", "resources": [ ... ] }
///   GET {base}/api/v1/resources/{id}
///       -> { ...один ресурс... }  либо 404
///   GET {base}/api/v1/resources?query=...&amp;application=...&amp;type=...
///       -> { "version": "1.0", "lastUpdated": "...", "resources": [ ... ] }
///
/// Провайдер не притворяется работающим сервером: если сервер недоступен,
/// он выбрасывает CatalogUnavailableException с понятным описанием,
/// а интерфейс показывает ошибку и предлагает вернуться к локальному каталогу.
/// </summary>
public class RemoteCatalogProvider : ICatalogProvider
{
    private readonly string _apiBaseUrl;
    private readonly HttpClient _httpClient;
    private readonly SessionService _sessionService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public RemoteCatalogProvider(string apiBaseUrl, HttpClient? httpClient = null, SessionService? sessionService = null)
    {
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _sessionService = sessionService ?? new SessionService(_apiBaseUrl);
    }

    public string GetSourceName() => "Remote Server";

    public string GetApiBaseUrl() => _apiBaseUrl;

    public async Task<List<Resource>> GetAllResourcesAsync()
    {
        var catalog = await GetCatalogAsync("catalog");
        return catalog.Resources;
    }

    public async Task<Resource?> GetResourceByIdAsync(string id)
    {
        try
        {
            var json = await GetStringAsync($"resources/{Uri.EscapeDataString(id)}");
            return JsonSerializer.Deserialize<Resource>(json, JsonOptions);
        }
        catch (CatalogUnavailableException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<List<Resource>> SearchResourcesAsync(string query)
    {
        var catalog = await GetCatalogAsync(
            $"resources?query={Uri.EscapeDataString(query)}");
        return catalog.Resources;
    }

    public async Task<List<Resource>> GetResourcesByApplicationAsync(string application)
    {
        var catalog = await GetCatalogAsync(
            $"resources?application={Uri.EscapeDataString(application)}");
        return catalog.Resources;
    }

    public async Task<List<Resource>> GetResourcesByTypeAsync(ResourceType type)
    {
        var catalog = await GetCatalogAsync(
            $"resources?type={Uri.EscapeDataString(type.ToString())}");
        return catalog.Resources;
    }

    public async Task<List<Resource>> GetFeaturedResourcesAsync()
    {
        var catalog = await GetCatalogAsync("resources?featured=true");
        return catalog.Resources;
    }

    /// <summary>Проверяет, отвечает ли сервер. Используется на странице настроек.</summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            await GetStringAsync("catalog");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CatalogResponse> GetCatalogAsync(string relativePath)
    {
        var json = await GetStringAsync(relativePath);

        try
        {
            var catalog = JsonSerializer.Deserialize<CatalogResponse>(json, JsonOptions);
            if (catalog == null)
            {
                throw new CatalogUnavailableException(
                    "The server returned an empty catalog response.", _apiBaseUrl);
            }

            return catalog;
        }
        catch (JsonException ex)
        {
            throw new CatalogUnavailableException(
                "The server response could not be parsed: invalid JSON.", _apiBaseUrl, 200, ex);
        }
    }

    private async Task<string> GetStringAsync(string relativePath)
    {
        var url = $"{_apiBaseUrl}/api/v1/{relativePath}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            _sessionService.ApplyAuthorization(request);
            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var message = status switch
                {
                    401 or 403 => "The server rejected the request: authorization required.",
                    404 => "The requested resource was not found on the server.",
                    >= 500 => "The server returned an error. Try again later.",
                    _ => $"The server returned an unexpected response (HTTP {status})."
                };

                throw new CatalogUnavailableException(message, _apiBaseUrl, status);
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            throw new CatalogUnavailableException(
                "Could not connect to the server. Check the address and your connection.",
                _apiBaseUrl, null, ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new CatalogUnavailableException(
                "The server did not respond in time. It may be unavailable.",
                _apiBaseUrl, null, ex);
        }
    }

    private class CatalogResponse
    {
        public string Version { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; }
        public List<Resource> Resources { get; set; } = new();
    }
}

/// <summary>
/// Ошибка получения каталога: недоступен сервер, отсутствует файл
/// или повреждены данные. Интерфейс показывает текст этой ошибки пользователю.
/// </summary>
public class CatalogUnavailableException : Exception
{
    /// <summary>Адрес сервера или путь к локальному файлу каталога.</summary>
    public string CatalogSource { get; }

    /// <summary>HTTP-код ответа сервера, если запрос дошёл до него.</summary>
    public int? StatusCode { get; }

    public CatalogUnavailableException(
        string message, string catalogSource, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        CatalogSource = catalogSource;
        StatusCode = statusCode;
    }
}