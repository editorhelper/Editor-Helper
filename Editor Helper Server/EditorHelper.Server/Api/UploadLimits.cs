namespace EditorHelper.Server.Api;

/// <summary>
/// Пределы размера загружаемых файлов.
///
/// Лимит задаётся у эндпоинта атрибутом RequestSizeLimit, а Kestrel получает
/// такой же потолок с запасом на multipart-обёртку. Без согласования крупный
/// файл отбивался бы транспортом с 413 раньше, чем до него дошла бы внятная
/// проверка в обработчике, а без RequestSizeLimit у JSON-эндпоинтов не было бы
/// никакого предела — каталог принимал бы тело размером с архив.
/// </summary>
public static class UploadLimits
{
    /// <summary>Обложка карточки: изображение, больше 5 МБ для обложки не нужно.</summary>
    public const long BannerBytes = 5L * 1024 * 1024;

    /// <summary>Архив ресурса, загружаемый в папку сервера.</summary>
    public const long FileBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Архив для отправки ботом в канал. Ограничение Telegram — 50 МБ, но
    /// точную границу задаёт <see cref="Telegram.TelegramClient.MaxBotUploadBytes"/>:
    /// здесь она только для потолка транспорта, чтобы обработчик успел объяснить
    /// причину понятным сообщением вместо транспортного 413.
    /// </summary>
    public const long TelegramBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Запас на multipart-границы и заголовки частей. Считается от наибольшего
    /// лимита: запас нужен ровно один раз на запрос.
    /// </summary>
    public const long MultipartOverheadBytes = 1024 * 1024;

    /// <summary>Потолок тела запроса для Kestrel.</summary>
    public const long MaxRequestBodyBytes = FileBytes + MultipartOverheadBytes;

    /// <summary>Предел для JSON-запросов без файлов: карточки, ссылки, входные данные.</summary>
    public const long JsonBodyBytes = 256 * 1024;
}
