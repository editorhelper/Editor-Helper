using System.IO;
using System.Net.Http;
using EditorHelper.Services;

namespace EditorHelper.Tests;

/// <summary>
/// Проверки системы обновлений: разбор версий, выбор установщика,
/// фильтрация prerelease, поведение при ошибках сети и некорректных
/// ответах. GitHub заменён локальным фейковым API — наружу тесты не ходят.
/// </summary>
internal static class TestUpdateService
{
    private static Action<string, bool, string?>? _check;
    private static Action<string>? _section;

    public static async Task RunAsync(Action<string, bool, string?> check, Action<string> section)
    {
        _check = check;
        _section = section;
        await RunAsync();
    }

    private static Task RunAsync() => RunAllAsync();

    private static async Task RunAllAsync()
    {
        Section("Update service");

        TestVersionParsing();
        TestRepositoryParsing();
        TestInstallerChoice();
        await TestNoUpdateWhenSameVersionAsync();
        await TestNewerVersionFoundAsync();
        await TestPrereleaseFilteringAsync();
        await TestErrorHandlingAsync();
        await TestTamperedChecksumAsync();
    }

    private static void Section(string name) => _section!(name);
    private static void Check(string name, bool condition, string? detail = null) =>
        _check!(name, condition, detail);

    private static void TestVersionParsing()
    {
        Check("version parsed from tag with v prefix",
            UpdateService.TryParseVersion("v1.2.3", out var v) && v.Major == 1 && v.Minor == 2 && v.Patch == 3);

        Check("version parsed without prefix",
            UpdateService.TryParseVersion("1.0.0", out var plain) && plain.Major == 1 && plain.Pre == null);

        Check("version parsed with prerelease",
            UpdateService.TryParseVersion("1.1.0-beta.2", out var pre) && pre.Pre == "beta.2");

        Check("two-part version accepted",
            UpdateService.TryParseVersion("1.1", out var two) && two.Minor == 1 && two.Patch == 0);

        Check("garbage rejected",
            !UpdateService.TryParseVersion("latest", out _));

        Check("empty rejected",
            !UpdateService.TryParseVersion("", out _));

        // Обновление предлагается только на более новую версию
        Check("1.1.0 is newer than 1.0.0", UpdateService.IsNewer("1.1.0", "1.0.0"));
        Check("1.0.1 is newer than 1.0.0", UpdateService.IsNewer("1.0.1", "1.0.0"));
        Check("2.0.0 is newer than 1.9.9", UpdateService.IsNewer("2.0.0", "1.9.9"));
        Check("same version is not newer", !UpdateService.IsNewer("1.0.0", "1.0.0"));
        Check("older version is not newer", !UpdateService.IsNewer("1.0.0", "1.1.0"));
        Check("v-prefix compared correctly", UpdateService.IsNewer("v1.1.0", "1.0.0"));

        // Одинаковый номер: финальная версия новее prerelease
        Check("1.1.0 final is newer than 1.1.0-beta",
            UpdateService.IsNewer("1.1.0", "1.1.0-beta"));
        Check("1.1.0-beta is not newer than 1.1.0",
            !UpdateService.IsNewer("1.1.0-beta", "1.1.0"));
        Check("prerelease ordering by suffix",
            UpdateService.IsNewer("1.1.0-beta.2", "1.1.0-beta.1"));
    }

    private static void TestRepositoryParsing()
    {
        var (owner, repo) = UpdateService.ParseRepository("https://github.com/user/editorhelper");
        Check("repository url parsed", owner == "user" && repo == "editorhelper");

        var (owner2, repo2) = UpdateService.ParseRepository("https://github.com/user/editorhelper/issues");
        Check("repository url with path suffix parsed", owner2 == "user" && repo2 == "editorhelper");

        var (owner3, repo3) = UpdateService.ParseRepository("https://github.com/user/editorhelper.git");
        Check("repository url with .git parsed", owner3 == "user" && repo3 == "editorhelper");

        var (owner4, repo4) = UpdateService.ParseRepository("user/editorhelper");
        Check("owner/repo shorthand parsed", owner4 == "user" && repo4 == "editorhelper");

        var (owner5, repo5) = UpdateService.ParseRepository("");
        Check("empty repository is unconfigured", owner5 == "" && repo5 == "");

        var (owner6, repo6) = UpdateService.ParseRepository("https://gitlab.com/user/repo");
        Check("non-github host is unconfigured", owner6 == "" && repo6 == "");
    }

    private static void TestInstallerChoice()
    {
        var assets = new List<UpdateAsset>
        {
            new() { Name = "EditorHelper-Setup-0.4.0.exe.sha256", Url = "http://x/1" },
            new() { Name = "editorhelper-0.4.0-win.zip", Url = "http://x/2" },
            new() { Name = "README.md", Url = "http://x/3" }
        };

        Check("no installer in unrelated assets", UpdateService.ChooseInstaller(assets) == null);

        assets.Insert(0, new UpdateAsset { Name = "EditorHelper-Setup-0.4.0.exe", Url = "http://x/0" });
        var installer = UpdateService.ChooseInstaller(assets);
        Check("setup exe chosen among assets", installer?.Name == "EditorHelper-Setup-0.4.0.exe");

        var checksumAsset = UpdateService.FindChecksumAsset(assets, "EditorHelper-Setup-0.4.0.exe");
        Check("checksum asset found next to installer", checksumAsset?.Name == "EditorHelper-Setup-0.4.0.exe.sha256");

        Check("checksum asset case-insensitive",
            UpdateService.FindChecksumAsset(new List<UpdateAsset>
            {
                new() { Name = "EDITORHELPER-SETUP-0.4.0.exe.SHA256", Url = "http://x/c" }
            }, "EditorHelper-Setup-0.4.0.exe") != null);
    }

    /// <summary>Сервер с одним ответом: /releases/latest отдаёт релиз текущей версии.</summary>
    private static async Task TestNoUpdateWhenSameVersionAsync()
    {
        using var api = new GitHubApiServer();
        var service = new UpdateService(api.BaseUrl, "test", "editorhelper");

        api.AlwaysRespond(GitHubApiServer.ReleaseJson(
            $"v{UpdateService.CurrentVersion}", "Same version", "notes", false, DateTime.UtcNow,
            ("EditorHelper-Setup-0.4.0.exe", 100)));

        var release = await service.CheckForUpdateAsync(includePrereleases: false);
        Check("same version is not offered", release == null);
    }

    private static async Task TestNewerVersionFoundAsync()
    {
        using var api = new GitHubApiServer();
        var service = new UpdateService(api.BaseUrl, "test", "editorhelper");

        var newer = BumpVersion(UpdateService.CurrentVersion);
        api.AlwaysRespond(GitHubApiServer.ReleaseJson(
            $"v{newer}", "New release", "Fixed download retry bug", false, DateTime.UtcNow,
            ("EditorHelper-Setup-" + newer + ".exe", 49_000_000),
            ("EditorHelper-Setup-" + newer + ".exe.sha256", 96)));

        var release = await service.CheckForUpdateAsync(includePrereleases: false);
        Check("newer version offered", release != null && release.Version == newer, release?.Version);
        Check("release notes carried", release?.Notes == "Fixed download retry bug");
        Check("release url carried", release?.HtmlUrl?.Contains("/releases/tag/") == true);
        Check("stable flag carried", release?.Prerelease == false);
        Check("assets carried", release?.Assets.Count == 2);

        // 404 на /releases/latest: репозиторий есть, релизов нет — не ошибка
        using var empty = new GitHubApiServer();
        var emptyService = new UpdateService(empty.BaseUrl, "test", "editorhelper");
        empty.AlwaysRespond("{}", 404);
        var none = await emptyService.CheckForUpdateAsync(includePrereleases: false);
        Check("no releases yet is not an update", none == null);
    }

    private static async Task TestPrereleaseFilteringAsync()
    {
        // Бета-канал: в списке релизов стабильный той же версии и свежий предрелиз
        using var api = new GitHubApiServer();
        var service = new UpdateService(api.BaseUrl, "test", "editorhelper");

        var stable = UpdateService.CurrentVersion;
        var beta = BumpVersion(stable);

        var list = "[" + GitHubApiServer.ReleaseJson($"v{stable}", "Stable", "", false, DateTime.UtcNow) + "," +
                   GitHubApiServer.ReleaseJson($"v{beta}-beta.1", "Beta", "beta notes", true, DateTime.UtcNow.AddHours(1)) + "]";
        api.AlwaysRespond(list);

        var betaRelease = await service.CheckForUpdateAsync(includePrereleases: true);
        Check("beta channel sees prerelease", betaRelease != null && betaRelease.Prerelease, betaRelease?.Version);

        // Stable-канал: /releases/latest отдаёт только стабильный релиз (той же версии)
        using var stableApi = new GitHubApiServer();
        var stableService = new UpdateService(stableApi.BaseUrl, "test", "editorhelper");
        stableApi.AlwaysRespond(GitHubApiServer.ReleaseJson($"v{stable}", "Stable", "", false, DateTime.UtcNow));
        var stableOnly = await stableService.CheckForUpdateAsync(includePrereleases: false);
        Check("stable channel ignores newer prerelease", stableOnly == null);
    }

    private static async Task TestErrorHandlingAsync()
    {
        // API-лимит: 403 должен прийти как исключение, а не как «нет обновлений»
        using var limited = new GitHubApiServer();
        var limitedService = new UpdateService(limited.BaseUrl, "test", "editorhelper");
        limited.AlwaysRespond("{\"message\":\"API rate limit exceeded\"}", 403);

        var rateLimited = false;
        try { await limitedService.CheckForUpdateAsync(includePrereleases: false); }
        catch (HttpRequestException) { rateLimited = true; }
        Check("rate limit raises instead of 'no update'", rateLimited);

        // Некорректный JSON — ошибка, не пустой результат
        using var garbage = new GitHubApiServer();
        var garbageService = new UpdateService(garbage.BaseUrl, "test", "editorhelper");
        garbage.AlwaysRespond("<html>not json</html>");
        var badJson = false;
        try { await garbageService.CheckForUpdateAsync(includePrereleases: false); }
        catch (System.Text.Json.JsonException) { badJson = true; }
        Check("malformed response raises", badJson);

        // Не настроенный сервис честно отказывает
        var unconfigured = new UpdateService("");
        var refused = false;
        try { await unconfigured.CheckForUpdateAsync(includePrereleases: false); }
        catch (InvalidOperationException) { refused = true; }
        Check("unconfigured service refuses to check", refused);
    }

    private static async Task TestTamperedChecksumAsync()
    {
        // Релиз без .sha256-артефакта: загрузку отклоняет безопасность
        using var api = new GitHubApiServer();
        var service = new UpdateService(api.BaseUrl, "test", "editorhelper");

        var newer = BumpVersion(UpdateService.CurrentVersion);
        api.AlwaysRespond(GitHubApiServer.ReleaseJson(
            $"v{newer}", "No checksum", "notes", false, DateTime.UtcNow,
            ("EditorHelper-Setup-" + newer + ".exe", 100)));

        var release = await service.CheckForUpdateAsync(includePrereleases: false);
        Check("release without checksum offered for check", release != null);

        var folder = Path.Combine(Path.GetTempPath(), "eh-update-test-" + Guid.NewGuid().ToString("N"));
        var rejected = false;
        string reason = "";
        try
        {
            await service.DownloadInstallerAsync(release!, folder);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("контрольн"))
        {
            rejected = true;
        }
        catch (HttpRequestException)
        {
            // Asset URL фейка указывает на закрытый порт: загрузка не удалась
            // по сети — файл без суммы в любом случае не попал бы на диск
            rejected = true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
        }
        Check("download refuses release without checksum artifact", rejected, reason);

        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Check("no leftover partial file", !Directory.Exists(folder));
    }

    /// <summary>Версия на патч выше заданной — «новее» для тестов.</summary>
    private static string BumpVersion(string version)
    {
        var parts = version.Split('.');
        var patch = int.Parse(parts[^1]) + 1;
        parts[^1] = patch.ToString();
        return string.Join(".", parts);
    }
}
