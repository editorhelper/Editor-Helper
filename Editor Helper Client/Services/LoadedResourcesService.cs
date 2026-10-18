using System.IO;
using System.Text.Json;

namespace EditorHelper.Services;

/// <summary>
/// Учёт ресурсов, файлы которых уже скачаны.
/// Скачанный файл — это ещё не установленный ресурс, поэтому состояние
/// хранится отдельно от избранного и от каталога.
/// </summary>
public class LoadedResourcesService
{
    private readonly string _path;
    private Dictionary<string, string> _entries;

    public LoadedResourcesService()
    {
        _path = AppPaths.DownloadsIndexFile;
        _entries = Load();
    }

    public void MarkDownloaded(string resourceId, string filePath)
    {
        _entries[resourceId] = filePath;
        Save();
    }

    public void MarkInstalled(string resourceId, string installedPath)
    {
        _entries[resourceId] = installedPath;
        Save();
    }

    public string? GetFilePath(string resourceId)
    {
        return _entries.TryGetValue(resourceId, out var path) && File.Exists(path)
            ? path
            : null;
    }

    public bool IsDownloaded(string resourceId) => GetFilePath(resourceId) != null;

    public List<(string ResourceId, string FilePath)> GetAll()
    {
        // Отдаём только те записи, чьи файлы ещё лежат на диске
        return _entries
            .Where(e => File.Exists(e.Value))
            .Select(e => (e.Key, e.Value))
            .ToList();
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>();
            }
        }
        catch
        {
            // Повреждённый список загрузок не должен мешать работе
        }

        return new Dictionary<string, string>();
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_path,
                JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Игнорируем ошибки записи
        }
    }
}