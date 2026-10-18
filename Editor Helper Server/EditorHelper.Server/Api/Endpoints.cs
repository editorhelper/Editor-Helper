using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EditorHelper.Server.Sources;
using EditorHelper.Server.Storage;
using EditorHelper.Server.Telegram;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EditorHelper.Server.Api;

public static class Endpoints
{
    private static readonly string[] ResourceTypes =
    {
        "Plugin", "Script", "Preset", "Overlay", "Sound", "Texture", "Template", "Extension", "Other"
    };

    private static readonly string[] InstallationTypes =
    {
        "Manual", "CopyToFolder", "Installer", "Script", "Extension"
    };

    private static readonly string[] Applications = { "After Effects", "Premiere Pro", "Photoshop" };

    public static void MapPublicEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/catalog", async Task<IResult> (HttpContext context, CatalogRepository repository, ServerConfig config) =>
        {
            var catalog = await repository.LoadCatalogAsync();
            return Results.Json(BuildEnvelope(catalog, context, config));
        });

        api.MapGet("/resources", async Task<IResult> (
            HttpContext context, CatalogRepository repository, ServerConfig config) =>
        {
            var catalog = await repository.LoadCatalogAsync();
            var envelope = BuildEnvelope(catalog, context, config);

            // Фильтры читаем из строки запроса напрямую: это GET, а параметр-класс
            // minimal API по умолчанию пытался бы брать из тела, которого нет
            var q = context.Request.Query;

            var query = q["query"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(query))
            {
                envelope.Resources = envelope.Resources.Where(r =>
                    r.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    r.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    r.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    r.Application.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    r.Type.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    r.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
            }

            var application = q["application"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(application))
                envelope.Resources = envelope.Resources.Where(r =>
                    r.Application.Equals(application, StringComparison.OrdinalIgnoreCase)).ToList();

            var type = q["type"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(type))
                envelope.Resources = envelope.Resources.Where(r =>
                    r.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();

            if (q["featured"].FirstOrDefault() is { } featured && bool.TryParse(featured, out var featuredFlag) && featuredFlag)
                envelope.Resources = envelope.Resources.Where(r => r.IsFeatured).ToList();

            return Results.Json(envelope);
        });

        api.MapGet("/resources/{id}", async Task<IResult> (
            HttpContext context, CatalogRepository repository, ServerConfig config, string id) =>
        {
            var record = await repository.FindAsync(id);
            if (record == null || record.Status != "published")
                return Results.NotFound(new { message = "Ресурс не найден." });

            return Results.Json(PublicResource.FromRecord(record, BaseUrl(context, config)));
        });

        api.MapGet("/resources/{id}/download", DownloadAsync);

        api.MapGet("/resources/{id}/banner", async Task<IResult> (
            HttpContext context, CatalogRepository repository, FileSourceResolver resolver,
            ILoggerFactory loggerFactory, string id, CancellationToken ct) =>
        {
            var record = await repository.FindAsync(id);
            if (record?.Banner == null || record.Status != "published")
                return Results.NotFound();

            return await StreamSourceAsync(context, resolver, record.Banner,
                ContentTypeForImage(record.Banner.FileName), attachment: false, ct,
                loggerFactory.CreateLogger("Banner"));
        });

        api.MapGet("/health", (ServerConfig config) => Results.Json(new
        {
            ok = true,
            telegram = config.TelegramConfigured
        }));
    }

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin/api");
        admin.AddEndpointFilter((context, next) =>
            EditorHelper.Server.Auth.AuthEndpoints.RequireRoleAsync(context, next, null, EditorHelper.Server.Auth.UserRole.Owner));

        // -------------------------------------------------------- Карточки

        admin.MapGet("/resources", async (CatalogRepository repository) =>
        {
            var catalog = await repository.LoadCatalogAsync();
            return Results.Json(catalog.Resources
                .OrderByDescending(r => r.UpdatedAt)
                .Select(AdminView.Of));
        });

        admin.MapGet("/resources/{id}", async Task<IResult> (CatalogRepository repository, string id) =>
        {
            var record = await repository.FindAsync(id);
            return record == null
                ? Results.NotFound(new { message = "Карточка не найдена." })
                : Results.Json(AdminView.Of(record));
        });

        admin.MapPost("/resources", async Task<IResult> (
            AdminResourceInput input, CatalogRepository repository, FileSourceResolver resolver, CancellationToken ct) =>
        {
            var record = new ResourceRecord { Id = NewId(input.Slug, input.Title) };
            var error = await ApplyInputAsync(record, input, repository, resolver, ct);
            if (error != null) return Results.UnprocessableEntity(new { message = error });

            await repository.UpsertAsync(record);
            return Results.Json(AdminView.Of(record), statusCode: 201);
        });

        admin.MapPut("/resources/{id}", async Task<IResult> (
            string id, AdminResourceInput input, CatalogRepository repository, FileSourceResolver resolver, CancellationToken ct) =>
        {
            var record = await repository.FindAsync(id);
            if (record == null)
                return Results.NotFound(new { message = "Карточка не найдена." });

            var error = await ApplyInputAsync(record, input, repository, resolver, ct);
            if (error != null) return Results.UnprocessableEntity(new { message = error });

            await repository.UpsertAsync(record);
            return Results.Json(AdminView.Of(record));
        });

        // Дубликат — новый черновик на основе существующей карточки: тот же
        // контент и источник, но другой id/slug и статус draft.
        admin.MapPost("/resources/{id}/duplicate", async Task<IResult> (
            string id, CatalogRepository repository) =>
        {
            var source = await repository.FindAsync(id);
            if (source == null) return Results.NotFound(new { message = "Карточка не найдена." });

            var copy = new ResourceRecord
            {
                Id = NewId(null, source.Title),
                Title = source.Title + " (копия)",
                Description = source.Description,
                LongDescription = source.LongDescription,
                Type = source.Type,
                Application = source.Application,
                Category = source.Category,
                Version = source.Version,
                Author = source.Author,
                Tags = source.Tags.ToList(),
                SupportedVersions = source.SupportedVersions.ToList(),
                InstallationType = source.InstallationType,
                FileName = source.FileName,
                FileSize = source.FileSize,
                Checksum = source.Checksum,
                IsFree = source.IsFree,
                DocumentationUrl = source.DocumentationUrl,
                Links = source.Links.Select(l => new ResourceLink { Label = l.Label, Url = l.Url }).ToList(),
                Note = source.Note,
                Status = "draft",
                Source = source.Source
            };
            copy.Slug = Slugify($"{source.Slug}-copy-{Guid.NewGuid():N}"[..12]);
            // Баннер не копируем: файл привязан к id исходной карточки,
            // а переиспользовать чужой путь — значит связать две карточки.

            await repository.UpsertAsync(copy);
            return Results.Json(AdminView.Of(copy), statusCode: 201);
        });

        admin.MapDelete("/resources/{id}", async Task<IResult> (CatalogRepository repository, string id) =>
        {
            var removed = await repository.DeleteAsync(id);
            return removed
                ? Results.Ok(new { message = "Карточка удалена." })
                : Results.NotFound(new { message = "Карточка не найдена." });
        });

        admin.MapPost("/resources/{id}/publish", async Task<IResult> (
            string id, CatalogRepository repository, FileSourceResolver resolver, CancellationToken ct) =>
        {
            var record = await repository.FindAsync(id);
            if (record == null) return Results.NotFound(new { message = "Карточка не найдена." });

            // Публикуем только то, что реально можно отдать: иначе в каталоге
            // появится карточка с нерабочей кнопкой скачивания
            var error = await TryPublishAsync(record, resolver, ct);
            if (error != null)
                return Results.Json(new { message = error }, statusCode: error.StartsWith("Файл недоступен") ? 409 : 422);

            await repository.UpsertAsync(record);
            return Results.Json(AdminView.Of(record));
        });

        admin.MapPost("/resources/{id}/unpublish", async Task<IResult> (
            string id, CatalogRepository repository) =>
        {
            var record = await repository.FindAsync(id);
            if (record == null) return Results.NotFound(new { message = "Карточка не найдена." });

            record.Status = "draft";
            await repository.UpsertAsync(record);
            return Results.Json(AdminView.Of(record));
        });

        admin.MapPost("/resources/{id}/hide", async Task<IResult> (
            string id, CatalogRepository repository) =>
        {
            var record = await repository.FindAsync(id);
            if (record == null) return Results.NotFound(new { message = "Карточка не найдена." });

            record.Status = "hidden";
            await repository.UpsertAsync(record);
            return Results.Json(AdminView.Of(record));
        });

        // Обложка для админки: публичный маршрут отдаёт баннер только у
        // опубликованных карточек, а форму надо показывать и для черновика.
        admin.MapGet("/resources/{id}/banner", async Task<IResult> (
            HttpContext context, string id, CatalogRepository repository, FileSourceResolver resolver,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var record = await repository.FindAsync(id);
            if (record?.Banner == null) return Results.NotFound();

            return await StreamSourceAsync(context, resolver, record.Banner,
                ContentTypeForImage(record.Banner.FileName), attachment: false, ct,
                loggerFactory.CreateLogger("AdminBanner"));
        });

        // Формы защищены заголовком X-Admin-Token: с чужего сайта браузер его
        // не подставит (нужен CORS preflight), поэтому antiforgery здесь лишний
        admin.MapPost("/resources/{id}/banner", async Task<IResult> (
            string id, IFormFile file, CatalogRepository repository, FileSourceResolver resolver) =>
        {
            var record = await repository.FindAsync(id);
            if (record == null) return Results.NotFound(new { message = "Карточка не найдена." });

            if (file.Length == 0)
                return Results.UnprocessableEntity(new { message = "Пустой файл баннера." });
            if (file.Length > UploadLimits.BannerBytes)
                return Results.UnprocessableEntity(new { message = "Баннер больше 5 МБ — это много для обложки." });

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
                return Results.UnprocessableEntity(new { message = "Баннер: только png, jpg или webp." });

            var relative = Path.Combine("banners", $"{record.Id}{extension}").Replace('\\', '/');
            var absolute = Path.Combine(resolver.LocalFilesRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

            await using (var target = File.Create(absolute))
                await file.CopyToAsync(target);

            record.Banner = new FileSourceRef
            {
                Kind = "local",
                LocalPath = relative,
                FileName = Path.GetFileName(file.FileName),
                SizeBytes = file.Length
            };

            await repository.UpsertAsync(record);
            return Results.Json(new { message = "Баннер сохранён.", banner = AdminView.Of(record).Banner });
        }).DisableAntiforgery();

        // ------------------------------------------------------- Telegram

        admin.MapGet("/telegram/status", async Task<IResult> (
            ServerConfig config, FileSourceResolver resolver) =>
        {
            if (!config.TelegramConfigured || resolver.Telegram == null)
            {
                return Results.Json(new
                {
                    enabled = false,
                    tokenSet = !string.IsNullOrWhiteSpace(config.TelegramToken),
                    channelSet = !string.IsNullOrWhiteSpace(config.TelegramChannel),
                    message = "Задайте TELEGRAM_BOT_TOKEN и TELEGRAM_CHANNEL_ID, чтобы включить интеграцию."
                });
            }

            try
            {
                var me = await resolver.Telegram.GetMeAsync();
                var chat = await resolver.Telegram.GetChatAsync(config.TelegramChannel!);
                var member = await resolver.Telegram.GetChatMemberAsync(config.TelegramChannel!, me.Id);

                return Results.Json(new
                {
                    enabled = true,
                    polling = config.UsePolling,
                    botUsername = me.Username,
                    channelTitle = chat.Title,
                    channelUsername = chat.Username,
                    botIsAdmin = member?.IsAdmin ?? false,
                    message = member is { IsAdmin: true }
                        ? "Бот на связи и видит посты канала."
                        : "Бот не администратор канала — посты не видны. Добавьте его в администраторы."
                });
            }
            catch (Exception ex) when (ex is TelegramException or HttpRequestException)
            {
                return Results.Json(new { enabled = false, message = $"Telegram недоступен: {ex.Message}" });
            }
        });

        admin.MapGet("/telegram/documents", async (CatalogRepository repository) =>
        {
            var data = await repository.LoadDiscoveredAsync();
            return Results.Json(data.Documents
                .OrderByDescending(d => d.SeenAt)
                .Select(d => new
                {
                    d.FileId,
                    d.FileUniqueId,
                    d.FileName,
                    d.SizeBytes,
                    d.MessageId,
                    d.MessageLink,
                    d.SeenAt,
                    d.BoundResourceId,
                    withinBotLimit = d.SizeBytes <= TelegramClient.MaxBotDownloadBytes
                }));
        });

        admin.MapPost("/telegram/check-link", async Task<IResult> (
            TelegramLinkInput input, CatalogRepository repository) =>
        {
            var parsed = ParseTelegramLink(input.Url);
            if (parsed == null)
            {
                return Results.UnprocessableEntity(new
                {
                    message = "Ссылка не похожа на пост Telegram. Жду формат https://t.me/<канал>/<id>."
                });
            }

            var data = await repository.LoadDiscoveredAsync();
            var document = data.Documents.FirstOrDefault(d =>
                d.MessageId == parsed.Value.MessageId &&
                (parsed.Value.ChannelName == null || d.MessageLink?.Contains(
                    "/" + parsed.Value.ChannelName + "/", StringComparison.OrdinalIgnoreCase) == true));

            if (document == null)
            {
                return Results.NotFound(new
                {
                    message = "Бот не видел этот пост. Bot API не умеет читать старые сообщения по ссылке — " +
                              "бот замечает только то, что отправлено в канал, пока он слушает. " +
                              "Перешлите архив в канал ещё раз (или отправьте через загрузку в Admin), " +
                              "и документ появится в списке обнаруженных."
                });
            }

            return Results.Json(new
            {
                document.FileId,
                document.FileName,
                document.SizeBytes,
                document.MessageId,
                document.MessageLink,
                withinBotLimit = document.SizeBytes <= TelegramClient.MaxBotDownloadBytes
            });
        });

        admin.MapPost("/telegram/verify-file", async Task<IResult> (
            VerifyFileInput input, FileSourceResolver resolver, CancellationToken ct) =>
        {
            if (resolver.Telegram == null)
                return Results.Json(new { available = false, message = "Telegram не настроен." });

            if (string.IsNullOrWhiteSpace(input.FileId))
                return Results.UnprocessableEntity(new { message = "Не указан file_id." });

            var source = new FileSourceRef
            {
                Kind = "telegram",
                TelegramFileId = input.FileId,
                FileName = input.FileName
            };

            var probe = await new TelegramFileSource(resolver.Telegram).ProbeAsync(source, ct);
            return Results.Json(new
            {
                available = probe.Available,
                fileName = probe.FileName ?? input.FileName,
                sizeBytes = probe.SizeBytes,
                message = probe.Error
            });
        });

        admin.MapPost("/telegram/upload", async Task<IResult> (
            IFormFile file, CatalogRepository repository, FileSourceResolver resolver,
            ServerConfig config, CancellationToken ct) =>
        {
            if (resolver.Telegram == null)
                return Results.Json(new { message = "Telegram не настроен на сервере." }, statusCode: 409);

            if (file.Length == 0)
                return Results.UnprocessableEntity(new { message = "Пустой файл." });

            if (file.Length > TelegramClient.MaxBotUploadBytes)
            {
                return Results.UnprocessableEntity(new
                {
                    message = $"Файл {file.Length / 1024 / 1024} МБ больше лимита загрузки ботом (50 МБ). " +
                              "Для крупных архивов используйте внешнюю ссылку."
                });
            }

            var fileName = Path.GetFileName(file.FileName);
            await using var stream = file.OpenReadStream();

            try
            {
                var message = await resolver.Telegram.SendDocumentAsync(
                    config.TelegramChannel!, fileName, stream, null, ct);

                var document = message.Document;
                if (document == null)
                    return Results.Json(new { message = "Telegram принял файл, но не вернул документ." }, statusCode: 502);

                // Регистрируем сразу, не дожидаясь поллинга: апдейт своего же
                // сообщения может прийти позже, а Admin должен показать файл сейчас
                await repository.UpdateDiscoveredAsync<object?>(data =>
                {
                    if (data.Documents.Any(d => d.FileUniqueId == document.FileUniqueId)) return null;

                    data.Documents.Insert(0, new DiscoveredDocument
                    {
                        FileId = document.FileId,
                        FileUniqueId = document.FileUniqueId,
                        FileName = document.FileName ?? fileName,
                        SizeBytes = document.FileSize ?? file.Length,
                        MessageId = message.MessageId,
                        ChatId = message.Chat?.Id.ToString() ?? config.TelegramChannel!,
                        MessageLink = message.Chat?.Username != null
                            ? $"https://t.me/{message.Chat.Username}/{message.MessageId}"
                            : null,
                        SeenAt = DateTime.UtcNow
                    });
                    return null;
                });

                return Results.Json(new
                {
                    fileId = document.FileId,
                    fileUniqueId = document.FileUniqueId,
                    fileName = document.FileName ?? fileName,
                    sizeBytes = document.FileSize ?? file.Length,
                    messageId = message.MessageId,
                    message = "Файл отправлен в канал и добавлен в список обнаруженных."
                });
            }
            catch (TelegramException ex)
            {
                return Results.Json(new { message = $"Telegram отклонил загрузку: {ex.Message}" }, statusCode: 502);
            }
        }).DisableAntiforgery();

        // -------------------------------------------------- Внешние ссылки

        admin.MapPost("/files/check-external", async Task<IResult> (
            ExternalUrlInput input, FileSourceResolver resolver, CancellationToken ct) =>
        {
            var http = (HttpFileSource)resolver.Resolve(new FileSourceRef { Kind = "http" })!;

            var source = new FileSourceRef { Kind = "http", ExternalUrl = input.Url };
            var probe = await http.ProbeAsync(source, ct);

            return Results.Json(new
            {
                available = probe.Available,
                fileName = probe.FileName,
                sizeBytes = probe.SizeBytes,
                message = probe.Error
            });
        });

        admin.MapPost("/files/upload", async Task<IResult> (
            IFormFile file, FileSourceResolver resolver, CancellationToken ct) =>
        {
            const long maxUploadBytes = UploadLimits.FileBytes;
            if (file.Length == 0)
                return Results.UnprocessableEntity(new { message = "Пустой файл." });
            if (file.Length > maxUploadBytes)
                return Results.Json(new { message = "Файл превышает локальный лимит загрузки 100 МБ." }, statusCode: 413);

            var fileName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return Results.UnprocessableEntity(new { message = "Недопустимое имя файла." });
            var relative = Path.Combine("uploads", $"{Guid.NewGuid():N}-{fileName}").Replace('\\', '/');
            var absolute = Path.Combine(resolver.LocalFilesRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

            await using var source = file.OpenReadStream();
            await using var target = new FileStream(absolute, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var sha = await SaveAndHashAsync(source, target, maxUploadBytes, ct);

            return Results.Json(new
            {
                kind = "local",
                localPath = relative,
                fileName,
                sizeBytes = file.Length,
                sha256 = sha
            });
        }).DisableAntiforgery();

        admin.MapPost("/sources/hash", async Task<IResult> (
            FileSourceRef source, FileSourceResolver resolver, CancellationToken ct) =>
        {
            var unsupported = resolver.DescribeUnsupported(source);
            if (unsupported != null)
                return Results.Json(new { message = unsupported }, statusCode: 409);

            var fileSource = resolver.Resolve(source)!;
            try
            {
                await using var stream = await fileSource.OpenReadAsync(source, ct);
                var sha = await SHA256.HashDataAsync(stream.Stream, ct);

                return Results.Json(new
                {
                    sha256 = Convert.ToHexString(sha).ToLowerInvariant(),
                    sizeBytes = stream.Length ?? -1
                });
            }
            catch (Exception ex) when (ex is TelegramException or HttpRequestException or FileNotFoundException or IOException)
            {
                return Results.Json(new { message = $"Не удалось прочитать файл: {ex.Message}" }, statusCode: 409);
            }
        });
    }

    // ------------------------------------------------------------ Отдача файла

    private static async Task<IResult> DownloadAsync(
        HttpContext context, CatalogRepository repository, FileSourceResolver resolver,
        ILogger<Program> logger, string id, CancellationToken ct)
    {
        var record = await repository.FindAsync(id);
        if (record == null || record.Status != "published")
            return Results.NotFound(new { message = "Ресурс не найден." });

        var unsupported = resolver.DescribeUnsupported(record.Source);
        if (unsupported != null)
            return Results.Json(new { message = unsupported }, statusCode: 503);

        var source = resolver.Resolve(record.Source)!;
        var fileName = record.FileName ?? record.Source.FileName ?? $"{record.Slug}.zip";

        return await StreamSourceAsync(context, resolver, record.Source,
            "application/octet-stream", attachment: true, ct, logger, fileName);
    }

    private static async Task<IResult> StreamSourceAsync(
        HttpContext context, FileSourceResolver resolver, FileSourceRef sourceRef,
        string contentType, bool attachment, CancellationToken ct, ILogger logger,
        string? fileName = null)
    {
        var source = resolver.Resolve(sourceRef);
        if (source == null) return Results.NotFound();

        try
        {
            await using var opened = await source.OpenReadAsync(sourceRef, ct);
            var response = context.Response;

            response.ContentType = contentType;
            if (opened.Length is > 0) response.ContentLength = opened.Length;

            var name = fileName ?? sourceRef.FileName;
            if (attachment && !string.IsNullOrEmpty(name))
            {
                response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileNameStar = name
                }.ToString();
            }

            await opened.Stream.CopyToAsync(response.Body, ct);
            return Results.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Клиент отменил загрузку или оборвал соединение — это не ошибка сервера
            logger.LogDebug("Отдача файла отменена клиентом.");
            return Results.Empty;
        }
        catch (TelegramException ex)
        {
            logger.LogWarning("Telegram не отдал файл: {Message}", ex.Message);
            return Results.Json(new { message = $"Хранилище недоступно: {ex.Message}" }, statusCode: 502);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Внешний источник недоступен: {Message}", ex.Message);
            return Results.Json(new { message = $"Источник файла недоступен: {ex.Message}" }, statusCode: 502);
        }
        catch (FileNotFoundException)
        {
            return Results.Json(new { message = "Файл источника не найден на сервере." }, statusCode: 404);
        }
    }

    private static CatalogEnvelope BuildEnvelope(
        CatalogData catalog, HttpContext context, ServerConfig config)
    {
        var baseUrl = BaseUrl(context, config);

        return new CatalogEnvelope
        {
            Version = catalog.Version,
            LastUpdated = catalog.LastUpdated,
            Resources = catalog.Resources
                .Where(r => r.Status == "published")
                .Select(r => PublicResource.FromRecord(r, baseUrl))
                .ToList()
        };
    }

    private static string BaseUrl(HttpContext context, ServerConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.PublicBaseUrl))
            return config.PublicBaseUrl.TrimEnd('/');

        return $"{context.Request.Scheme}://{context.Request.Host}";
    }

    private static string ContentTypeForImage(string? fileName) =>
        Path.GetExtension(fileName)?.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

    // ------------------------------------------------------------ Валидация карточек

    private static async Task<string?> ApplyInputAsync(
        ResourceRecord record, AdminResourceInput input,
        CatalogRepository repository, FileSourceResolver resolver, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Title)) return "Название обязательно.";

        if (!ResourceTypes.Contains(input.Type, StringComparer.OrdinalIgnoreCase))
            return $"Неизвестный тип \"{input.Type}\". Допустимые: {string.Join(", ", ResourceTypes)}.";

        if (!InstallationTypes.Contains(input.InstallationType, StringComparer.OrdinalIgnoreCase))
            return $"Неизвестный способ установки \"{input.InstallationType}\". " +
                   $"Допустимые: {string.Join(", ", InstallationTypes)}.";

        if (!Applications.Contains(input.Application, StringComparer.OrdinalIgnoreCase))
            return $"Неизвестная программа \"{input.Application}\". Допустимые: {string.Join(", ", Applications)}.";

        var documentation = string.IsNullOrWhiteSpace(input.DocumentationUrl) ? null : input.DocumentationUrl.Trim();
        if (documentation != null)
        {
            var docError = SsrfGuard.ValidateUrl(documentation);
            if (docError != null) return $"Ссылка на документацию: {docError}";
        }

        var links = NormalizeLinks(input.Links, out var linksError);
        if (linksError != null) return linksError;

        var slug = string.IsNullOrWhiteSpace(input.Slug) ? Slugify(input.Title) : Slugify(input.Slug);
        if (string.IsNullOrEmpty(slug)) return "Не удалось построить slug из названия.";

        var catalog = await repository.LoadCatalogAsync();
        if (catalog.Resources.Any(r =>
                r.Id != record.Id && string.Equals(r.Slug, slug, StringComparison.OrdinalIgnoreCase)))
            return $"Slug \"{slug}\" уже занят другой карточкой.";

        record.Slug = slug;
        record.Title = input.Title.Trim();
        record.Description = input.Description?.Trim() ?? "";
        record.LongDescription = string.IsNullOrWhiteSpace(input.LongDescription) ? null : input.LongDescription.Trim();
        record.Type = Capitalize(ResourceTypes, input.Type);
        record.Application = Applications.First(a => a.Equals(input.Application, StringComparison.OrdinalIgnoreCase));
        record.Category = input.Category?.Trim() ?? "";
        record.Version = string.IsNullOrWhiteSpace(input.Version) ? "1.0.0" : input.Version.Trim();
        record.Author = string.IsNullOrWhiteSpace(input.Author) ? null : input.Author.Trim();
        record.Tags = input.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList() ?? new();
        record.SupportedVersions = input.SupportedVersions?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList() ?? new();
        record.InstallationType = Capitalize(InstallationTypes, input.InstallationType);
        record.IsFeatured = input.IsFeatured;
        record.IsFree = input.IsFree;
        record.FileName = string.IsNullOrWhiteSpace(input.FileName) ? null : input.FileName.Trim();
        record.Checksum = string.IsNullOrWhiteSpace(input.Checksum) ? null : input.Checksum.Trim();
        record.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        record.DocumentationUrl = documentation;
        record.Links = links;

        if (input.Source != null)
        {
            var error = await ValidateSourceAsync(input.Source, resolver);
            if (error != null) return error;
            record.Source = input.Source;
        }

        if (input.FileSize is > 0) record.FileSize = input.FileSize.Value;
        else if (record.Source.SizeBytes > 0) record.FileSize = record.Source.SizeBytes;

        // Статус из формы: draft и hidden сохраняются сразу, а published проходит
        // ту же проверку файла, что и отдельная кнопка публикации — иначе в
        // каталоге окажется карточка с нерабочей кнопкой скачивания.
        if (!string.IsNullOrWhiteSpace(input.Status))
        {
            var status = input.Status.Trim().ToLowerInvariant();
            if (status is not ("draft" or "published" or "hidden"))
                return $"Неизвестный статус \"{input.Status}\". Допустимые: draft, published, hidden.";

            if (status == "published")
            {
                var publishError = await TryPublishAsync(record, resolver, ct);
                if (publishError != null) return publishError;
            }
            else
            {
                record.Status = status;
            }
        }

        await MarkDocumentBoundAsync(repository, record);
        return null;
    }

    /// <summary>
    /// Переводит карточку в published, если файл реально можно отдать.
    /// Возвращает причину отказа или null при успехе.
    /// </summary>
    private static async Task<string?> TryPublishAsync(ResourceRecord record, FileSourceResolver resolver, CancellationToken ct)
    {
        var unsupported = resolver.DescribeUnsupported(record.Source);
        if (unsupported != null) return unsupported;

        var source = resolver.Resolve(record.Source);
        if (source == null) return "Источник файла не поддерживается.";

        var probe = await source.ProbeAsync(record.Source, ct);
        if (!probe.Available) return $"Файл недоступен: {probe.Error}";

        record.Status = "published";
        if (record.PublishedAt == default) record.PublishedAt = DateTime.UtcNow;
        record.Source.SizeBytes = probe.SizeBytes;
        if (string.IsNullOrEmpty(record.FileName)) record.FileName = probe.FileName;
        if (record.FileSize <= 0) record.FileSize = probe.SizeBytes;
        return null;
    }

    /// <summary>
    /// Приводит список ссылок карточки к безопасному виду: непустые подписи
    /// и URL, только http(s), не больше восьми штук.
    /// </summary>
    private static List<ResourceLink> NormalizeLinks(List<ResourceLink>? input, out string? error)
    {
        error = null;
        var result = new List<ResourceLink>();
        if (input == null) return result;

        if (input.Count > 8)
        {
            error = "Слишком много дополнительных ссылок — максимум 8.";
            return result;
        }

        foreach (var link in input)
        {
            if (link == null) continue;
            var url = link.Url?.Trim() ?? "";
            var label = link.Label?.Trim() ?? "";

            // Полностью пустая строка — пользователь не заполнил поле, пропускаем
            if (url.Length == 0 && label.Length == 0) continue;

            if (url.Length == 0 || label.Length == 0)
            {
                error = "У дополнительной ссылки нужны и название, и адрес.";
                return result;
            }

            var urlError = SsrfGuard.ValidateUrl(url);
            if (urlError != null)
            {
                error = $"Ссылка «{label}»: {urlError}";
                return result;
            }

            result.Add(new ResourceLink
            {
                Label = new string(label.Where(c => !char.IsControl(c)).Take(60).ToArray()),
                Url = url
            });
        }

        return result;
    }

    /// <summary>
    /// Отмечает в журнале обнаруженных документов, к какой карточке привязан
    /// файл: список в Admin показывает занятые архивы, а при смене источника
    /// старая привязка снимается.
    /// </summary>
    private static async Task MarkDocumentBoundAsync(CatalogRepository repository, ResourceRecord record)
    {
        var boundFileId = record.Source.Kind == "telegram" ? record.Source.TelegramFileId : null;

        await repository.UpdateDiscoveredAsync<object?>(data =>
        {
            foreach (var document in data.Documents)
            {
                if (document.FileId == boundFileId)
                    document.BoundResourceId = record.Id;
                else if (document.BoundResourceId == record.Id)
                    document.BoundResourceId = null;
            }
            return null;
        });
    }

    private static async Task<string?> ValidateSourceAsync(FileSourceRef source, FileSourceResolver resolver)
    {
        switch (source.Kind.ToLowerInvariant())
        {
            case "telegram":
                if (string.IsNullOrWhiteSpace(source.TelegramFileId))
                    return "Для источника Telegram нужен file_id — выберите файл из списка обнаруженных.";
                break;

            case "http":
                var error = resolver.AllowLocalExternalUrls
                    ? SsrfGuard.ValidateUrlSyntaxOnly(source.ExternalUrl)
                    : await SsrfGuard.ValidateResolvedAsync(source.ExternalUrl ?? "", CancellationToken.None);
                if (error != null) return error;
                break;

            case "local":
                if (string.IsNullOrWhiteSpace(source.LocalPath))
                    return "Для локального источника нужен путь к файлу.";
                break;

            default:
                return $"Неизвестный источник \"{source.Kind}\". Допустимые: telegram, http, local.";
        }

        source.Kind = source.Kind.ToLowerInvariant();
        return null;
    }

    private static string Capitalize(string[] allowed, string value) =>
        allowed.First(a => a.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static string Slugify(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasDash = true;

        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
            {
                builder.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string NewId(string? slug, string title)
    {
        var basis = string.IsNullOrWhiteSpace(slug) ? Slugify(title) : Slugify(slug);
        if (string.IsNullOrEmpty(basis)) basis = "resource";
        return $"{basis}-{Guid.NewGuid():N}"[..Math.Min(basis.Length + 9, 48)];
    }

    private static async Task<string> SaveAndHashAsync(Stream source, FileStream target, long maxBytes = long.MaxValue, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        await using var cryptoStream = new CryptoStream(target, sha, CryptoStreamMode.Write, leaveOpen: true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new InvalidDataException("Upload exceeds the configured limit.");
            await cryptoStream.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await cryptoStream.FlushFinalBlockAsync(ct);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// Разбор ссылки на пост: https://t.me/<канал>/<id> или https://t.me/c/<внутренний>/<id>.
    /// Возвращает id сообщения и имя канала (для приватных каналов — null).
    /// </summary>
    public static (long MessageId, string? ChannelName)? ParseTelegramLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.EndsWith("t.me", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // t.me/c/123456/789 — приватный канал, t.me/<name>/789 — публичный
        if (parts.Length == 3 && parts[0].Equals("c", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(parts[2], out var privateId))
            return (privateId, null);

        if (parts.Length == 2 && long.TryParse(parts[1], out var publicId))
            return (publicId, parts[0]);

        return null;
    }
}

// ---------------------------------------------------------------- Входные модели

public class AdminResourceInput
{
    public string? Slug { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? LongDescription { get; set; }
    public string Type { get; set; } = "Other";
    public string Application { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? SupportedVersions { get; set; }
    public string InstallationType { get; set; } = "Manual";
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public string? Checksum { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsFree { get; set; } = true;
    public FileSourceRef? Source { get; set; }
    public string? Note { get; set; }
    public string? DocumentationUrl { get; set; }
    public List<ResourceLink>? Links { get; set; }
    /// <summary>Явная установка статуса при сохранении: draft, published, hidden.</summary>
    public string? Status { get; set; }
}

public class TelegramLinkInput
{
    public string? Url { get; set; }
}

public class VerifyFileInput
{
    public string? FileId { get; set; }
    public string? FileName { get; set; }
}

public class ExternalUrlInput
{
    public string? Url { get; set; }
}

/// <summary>Карточка для админки: всё, что нужно форме, включая черновики и источник.</summary>
public class AdminView
{
    public string Id { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? LongDescription { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> SupportedVersions { get; set; } = new();
    public string InstallationType { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public string? Checksum { get; set; }
    public bool IsFeatured { get; set; }
    public bool IsFree { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string? DocumentationUrl { get; set; }
    public List<ResourceLink> Links { get; set; } = new();
    public DateTime PublishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public FileSourceRef Source { get; set; } = new();
    public FileSourceRef? Banner { get; set; }

    public static AdminView Of(ResourceRecord r) => new()
    {
        Id = r.Id,
        Slug = r.Slug,
        Title = r.Title,
        Description = r.Description,
        LongDescription = r.LongDescription,
        Type = r.Type,
        Application = r.Application,
        Category = r.Category,
        Version = r.Version,
        Author = r.Author,
        Tags = r.Tags,
        SupportedVersions = r.SupportedVersions,
        InstallationType = r.InstallationType,
        FileName = r.FileName ?? r.Source.FileName,
        FileSize = r.FileSize > 0 ? r.FileSize : r.Source.SizeBytes,
        Checksum = r.Checksum,
        IsFeatured = r.IsFeatured,
        IsFree = r.IsFree,
        Status = r.Status,
        Note = r.Note,
        DocumentationUrl = r.DocumentationUrl,
        Links = r.Links,
        PublishedAt = r.PublishedAt,
        UpdatedAt = r.UpdatedAt,
        Source = r.Source,
        Banner = r.Banner
    };
}
