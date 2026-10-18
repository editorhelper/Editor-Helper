namespace EditorHelper.Server.Storage;

/// <summary>
/// Карточка ресурса в том виде, в каком она лежит на сервере: те же поля, что
/// видит клиент, плюс служебные — черновик/публикация и источник файла.
/// Служебная часть наружу не отдаётся (см. Api/PublicResource).
/// </summary>
public class ResourceRecord
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? LongDescription { get; set; }

    /// <summary>Строкой, а не enum: клиент требует строковые значения ("Plugin"), а неизвестное значение лучше отклонить при сохранении, чем получить молча дефолт при разборе.</summary>
    public string Type { get; set; } = "Other";

    public string Application { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string? Author { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> SupportedVersions { get; set; } = new();

    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public string InstallationType { get; set; } = "Manual";

    public DateTime PublishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsFree { get; set; } = true;
    public string? Checksum { get; set; }

    /// <summary>"draft", "published" или "hidden". В публичный каталог попадает только published.</summary>
    public string Status { get; set; } = "draft";

    /// <summary>Ссылка на документацию или инструкцию. Отдаётся клиенту как есть.</summary>
    public string? DocumentationUrl { get; set; }

    /// <summary>Дополнительные ссылки карточки (сайт автора, демо, changelog).</summary>
    public List<ResourceLink> Links { get; set; } = new();

    public FileSourceRef Source { get; set; } = new();
    public FileSourceRef? Banner { get; set; }

    /// <summary>Внутренняя заметка админа, клиенту не показывается.</summary>
    public string? Note { get; set; }
}

/// <summary>Именованная ссылка в карточке ресурса.</summary>
public class ResourceLink
{
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Ссылка на файл. Kind определяет, какой IFileSource с ней работает:
/// "telegram" — документ в канале, "http" — внешняя ссылка, "local" — файл
/// рядом с сервером (нужен для проверки всей цепочки без Telegram).
/// </summary>
public class FileSourceRef
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

    /// <summary>Когда источник добавлен — для сортировки списка обнаруженных архивов.</summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Последняя известная проблема источника (нет доступа, файл велик для бота). Пусто — проблем не видели.</summary>
    public string? LastError { get; set; }
}