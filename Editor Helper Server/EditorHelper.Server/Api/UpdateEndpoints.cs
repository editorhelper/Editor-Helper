using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using EditorHelper.Server.Auth;

namespace EditorHelper.Server.Api;

/// <summary>
/// Серверные метаданные обновлений. Клиент не ходит в GitHub API напрямую:
/// он спрашивает этот эндпоинт со своей Bearer-сессией, а сервер решает,
/// что можно показать конкретному пользователю.
///
/// Зачем так:
/// - Stable-релизы публичны и в бете-канале не нуждаются, но даже их описание
///   отдаётся отсюда — единая точка, где версии сравниваются;
/// - Beta-релизы живут в отдельном приватном репозитории (см. BetaRepository),
///   их метаданные и артефакты без авторизации не видны вообще;
/// - доступ к бете проверяется по сессии на каждый запрос: отзыв флага
///   is_beta_tester мгновенно прекращает выдачу бета-метаданных.
///
/// Загрузка установщика тоже идёт через сервер (GET /download), потому что
/// приватный артефакт GitHub требует авторизации, а токен в клиенте хранить
/// нельзя. Файл стримится серверу -> клиенту; целостность подтверждает
/// SHA-256 из метаданных, посчитанный по фактическому потоку.
/// </summary>
public static class UpdateEndpoints
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static void MapUpdateEndpoints(this WebApplication app)
    {
        var updates = app.MapGroup("/api/v1/updates");

        // Проверка обновления. Ответ зависит от прав пользователя:
        // бета-секция присутствует только при is_beta_tester и только
        // когда клиент явно запросил бета-канал
        updates.MapGet("/check", async Task<IResult> (
            HttpContext context, IdentityStore store, ServerConfig config, CancellationToken ct) =>
        {
            var session = await AuthEndpoints.CurrentSessionAsync(context, store, ct);
            if (session == null)
                return Results.Json(new { message = "Войдите в аккаунт, чтобы проверять обновления." }, statusCode: 401);

            if (string.IsNullOrWhiteSpace(config.StableRepository))
                return Results.Json(new { message = "Обновления не настроены на сервере." }, statusCode: 503);

            var wantsBeta = string.Equals(context.Request.Query["channel"], "beta", StringComparison.OrdinalIgnoreCase)
                            && session.User.IsBetaTester;

            var (stable, beta, error) = await FetchReleasesAsync(config, includeBeta: wantsBeta, ct);
            if (error != null)
                return Results.Json(new { message = error }, statusCode: 502);

            return Results.Json(BuildResponse(session.User, stable, beta, config));
        });

        // Загрузка установщика. Разрешение проверяется на каждый запрос по
        // сессии, поэтому отозванный доступ прерывает даже начатую серию.
        updates.MapGet("/download", async Task<IResult> (
            HttpContext context, IdentityStore store, ServerConfig config, CancellationToken ct) =>
        {
            var session = await AuthEndpoints.CurrentSessionAsync(context, store, ct);
            if (session == null)
                return Results.Json(new { message = "Требуется вход в аккаунт." }, statusCode: 401);

            var channel = context.Request.Query["channel"].FirstOrDefault() ?? "stable";
            var tag = context.Request.Query["tag"].FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(tag))
                return Results.UnprocessableEntity(new { message = "Не указан тег релиза." });

            var (repository, token) = RepositoryFor(config, channel, session.User.IsBetaTester);
            if (repository == null)
                return Results.Json(new { message = "Канал обновлений недоступен." }, statusCode: 403);

            var (stable, beta, error) = await FetchReleasesAsync(config, includeBeta: channel == "beta", ct);
            if (error != null)
                return Results.Json(new { message = error }, statusCode: 502);

            var release = FindByTag(channel == "beta" ? beta : stable, tag);
            if (release == null)
                return Results.Json(new { message = "Релиз недоступен для вашего аккаунта." }, statusCode: 403);

            var installer = release.Assets.FirstOrDefault(a =>
                a.Name.StartsWith("EditorHelper-Setup-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (installer == null)
                return Results.Json(new { message = "В релизе нет установщика." }, statusCode: 404);

            // Поток сервер -> клиент: файл не собирается в память
            using var request = new HttpRequestMessage(HttpMethod.Get, installer.Url);
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

            HttpResponseMessage upstream;
            try
            {
                upstream = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException)
            {
                return Results.Json(new { message = "Хранилище обновлений недоступно." }, statusCode: 502);
            }

            if (!upstream.IsSuccessStatusCode)
            {
                upstream.Dispose();
                return Results.Json(new { message = "Файл обновления недоступен." }, statusCode: 502);
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            if (upstream.Content.Headers.ContentLength is { } length)
                context.Response.ContentLength = length;
            if (upstream.Content.Headers.ContentDisposition?.FileName is { } fileName)
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

            await using var stream = await upstream.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(context.Response.Body, 81920, ct);
            return Results.Empty;
        });
    }

    /// <summary>
    /// Единый конвейер метаданных для /check и /download: и Stable, и Beta
    /// описываются одинаково, чтобы сравнение версий не смешивало каналы.
    /// </summary>
    internal static async Task<(List<GitHubApiRelease>? Stable, List<GitHubApiRelease>? Beta, string? Error)> FetchReleasesAsync(
        ServerConfig config, bool includeBeta, CancellationToken ct)
    {
        var stable = await TryListAsync(config.StableRepository, null, ct);
        if (stable == null) return (null, null, "Не удалось получить список стабильных релизов.");

        List<GitHubApiRelease>? beta = null;
        if (includeBeta && !string.IsNullOrWhiteSpace(config.BetaRepository))
        {
            beta = await TryListAsync(config.BetaRepository, config.BetaRepositoryToken, ct);
            if (beta == null) return (null, null, "Не удалось получить список бета-релизов.");
        }

        return (stable, beta, null);
    }

    private static object BuildResponse(AuthUser user, List<GitHubApiRelease> stable, List<GitHubApiRelease>? beta, ServerConfig config)
    {
        var latestStable = stable
            .Where(r => r.Draft != true && r.Prerelease != true && !string.IsNullOrWhiteSpace(r.TagName))
            .OrderByDescending(r => r.TagName, TagComparer)
            .FirstOrDefault();

        // Бета-секция присутствует в ответе только для авторизованных
        // тестировщиков — пользователь без доступа не увидит ни версии,
        // ни описания, ни ссылок
        object? betaSection = null;
        if (user.IsBetaTester && beta != null)
        {
            var latestBeta = beta
                .Where(r => r.Draft != true && !string.IsNullOrWhiteSpace(r.TagName))
                .OrderByDescending(r => r.TagName, TagComparer)
                .FirstOrDefault();

            if (latestBeta != null)
            {
                betaSection = new
                {
                    allowed = true,
                    release = ToPublicRelease(latestBeta, config, "beta")
                };
            }
        }

        return new
        {
            stable = latestStable == null ? null : ToPublicRelease(latestStable, config, "stable"),
            beta = betaSection ?? (user.IsBetaTester ? new { allowed = true, release = (object?)null } : null)
        };
    }

    private static GitHubApiRelease? FindByTag(List<GitHubApiRelease>? releases, string tag) =>
        releases?.FirstOrDefault(r =>
            string.Equals(r.TagName, tag, StringComparison.OrdinalIgnoreCase) && r.Draft != true);

    private static object ToPublicRelease(GitHubApiRelease release, ServerConfig config, string channel) => new
    {
        tag = release.TagName,
        title = release.Name ?? release.TagName,
        notes = release.Body ?? "",
        publishedAt = release.PublishedAt,
        prerelease = release.Prerelease,
        // Ссылки ведут на сервер, а не в GitHub: так права проверяются при
        // каждой загрузке, и прямой URL бета-файла наружу не утекает
        installerUrl = $"/api/v1/updates/download?channel={channel}&tag={Uri.EscapeDataString(release.TagName)}",
        installerName = release.Assets
            .FirstOrDefault(a => a.Name.StartsWith("EditorHelper-Setup-", StringComparison.OrdinalIgnoreCase) &&
                                 a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))?.Name,
        sizeBytes = release.Assets
            .FirstOrDefault(a => a.Name.StartsWith("EditorHelper-Setup-", StringComparison.OrdinalIgnoreCase) &&
                                 a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))?.Size ?? 0,
        htmlUrl = release.HtmlUrl
    };

    private static (string? Repository, string? Token) RepositoryFor(ServerConfig config, string channel, bool isBeta) =>
        channel.ToLowerInvariant() switch
        {
            // Запрос беты без права — откажем явно, а не подменим стейблом:
            // подмена каналов хуже честного отказа
            "beta" when !isBeta => (null, null),
            "beta" => (config.BetaRepository, config.BetaRepositoryToken),
            _ => (config.StableRepository, null)
        };

    private static async Task<List<GitHubApiRelease>?> TryListAsync(string repository, string? token, CancellationToken ct)
    {
        // owner/repo -> API URL
        var parts = repository.Trim().TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var url = $"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases?per_page=20";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("EditorHelper-Server");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<List<GitHubApiRelease>>(JsonOptions, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal sealed class GitHubApiRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("prerelease")] public bool? Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool? Draft { get; set; }
        [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GitHubApiAsset>? Assets { get; set; }
    }

    internal sealed class GitHubApiAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    /// <summary>
    /// Сравнение тегов релизов: vX.Y.Z (+ необязательный суффикс -beta.N).
    /// Сравнение числовое, не строковое — иначе v0.10.0 «меньше» v0.9.0.
    /// </summary>
    public static int CompareTags(string? left, string? right) => TagComparer.Compare(left, right);

    internal static readonly IComparer<string?> TagComparer = new ReleaseTagComparer();

    private sealed class ReleaseTagComparer : IComparer<string?>
    {
        public int Compare(string? x, string? y)
        {
            (int, int, int, string?) Parse(string? tag)
            {
                if (string.IsNullOrWhiteSpace(tag)) return (0, 0, 0, null);
                var value = tag.Trim();
                if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
                var dash = value.IndexOf('-');
                var pre = dash >= 0 ? value[(dash + 1)..] : null;
                if (dash >= 0) value = value[..dash];
                var numbers = new int[3];
                var segments = value.Split('.');
                for (int i = 0; i < Math.Min(3, segments.Length); i++)
                    if (int.TryParse(segments[i], out var n)) numbers[i] = n;
                return (numbers[0], numbers[1], numbers[2], pre);
            }

            var (aMajor, aMinor, aPatch, aPre) = Parse(x);
            var (bMajor, bMinor, bPatch, bPre) = Parse(y);

            var cmp = aMajor.CompareTo(bMajor);
            if (cmp != 0) return cmp;
            cmp = aMinor.CompareTo(bMinor);
            if (cmp != 0) return cmp;
            cmp = aPatch.CompareTo(bPatch);
            if (cmp != 0) return cmp;

            // Одинаковый номер: финальная версия новее любого суффикса
            if (aPre == null && bPre == null) return 0;
            if (aPre == null) return 1;
            if (bPre == null) return -1;
            return string.CompareOrdinal(aPre, bPre);
        }
    }
}
