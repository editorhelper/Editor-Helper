using EditorHelper.Server.Storage;

namespace EditorHelper.Server.Sources;

/// <summary>
/// Абстракция источника файла. Клиент про неё не знает и всегда качает через
/// API сервера, поэтому хранилище можно заменить, не трогая клиент.
/// </summary>
public interface IFileSource
{
    /// <summary>Значение FileSourceRef.Kind, которое обрабатывает источник.</summary>
    string Kind { get; }

    /// <summary>Проверяет доступность и возвращает фактические имя и размер.</summary>
    Task<FileProbe> ProbeAsync(FileSourceRef source, CancellationToken ct);

    /// <summary>Открывает поток чтения. Поток отдаётся клиенту без полной загрузки в память.</summary>
    Task<SourceStream> OpenReadAsync(FileSourceRef source, CancellationToken ct);
}

public class FileProbe
{
    public bool Available { get; set; }
    public string? FileName { get; set; }
    public long SizeBytes { get; set; }
    public string? Error { get; set; }
}

/// <summary>Поток вместе с тем, что его освободит — HTTP-ответ или файловый дескриптор.</summary>
public sealed class SourceStream : IAsyncDisposable
{
    private readonly IDisposable? _owner;

    public SourceStream(Stream stream, long? length, IDisposable? owner = null)
    {
        Stream = stream;
        Length = length;
        _owner = owner;
    }

    public Stream Stream { get; }
    public long? Length { get; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        _owner?.Dispose();
    }
}
