using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditorHelper.Services;

/// <summary>Релиз, найденный в GitHub Releases.</summary>
public sealed class UpdateRelease
{
    /// <summary>Версия, разобранная из тега: v1.2.0 -> 1.2.0.</summary>
    public string Version { get; init; } = "";

    /// <summary>Сырой тег релиза (v1.2.0).</summary>
    public string Tag { get; init; } = "";

    /// <summary>Название релиза.</summary>
    public string Title { get; init; } = "";

    /// <summary>Описание изменений из Release Notes (тело релиза).</summary>
    public string Notes { get; init; } = "";

    /// <summary>Страница релиза в браузере.</summary>
    public string HtmlUrl { get; init; } = "";

    /// <summary>Предрелиз (бета-канал).</summary>
    public bool Prerelease { get; init; }

    /// <summary>Ссылки на установщики из вложений релиза.</summary>
    public List<UpdateAsset> Assets { get; init; } = new();

    /// <summary>Дата публикации.</summary>
    public DateTime PublishedAt { get; init; }
}

/// <summary>Вложение релиза: установщик или контрольный файл.</summary>
public sealed class UpdateAsset
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 из имени файла вида EditorHelper-Setup-0.4.0.exe.sha256.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>Один шаг прогресса загрузки установщика.</summary>
public sealed class UpdateProgress
{
    public long ReceivedBytes { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>
/// Проверка и установка обновлений через GitHub Releases.
///
/// Используются только публичные эндпоинты GitHub без аутентификации:
///   GET https://api.github.com/repos/{owner}/{repo}/releases/latest
///   GET https://api.github.com/repos/{owner}/{repo}/releases
/// Токен в клиенте не нужен и не хранится: публикация релизов —
/// задача владельца через GitHub Actions.
///
/// Целостность установщика проверяется контрольной суммой из отдельного
/// артефакта релиза (.sha256), публикуемого той же сборкой GitHub Actions
/// из того же тега. Подпись кода (Authenticode) не задействована: сертификата
/// нет, и это ограничение отражено в SECURITY.md.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _owner;
    private readonly string _repo;
    private readonly string _apiBaseOverride;

    public UpdateService(string repositoryUrl)
    {
        var (owner, repo) = ParseRepository(repositoryUrl);
        _owner = owner;
        _repo = repo;
        _apiBaseOverride = "";
    }

    /// <summary>
    /// Тестовый конструктор: base задаётся напрямую, чтобы проверки работали
    /// против локального фейкового API, а не настоящего GitHub.
    /// </summary>
    public UpdateService(string apiBase, string owner, string repo)
    {
        _owner = owner;
        _repo = repo;
        _apiBaseOverride = apiBase.TrimEnd('/');
    }

    /// <summary>Владелец и репозиторий (для отображения в интерфейсе).</summary>
    public string Repository => $"{_owner}/{_repo}";

    public string ApiBase => _apiBaseOverride.Length > 0
        ? $"{_apiBaseOverride}/repos/{_owner}/{_repo}"
        : $"https://api.github.com/repos/{_owner}/{_repo}";

    /// <summary>
    /// Адрес репозитория вида https://github.com/owner/repo. Пустая строка —
    /// обновления не настроены.
    /// </summary>
    public bool IsConfigured => _owner.Length > 0 && _repo.Length > 0;

    /// <summary>Текущая установленная версия приложения.</summary>
    public static string CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Последний подходящий релиз для канала. Stable видит только обычные
    /// релизы, Beta — ещё и предрелизы. Предлагается только более новая версия.
    /// Возвращает null, когда обновлений нет или они не подходят.
    /// </summary>
    public async Task<UpdateRelease?> CheckForUpdateAsync(bool includePrereleases, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("Адрес репозитория обновлений не настроен.");

        // /releases/latest отдаёт только обычный (не prerelease, не draft) релиз.
        // Для бета-канала нужны все релизы: последним берётся самый свежий
        // предрелиз или обычный — что выпущено позже.
        List<UpdateRelease?> releases;
        if (includePrereleases)
        {
            releases = (await ListReleasesAsync(ct)).Cast<UpdateRelease?>().ToList();
        }
        else
        {
            releases = new List<UpdateRelease?> { await GetLatestStableAsync(ct) };
        }

        var best = releases
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Version))
            .Where(r => includePrereleases || !r!.Prerelease)
            .Where(r => IsNewer(r!.Version, CurrentVersion))
            .OrderByDescending(r => r!.Version, VersionComparer)
            .FirstOrDefault();

        return best;
    }

    /// <summary>Список последних релизов (до 30) для бета-канала.</summary>
    public async Task<List<UpdateRelease>> ListReleasesAsync(CancellationToken ct = default)
    {
        using var request = ApiRequest("releases?per_page=30");
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var raw = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, Json, ct)
                  ?? new List<GitHubRelease>();

        // Draft-релизы видны только авторизованному владельцу — в публичном
        // ответе их не бывает, но фильтр оставлен как страховка
        return raw
            .Where(r => r.Draft != true)
            .Select(FromGitHub)
            .Where(r => r != null)
            .Select(r => r!)
            .ToList();
    }

    /// <summary>Последний стабильный релиз (GitHub сам исключает prerelease и draft).</summary>
    public async Task<UpdateRelease?> GetLatestStableAsync(CancellationToken ct = default)
    {
        using var request = ApiRequest("releases/latest");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // репозиторий есть, релизов ещё нет
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var raw = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, Json, ct);
        return raw == null ? null : FromGitHub(raw);
    }

    /// <summary>
    /// Скачивает установщик релиза в указанную папку и проверяет контрольную
    /// сумму, если рядом с установщиком опубликован .sha256-артефакт.
    /// Несовпавшая сумма — ошибка, файл удаляется.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        UpdateRelease release, string destinationFolder,
        IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        var installer = ChooseInstaller(release.Assets);
        if (installer == null)
            throw new InvalidOperationException(
                "В релизе нет установщика для Windows (EditorHelper-Setup-*.exe).");

        // Сумма для установщика ищется до загрузки: без неё файл проверять нечем
        var checksum = await TryFetchChecksumForAsync(release.Assets, installer.Name, ct);
        if (checksum == null)
            throw new InvalidDataException(
                $"В релизе нет контрольной суммы для {installer.Name}. " +
                "Обновление не устанавливается без подтверждения целостности файла.");

        Directory.CreateDirectory(destinationFolder);
        var path = Path.Combine(destinationFolder, installer.Name);

        using var request = new HttpRequestMessage(HttpMethod.Get, installer.Url);
        // browser_download_url отдаёт redirect на objects.githubusercontent.com —
        // HttpClient следует ему автоматически; редиректы GitHub в HTTPS
        request.Headers.Add("User-Agent", UserAgent);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? installer.SizeBytes;
        long received = 0;

        await using (var content = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                progress?.Report(new UpdateProgress { ReceivedBytes = received, TotalBytes = total });
            }
        }

        if (total > 0 && received != total)
        {
            TryDelete(path);
            throw new IOException(
                $"Загрузка прервана: получено {received} из {total} байт.");
        }

        var actualHash = await Sha256FileAsync(path, ct);
        if (!string.Equals(actualHash, checksum, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new InvalidDataException(
                "Контрольная сумма установщика не совпала. Файл удалён — " +
                "обновление не установлено. Повторите попытку позже.");
        }

        return path;
    }

    /// <summary>
    /// Установщик для этой машины: x64-архитектура, формат EditorHelper-Setup-*.exe.
    /// Прочие вложения (суммы, архивы, исходники) не подходят.
    /// </summary>
    public static UpdateAsset? ChooseInstaller(IReadOnlyList<UpdateAsset> assets)
    {
        foreach (var asset in assets)
        {
            var name = asset.Name.ToLowerInvariant();
            if (name.StartsWith("editorhelper-setup-", StringComparison.Ordinal) &&
                name.EndsWith(".exe", StringComparison.Ordinal))
                return asset;
        }
        return null;
    }

    /// <summary>Сумма для установщика: ищется файл с его именем + .sha256.</summary>
    public static UpdateAsset? FindChecksumAsset(IReadOnlyList<UpdateAsset> assets, string installerName)
    {
        var expected = installerName.ToLowerInvariant() + ".sha256";
        return assets.FirstOrDefault(a => a.Name.ToLowerInvariant() == expected);
    }

    /// <summary>
    /// Скачивает и разбирает .sha256-артефакт: файл содержит hex-сумму
    /// (и, по традиции shasum, имя файла в той же строке).
    /// </summary>
    public async Task<string?> FetchChecksumAsync(UpdateAsset checksumAsset, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, checksumAsset.Url);
        request.Headers.Add("User-Agent", UserAgent);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (text.Length == 0) return null;

        // Формат: "<hex>  <filename>" или просто "<hex>"
        var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var value = parts[0];
        return value.Length is 64 or 128 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
            ? value
            : null;
    }

    /// <summary>Сумма установщика, если .sha256-артефакт опубликован и корректен.</summary>
    private async Task<string?> TryFetchChecksumForAsync(IReadOnlyList<UpdateAsset> assets, string installerName, CancellationToken ct)
    {
        var asset = FindChecksumAsset(assets, installerName);
        return asset == null ? null : await FetchChecksumAsync(asset, ct);
    }

    private HttpRequestMessage ApiRequest(string path) =>
        new(HttpMethod.Get, $"{ApiBase}/{path}");

    private const string UserAgent = "EditorHelper-Update-Check";

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub требует User-Agent; без него отвечает 403
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        // API отвечает JSON; Accept выбирает стабильную версию без бета-полей
        client.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Разбор адреса репозитория: https://github.com/owner/repo (с хвостами
    /// issues, слэшами, .git) или краткая форма owner/repo.
    /// </summary>
    public static (string Owner, string Repo) ParseRepository(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return ("", "");

        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        string path;
        int marker = trimmed.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            path = trimmed[(marker + "github.com".Length)..].Trim('/');
        else if (trimmed.Contains('/') && !trimmed.Contains(' ') && !trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            path = trimmed; // краткая форма owner/repo
        else
            return ("", "");

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2) return ("", "");

        // Игнорируем хвосты вида /releases, /issues/latest
        return (segments[0], segments[1]);
    }

    /// <summary>Сравнение версий с поддержкой prerelease-метки (-beta.1).</summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!TryParseVersion(candidate, out var c)) return false;
        if (!TryParseVersion(current, out var cur)) return false;
        return CompareVersions(c, cur) > 0;
    }

    public static int CompareVersions((int Major, int Minor, int Patch, string? Pre) a, (int Major, int Minor, int Patch, string? Pre) b)
    {
        var numeric = a.Major.CompareTo(b.Major);
        if (numeric != 0) return numeric;
        numeric = a.Minor.CompareTo(b.Minor);
        if (numeric != 0) return numeric;
        numeric = a.Patch.CompareTo(b.Patch);
        if (numeric != 0) return numeric;

        // Одинаковые номера: обычная версия новее prerelease,
        // prerelease сравнивается по своей метке
        if (a.Pre == null && b.Pre == null) return 0;
        if (a.Pre == null) return 1;
        if (b.Pre == null) return -1;
        return string.CompareOrdinal(a.Pre, b.Pre);
    }

    public static bool TryParseVersion(string text, out (int Major, int Minor, int Patch, string? Pre) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        var dash = value.IndexOf('-');
        var pre = dash >= 0 ? value[(dash + 1)..] : null;
        if (dash >= 0) value = value[..dash];

        var parts = value.Split('.');
        if (parts.Length is < 1 or > 3) return false;

        var numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var n) || n < 0) return false;
            numbers[i] = n;
        }

        version = (numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    internal static IComparer<string> VersionComparer { get; } = new VersionStringComparer();

    private sealed class VersionStringComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            TryParseVersion(x ?? "", out var a);
            TryParseVersion(y ?? "", out var b);
            return CompareVersions(a, b);
        }
    }

    private static UpdateRelease? FromGitHub(GitHubRelease raw)
    {
        if (string.IsNullOrWhiteSpace(raw.TagName)) return null;

        var version = raw.TagName.Trim();
        if (version.StartsWith('v') || version.StartsWith('V')) version = version[1..];

        return new UpdateRelease
        {
            Version = version,
            Tag = raw.TagName,
            Title = raw.Name ?? raw.TagName,
            Notes = raw.Body ?? "",
            HtmlUrl = raw.HtmlUrl ?? "",
            Prerelease = raw.Prerelease == true,
            PublishedAt = ParseDate(raw.PublishedAt),
            Assets = raw.Assets?
                .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
                .Select(a => new UpdateAsset
                {
                    Name = a.Name!,
                    Url = a.BrowserDownloadUrl!,
                    SizeBytes = a.Size,
                    Sha256 = null
                })
                .ToList() ?? new List<UpdateAsset>()
        };
    }

    private static DateTime ParseDate(string? iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var date)
            ? date
            : default;

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* файл занят — сообщение об ошибке важнее */ }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("prerelease")] public bool? Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool? Draft { get; set; }
        [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
