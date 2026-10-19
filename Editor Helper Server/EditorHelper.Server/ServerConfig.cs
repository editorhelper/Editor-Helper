using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditorHelper.Server;

/// <summary>
/// Настройки сервера. Секреты приходят из переменных окружения, чтобы токен
/// не оказался ни в репозитории, ни в командной строке процесса. Файл
/// server-data/secrets.json — запасной вариант для тех, кому удобнее держать
/// значения рядом с данными; он не попадает в git (см. .gitignore).
/// </summary>
public class ServerConfig
{
    public string? TelegramToken { get; set; }
    public string? TelegramChannel { get; set; }
    public string? TelegramChannelUrl { get; set; }
    public string? TelegramBotUsername { get; set; }

    public long? OwnerTelegramId { get; set; }

    /// <summary>
    /// Базовый адрес Bot API. По умолчанию api.telegram.org; можно указать
    /// свой локальный Bot API-сервер — Telegram такую схему официально
    /// поддерживает, тесты используют её для фейка.
    /// </summary>
    public string TelegramApiBase { get; set; } = "https://api.telegram.org";

    /// <summary>
    /// Long polling вместо webhook. Локально нет публичного HTTPS, а Telegram
    /// не принимает webhook на произвольный адрес, поэтому это основной режим.
    /// </summary>
    public bool UsePolling { get; set; } = true;

    public int PollTimeoutSeconds { get; set; } = 25;

    /// <summary>
    /// Разрешить внешние ссылки на локальные адреса. Только для тестов, где
    /// «внешний» источник поднят на этой же машине; в бою должно быть false,
    /// иначе SSRF-защита теряет смысл.
    /// </summary>
    public bool AllowLocalExternalUrls { get; set; }

    public string AdminToken { get; set; } = string.Empty;
    public bool EnableDevelopmentLogin { get; set; }
    public bool AllowLegacyAdminToken { get; set; }

    public string DataDir { get; set; } = string.Empty;

    /// <summary>
    /// Базовый адрес, который сервер подставляет в downloadUrl. Пусто — взять
    /// из запроса (удобно локально, когда адрес знает только клиент).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    public int Port { get; set; } = 5080;
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Публичный репозиторий со стабильными релизами (owner/repo или полный
    /// адрес). Стабильные обновления доступны всем вошедшим пользователям.
    /// </summary>
    public string StableRepository { get; set; } = "";

    /// <summary>
    /// Приватный репозиторий с бета-релизами. Его артефакты недоступны
    /// анонимно; сервер читает их по токену BetaRepositoryToken и раздаёт
    /// только пользователям с флагом is_beta_tester.
    /// </summary>
    public string BetaRepository { get; set; } = "";

    /// <summary>
    /// GitHub-токен с правом чтения приватного бета-репозитория
    /// (fine-grained, Contents: Read-only). Живёт только на сервере,
    /// в клиент не попадает никогда.
    /// </summary>
    public string? BetaRepositoryToken { get; set; }

    /// <summary>
    /// Ограничение частоты запросов по адресу клиента. Включено по умолчанию;
    /// отключается переменной окружения в тестовых сценариях, где один
    /// локальный адрес делает сотни запросов подряд.
    /// </summary>
    public bool EnableRateLimiting { get; set; } = true;

    /// <summary>Сколько запросов в минуту разрешено одному адресу на публичное API.</summary>
    public int RateLimitPerMinute { get; set; } = 120;

    /// <summary>Предел для входа: подбор кодов и запросов Telegram дороже каталога.</summary>
    public int AuthRateLimitPerMinute { get; set; } = 20;

    /// <summary>Предел админки на одну сессию владельца.</summary>
    public int OwnerRateLimitPerMinute { get; set; } = 240;

    public bool TelegramConfigured =>
        !string.IsNullOrWhiteSpace(TelegramToken) && !string.IsNullOrWhiteSpace(TelegramChannel);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Переменные окружения главнее файла: так значение можно разово переопределить,
    /// не правя данные на диске.
    /// </summary>
    public static ServerConfig Load(string contentRoot)
    {
        var dataDir = Environment.GetEnvironmentVariable("EDITORHELPER_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Path.Combine(AppContext.BaseDirectory, "server-data");

        dataDir = Path.GetFullPath(dataDir);

        var config = new ServerConfig
        {
            DataDir = dataDir,
            TelegramToken = CleanTelegramSecret(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")),
            TelegramChannel = CleanTelegramSecret(Environment.GetEnvironmentVariable("TELEGRAM_CHANNEL_ID")),
            TelegramChannelUrl = CleanTelegramSecret(Environment.GetEnvironmentVariable("TELEGRAM_CHANNEL_URL")),
            TelegramBotUsername = CleanTelegramSecret(Environment.GetEnvironmentVariable("TELEGRAM_BOT_USERNAME")),
            AdminToken = Environment.GetEnvironmentVariable("EDITORHELPER_ADMIN_TOKEN") ?? "",
            EnableDevelopmentLogin = bool.TryParse(Environment.GetEnvironmentVariable("EDITORHELPER_ENABLE_DEV_LOGIN"), out var devLogin) && devLogin,
            AllowLegacyAdminToken = bool.TryParse(Environment.GetEnvironmentVariable("EDITORHELPER_ALLOW_LEGACY_ADMIN_TOKEN"), out var legacyAdmin)
                ? legacyAdmin
                : string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase),
            OwnerTelegramId = long.TryParse(Environment.GetEnvironmentVariable("EDITORHELPER_OWNER_TELEGRAM_ID"), out var ownerId) ? ownerId : null,
            PublicBaseUrl = Environment.GetEnvironmentVariable("EDITORHELPER_PUBLIC_URL")
        };

        config.StableRepository = Environment.GetEnvironmentVariable("EDITORHELPER_STABLE_REPO") ?? "";
        config.BetaRepository = Environment.GetEnvironmentVariable("EDITORHELPER_BETA_REPO") ?? "";
        config.BetaRepositoryToken = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDITORHELPER_BETA_REPO_TOKEN"))
            ? null
            : Environment.GetEnvironmentVariable("EDITORHELPER_BETA_REPO_TOKEN");

        if (int.TryParse(Environment.GetEnvironmentVariable("EDITORHELPER_PORT"), out var port))
            config.Port = port;

        if (bool.TryParse(Environment.GetEnvironmentVariable("EDITORHELPER_RATE_LIMITING"), out var rateLimiting))
            config.EnableRateLimiting = rateLimiting;

        if (int.TryParse(Environment.GetEnvironmentVariable("EDITORHELPER_RATE_LIMIT_PER_MINUTE"), out var perMinute) && perMinute > 0)
            config.RateLimitPerMinute = perMinute;

        if (int.TryParse(Environment.GetEnvironmentVariable("EDITORHELPER_AUTH_RATE_LIMIT_PER_MINUTE"), out var authPerMinute) && authPerMinute > 0)
            config.AuthRateLimitPerMinute = authPerMinute;

        if (int.TryParse(Environment.GetEnvironmentVariable("EDITORHELPER_OWNER_RATE_LIMIT_PER_MINUTE"), out var ownerPerMinute) && ownerPerMinute > 0)
            config.OwnerRateLimitPerMinute = ownerPerMinute;

        if (bool.TryParse(Environment.GetEnvironmentVariable("TELEGRAM_USE_POLLING"), out var polling))
            config.UsePolling = polling;

        var apiBase = Environment.GetEnvironmentVariable("TELEGRAM_API_BASE");
        if (!string.IsNullOrWhiteSpace(apiBase))
            config.TelegramApiBase = apiBase;

        if (int.TryParse(Environment.GetEnvironmentVariable("TELEGRAM_POLL_TIMEOUT"), out var pollTimeout))
            config.PollTimeoutSeconds = pollTimeout;

        var secretsPath = Path.Combine(dataDir, "secrets.json");
        if (File.Exists(secretsPath))
        {
            try
            {
                var fromFile = JsonSerializer.Deserialize<SecretsFile>(
                    File.ReadAllText(secretsPath), JsonOptions);

                if (fromFile != null)
                {
                    config.TelegramToken ??= CleanTelegramSecret(fromFile.TelegramToken);
                    config.TelegramChannel ??= CleanTelegramSecret(fromFile.TelegramChannel);
                    config.TelegramChannelUrl ??= CleanTelegramSecret(fromFile.TelegramChannelUrl);
                    config.TelegramBotUsername ??= CleanTelegramSecret(fromFile.TelegramBotUsername);
                    if (string.IsNullOrEmpty(config.AdminToken))
                        config.AdminToken = fromFile.AdminToken ?? "";
                    config.PublicBaseUrl ??= fromFile.PublicBaseUrl;
                    if (fromFile.Port is > 0) config.Port = fromFile.Port.Value;
                    config.StableRepository = string.IsNullOrWhiteSpace(config.StableRepository)
                        ? fromFile.StableRepository ?? ""
                        : config.StableRepository;
                    config.BetaRepository = string.IsNullOrWhiteSpace(config.BetaRepository)
                        ? fromFile.BetaRepository ?? ""
                        : config.BetaRepository;
                    config.BetaRepositoryToken ??= string.IsNullOrWhiteSpace(fromFile.BetaRepositoryToken)
                        ? null
                        : fromFile.BetaRepositoryToken;
                }
            }
            catch
            {
                // Битый файл секретов не должен мешать запуску: сервер поднимется
                // без Telegram, а в консоли будет видно, что интеграция выключена
            }
        }

        if (string.IsNullOrWhiteSpace(config.AdminToken))
            config.AdminToken = LoadOrCreateAdminToken(dataDir);

        return config;
    }

    /// <summary>
    /// Токен админки при первом запуске генерируется сам: иначе панель
    /// оказалась бы либо открытой всем, либо недоступной до ручной настройки.
    /// </summary>
    private static string LoadOrCreateAdminToken(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "admin-token.txt");

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0) return existing;
        }

        var token = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        File.WriteAllText(path, token);
        return token;
    }

    private class SecretsFile
    {
        public string? TelegramToken { get; set; }
        public string? TelegramChannel { get; set; }
        public string? TelegramChannelUrl { get; set; }
        public string? TelegramBotUsername { get; set; }
        public string? AdminToken { get; set; }
        public string? PublicBaseUrl { get; set; }
        public string? StableRepository { get; set; }
        public string? BetaRepository { get; set; }
        public string? BetaRepositoryToken { get; set; }
        public int? Port { get; set; }
    }

    /// <summary>
    /// Секреты Telegram приходят из env-переменных PowerShell или ручного JSON —
    /// и там регулярно оказываются невидимые хвостовые пробелы или перевод строки.
    /// Telegram отвечает на такой токен 404 Not Found, что выглядит как «неверный
    /// метод», хотя повреждён сам токен. Поэтому значение чистится до проверки.
    /// </summary>
    private static string? CleanTelegramSecret(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}