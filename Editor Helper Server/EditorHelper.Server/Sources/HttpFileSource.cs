using System.Net;
using EditorHelper.Server.Storage;

namespace EditorHelper.Server.Sources;

/// <summary>
/// Внешняя HTTP/HTTPS-ссылка. Единственный источник для архивов крупнее 20 МБ,
/// которые Bot API не отдаёт. Каждый запрос и каждое перенаправление проходят
/// SSRF-проверку: редирект на внутренний адрес так же опасен, как прямая ссылка.
/// </summary>
public class HttpFileSource : IFileSource
{
    private readonly HttpClient _http;
    private readonly bool _bypassSsrfGuard;

    /// <param name="bypassSsrfGuard">
    /// Только для интеграционных тестов, где «внешний» источник поднят на
    /// localhost: SSRF-защита блокировала бы его по делу. В сервере флаг
    /// никогда не включается.
    /// </param>
    public HttpFileSource(HttpClient? http = null, bool bypassSsrfGuard = false)
    {
        _bypassSsrfGuard = bypassSsrfGuard;

        // Редиректы обрабатываем сами, чтобы проверять каждый переход
        _http = http ?? new HttpClient(new NoRedirectHandler())
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    public string Kind => "http";

    public async Task<FileProbe> ProbeAsync(FileSourceRef source, CancellationToken ct)
    {
        var url = source.ExternalUrl;
        if (string.IsNullOrWhiteSpace(url))
            return new FileProbe { Available = false, Error = "Пустая ссылка." };

        var error = _bypassSsrfGuard
            ? SsrfGuard.ValidateUrlSyntaxOnly(url)
            : await SsrfGuard.ValidateResolvedAsync(url, ct);
        if (error != null) return new FileProbe { Available = false, Error = error };

        try
        {
            // GET, а не HEAD: многие хостинги HEAD не поддерживают (405). Тело не
            // читаем — ResponseHeadersRead отдаёт управление после заголовков.
            using var response = await SendFollowingRedirectsAsync(
                new HttpRequestMessage(HttpMethod.Get, url), ct, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                return new FileProbe
                {
                    Available = false,
                    Error = $"Источник ответил HTTP {(int)response.StatusCode}."
                };
            }

            return new FileProbe
            {
                Available = true,
                FileName = source.FileName ?? FileNameFromResponse(response, url),
                SizeBytes = response.Content.Headers.ContentLength ?? source.SizeBytes
            };
        }
        catch (HttpRequestException ex)
        {
            return new FileProbe { Available = false, Error = $"Не удалось проверить ссылку: {ex.Message}" };
        }
    }

    public async Task<SourceStream> OpenReadAsync(FileSourceRef source, CancellationToken ct)
    {
        var url = source.ExternalUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new HttpRequestException("Пустая ссылка источника.");

        var error = _bypassSsrfGuard ? null : await SsrfGuard.ValidateResolvedAsync(url, ct);
        if (error != null) throw new HttpRequestException(error);

        var response = await SendFollowingRedirectsAsync(
            new HttpRequestMessage(HttpMethod.Get, url), ct, HttpCompletionOption.ResponseHeadersRead);

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var length = response.Content.Headers.ContentLength;
        return new SourceStream(stream, length, response);
    }

    private static string? FileNameFromResponse(HttpResponseMessage response, string url)
    {
        var disposition = response.Content.Headers.ContentDisposition?.FileNameStar
                        ?? response.Content.Headers.ContentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(disposition))
            return disposition.Trim('"');

        var name = Path.GetFileName(new Uri(url).AbsolutePath);
        return string.IsNullOrWhiteSpace(name) ? null : Uri.UnescapeDataString(name);
    }

    /// <summary>
    /// Следует редиректам вручную и на каждом шаге проверяет адрес, чтобы
    /// перенаправление во внутреннюю сеть не обошло SSRF-защиту.
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpRequestMessage request, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        const int maxRedirects = 5;

        for (var i = 0; ; i++)
        {
            var response = await _http.SendAsync(request, completion, ct);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                return response;

            if (i >= maxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("Слишком много перенаправлений.");
            }

            var next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(response.RequestMessage!.RequestUri!, response.Headers.Location);

            response.Dispose();

            if (!_bypassSsrfGuard)
            {
                var redirectError = await SsrfGuard.ValidateResolvedAsync(next.ToString(), ct);
                if (redirectError != null)
                    throw new HttpRequestException($"Перенаправление отклонено: {redirectError}");
            }

            // Тело запроса при редиректе повторно не отправить — дальше идём GET
            request = new HttpRequestMessage(HttpMethod.Get, next);
        }
    }

    private static bool IsRedirect(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Moved or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        (HttpStatusCode)308 => true,
        _ => false
    };

    private sealed class NoRedirectHandler : HttpClientHandler
    {
        public NoRedirectHandler() => AllowAutoRedirect = false;
    }
}
