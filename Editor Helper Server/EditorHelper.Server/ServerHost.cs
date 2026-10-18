using EditorHelper.Server.Api;
using EditorHelper.Server.Auth;
using EditorHelper.Server.Sources;
using EditorHelper.Server.Storage;
using EditorHelper.Server.Telegram;

namespace EditorHelper.Server;

/// <summary>
/// Сборка веб-хоста вынесена из Program, чтобы тесты могли поднять такой же
/// сервер в своём процессе и со своей конфигурацией.
/// </summary>
public static class ServerHost
{
    public static WebApplication Create(
        string[] args,
        ServerConfig? config = null,
        Action<ILoggingBuilder>? logging = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        logging?.Invoke(builder.Logging);

        config ??= ServerConfig.Load(builder.Environment.ContentRootPath);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<RateLimiter>();
        builder.Services.AddSingleton<IdentityStore>();
        builder.Services.AddSingleton(_ => new TelegramClient(config.TelegramToken ?? "", apiBase: config.TelegramApiBase));
        builder.Services.AddSingleton<CatalogRepository>(_ => new CatalogRepository(config.DataDir));
        builder.Services.AddSingleton<FileSourceResolver>();
        builder.Services.AddHostedService<TelegramBotService>();

        builder.WebHost.UseUrls($"http://{config.ListenAddress}:{config.Port}");

        // Потолок размера тела запроса: совпадает с лимитом самой крупной
        // загрузки, иначе транспорт отбивал бы её с 413 раньше, чем до неё
        // дошла бы внятная проверка в обработчике.
        //
        // Это именно потолок. Точные пределы задаются у каждого эндпоинта через
        // RequestSizeLimit (см. Endpoints), поэтому JSON-формы не принимают
        // тела размером с архив.
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = UploadLimits.MaxRequestBodyBytes);

        var app = builder.Build();

        if (config.EnableRateLimiting) app.UseRateLimiting();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (config.OwnerTelegramId is > 0)
            {
                _ = Task.Run(async () =>
                {
                    try { await app.Services.GetRequiredService<IdentityStore>().EnsureConfiguredOwnerAsync(config.OwnerTelegramId.Value); }
                    catch (Exception ex) { app.Logger.LogError("Could not initialize configured Owner: {Message}", ex.Message); }
                });
            }
        });

        app.MapAuthEndpoints();
        app.MapPublicEndpoints();
        app.MapAdminEndpoints();

        // Админка — статическая страница из wwwroot, доступна по /admin
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            var fileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(wwwroot);

            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = fileProvider,
                RequestPath = "/admin"
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                RequestPath = "/admin"
            });
        }

        return app;
    }
}
