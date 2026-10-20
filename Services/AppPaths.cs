using System.IO;

namespace EditorHelper.Services;

/// <summary>
/// Пути к данным приложения разделены на две группы.
///
/// Program — папка рядом с exe: демонстрационный каталог, архивы и обложки.
/// Она только читается, поэтому установка в Program Files не ломает работу.
///
/// UserData — %LOCALAPPDATA%\EditorHelper: настройки, избранное, учёт загрузок,
/// сессия и идентификатор устройства. Эти файлы приложение создаёт и меняет
/// само, а записывать их рядом с exe из-под обычной учётной записи нельзя —
/// запись молча не удалась бы и пользователь терял бы состояние при каждом
/// запуске.
/// </summary>
public static class AppPaths
{
    private static string? _root;
    private static string? _userData;

    /// <summary>Папка, рядом с которой лежит exe. Только чтение.</summary>
    public static string Root => _root ??= AppContext.BaseDirectory;

    public static string DataFolder => Path.Combine(Root, "Data");

    public static string CatalogFile => Path.Combine(DataFolder, "Catalog", "catalog.json");

    public static string AssetsFolder => Path.Combine(DataFolder, "Assets");

    public static string ThumbnailsFolder => Path.Combine(DataFolder, "Thumbnails");

    /// <summary>Папка изменяемых данных текущего пользователя Windows.</summary>
    public static string UserDataFolder => _userData ??= CreateUserDataFolder();

    public static string FavoritesFile => Path.Combine(UserDataFolder, "favorites.json");

    public static string DownloadsIndexFile => Path.Combine(UserDataFolder, "downloads.json");

    public static string SettingsFile => Path.Combine(UserDataFolder, "settings.json");

    private static string CreateUserDataFolder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EditorHelper");

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Переносит состояние из папки рядом с exe — так его хранили переносимые
    /// сборки. Копируем, а не перемещаем: исходная папка остаётся рабочей, и
    /// повторный перенос ничего не ломает. Существующие файлы не перезаписываются.
    /// </summary>
    public static void MigrateLegacyState()
    {
        var legacy = new[]
        {
            (Path.Combine(Root, "settings.json"), SettingsFile),
            (Path.Combine(DataFolder, "favorites.json"), FavoritesFile),
            (Path.Combine(DataFolder, "downloads.json"), DownloadsIndexFile),
            (Path.Combine(DataFolder, "session.bin"), Path.Combine(UserDataFolder, "session.bin")),
            (Path.Combine(DataFolder, "device.key"), Path.Combine(UserDataFolder, "device.key"))
        };

        foreach (var (source, target) in legacy)
        {
            try
            {
                if (File.Exists(target) || !File.Exists(source)) continue;
                File.Copy(source, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Старые данные недоступны — стартуем с чистого состояния
            }
        }
    }
}
