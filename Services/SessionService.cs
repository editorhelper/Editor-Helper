using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EditorHelper.Services;

public sealed record SessionUser(long Id, long? TelegramId, string DisplayName, string Role, bool IsBlocked, DateTime CreatedAt, DateTime? LastLoginAt);
public sealed record SessionLogin(string Token, DateTime ExpiresAt, SessionUser User);
public sealed record SessionChallenge(string RequestId, DateTime ExpiresAt, string? BotUsername = null, bool DevelopmentOnly = false);

/// <summary>
/// Аккаунт привязан к другому устройству (HTTP 409). Вход возможен после
/// отвязки текущего устройства через Telegram-бота.
/// </summary>
public sealed class DeviceConflictException(string message) : InvalidOperationException(message);

public sealed class SessionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _baseUrl;
    public SessionUser? CurrentUser { get; private set; }
    public bool IsAuthenticated => CurrentUser != null && !string.IsNullOrWhiteSpace(LoadToken());

    /// <summary>Адрес сервера сессии — нужен другим сервисам (админка, баннеры).</summary>
    public string BaseUrl => _baseUrl;

    public SessionService(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

    public async Task<SessionChallenge> StartTelegramLoginAsync(CancellationToken ct = default)
    {
        using var response = await Http.PostAsync(_baseUrl + "/api/v1/auth/telegram/start", null, ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, "Не удалось начать Telegram-вход.", ct);
        return await response.Content.ReadFromJsonAsync<SessionChallenge>(cancellationToken: ct)
               ?? throw new InvalidDataException("Сервер вернул пустой запрос входа.");
    }

    public async Task<string> GetTelegramLoginStatusAsync(string requestId, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(_baseUrl + "/api/v1/auth/telegram/status/" + Uri.EscapeDataString(requestId), ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, "Не удалось проверить Telegram-вход.", ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown";
    }

    public async Task CancelTelegramLoginAsync(string requestId, CancellationToken ct = default)
    {
        using var response = await Http.PostAsJsonAsync(_baseUrl + "/api/v1/auth/telegram/cancel", new { requestId }, ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, "Не удалось отменить вход.", ct);
    }

    /// <summary>
    /// Завершает Telegram-вход. Вместе с requestId отправляется криптографический
    /// идентификатор этого устройства (сервер хранит только его хеш): при первом
    /// входе аккаунт автоматически привязывается к устройству, повторный вход с
    /// него проходит как обычно, а вход с чужого устройства сервер отклоняет —
    /// это приходит как <see cref="DeviceConflictException"/>.
    /// </summary>
    public async Task<SessionUser> CompleteTelegramLoginAsync(string requestId, CancellationToken ct = default)
    {
        using var response = await Http.PostAsJsonAsync(_baseUrl + "/api/v1/auth/telegram/complete",
            new { requestId, deviceId = DeviceIdentity.GetDeviceId(), deviceLabel = DeviceIdentity.GetDeviceLabel() }, ct);

        if ((int)response.StatusCode == StatusCodes409Conflict)
            throw new DeviceConflictException(await MessageAsync(response,
                "Аккаунт привязан к другому устройству. Отвяжите его через Telegram-бота и войдите снова.", ct));

        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, "Telegram-авторизация не подтверждена.", ct);
        var result = await response.Content.ReadFromJsonAsync<SessionLogin>(cancellationToken: ct)
                     ?? throw new InvalidDataException("Сервер вернул пустую сессию.");
        SaveToken(result.Token);
        CurrentUser = result.User;
        return result.User;
    }

    private const int StatusCodes409Conflict = 409;

    private static async Task<string> MessageAsync(HttpResponseMessage response, string fallback, CancellationToken ct)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (json.RootElement.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
                return text;
        }
        catch { }
        return fallback;
    }

    public async Task<SessionChallenge> StartDevelopmentLoginAsync(string displayName, CancellationToken ct = default)
    {
        using var response = await Http.PostAsJsonAsync(_baseUrl + "/api/v1/auth/dev/start", new { displayName }, ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, "Локальный Development-вход недоступен.", ct);
        return await response.Content.ReadFromJsonAsync<SessionChallenge>(cancellationToken: ct)
               ?? throw new InvalidDataException("Сервер вернул пустой запрос входа.");
    }

    public async Task<SessionUser> CompleteDevelopmentLoginAsync(string requestId, string code, CancellationToken ct = default)
    {
        using var response = await Http.PostAsJsonAsync(_baseUrl + "/api/v1/auth/dev/complete", new { requestId, code }, ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, "Код Development-входа недействителен или истёк.", ct);
        var result = await response.Content.ReadFromJsonAsync<SessionLogin>(cancellationToken: ct)
                     ?? throw new InvalidDataException("Сервер вернул пустую сессию.");
        SaveToken(result.Token);
        CurrentUser = result.User;
        return result.User;
    }

    public void ApplyAuthorization(HttpRequestMessage request)
    {
        var token = LoadToken();
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<SessionUser?> RestoreAsync(CancellationToken ct = default)
    {
        var token = LoadToken();
        if (string.IsNullOrWhiteSpace(token)) return CurrentUser = null;
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/api/v1/auth/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            ClearToken();
            return CurrentUser = null;
        }
        return CurrentUser = await response.Content.ReadFromJsonAsync<SessionUser>(cancellationToken: ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var token = LoadToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/v1/auth/logout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, ct);
        }
        ClearToken();
        CurrentUser = null;
    }

    private static async Task<InvalidOperationException> ErrorAsync(HttpResponseMessage response, string fallback, CancellationToken ct)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (json.RootElement.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
                return new InvalidOperationException(text);
        }
        catch { }
        return new InvalidOperationException($"{fallback} (HTTP {(int)response.StatusCode})");
    }

    private static string TokenFile => Path.Combine(AppPaths.UserDataFolder, "session.bin");
    private static void SaveToken(string token)
    {
        Directory.CreateDirectory(AppPaths.UserDataFolder);
        var input = Encoding.UTF8.GetBytes(token);
        File.WriteAllBytes(TokenFile, ProtectedData.Protect(input, null, DataProtectionScope.CurrentUser));
        CryptographicOperations.ZeroMemory(input);
    }
    private static string? LoadToken()
    {
        try
        {
            if (!File.Exists(TokenFile)) return null;
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch { return null; }
    }
    private static void ClearToken() { try { if (File.Exists(TokenFile)) File.Delete(TokenFile); } catch { } }
}
