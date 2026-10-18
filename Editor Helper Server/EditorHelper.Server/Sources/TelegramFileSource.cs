using EditorHelper.Server.Storage;
using EditorHelper.Server.Telegram;

namespace EditorHelper.Server.Sources;

/// <summary>
/// Источник на Telegram: файл лежит в канале, сервер тянет его по file_id
/// через getFile и отдаёт клиенту потоком. Ссылку не кэшируем — она живёт
/// около часа, поэтому запрашиваем перед каждой отдачей.
/// </summary>
public class TelegramFileSource : IFileSource
{
    private readonly TelegramClient _telegram;

    public TelegramFileSource(TelegramClient telegram)
    {
        _telegram = telegram;
    }

    public string Kind => "telegram";

    public async Task<FileProbe> ProbeAsync(FileSourceRef source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.TelegramFileId))
            return NotAvailable("У источника не указан telegram file_id.");

        try
        {
            var file = await _telegram.GetFileAsync(source.TelegramFileId, ct);
            var size = file.FileSize ?? source.SizeBytes;

            if (size > TelegramClient.MaxBotDownloadBytes)
            {
                return new FileProbe
                {
                    Available = false,
                    FileName = source.FileName,
                    SizeBytes = size,
                    Error = $"Файл {Format(size)} больше лимита скачивания ботом (20 МБ). " +
                            "Такой архив можно отдавать только по внешней ссылке."
                };
            }

            return new FileProbe
            {
                Available = true,
                FileName = source.FileName,
                SizeBytes = size
            };
        }
        catch (TelegramException ex)
        {
            return NotAvailable(ex.Message);
        }
    }

    public async Task<SourceStream> OpenReadAsync(FileSourceRef source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.TelegramFileId))
            throw new TelegramException("У источника не указан telegram file_id.");

        var file = await _telegram.GetFileAsync(source.TelegramFileId, ct);

        if ((file.FileSize ?? source.SizeBytes) > TelegramClient.MaxBotDownloadBytes)
        {
            throw new TelegramException(
                "Файл больше 20 МБ — Bot API не отдаёт такие целиком. Используйте внешнюю ссылку.");
        }

        // Ответ Telegram держит поток; освобождаем его вместе со стримом
        var response = await _telegram.OpenFileStreamAsync(file, ct);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        var length = response.Content.Headers.ContentLength ?? file.FileSize;

        return new SourceStream(stream, length, response);
    }

    private static FileProbe NotAvailable(string error) =>
        new() { Available = false, Error = error };

    private static string Format(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }
}
