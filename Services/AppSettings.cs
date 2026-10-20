using System.IO;
using System.Text.Json;

namespace EditorHelper.Services;

/// <summary>
/// Настройки приложения. Хранятся в settings.json рядом с exe.
/// Именно здесь переключается источник каталога: локальный файл или сервер.
/// </summary>
public class AppSettings
{
    /// <summary>"local" или "remote". По умолчанию берётся из конфигурации сборки.</summary>
    public string CatalogSource { get; set; } = BuildDefaults.CatalogSource;

    /// <summary>Базовый адрес API сервера каталога.</summary>
    public string ApiBaseUrl { get; set; } = BuildDefaults.ApiBaseUrl;

    public bool RequireAuthentication { get; set; }

    /// <summary>Язык интерфейса: en, ru, pl.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Папка для загруженных файлов. null = папка по умолчанию.</summary>
    public string? DownloadFolder { get; set; }

    /// <summary>
    /// Фоновые и интерфейсные анимации: живой космос, переходы, наведение.
    /// При false интерфейс остаётся статичным — для слабых машин и для тех,
    /// кому движение мешает.
    /// </summary>
    public bool EffectsEnabled { get; set; } = true;

    /// <summary>
    /// Канал обновлений. Желание клиента: сервер всё равно не отдаст
    /// бета-метаданные без права is_beta_tester, поэтому включённый флаг без
    /// разрешения просто не даёт эффекта.
    /// </summary>
    public bool UpdateBetaChannel { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string SettingsPath => AppPaths.SettingsFile;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // Повреждённый файл настроек не должен мешать запуску
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Игнорируем ошибки записи настроек
        }
    }
}