using System.IO;
using System.Windows.Media.Imaging;
using EditorHelper.Services;

namespace EditorHelper.Ui;

/// <summary>
/// Загружает изображения обложек. Отсутствующая или повреждённая картинка —
/// это не ошибка приложения: карточка просто покажет тип ресурса.
/// </summary>
public static class ThumbnailLoader
{
    private static readonly Dictionary<string, BitmapImage?> Cache = new();

    public static BitmapImage? TryLoad(string? thumbnail)
    {
        if (string.IsNullOrWhiteSpace(thumbnail)) return null;

        if (Cache.TryGetValue(thumbnail, out var cached)) return cached;

        var result = Load(thumbnail);
        Cache[thumbnail] = result;
        return result;
    }

    private static BitmapImage? Load(string thumbnail)
    {
        try
        {
            // Поддерживаем и локальный путь, и URL будущего сервера
            var isRemote = Uri.TryCreate(thumbnail, UriKind.Absolute, out var absolute) &&
                           (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps);

            var uri = isRemote
                ? absolute!
                : new Uri(Path.Combine(AppPaths.Root, thumbnail), UriKind.Absolute);

            if (uri.IsFile && !File.Exists(uri.LocalPath)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 480;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch
        {
            return null;
        }
    }
}