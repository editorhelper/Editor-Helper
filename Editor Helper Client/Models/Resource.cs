namespace EditorHelper.Models;

public class Resource
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? LongDescription { get; set; }
    public ResourceType Type { get; set; }
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
    public InstallationType InstallationType { get; set; }
    public List<string> SupportedVersions { get; set; } = new();
    public DateTime PublishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsFree { get; set; } = true;
    public string? Checksum { get; set; }
}

/// <summary>Именованная ссылка в карточке ресурса (сайт автора, демо, changelog).</summary>
public class ResourceLink
{
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public enum ResourceType
{
    Plugin,
    Script,
    Preset,
    Overlay,
    Sound,
    Texture,
    Template,
    Extension,
    Other
}

public enum InstallationType
{
    Manual,
    CopyToFolder,
    Installer,
    Script,
    Extension
}
