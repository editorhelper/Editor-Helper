using EditorHelper.Server.Storage;

namespace EditorHelper.Server.Sources;

/// <summary>
/// Файл рядом с сервером. Используется для импорта архива через Admin и для
/// проверки всей цепочки скачивания, когда Telegram ещё не настроен. Путь
/// всегда разрешается внутри папки данных, чтобы ссылка вида ../../ не ушла
/// за её пределы.
/// </summary>
public class LocalFileSource : IFileSource
{
    private readonly string _root;

    public LocalFileSource(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Kind => "local";

    public string Root => _root;

    public Task<FileProbe> ProbeAsync(FileSourceRef source, CancellationToken ct)
    {
        var path = Resolve(source.LocalPath);
        if (path == null || !File.Exists(path))
            return Task.FromResult(new FileProbe { Available = false, Error = "Локальный файл не найден." });

        var info = new FileInfo(path);
        return Task.FromResult(new FileProbe
        {
            Available = true,
            FileName = source.FileName ?? info.Name,
            SizeBytes = info.Length
        });
    }

    public Task<SourceStream> OpenReadAsync(FileSourceRef source, CancellationToken ct)
    {
        var path = Resolve(source.LocalPath);
        if (path == null || !File.Exists(path))
            throw new FileNotFoundException("Локальный файл источника не найден.", path);

        Stream stream = File.OpenRead(path);
        return Task.FromResult(new SourceStream(stream, new FileInfo(path).Length));
    }

    /// <summary>Разрешает относительный путь строго внутри корня источника.</summary>
    private string? Resolve(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return null;

        var candidate = Path.IsPathRooted(localPath)
            ? Path.GetFullPath(localPath)
            : Path.GetFullPath(Path.Combine(_root, localPath));

        var normalizedRoot = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }
}
