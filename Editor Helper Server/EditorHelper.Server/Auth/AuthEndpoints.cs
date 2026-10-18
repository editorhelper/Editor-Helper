using System.Text;
using EditorHelper.Server.Api;
using EditorHelper.Server.Telegram;

namespace EditorHelper.Server.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapPost("/dev/bootstrap-owner", async (DevelopmentBootstrapInput input, HttpContext context, IdentityStore store, ServerConfig config, IWebHostEnvironment environment, CancellationToken ct) =>
        {
            if (!environment.IsDevelopment() || !config.EnableDevelopmentLogin || !IsLoopback(context.Connection.RemoteIpAddress))
                return Results.NotFound();
            if (!LegacyAdminAllowed(context, config, environment)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(input.DisplayName)) return Results.UnprocessableEntity(new { message = "Укажите имя владельца." });
            var result = await store.BootstrapOwnerAsync(input.DisplayName, config.OwnerTelegramId, ct);
            return result.Created ? Results.Json(new { userId = result.UserId, message = "Owner создан. Удалите legacy-токен и сохраните его только в серверных секретах." }, statusCode: 201) : Results.Conflict(new { message = "Owner уже настроен." });
        });
        auth.MapPost("/telegram/start", async (IdentityStore store, ServerConfig config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(config.TelegramToken)) return Results.Json(new { message = "Telegram bot is not configured." }, statusCode: 503);
            var challenge = await store.StartTelegramAuthAsync(TimeSpan.FromMinutes(5), ct);
            return Results.Json(new { challenge.RequestId, challenge.ExpiresAt, botUsername = config.TelegramBotUsername });
        });
        auth.MapGet("/telegram/status/{requestId}", async (string requestId, IdentityStore store, CancellationToken ct) =>
        {
            var challenge = await store.GetTelegramChallengeAsync(requestId, ct);
            return challenge == null
                ? Results.Json(new { status = "expired" })
                : Results.Json(new
                {
                    status = challenge.Declined ? "declined"
                        : challenge.TelegramId != 0 && await store.IsTelegramChallengeConfirmedAsync(requestId, ct) ? "confirmed"
                        : "waiting",
                    expiresAt = challenge.ExpiresAt
                });
        });
        auth.MapPost("/telegram/complete", async (TelegramLoginComplete input, IdentityStore store, ServerConfig config, TelegramClient telegram, CancellationToken ct) =>
        {
            var challenge = await store.GetConfirmedTelegramChallengeAsync(input.RequestId, ct);
            if (challenge == null) return Results.Json(new { message = "Подтверждение не получено, истекло или уже использовано." }, statusCode: 401);
            if (!string.IsNullOrWhiteSpace(config.TelegramChannel))
            {
                TgChatMember membership;
                try { membership = await telegram.GetChatMemberStrictAsync(config.TelegramChannel, challenge.TelegramId, ct); }
                catch (TelegramException) { return Results.Json(new { message = "Не удалось проверить подписку на канал. Повторите попытку позже." }, statusCode: 503); }
                if (!membership.HasActiveMembership) return Results.Json(new { message = "Для входа подпишитесь на канал и повторите проверку." }, statusCode: 403);
            }

            // Идентификатор устройства — криптографический секрет клиента.
            // В базу попадает только его SHA-256. Пустой deviceId означает
            // клиента без поддержки привязки: вход работает как раньше.
            var deviceId = CleanDeviceId(input.DeviceId);
            var deviceHash = deviceId == null ? null : DeviceIdentityHash(deviceId);
            var outcome = await store.CompleteTelegramAuthAsync(input.RequestId, config.OwnerTelegramId, deviceHash, input.DeviceLabel, ct);
            if (outcome.Conflict != null)
            {
                return Results.Json(new
                {
                    message = "Аккаунт уже привязан к другому устройству. Отвяжите его через Telegram-бота («Мои устройства» → «Отвязать устройство»), затем войдите снова.",
                    boundAt = outcome.Conflict.BoundAt,
                    lastSeenAt = outcome.Conflict.LastSeenAt,
                    deviceLabel = outcome.Conflict.DeviceLabel
                }, statusCode: 409);
            }
            return outcome.Session == null ? Results.Json(new { message = "Сессия уже использована или истекла." }, statusCode: 401) : Results.Json(outcome.Session);
        });
        auth.MapPost("/telegram/cancel", async (TelegramLoginCancel input, IdentityStore store, CancellationToken ct) =>
        {
            await store.CancelTelegramChallengeAsync(input.RequestId, ct);
            return Results.NoContent();
        });
        auth.MapPost("/dev/start", async (DevelopmentLoginInput input, IdentityStore store, ServerConfig config, IWebHostEnvironment environment, CancellationToken ct) =>
        {
            if (!environment.IsDevelopment() || !config.EnableDevelopmentLogin)
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.DisplayName))
                return Results.UnprocessableEntity(new { message = "Укажите имя тестового пользователя." });
            var challenge = await store.StartDevelopmentLoginAsync(input.DisplayName, TimeSpan.FromMinutes(5), ct);
            return Results.Json(challenge);
        });
        auth.MapPost("/dev/complete", async (DevelopmentLoginComplete input, IdentityStore store, ServerConfig config, IWebHostEnvironment environment, CancellationToken ct) =>
        {
            if (!environment.IsDevelopment() || !config.EnableDevelopmentLogin)
                return Results.NotFound();
            var result = await store.CompleteDevelopmentLoginAsync(input.RequestId, input.Code, ct);
            return result == null ? Results.Json(new { message = "Код недействителен или уже использован." }, statusCode: 401) : Results.Json(result);
        });
        auth.MapGet("/session", async (HttpContext context, IdentityStore store, CancellationToken ct) =>
        {
            var session = await CurrentSessionAsync(context, store, ct);
            return session == null ? Results.Unauthorized() : Results.Json(session.User);
        });
        auth.MapPost("/logout", async (HttpContext context, IdentityStore store, CancellationToken ct) =>
        {
            var token = BearerToken(context);
            if (token == null) return Results.Unauthorized();
            await store.RevokeSessionAsync(token, ct);
            return Results.NoContent();
        });

        var users = app.MapGroup("/admin/api/users");
        users.AddEndpointFilter((context, next) => RequireRoleAsync(context, next, store: null, UserRole.Owner));
        users.MapGet("", async (HttpContext context, IdentityStore store, CancellationToken ct) =>
        {
            var session = await CurrentSessionAsync(context, store, ct);
            if (session == null) return Results.Unauthorized();
            return Results.Json(await store.ListUsersAsync(context.Request.Query["q"].FirstOrDefault(), ct));
        });
        users.MapPut("/{id:long}/role", async (long id, ChangeRoleInput input, HttpContext context, IdentityStore store, CancellationToken ct) =>
        {
            var session = await CurrentSessionAsync(context, store, ct);
            if (session == null) return Results.Unauthorized();
            if (!Enum.TryParse<UserRole>(input.Role, true, out var role))
                return Results.UnprocessableEntity(new { message = "Неизвестная роль." });
            var result = await store.ChangeRoleAsync(session.User.Id, id, role, ct);
            return result.Success ? Results.Ok(new { message = result.Message }) : Results.Json(new { message = result.Message }, statusCode: 403);
        });
        users.MapPut("/{id:long}/block", async (long id, BlockUserInput input, HttpContext context, IdentityStore store, CancellationToken ct) =>
        {
            var session = await CurrentSessionAsync(context, store, ct);
            if (session == null) return Results.Unauthorized();
            var result = await store.SetBlockedAsync(session.User.Id, id, input.Blocked, ct);
            return result.Success ? Results.Ok(new { message = result.Message }) : Results.Json(new { message = result.Message }, statusCode: 403);
        });

        var favorites = app.MapGroup("/api/v1/me/favorites");
        favorites.MapGet("", async (HttpContext context, IdentityStore store, CancellationToken ct) =>
        {
            var session = await CurrentSessionAsync(context, store, ct);
            return session == null ? Results.Unauthorized() : Results.Json(await store.GetFavoritesAsync(session.User.Id, ct));
        });
        favorites.MapPut("/{resourceId}", async (string resourceId, HttpContext context, IdentityStore store, CancellationToken ct) =>
        {
            var session = await CurrentSessionAsync(context, store, ct);
            if (session == null) return Results.Unauthorized();
            if (!ValidFavoriteId(resourceId)) return Results.UnprocessableEntity(new { message = "Недопустимый идентификатор избранного." });
            await store.AddFavoriteAsync(session.User.Id, resourceId, ct);
            return Results.NoContent();
        });
        favorites.MapDelete("/{resourceId}", async (string resourceId, HttpContext context, IdentityStore store, CancellationToken ct) =>
        {
            var session = await CurrentSessionAsync(context, store, ct);
            if (session == null) return Results.Unauthorized();
            if (!ValidFavoriteId(resourceId)) return Results.UnprocessableEntity(new { message = "Недопустимый идентификатор избранного." });
            await store.RemoveFavoriteAsync(session.User.Id, resourceId, ct);
            return Results.NoContent();
        });

        app.MapGet("/admin/api/audit", async (HttpContext context, IdentityStore store, ServerConfig config, CancellationToken ct) =>
        {
            var session = await CurrentSessionAsync(context, store, ct);
            if (session?.User.Role != UserRole.Owner && !LegacyAdminAllowed(context, config, app.Environment)) return Results.Unauthorized();
            return Results.Json(await store.ListAuditAsync(ct));
        });
    }

    public static async Task<AuthSession?> CurrentSessionAsync(HttpContext context, IdentityStore store, CancellationToken ct)
    {
        var token = BearerToken(context);
        return token == null ? null : await store.AuthenticateAsync(token, ct);
    }

    public static string? BearerToken(HttpContext context)
    {
        var value = context.Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : null;
    }

    public static bool LegacyAdminAllowed(HttpContext context, ServerConfig config, IWebHostEnvironment environment) =>
        environment.IsDevelopment() && config.AllowLegacyAdminToken && AdminAuth.IsValid(context, config);

    public static async ValueTask<object?> RequireRoleAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next, IdentityStore? store, UserRole minimum)
    {
        var http = context.HttpContext;
        var requiredStore = store ?? http.RequestServices.GetRequiredService<IdentityStore>();
        var config = http.RequestServices.GetRequiredService<ServerConfig>();
        var environment = http.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var session = await CurrentSessionAsync(http, requiredStore, http.RequestAborted);
        if (session != null && HasMinimumRole(session.User.Role, minimum))
        {
            var result = await next(context);
            if (http.Request.Method is not "GET" and not "HEAD")
                await requiredStore.AddAuditAsync(session.User.Id, $"admin.{http.Request.Method.ToLowerInvariant()}", http.Request.Path, null, http.RequestAborted);
            return result;
        }
        if (session == null && LegacyAdminAllowed(http, config, environment)) return await next(context);
        return Results.Json(new { message = "Требуется соответствующая роль." }, statusCode: session == null ? 401 : 403);
    }

    private static bool ValidFavoriteId(string value) => value.Length is > 0 and <= 160 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    /// DeviceId от клиента: base64url или hex, 16–64 байта энтропии.
    /// Пустое значение допустимо (клиент без привязки), мусор — нет:
    /// иначе «идентификатором» стал бы любой повторяющийся мусор.
    /// </summary>
    private static string? CleanDeviceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var id = value.Trim();
        if (id.Length is < 16 or > 128) return null;
        return id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? id : null;
    }

    /// <summary>В базе хранится только SHA-256 идентификатора устройства.</summary>
    private static string DeviceIdentityHash(string deviceId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)));

    private static bool IsLoopback(System.Net.IPAddress? address) => address != null && System.Net.IPAddress.IsLoopback(address);

    private static bool HasMinimumRole(UserRole actual, UserRole minimum) =>
        minimum == UserRole.Owner ? actual == UserRole.Owner : actual != UserRole.User;
}

public sealed class TelegramLoginComplete { public string RequestId { get; set; } = ""; public string? DeviceId { get; set; } public string? DeviceLabel { get; set; } }
public sealed class TelegramLoginCancel { public string RequestId { get; set; } = ""; }
public sealed class DevelopmentBootstrapInput { public string DisplayName { get; set; } = ""; public long? TelegramId { get; set; } }
public sealed class DevelopmentLoginInput { public string DisplayName { get; set; } = ""; }
public sealed class DevelopmentLoginComplete { public string RequestId { get; set; } = ""; public string Code { get; set; } = ""; }
public sealed class ChangeRoleInput { public string Role { get; set; } = ""; }
public sealed class BlockUserInput { public bool Blocked { get; set; } }
