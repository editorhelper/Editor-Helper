using System.Text.Json;
using EditorHelper.Server;
using EditorHelper.Server.Storage;

var app = ServerHost.Create(args, logging: l => l.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
}));

var config = app.Services.GetRequiredService<ServerConfig>();
var repository = app.Services.GetRequiredService<CatalogRepository>();

await SeedFromExistingCatalogAsync(repository);

var log = app.Logger;
log.LogInformation("Editor Helper server: http://{Address}:{Port}", config.ListenAddress, config.Port);
log.LogInformation("Админка: http://{Address}:{Port}/admin", config.ListenAddress, config.Port);
log.LogInformation("Данные: {DataDir}", config.DataDir);
log.LogInformation("Telegram: {State}", config.TelegramConfigured
    ? "настроен, канал " + config.TelegramChannel
    : "не настроен — работают локальные файлы и внешние ссылки");
log.LogInformation("Токен админки лежит в {File}", Path.Combine(config.DataDir, "admin-token.txt"));

await app.RunAsync();

/// <summary>
/// Первый запуск: если своих данных ещё нет, а в проекте лежит демонстрационный
/// catalog.json, переносим его в хранилище сервера. Файлы архивов копируются
/// в папку данных, чтобы отдача не зависела от расположения исходников, а
/// существующие описания ресурсов не потерялись.
/// </summary>
static async Task SeedFromExistingCatalogAsync(CatalogRepository repository)
{
    var existing = await repository.LoadCatalogAsync();
    if (existing.Resources.Count > 0) return;

    var seedRoot = Path.Combine(AppContext.BaseDirectory, "Data");
    var catalogPath = Path.Combine(seedRoot, "Catalog", "catalog.json");
    if (!File.Exists(catalogPath)) return;

    var filesRoot = Path.Combine(repository.DataDir, "files");

    try
    {
        var json = await File.ReadAllTextAsync(catalogPath);
        var legacy = JsonSerializer.Deserialize<LegacyCatalog>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (legacy?.Resources is not { Count: > 0 }) return;

        foreach (var item in legacy.Resources)
        {
            CopyIfExists(item.DownloadUrl);
            CopyIfExists(item.Thumbnail);
            await repository.UpsertAsync(item.ToRecord());
        }
    }
    catch (JsonException)
    {
        // Старый каталог повреждён — стартуем пустыми, это не смертельно
    }

    void CopyIfExists(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("http")) return;

        var source = Path.Combine(seedRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(source)) return;

        var target = Path.Combine(filesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target)) File.Copy(source, target);
    }

}

file class LegacyCatalog
{
    public List<LegacyResource> Resources { get; set; } = new();
}

file class LegacyResource
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? LongDescription { get; set; }
    public string Type { get; set; } = "Other";
    public string Application { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Thumbnail { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string? Author { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public string? DownloadUrl { get; set; }
    public string InstallationType { get; set; } = "Manual";
    public List<string> SupportedVersions { get; set; } = new();
    public DateTime PublishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsFree { get; set; } = true;
    public string? Checksum { get; set; }

    public ResourceRecord ToRecord() => new()
    {
        Id = Id,
        Slug = Slug,
        Title = Title,
        Description = Description,
        LongDescription = LongDescription,
        Type = Type,
        Application = Application,
        Category = Category,
        Version = Version,
        Author = Author,
        Tags = Tags,
        SupportedVersions = SupportedVersions,
        FileName = FileName,
        FileSize = FileSize,
        InstallationType = InstallationType,
        PublishedAt = PublishedAt,
        UpdatedAt = UpdatedAt,
        IsFeatured = IsFeatured,
        IsFree = IsFree,
        Checksum = Checksum,
        Status = "published",

        Source = new FileSourceRef
        {
            Kind = "local",
            LocalPath = DownloadUrl,
            FileName = FileName,
            SizeBytes = FileSize,
            Sha256 = Checksum?.StartsWith("sha256:") == true ? Checksum["sha256:".Length..] : null
        },

        Banner = string.IsNullOrWhiteSpace(Thumbnail) ? null : new FileSourceRef
        {
            Kind = "local",
            LocalPath = Thumbnail,
            FileName = Path.GetFileName(Thumbnail)
        }
    };
}
