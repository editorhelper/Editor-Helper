using EditorHelper.Models;

namespace EditorHelper.Services;

public interface ICatalogProvider
{
    Task<List<Resource>> GetAllResourcesAsync();
    Task<Resource?> GetResourceByIdAsync(string id);
    Task<List<Resource>> SearchResourcesAsync(string query);
    Task<List<Resource>> GetResourcesByApplicationAsync(string application);
    Task<List<Resource>> GetResourcesByTypeAsync(ResourceType type);
    Task<List<Resource>> GetFeaturedResourcesAsync();
    string GetSourceName();
}
