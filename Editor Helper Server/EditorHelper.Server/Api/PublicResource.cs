using System.Text.Json.Serialization;
using EditorHelper.Server.Storage;

namespace EditorHelper.Server.Api;

/// <summary>
/// Ресурс в том виде, в каком его видит клиент: формат совпадает с локальным
/// catalog.json, чтобы описания были взаимозаменяемы. Служебные поля сервера
/// (черновик, источник, заметка) сюда не попадают — клиент не должен знать
/// ни про Telegram, ни про внутреннее устройство хранилища.
/// </summary>
public class PublicResource
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? LongDescription { get; set; }
    public string Type { get; set; } = "Other";
    public string Application { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Thumbnail { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string? Author { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public string? DownloadUrl { get; set; }
    public string? DocumentationUrl { get; set; }
    public List<ResourceLink> Links { get; set; } = new();
    public string InstallationType { get; set; } = "Manual";
    public List<string> SupportedVersions { get; set; } = new();
    public DateTime PublishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsFree { get; set; } = true;
    public string? Checksum { get; set; }

    public static PublicResource FromRecord(ResourceRecord record, string baseUrl)
    {
        var id = Uri.EscapeDataString(record.Id);

        return new PublicResource
        {
            Id = record.Id,
            Slug = record.Slug,
            Title = record.Title,
            Description = record.Description,
            LongDescription = record.LongDescription,
            Type = record.Type,
            Application = record.Application,
            Category = record.Category,
            Thumbnail = record.Banner != null
                ? $"{baseUrl}/api/v1/resources/{id}/banner"
                : null,
            Version = record.Version,
            Author = record.Author,
            Tags = record.Tags,
            FileName = record.FileName ?? record.Source.FileName,
            FileSize = record.FileSize > 0 ? record.FileSize : record.Source.SizeBytes,
            DownloadUrl = $"{baseUrl}/api/v1/resources/{id}/download",
            DocumentationUrl = record.DocumentationUrl,
            Links = record.Links,
            InstallationType = record.InstallationType,
            SupportedVersions = record.SupportedVersions,
            PublishedAt = record.PublishedAt,
            UpdatedAt = record.UpdatedAt,
            IsFeatured = record.IsFeatured,
            IsFree = record.IsFree,
            Checksum = record.Checksum ?? (
                string.IsNullOrEmpty(record.Source.Sha256) ? null : $"sha256:{record.Source.Sha256}")
        };
    }
}

public class CatalogEnvelope
{
    public string Version { get; set; } = "1.0";
    public DateTime LastUpdated { get; set; }
    public List<PublicResource> Resources { get; set; } = new();
}
