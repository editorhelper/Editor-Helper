using System.IO;
using System.Text;
using EditorHelper.Models;
using EditorHelper.Services;
using EditorHelper.Ui;

namespace EditorHelper.Tests;

/// <summary>
/// Проверки каталога, загрузки и установки. Запуск: dotnet run --project tests/EditorHelper.Tests
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    public static async Task<int> Main()
    {
        Console.WriteLine("Editor Helper — service tests");
        Console.WriteLine(new string('=', 60));

        await TestCatalogLoading();
        await TestCatalogSearch();
        await TestCatalogFiltering();
        await TestCatalogErrors();
        await TestDownloadService();
        await TestDownloadCancellationAndRetry();
        await TestChecksumVerification();
        await TestInstallationRules();
        await TestUpdateService.RunAsync(Check, Section);
        TestFormatting();

        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"Passed: {_passed}   Failed: {_failed}");

        if (Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures:");
            foreach (var f in Failures) Console.WriteLine($"  - {f}");
        }

        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- Каталог

    private static async Task TestCatalogLoading()
    {
        Section("Catalog loading");

        var provider = new LocalCatalogProvider();
        var resources = await provider.GetAllResourcesAsync();

        Check("catalog contains resources", resources.Count > 0, $"count = {resources.Count}");
        Check("resource ids are unique",
            resources.Select(r => r.Id).Distinct().Count() == resources.Count);
        Check("resource slugs are unique",
            resources.Select(r => r.Slug).Distinct().Count() == resources.Count);
        Check("every resource has a title",
            resources.All(r => !string.IsNullOrWhiteSpace(r.Title)));
        Check("every resource has an application",
            resources.All(r => !string.IsNullOrWhiteSpace(r.Application)));
        Check("every resource has a version",
            resources.All(r => !string.IsNullOrWhiteSpace(r.Version)));
        Check("enum values parsed, not defaulted",
            resources.Any(r => r.Type != ResourceType.Plugin),
            string.Join(", ", resources.Select(r => r.Type).Distinct()));
        Check("installation types parsed",
            resources.Select(r => r.InstallationType).Distinct().Count() > 1,
            string.Join(", ", resources.Select(r => r.InstallationType).Distinct()));
        Check("tags parsed",
            resources.All(r => r.Tags.Count > 0));

        // Демонстрационные данные должны покрывать все три программы
        foreach (var app in new[] { "After Effects", "Premiere Pro", "Photoshop" })
        {
            Check($"catalog has {app} resources",
                resources.Any(r => r.Application == app));
        }

        // Каждый ресурс должен указывать на существующий файл или на URL
        foreach (var r in resources)
        {
            if (string.IsNullOrWhiteSpace(r.DownloadUrl))
            {
                Check($"{r.Id} has a download source", false, "downloadUrl is empty");
                continue;
            }

            if (r.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = Path.Combine(AppPaths.Root, r.DownloadUrl);
            Check($"{r.Id} source file exists", File.Exists(path),
                File.Exists(path) ? null : path);
        }
    }

    private static async Task TestCatalogSearch()
    {
        Section("Search");

        var provider = new LocalCatalogProvider();

        var byTitle = await provider.SearchResourcesAsync("Motion Tools");
        Check("finds resource by title", byTitle.Any(r => r.Id == "ae-motion-tools-v2"));

        var byTag = await provider.SearchResourcesAsync("keyframes");
        Check("finds resource by tag", byTag.Any(r => r.Id == "ae-motion-tools-v2"));

        var byApplication = await provider.SearchResourcesAsync("Premiere");
        Check("finds resources by application name",
            byApplication.All(r => r.Application == "Premiere Pro") && byApplication.Count == 2,
            $"count = {byApplication.Count}");

        // Поиск по типу: скрипты находятся по слову "script",
        // даже если в названии его нет
        var byType = await provider.SearchResourcesAsync("script");
        Check("finds scripts by resource type",
            byType.Count == 2 && byType.All(r => r.Type == ResourceType.Script),
            $"count = {byType.Count}");

        Check("search is case-insensitive",
            (await provider.SearchResourcesAsync("MOTION")).Count ==
            (await provider.SearchResourcesAsync("motion")).Count);

        var noMatch = await provider.SearchResourcesAsync("zzzz-nothing-matches");
        Check("returns empty list for no matches", noMatch.Count == 0);
    }

    private static async Task TestCatalogFiltering()
    {
        Section("Filtering");

        var provider = new LocalCatalogProvider();
        var all = await provider.GetAllResourcesAsync();

        var ae = await provider.GetResourcesByApplicationAsync("After Effects");
        Check("filters After Effects", ae.Count == 3 && ae.All(r => r.Application == "After Effects"),
            $"count = {ae.Count}");

        var pr = await provider.GetResourcesByApplicationAsync("Premiere Pro");
        Check("filters Premiere Pro", pr.Count == 2, $"count = {pr.Count}");

        var ps = await provider.GetResourcesByApplicationAsync("Photoshop");
        Check("filters Photoshop", ps.Count == 1, $"count = {ps.Count}");

        var scripts = await provider.GetResourcesByTypeAsync(ResourceType.Script);
        Check("filters scripts", scripts.Count == 2, $"count = {scripts.Count}");

        var plugins = await provider.GetResourcesByTypeAsync(ResourceType.Plugin);
        Check("filters plugins", plugins.Count == 2, $"count = {plugins.Count}");

        var featured = await provider.GetFeaturedResourcesAsync();
        Check("filters featured", featured.Count > 0 && featured.All(r => r.IsFeatured),
            $"count = {featured.Count}");

        var bySlug = await provider.GetResourceByIdAsync("motion-tools");
        Check("lookup by slug works", bySlug?.Id == "ae-motion-tools-v2");

        var byId = await provider.GetResourceByIdAsync("ae-auto-backup");
        Check("lookup by id works", byId?.Slug == "auto-backup");

        var missing = await provider.GetResourceByIdAsync("does-not-exist");
        Check("lookup of missing id returns null", missing == null);

        Check("filters do not lose resources",
            ae.Count + pr.Count + ps.Count <= all.Count);
    }

    private static async Task TestCatalogErrors()
    {
        Section("Catalog error handling");

        // Отсутствующий файл каталога — это ошибка с понятным текстом,
        // а не пустой список ресурсов
        var missing = new LocalCatalogProvider("Data/Catalog/not-here.json");
        try
        {
            await missing.GetAllResourcesAsync();
            Check("missing file raises error", false, "no exception");
        }
        catch (CatalogUnavailableException ex)
        {
            Check("missing file raises CatalogUnavailableException", true);
            Check("error names the missing path",
                ex.Message.Contains("not-here.json"), ex.Message.Replace("\n", " | "));
        }

        var badPath = Path.Combine(AppPaths.DataFolder, "Catalog", "broken-test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        await File.WriteAllTextAsync(badPath,
            "{ \"resources\": [ { \"id\": 12, \"type\": \"NotAType\" } ] }");

        var broken = new LocalCatalogProvider("Data/Catalog/broken-test.json");
        try
        {
            await broken.GetAllResourcesAsync();
            Check("malformed catalog raises error", false, "no exception");
        }
        catch (CatalogUnavailableException)
        {
            Check("malformed catalog raises CatalogUnavailableException", true);
        }

        File.Delete(badPath);

        var remote = new RemoteCatalogProvider("http://127.0.0.1:9/unreachable");
        try
        {
            await remote.GetAllResourcesAsync();
            Check("unreachable server raises error", false, "no exception");
        }
        catch (CatalogUnavailableException ex)
        {
            Check("unreachable server raises CatalogUnavailableException", true);
            Check("error message is actionable",
                ex.Message.Contains("connect"), ex.Message);
        }
    }

    // ------------------------------------------------------------- Загрузка

    private static async Task TestDownloadService()
    {
        Section("Download");

        var folder = Path.Combine(AppContext.BaseDirectory, "TestOutput", "downloads");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);

        var service = new DownloadService(folder);
        var provider = new LocalCatalogProvider();
        var all = await provider.GetAllResourcesAsync();

        foreach (var resource in all)
        {
            try
            {
                var result = await service.DownloadResourceAsync(resource, overwrite: true);
                Check($"downloads {resource.Id}", File.Exists(result.FilePath));
            }
            catch (Exception ex)
            {
                Check($"downloads {resource.Id}", false, ex.Message);
            }
        }

        // Существующий файл не перезаписывается без явного разрешения
        var first = all.First();
        var again = await service.DownloadResourceAsync(first);
        Check("existing file is not overwritten by default", again.WasSkipped);

        // Ресурс без источника загрузки
        var noSource = new Resource
        {
            Id = "no-source", Slug = "no-source", Title = "No Source",
            FileName = "x.zip", DownloadUrl = null
        };

        try
        {
            await service.DownloadResourceAsync(noSource);
            Check("resource without source is rejected", false, "no exception");
        }
        catch (InvalidOperationException)
        {
            Check("resource without source is rejected", true);
        }

        // Отсутствующий исходный файл
        var ghost = new Resource
        {
            Id = "ghost", Slug = "ghost", Title = "Ghost",
            FileName = "ghost.zip", DownloadUrl = "Data/Assets/ghost.zip"
        };

        try
        {
            await service.DownloadResourceAsync(ghost);
            Check("missing source file is reported", false, "no exception");
        }
        catch (FileNotFoundException)
        {
            Check("missing source file is reported", true);
        }

        // Прогресс
        var reports = new List<int>();
        var progress = new Progress<DownloadProgress>(p => reports.Add(p.Percent));
        await service.DownloadResourceAsync(first, overwrite: true, progress: progress);
        await Task.Delay(150);
        Check("progress is reported", reports.Count > 0, $"reports = {reports.Count}");
        Check("progress reaches 100", reports.Count > 0 && reports.Max() == 100,
            string.Join(", ", reports));
    }

    /// <summary>
    /// Отмена и повторы сетевой загрузки. Сервер — крошечный TCP-слушатель на
    /// loopback: наружу тест не ходит, но HTTP-путь DownloadService проверяет честно.
    /// </summary>
    private static async Task TestDownloadCancellationAndRetry()
    {
        Section("Download cancellation & retry");

        var folder = Path.Combine(AppContext.BaseDirectory, "TestOutput", "cancel");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);

        // --- Отмена: сервер отдаёт большой файл медленно, обрываем на середине
        var body = new byte[4 * 1024 * 1024];
        new Random(42).NextBytes(body);

        using var slowServer = new LoopbackHttpServer(async stream =>
        {
            await WriteHeadAsync(stream, body.Length);
            // Отдаём по кусочку с паузой, чтобы отмена успела сработать mid-download
            for (var sent = 0; sent < body.Length; sent += 8192)
            {
                var chunk = Math.Min(8192, body.Length - sent);
                await stream.WriteAsync(body.AsMemory(sent, chunk));
                await Task.Delay(5);
            }
        });

        var service = new DownloadService(folder);
        var resource = new Resource
        {
            Id = "slow", Slug = "slow", Title = "Slow", FileName = "slow.bin",
            DownloadUrl = slowServer.Url
        };

        using var cts = new CancellationTokenSource();
        var destination = Path.Combine(folder, "slow.bin");

        cts.CancelAfter(300);
        var canceled = false;
        try
        {
            await service.DownloadResourceAsync(resource, overwrite: true, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Check("cancellation throws OperationCanceledException", canceled);
        Check("partial file removed after cancel", !File.Exists(destination));

        // --- Повтор: первый запрос рвётся сразу, второй отдаёт файл целиком
        var attempts = 0;
        using var flakyServer = new LoopbackHttpServer(async stream =>
        {
            attempts++;
            if (attempts == 1)
            {
                // Обрыв соединения без ответа — сетевой сбой, который надо повторить
                stream.Dispose();
                return;
            }

            var small = Encoding.UTF8.GetBytes("editor-helper-payload");
            await WriteHeadAsync(stream, small.Length);
            await stream.WriteAsync(small);
        });

        var flaky = new Resource
        {
            Id = "flaky", Slug = "flaky", Title = "Flaky", FileName = "flaky.bin",
            DownloadUrl = flakyServer.Url
        };

        var result = await service.DownloadResourceAsync(flaky, overwrite: true);
        Check("download retried after network failure", attempts >= 2, $"attempts = {attempts}");
        Check("retry produced the file", File.Exists(result.FilePath));

        static async Task WriteHeadAsync(System.Net.Sockets.NetworkStream stream, long contentLength)
        {
            var head = "HTTP/1.1 200 OK\r\nContent-Length: " + contentLength +
                       "\r\nContent-Type: application/octet-stream\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        }
    }

    private static async Task TestChecksumVerification()
    {
        Section("Checksum verification");

        var folder = Path.Combine(AppContext.BaseDirectory, "TestOutput", "checksums");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);

        var service = new DownloadService(folder);
        var provider = new LocalCatalogProvider();
        var all = await provider.GetAllResourcesAsync();

        // Все ресурсы демо-каталога имеют контрольные суммы и должны их пройти
        foreach (var resource in all)
        {
            var result = await service.DownloadResourceAsync(resource, overwrite: true);
            Check($"checksum verified for {resource.Id}",
                result.Checksum == ChecksumStatus.Verified,
                result.Checksum.ToString());
        }

        // Подменённый файл должен быть отвергнут и удалён
        var tampered = new Resource
        {
            Id = "tampered", Slug = "tampered", Title = "Tampered",
            FileName = "tampered.bin",
            DownloadUrl = "Data/Assets/auto-backup.jsx",
            Checksum = "sha256:" + new string('0', 64)
        };

        var tamperedPath = Path.Combine(folder, "tampered.bin");
        try
        {
            await service.DownloadResourceAsync(tampered, overwrite: true);
            Check("tampered file is rejected", false, "no exception");
        }
        catch (InvalidDataException)
        {
            Check("tampered file is rejected", true);
            Check("tampered file is deleted", !File.Exists(tamperedPath));
        }

        // Ресурс без суммы скачивается, но помечается как непроверенный
        var noSum = new Resource
        {
            Id = "no-sum", Slug = "no-sum", Title = "No Sum",
            FileName = "no-sum.bin",
            DownloadUrl = "Data/Assets/auto-backup.jsx",
            Checksum = null
        };

        var noSumResult = await service.DownloadResourceAsync(noSum, overwrite: true);
        Check("resource without checksum is accepted",
            noSumResult.Checksum == ChecksumStatus.NotProvided);

        // Неверный формат суммы не должен молча пропускать проверку
        var badFormat = new Resource
        {
            Id = "bad-format", Slug = "bad-format", Title = "Bad Format",
            FileName = "bad-format.bin",
            DownloadUrl = "Data/Assets/auto-backup.jsx",
            Checksum = "sha999:abc"
        };

        try
        {
            await service.DownloadResourceAsync(badFormat, overwrite: true);
            Check("unsupported checksum algorithm is rejected", false, "no exception");
        }
        catch (NotSupportedException)
        {
            Check("unsupported checksum algorithm is rejected", true);
        }
    }

    // ------------------------------------------------------------- Установка

    private static async Task TestInstallationRules()
    {
        Section("Installation rules");

        var provider = new LocalCatalogProvider();
        var all = await provider.GetAllResourcesAsync();
        var installer = new InstallationService();

        // Ресурс со своим установщиком не запускается автоматически
        var msi = all.First(r => r.InstallationType == InstallationType.Extension);
        var msiResult = await installer.InstallResourceAsync(msi, "C:\\fake\\file.msi");
        Check("installer resource is not run automatically",
            msiResult.RequiresManualSteps && msiResult.Success);
        Check("installer resource provides instructions",
            !string.IsNullOrWhiteSpace(msiResult.Instructions));

        // Ручная установка тоже даёт инструкции
        var manual = all.First(r => r.InstallationType == InstallationType.Manual);
        var manualResult = await installer.InstallResourceAsync(manual, "C:\\fake\\file.zip");
        Check("manual resource provides instructions",
            manualResult.RequiresManualSteps && manualResult.Success);
        Check("instructions mention the application folder",
            manualResult.Instructions!.Contains(msi.Application) ||
            manualResult.Instructions!.Contains("Adobe"));

        // Копирование в несуществующую папку не должно создавать её молча
        var copyResult = await installer.InstallToFolderAsync(
            all.First(), "C:\\fake\\file.jsx", "C:\\definitely-not-a-real-folder-xyz");

        Check("missing install folder is reported",
            !copyResult.Success && copyResult.Message.Contains("does not exist"),
            copyResult.Message);

        // Каждой программе сопоставлена папка установки
        foreach (var app in new[] { "After Effects", "Premiere Pro", "Photoshop" })
        {
            var folder = installer.GetSuggestedFolder(app);
            Check($"install folder known for {app}",
                !string.IsNullOrEmpty(folder) && folder.Contains("Adobe"),
                folder ?? "(null)");
        }

        Check("unknown application has no install folder",
            installer.GetSuggestedFolder("Blender") == null);

        // Инструкции существуют для всех демо-ресурсов
        foreach (var r in all)
        {
            var text = installer.GetManualInstructions(r);
            Check($"instructions exist for {r.Id}",
                !string.IsNullOrWhiteSpace(text) && text.Length > 30);
        }
    }

    // ------------------------------------------------------------- Формат

    private static void TestFormatting()
    {
        Section("Formatting");

        Format.Culture = new System.Globalization.CultureInfo("en-US");

        Check("date formatted in English",
            Format.Date(new DateTime(2024, 3, 15)) == "March 15, 2024",
            Format.Date(new DateTime(2024, 3, 15)));

        Check("unset date shows placeholder",
            Format.Date(default) == "—");

        Check("small size in bytes", Format.FileSize(512) == "512 B",
            Format.FileSize(512));

        Check("size in KB", Format.FileSize(2048) == "2 KB", Format.FileSize(2048));

        Check("size in MB", Format.FileSize(15 * 1024 * 1024) == "15 MB",
            Format.FileSize(15 * 1024 * 1024));

        Check("zero size shows placeholder", Format.FileSize(0) == "—");

        // Русская локаль должна давать русские названия месяцев
        Format.Culture = new System.Globalization.CultureInfo("ru-RU");
        var ru = Format.Date(new DateTime(2024, 3, 15));
        Check("date formatted in Russian when configured",
            ru.Contains("март"), ru);

        // Возвращаем английскую локаль по умолчанию
        Format.Culture = new System.Globalization.CultureInfo("en-US");
    }

    // ------------------------------------------------------------- Помощники

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {name}");
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"   ok   {name}");
        }
        else
        {
            _failed++;
            var line = detail == null ? name : $"{name} ({detail})";
            Failures.Add(line);
            Console.WriteLine($"   FAIL {line}");
        }
    }
}