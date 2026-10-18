using EditorHelper.Server.Storage;
using EditorHelper.Server.Telegram;

namespace EditorHelper.Server.Sources;

/// <summary>
/// Выбирает источник по Kind ссылки. Telegram может быть не настроен —
/// тогда его архивы честно считаются недоступными, а остальные продолжают работать.
/// </summary>
public class FileSourceResolver
{
    private readonly Dictionary<string, IFileSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly LocalFileSource _local;
    private readonly TelegramClient? _telegram;

    public FileSourceResolver(ServerConfig config, CatalogRepository repository)
    {
        AllowLocalExternalUrls = config.AllowLocalExternalUrls;

        _local = new LocalFileSource(Path.Combine(repository.DataDir, "files"));
        _sources[_local.Kind] = _local;

        var http = new HttpFileSource(bypassSsrfGuard: config.AllowLocalExternalUrls);
        _sources[http.Kind] = http;

        if (config.TelegramConfigured)
        {
            _telegram = new TelegramClient(config.TelegramToken!, apiBase: config.TelegramApiBase);
            _sources["telegram"] = new TelegramFileSource(_telegram);
        }
    }

    /// <summary>Тестовый режим: SSRF-проверка адресов ослаблена (см. ServerConfig).</summary>
    public bool AllowLocalExternalUrls { get; }

    public bool TelegramEnabled => _telegram != null;

    public TelegramClient? Telegram => _telegram;

    public string LocalFilesRoot => _local.Root;

    public IFileSource? Resolve(FileSourceRef source)
    {
        return _sources.TryGetValue(source.Kind, out var fileSource) ? fileSource : null;
    }

    public string? DescribeUnsupported(FileSourceRef source)
    {
        if (_sources.ContainsKey(source.Kind)) return null;

        return source.Kind == "telegram"
            ? "Telegram не настроен на сервере — архив недоступен, пока не заданы токен и канал."
            : $"Неизвестный источник файла: {source.Kind}.";
    }
}
