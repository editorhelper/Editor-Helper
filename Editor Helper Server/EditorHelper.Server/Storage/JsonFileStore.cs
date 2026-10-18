using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditorHelper.Server.Storage;

/// <summary>
/// Хранилище на JSON-файле. База данных здесь не нужна: записей сотни, а не
/// миллионы, зато файл можно посмотреть глазами и скопировать как бэкап.
/// Запись идёт во временный файл с последующей заменой, чтобы внезапное
/// завершение процесса не оставило обрезанный JSON.
/// </summary>
public class JsonFileStore<T> where T : class, new()
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public JsonFileStore(string path)
    {
        _path = path;
    }

    public string FilePath => _path;

    public async Task<T> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await ReadUnlockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(T data)
    {
        await _gate.WaitAsync();
        try
        {
            await WriteUnlockedAsync(data);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Читает, меняет и записывает под одним замком — гонок между запросами не будет.</summary>
    public async Task<TResult> UpdateAsync<TResult>(Func<T, TResult> change)
    {
        await _gate.WaitAsync();
        try
        {
            var data = await ReadUnlockedAsync();
            var result = change(data);
            await WriteUnlockedAsync(data);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ReadUnlockedAsync()
    {
        if (!File.Exists(_path)) return new T();

        try
        {
            var json = await File.ReadAllTextAsync(_path);
            if (string.IsNullOrWhiteSpace(json)) return new T();
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
        }
        catch (JsonException ex)
        {
            // Молча затирать повреждённый файл нельзя — иначе потеряются
            // карточки. Откладываем копию и начинаем с чистого листа.
            var backup = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Move(_path, backup); } catch { }
            throw new InvalidDataException(
                $"Файл {Path.GetFileName(_path)} повреждён и отложен как {Path.GetFileName(backup)}: {ex.Message}",
                ex);
        }
    }

    private async Task WriteUnlockedAsync(T data)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(data, JsonOptions));

        if (File.Exists(_path)) File.Replace(temp, _path, null);
        else File.Move(temp, _path);
    }
}
