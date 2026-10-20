using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EditorHelper.Models;

namespace EditorHelper.Services;

public class LocalCatalogProvider : ICatalogProvider
{
    private readonly string _catalogPath;
    private List<Resource>? _cachedResources;

    /// <summary>
    /// Единые настройки разбора JSON. JsonStringEnumConverter обязателен:
    /// в каталоге тип и способ установки записаны строками ("Plugin", "Script"),
    /// и без конвертера разбор падает, оставляя каталог пустым.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public LocalCatalogProvider(string? catalogPath = null)
    {
        // Путь разрешается относительно рабочей папки приложения, а не текущей
        // рабочей директории — иначе каталог не находится при запуске exe.
        _catalogPath = catalogPath != null
            ? Path.Combine(AppPaths.Root, catalogPath)
            : AppPaths.CatalogFile;
    }

    public string GetSourceName() => "Local Development";

    /// <summary>Путь к файлу каталога — показывается на странице настроек.</summary>
    public string GetCatalogPath() => _catalogPath;

    public async Task<List<Resource>> GetAllResourcesAsync()
    {
        if (_cachedResources != null)
            return _cachedResources;

        if (!File.Exists(_catalogPath))
        {
            throw new CatalogUnavailableException(
                $"Catalog file not found:\n{_catalogPath}\n\n" +
                "Make sure the Data folder is next to EditorHelper.exe.",
                _catalogPath);
        }

        var json = await File.ReadAllTextAsync(_catalogPath);

        try
        {
            var catalog = JsonSerializer.Deserialize<CatalogWrapper>(json, JsonOptions);

            if (catalog == null)
                throw new CatalogUnavailableException(
                    "The catalog file is empty or has an unexpected structure.",
                    _catalogPath);

            _cachedResources = catalog.Resources;
            return _cachedResources;
        }
        catch (JsonException ex)
        {
            // Ошибку формата показываем пользователю явно, а не прячем за пустым списком
            throw new CatalogUnavailableException(
                $"The catalog could not be parsed ({ex.Path}).\n\n{ex.Message}",
                _catalogPath, null, ex);
        }
    }

    public async Task<Resource?> GetResourceByIdAsync(string id)
    {
        var resources = await GetAllResourcesAsync();
        return resources.FirstOrDefault(r => r.Id == id || r.Slug == id);
    }

    public async Task<List<Resource>> SearchResourcesAsync(string query)
    {
        var resources = await GetAllResourcesAsync();

        return resources.Where(r =>
            r.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            r.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            r.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            r.Application.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            r.Type.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
            r.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))
        ).ToList();
    }

    public async Task<List<Resource>> GetResourcesByApplicationAsync(string application)
    {
        var resources = await GetAllResourcesAsync();
        return resources
            .Where(r => r.Application.Equals(application, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<List<Resource>> GetResourcesByTypeAsync(ResourceType type)
    {
        var resources = await GetAllResourcesAsync();
        return resources.Where(r => r.Type == type).ToList();
    }

    public async Task<List<Resource>> GetFeaturedResourcesAsync()
    {
        var resources = await GetAllResourcesAsync();
        return resources.Where(r => r.IsFeatured).ToList();
    }

    public void ClearCache() => _cachedResources = null;

    private class CatalogWrapper
    {
        public string Version { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; }
        public List<Resource> Resources { get; set; } = new();
    }
}