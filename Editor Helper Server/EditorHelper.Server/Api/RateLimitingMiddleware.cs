using EditorHelper.Server.Auth;

namespace EditorHelper.Server.Api;

/// <summary>
/// Ограничение частоты запросов по адресу клиента.
///
/// Пределы различаются по назначению маршрута. Самые строгие — на завершении
/// входа, где подбираются одноразовые коды. Опрос статуса подтверждения мягче:
/// клиент штатно спрашивает его раз в две секунды, и жёсткий лимит сломал бы
/// обычный вход. Скачивание файлов и обложек не ограничивается вовсе — прервать
/// загрузку архива на середине хуже, чем дать клиенту докачать его.
/// </summary>
public static class RateLimitingMiddleware
{
    public static IApplicationBuilder UseRateLimiting(this WebApplication app) =>
        app.UseMiddleware<RateLimitingFilter>();

    private sealed class RateLimitingFilter
    {
        private readonly RequestDelegate _next;

        public RateLimitingFilter(RequestDelegate next) => _next = next;

        public async Task Invoke(HttpContext context, RateLimiter limiter, ServerConfig config)
        {
            var path = context.Request.Path.Value ?? "";

            // Отдача архивов: большой поток на несколько минут, лимит по числу
            // запросов здесь только мешал бы
            if (path.Contains("/download", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/banner", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            // Предел тела запроса ставится по маршруту: у JSON-форм он маленький,
            // у загрузок — свой. Ограничение относится только к запросам с телом.
            ApplyBodySizeLimit(context, path);

            var tier = Classify(path);

            // Админка требует роль Owner, поэтому её лимит привязан к сессии,
            // а не к адресу: за одним NAT могут сидеть и владелец, и чужой клиент
            var perSession = tier is Tier.Owner or Tier.AuthCompletion;
            var decision = limiter.Allow(Key(context, perSession), LimitFor(tier, config));
            limiter.CleanupExpired();

            if (!decision.Allowed)
            {
                context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Слишком много запросов. Повторите попытку через несколько секунд."
                });
                return;
            }

            context.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString();
            await _next(context);
        }

        private enum Tier { Public, AuthCompletion, AuthPolling, Owner }

        private static Tier Classify(string path)
        {
            if (path.StartsWith("/api/v1/auth", StringComparison.OrdinalIgnoreCase))
            {
                // Завершение входа принимает одноразовый код — подбирать его
                // должно быть дорого
                return path.EndsWith("/complete", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith("/logout", StringComparison.OrdinalIgnoreCase)
                    ? Tier.AuthCompletion
                    : Tier.AuthPolling;
            }

            return path.StartsWith("/admin/api", StringComparison.OrdinalIgnoreCase)
                ? Tier.Owner
                : Tier.Public;
        }

        private static int LimitFor(Tier tier, ServerConfig config) => tier switch
        {
            Tier.AuthCompletion => config.AuthRateLimitPerMinute,
            Tier.Owner => config.OwnerRateLimitPerMinute,
            _ => config.RateLimitPerMinute
        };

        private static string Key(HttpContext context, bool perSession)
        {
            if (perSession)
            {
                var token = AuthEndpoints.BearerToken(context);
                if (!string.IsNullOrWhiteSpace(token)) return "session:" + token;
            }

            return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        }

        /// <summary>
        /// Предел размера тела запроса по маршруту. Kestrel сам режет тело по
        /// общему потолку (см. ServerHost); здесь он уточняется точнее: JSON-форма
        /// карточки не принимает тело размером с архив, а загрузки — принимают.
        /// </summary>
        private static void ApplyBodySizeLimit(HttpContext context, string path)
        {
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                return;

            if (path.EndsWith("/files/upload", StringComparison.OrdinalIgnoreCase))
                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                    .MaxRequestBodySize = UploadLimits.MaxRequestBodyBytes;
            else if (path.EndsWith("/telegram/upload", StringComparison.OrdinalIgnoreCase))
                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                    .MaxRequestBodySize = UploadLimits.TelegramBytes + UploadLimits.MultipartOverheadBytes;
            else if (path.EndsWith("/banner", StringComparison.OrdinalIgnoreCase))
                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                    .MaxRequestBodySize = UploadLimits.BannerBytes + UploadLimits.MultipartOverheadBytes;
            else if (path.StartsWith("/admin/api", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/api/v1/auth", StringComparison.OrdinalIgnoreCase))
                context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()!
                    .MaxRequestBodySize = UploadLimits.JsonBodyBytes;
        }
    }
}
