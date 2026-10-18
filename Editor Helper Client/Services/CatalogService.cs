using EditorHelper.Models;

namespace EditorHelper.Services;

/// <summary>
/// Точка доступа к каталогу для всего приложения.
/// Интерфейс не знает, откуда приходят данные — из локального файла или с сервера.
/// </summary>
public class CatalogService
{
    private static CatalogService? _instance;
    private ICatalogProvider _provider;

    public static CatalogService Instance => _instance ??= new CatalogService();

    /// <summary>Источник каталога изменился (переключение local/remote).</summary>
    public event EventHandler? ProviderChanged;

    private CatalogService()
    {
        var settings = AppSettings.Load();
        _provider = CreateProvider(settings);
    }

    /// <summary>Создаёт провайдер по текущим настройкам.</summary>
    public static ICatalogProvider CreateProvider(AppSettings settings)
    {
        if (string.Equals(settings.CatalogSource, "remote", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteCatalogProvider(settings.ApiBaseUrl);
        }

        return new LocalCatalogProvider();
    }

    /// <summary>Переключает источник по настройкам и сообщает об этом интерфейсу.</summary>
    public void ApplySettings(AppSettings settings)
    {
        _provider = CreateProvider(settings);
        ProviderChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetProvider(ICatalogProvider provider)
    {
        _provider = provider;
        ProviderChanged?.Invoke(this, EventArgs.Empty);
    }

    public ICatalogProvider GetProvider() => _provider;

    public bool IsRemote => _provider is RemoteCatalogProvider;

    public async Task<List<Resource>> GetAllResourcesAsync()
    {
        return await _provider.GetAllResourcesAsync();
    }

    public async Task<Resource?> GetResourceByIdAsync(string id)
    {
        return await _provider.GetResourceByIdAsync(id);
    }

    public async Task<List<Resource>> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetAllResourcesAsync();

        return await _provider.SearchResourcesAsync(query);
    }

    public async Task<List<Resource>> GetByApplicationAsync(string application)
    {
        return await _provider.GetResourcesByApplicationAsync(application);
    }

    public async Task<List<Resource>> GetByTypeAsync(ResourceType type)
    {
        return await _provider.GetResourcesByTypeAsync(type);
    }

    public async Task<List<Resource>> GetFeaturedAsync()
    {
        return await _provider.GetFeaturedResourcesAsync();
    }

    public async Task<Dictionary<string, int>> GetStatsAsync()
    {
        var all = await GetAllResourcesAsync();
        return new Dictionary<string, int>
        {
            ["Total"] = all.Count,
            ["Plugins"] = all.Count(r => r.Type == ResourceType.Plugin),
            ["Scripts"] = all.Count(r => r.Type == ResourceType.Script),
            ["Presets"] = all.Count(r => r.Type == ResourceType.Preset),
            ["Templates"] = all.Count(r => r.Type == ResourceType.Template),
            ["AfterEffects"] = all.Count(r => r.Application == "After Effects"),
            ["PremierePro"] = all.Count(r => r.Application == "Premiere Pro"),
            ["Photoshop"] = all.Count(r => r.Application == "Photoshop")
        };
    }

    public string GetSourceName() => _provider.GetSourceName();
}