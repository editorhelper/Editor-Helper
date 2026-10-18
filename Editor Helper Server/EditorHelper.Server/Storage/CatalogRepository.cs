namespace EditorHelper.Server.Storage;

/// <summary>
/// Каталог на диске: карточки ресурсов и журнал обнаруженных в канале
/// документов. Один файл на коллекцию, все операции под замком хранилища.
/// </summary>
public class CatalogRepository
{
    private readonly JsonFileStore<CatalogData> _catalog;
    private readonly JsonFileStore<DiscoveredData> _discovered;

    public CatalogRepository(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _catalog = new JsonFileStore<CatalogData>(Path.Combine(dataDir, "catalog.json"));
        _discovered = new JsonFileStore<DiscoveredData>(Path.Combine(dataDir, "discovered.json"));
    }

    public string DataDir => System.IO.Path.GetDirectoryName(_catalog.FilePath)!;

    public Task<CatalogData> LoadCatalogAsync() => _catalog.LoadAsync();

    public Task<TResult> UpdateCatalogAsync<TResult>(Func<CatalogData, TResult> change) =>
        _catalog.UpdateAsync(change);

    public Task<DiscoveredData> LoadDiscoveredAsync() => _discovered.LoadAsync();

    public Task<TResult> UpdateDiscoveredAsync<TResult>(Func<DiscoveredData, TResult> change) =>
        _discovered.UpdateAsync(change);

    // ------------------------------------------------------------ Карточки

    public async Task<ResourceRecord?> FindAsync(string idOrSlug)
    {
        var catalog = await _catalog.LoadAsync();
        return catalog.Resources.FirstOrDefault(r =>
            string.Equals(r.Id, idOrSlug, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Slug, idOrSlug, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ResourceRecord> UpsertAsync(ResourceRecord record)
    {
        return await _catalog.UpdateAsync(catalog =>
        {
            var index = catalog.Resources.FindIndex(r => r.Id == record.Id);
            record.UpdatedAt = DateTime.UtcNow;

            if (index >= 0)
            {
                catalog.Resources[index] = record;
            }
            else
            {
                catalog.Resources.Add(record);
            }

            catalog.LastUpdated = DateTime.UtcNow;
            return record;
        });
    }

    public Task<bool> DeleteAsync(string id)
    {
        return _catalog.UpdateAsync(catalog =>
        {
            var removed = catalog.Resources.RemoveAll(r => r.Id == id) > 0;
            if (removed) catalog.LastUpdated = DateTime.UtcNow;
            return removed;
        });
    }

    /// <summary>Миграция на случай, если структура каталога изменится.</summary>
    public async Task MigrateAsync()
    {
        await _catalog.UpdateAsync(catalog =>
        {
            var changed = false;
            foreach (var r in catalog.Resources)
            {
                if (string.IsNullOrEmpty(r.Status))
                {
                    r.Status = "published";
                    changed = true;
                }

                // Списки могли прийти null из старого файла каталога
                if (r.Links == null) { r.Links = new(); changed = true; }
                if (r.Tags == null) { r.Tags = new(); changed = true; }
                if (r.SupportedVersions == null) { r.SupportedVersions = new(); changed = true; }
            }
            return changed;
        });
    }
}

public class CatalogData
{
    public string Version { get; set; } = "1.0";
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public List<ResourceRecord> Resources { get; set; } = new();
}

/// <summary>
/// Документы, замеченные поллингом в канале. Хранятся отдельно от карточек:
/// один архив можно привязать к карточке позже или не привязывать вовсе, а
/// дубликаты отсекаются по file_unique_id.
/// </summary>
public class DiscoveredData
{
    public List<DiscoveredDocument> Documents { get; set; } = new();
    public long LastUpdateId { get; set; }
}

public class DiscoveredDocument
{
    public string FileId { get; set; } = string.Empty;
    public string FileUniqueId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long MessageId { get; set; }
    public string ChatId { get; set; } = string.Empty;
    public string? MessageLink { get; set; }
    public DateTime SeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>id карточки, к которой документ уже привязан — в списке помечается как занятый.</summary>
    public string? BoundResourceId { get; set; }
}
