using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditorHelper.Server.Telegram;

/// <summary>
/// Минимальный клиент Bot API: только те методы, которые нужны серверу —
/// getMe, getChat, getChatMember, getUpdates, getFile, sendDocument.
/// Базовый адрес можно подменить, это используется в тестах с фейковым сервером.
/// </summary>
public class TelegramClient
{
    /// <summary>Лимит скачивания через Bot API — жёсткое ограничение Telegram.</summary>
    public const long MaxBotDownloadBytes = 20 * 1024 * 1024;

    /// <summary>Лимит загрузки через sendDocument.</summary>
    public const long MaxBotUploadBytes = 50 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _apiBase;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TelegramClient(string token, HttpClient? http = null, string? apiBase = null)
    {
        _token = token;
        _apiBase = (apiBase ?? "https://api.telegram.org").TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
    }

    public async Task<TgUser> GetMeAsync(CancellationToken ct = default)
    {
        var result = await CallAsync<TgUser>("getMe", null, ct);
        return result!;
    }

    public async Task<TgChat> GetChatAsync(string chatId, CancellationToken ct = default)
    {
        var result = await CallAsync<TgChat>("getChat", new { chat_id = chatId }, ct);
        return result!;
    }

    /// <summary>
    /// Статус участника канала. null — только когда Telegram честно подтвердил,
    /// что участник не найден («user not found»); недоступность списка участников
    /// и прочие ошибки API пробрасываются как исключение, а не превращаются в
    /// «не админ», иначе диагностика теряется.
    /// </summary>
    public async Task<TgChatMember?> GetChatMemberAsync(string chatId, long userId, CancellationToken ct = default)
    {
        try
        {
            return await GetChatMemberStrictAsync(chatId, userId, ct);
        }
        catch (TelegramException ex) when (ex.Message.Contains("USER_NOT_PARTICIPANT", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("user not found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    public async Task<TgChatMember> GetChatMemberStrictAsync(string chatId, long userId, CancellationToken ct = default)
    {
        return await CallAsync<TgChatMember>("getChatMember", new { chat_id = chatId, user_id = userId }, ct)
               ?? throw new TelegramException("Telegram did not return a membership result.");
    }

    public async Task<List<TgUpdate>> GetUpdatesAsync(
        long offset, int timeoutSeconds, string[]? allowedUpdates, CancellationToken ct)
    {
        // Таймаут запроса больше long-poll таймаута Telegram, иначе HttpClient
        // оборвёт соединение раньше, чем сервер ответит пустым списком
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 15));

        var payload = new Dictionary<string, object>
        {
            ["offset"] = offset,
            ["timeout"] = timeoutSeconds,
            ["limit"] = 100
        };
        if (allowedUpdates is { Length: > 0 })
            payload["allowed_updates"] = allowedUpdates;

        var updates = await CallAsync<List<TgUpdate>>("getUpdates", payload, timeoutCts.Token);
        return updates ?? new List<TgUpdate>();
    }

    /// <summary>
    /// Возвращает file_path для скачивания. Ссылка живёт около часа, поэтому
    /// кэшировать её нельзя — запрашиваем перед каждой отдачей файла.
    /// </summary>
    public async Task<TgMessage> SendMessageAsync(string chatId, string text, object? replyMarkup = null, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object> { ["chat_id"] = chatId, ["text"] = text };
        if (replyMarkup != null) payload["reply_markup"] = replyMarkup;
        var result = await CallAsync<TgMessage>("sendMessage", payload, ct);
        return result!;
    }

    public async Task AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object> { ["callback_query_id"] = callbackQueryId };
        if (!string.IsNullOrWhiteSpace(text)) payload["text"] = text;
        await CallAsync<JsonElement>("answerCallbackQuery", payload, ct);
    }

    public async Task<TgFile> GetFileAsync(string fileId, CancellationToken ct = default)
    {
        var result = await CallAsync<TgFile>("getFile", new { file_id = fileId }, ct);
        return result!;
    }

    public Uri GetFileDownloadUri(TgFile file)
    {
        if (string.IsNullOrEmpty(file.FilePath))
            throw new TelegramException("Telegram не вернул file_path для этого файла.");

        return new Uri($"{_apiBase}/file/bot{_token}/{file.FilePath}");
    }

    /// <summary>Открывает поток скачивания файла. Ответ нужно освобождать вместе с потоком.</summary>
    public async Task<HttpResponseMessage> OpenFileStreamAsync(TgFile file, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            GetFileDownloadUri(file), HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new TelegramException(
                $"Telegram отдал {(int)response.StatusCode} при скачивании файла.");
        }

        return response;
    }

    /// <summary>Загрузка документа в канал из Admin. Лимит Telegram — 50 МБ.</summary>
    public async Task<TgMessage> SendDocumentAsync(
        string chatId, string fileName, Stream content, string? caption, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();

        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "document", fileName);
        form.Add(new StringContent(chatId), "chat_id");
        if (!string.IsNullOrWhiteSpace(caption))
            form.Add(new StringContent(caption), "caption");

        using var response = await _http.PostAsync($"{_apiBase}/bot{_token}/sendDocument", form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = Parse<TgMessage>(body);

        if (!response.IsSuccessStatusCode || parsed?.Ok != true)
            throw new TelegramException(parsed?.Description ?? $"sendDocument вернул HTTP {(int)response.StatusCode}.");

        return parsed.Result!;
    }

    private async Task<T?> CallAsync<T>(string method, object? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/bot{_token}/{method}");
        if (payload != null)
            request.Content = JsonContent.Create(payload, options: JsonOptions);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = Parse<T>(body);

        if (!response.IsSuccessStatusCode || parsed?.Ok != true)
        {
            // Текст ошибки Telegram безопасен: токен в ответах не встречается.
            // Код ответа и метод дают точный диагноз: 401 — токен отозван/неверен,
            // 404 — токен повреждён (пробел, плейсхолдер) или неверный путь.
            var description = parsed?.Description;
            var detail = string.IsNullOrWhiteSpace(description)
                ? $"HTTP {(int)response.StatusCode} ({response.StatusCode})"
                : $"HTTP {(int)response.StatusCode} ({response.StatusCode}): {description}";
            throw new TelegramException($"Метод {method} не выполнен — {detail}");
        }

        return parsed.Result;
    }

    private static TgResponse<T>? Parse<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<TgResponse<T>>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public class TelegramException : Exception
{
    public TelegramException(string message) : base(message) { }
}

public class TgResponse<T>
{
    public bool Ok { get; set; }
    public T? Result { get; set; }
    public string? Description { get; set; }
    [JsonPropertyName("error_code")]
    public int ErrorCode { get; set; }
}

public class TgUser
{
    public long Id { get; set; }
    [JsonPropertyName("is_bot")] public bool IsBot { get; set; }
    [JsonPropertyName("first_name")] public string FirstName { get; set; } = "";
    public string? Username { get; set; }
}

public class TgChat
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string? Title { get; set; }
    public string? Username { get; set; }
}

public class TgChatMember
{
    [JsonPropertyName("user")] public TgUser? User { get; set; }
    public string Status { get; set; } = "";
    [JsonPropertyName("is_member")] public bool IsMember { get; set; }

    public bool IsAdmin => Status is "administrator" or "creator";
    public bool HasActiveMembership => Status is "member" or "administrator" or "creator" || Status == "restricted" && IsMember;
}

public class TgUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; set; }
    [JsonPropertyName("channel_post")] public TgMessage? ChannelPost { get; set; }
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }
    [JsonPropertyName("callback_query")] public TgCallbackQuery? CallbackQuery { get; set; }
}

public class TgCallbackQuery
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("from")] public TgUser? From { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }
}

public class TgMessageUser { [JsonPropertyName("id")] public long Id { get; set; } [JsonPropertyName("username")] public string? Username { get; set; } [JsonPropertyName("first_name")] public string? FirstName { get; set; } }


public class TgMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    public TgChat? Chat { get; set; }
    [JsonPropertyName("from")] public TgMessageUser? From { get; set; }
    public string? Text { get; set; }
    public TgDocument? Document { get; set; }
    public string? Caption { get; set; }
}

public class TgDocument
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    [JsonPropertyName("file_unique_id")] public string FileUniqueId { get; set; } = "";
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
}

public class TgFile
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    [JsonPropertyName("file_unique_id")] public string FileUniqueId { get; set; } = "";
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
    [JsonPropertyName("file_path")] public string? FilePath { get; set; }
}
