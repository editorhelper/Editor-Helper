using System.Collections.Concurrent;
using EditorHelper.Server.Auth;
using EditorHelper.Server.Storage;

namespace EditorHelper.Server.Telegram;

public sealed class TelegramBotService : BackgroundService
{
    private readonly ServerConfig _config;
    private readonly IdentityStore _identity;
    private readonly CatalogRepository _catalog;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly AdminActionTickets _pending = new();

    public TelegramBotService(ServerConfig config, IdentityStore identity, CatalogRepository catalog, ILogger<TelegramBotService> logger)
    {
        _config = config;
        _identity = identity;
        _catalog = catalog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_config.TelegramToken))
        {
            _logger.LogInformation("Telegram bot is not configured; login, admin and discovery handlers are disabled.");
            return;
        }

        var bot = new TelegramClient(_config.TelegramToken, apiBase: _config.TelegramApiBase);
        TgUser me;
        try
        {
            me = await bot.GetMeAsync(stoppingToken);
            if (!string.IsNullOrWhiteSpace(_config.TelegramChannel)) await VerifyChannelAccessAsync(bot, me, stoppingToken);
        }
        catch (TelegramException ex)
        {
            _logger.LogError(ex,
                "Telegram bot startup failed: {Message}. Check TELEGRAM_BOT_TOKEN (401 Unauthorized = token revoked or wrong, 404 Not Found = token value is corrupted, e.g. stray whitespace or placeholder), TELEGRAM_CHANNEL_ID and bot admin rights in the channel. The token itself is not logged.",
                ex.Message);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex,
                "Telegram bot startup failed: could not reach {ApiBase} — network, proxy or TELEGRAM_API_BASE problem.",
                _config.TelegramApiBase);
            return;
        }

        using var gate = PollingGate.Acquire("bot:" + me.Id);
        if (gate == null)
        {
            _logger.LogWarning("Telegram polling already active for bot {BotId}.", me.Id);
            return;
        }

        var offset = await LoadOffsetAsync();
        _logger.LogInformation("Single Telegram bot @{Username} is running login, admin and catalog handlers.", me.Username);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdatesAsync(offset, Math.Min(_config.PollTimeoutSeconds, 20), new[] { "message", "callback_query", "channel_post" }, stoppingToken);
                foreach (var update in updates)
                {
                    offset = Math.Max(offset, update.UpdateId + 1);
                    await HandleUpdateAsync(bot, update, stoppingToken);
                }
                if (updates.Count > 0 && !string.IsNullOrWhiteSpace(_config.TelegramChannel)) await SaveOffsetAsync(offset);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("Telegram polling failed: {Message}", ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleUpdateAsync(TelegramClient bot, TgUpdate update, CancellationToken ct)
    {
        if (update.ChannelPost != null)
        {
            await HandleChannelPostAsync(update, ct);
            return;
        }
        if (update.Message?.From is { } sender && update.Message.Chat is { } chat && !string.IsNullOrWhiteSpace(update.Message.Text))
        {
            await HandlePrivateMessageAsync(bot, chat.Id.ToString(), new TgUser { Id = sender.Id, Username = sender.Username, FirstName = sender.FirstName ?? "" }, update.Message.Text.Trim(), ct);
            return;
        }
        if (update.CallbackQuery != null) await HandleCallbackAsync(bot, update.CallbackQuery, ct);
    }

    private async Task HandlePrivateMessageAsync(TelegramClient bot, string chatId, TgUser sender, string text, CancellationToken ct)
    {
        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            var payload = text["/start".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(payload))
            {
                await HandleLoginDeepLinkAsync(bot, chatId, sender, payload, ct);
                return;
            }
            var account = await GetAuthorizedUserAsync(sender.Id, ct);
            if (account is { Role: UserRole.Owner, IsBlocked: false })
            {
                await bot.SendMessageAsync(chatId, "Откройте Editor Helper и нажмите «Войти через Telegram» — я пришлю подтверждение сюда. Пароль Telegram не требуется. Используйте inline-меню ниже для управления.", MainKeyboard(), ct);
                return;
            }
            // Не Owner, но есть аккаунт: доступен только просмотр своих устройств
            var self = await GetSelfUserAsync(sender.Id, ct);
            await bot.SendMessageAsync(chatId, "Откройте Editor Helper и нажмите «Войти через Telegram» — я пришлю подтверждение сюда. Пароль Telegram не требуется.",
                self != null ? SelfKeyboard() : null, ct);
            return;
        }

        // Команда устройств доступна любому владельцу аккаунта, не только Owner
        if (text.StartsWith("/devices", StringComparison.OrdinalIgnoreCase))
        {
            await ShowMyDevicesAsync(bot, chatId, sender.Id, ct);
            return;
        }

        var user = await GetAuthorizedUserAsync(sender.Id, ct);
        if (user == null) { await bot.SendMessageAsync(chatId, "Команда не распознана. Для входа откройте Editor Helper и нажмите «Войти через Telegram».", ct: ct); return; }
        if (text.StartsWith("/find ", StringComparison.OrdinalIgnoreCase))
            await ShowUserAsync(bot, chatId, user, text[6..].Trim(), ct);
        else if (text.StartsWith("/userinfo", StringComparison.OrdinalIgnoreCase))
            await HandleUserInfoCommandAsync(bot, chatId, user, text["/userinfo".Length..].Trim(), ct);
        else if (text.StartsWith("/role ", StringComparison.OrdinalIgnoreCase))
            await HandleRoleCommandAsync(bot, chatId, user, text["/role".Length..].Trim(), ct);
    }

    /// <summary>
    /// /role &lt;Telegram ID&gt; &lt;user|vip&gt; — выдача и снятие VIP. Доступно
    /// только Owner; роль проверяется на сервере при каждом вызове
    /// ChangeRoleAsync, а не только при разборе команды.
    /// </summary>
    private async Task HandleRoleCommandAsync(TelegramClient bot, string chatId, AuthUser actor, string args, CancellationToken ct)
    {
        if (actor.Role != UserRole.Owner)
        {
            await bot.SendMessageAsync(chatId, "Управление ролями доступно только Owner.", ct: ct);
            return;
        }

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var telegramId))
        {
            await bot.SendMessageAsync(chatId, "Формат: /role <Telegram ID> <user|vip>", ct: ct);
            return;
        }

        var role = parts[1].ToLowerInvariant() switch
        {
            "user" => UserRole.User,
            "vip" => UserRole.VIP,
            _ => (UserRole?)null
        };
        if (role == null)
        {
            await bot.SendMessageAsync(chatId, "Неизвестная роль. Доступно: user, vip.", ct: ct);
            return;
        }

        var target = await _identity.FindUserAsync(telegramId.ToString(), ct);
        if (target == null)
        {
            await bot.SendMessageAsync(chatId, $"Пользователь с Telegram ID {telegramId} не найден. Он должен хотя бы раз войти через Editor Helper.", ct: ct);
            return;
        }

        var result = await _identity.ChangeRoleAsync(actor.Id, target.Id, role.Value, ct);
        await bot.SendMessageAsync(chatId, (result.Success ? "✅ " : "⛔ ") + result.Message +
            (result.Success ? $"\n{target.DisplayName}: {role.Value}" : ""), ct: ct);
        if (result.Success) await ShowUserAsync(bot, chatId, actor, telegramId.ToString(), ct);
    }

    /// <summary>/userinfo &lt;Telegram ID&gt; — информация о пользователе и роли.</summary>
    private async Task HandleUserInfoCommandAsync(TelegramClient bot, string chatId, AuthUser actor, string args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await ShowUserAsync(bot, chatId, actor, actor.Id.ToString(), ct);
            return;
        }
        if (!long.TryParse(args, out var telegramId) && !long.TryParse(args.TrimStart('#'), out telegramId))
        {
            await bot.SendMessageAsync(chatId, "Формат: /userinfo <Telegram ID> или /userinfo без аргумента — ваш профиль.", ct: ct);
            return;
        }
        await ShowUserAsync(bot, chatId, actor, telegramId.ToString(), ct);
    }

    /// <summary>
    /// Deep-link из приложения: /start &lt;requestId&gt;. requestId приходит из
    /// query-параметра ссылки, которую открыл клиент, поэтому он привязан
    /// к конкретной попытке входа; чужую или истёкшую подтвердить нельзя.
    /// </summary>
    private async Task HandleLoginDeepLinkAsync(TelegramClient bot, string chatId, TgUser sender, string requestId, CancellationToken ct)
    {
        if (Guid.TryParseExact(requestId.Replace("-", ""), "N", out _))
            requestId = requestId.Replace("-", "");

        var challenge = await _identity.GetTelegramChallengeAsync(requestId, ct);
        if (challenge == null || challenge.Declined)
        {
            await bot.SendMessageAsync(chatId, "Запрос на вход не найден, истёк или уже использован. Нажмите «Войти через Telegram» в Editor Helper ещё раз.", ct: ct);
            return;
        }

        await bot.SendMessageAsync(chatId, "Хотите войти в Editor Helper?", new
        {
            inline_keyboard = new object[][]
            {
                new[] { Button("✅ Да, войти", $"login:allow:{requestId}"), Button("❌ Нет, отмена", $"login:deny:{requestId}") }
            }
        }, ct);
    }

    private async Task HandleLoginCallbackAsync(TelegramClient bot, TgCallbackQuery callback, string[] parts, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat!.Id.ToString();
        var sender = callback.From!;

        if (parts.Length != 3 || !Guid.TryParseExact(parts[2].Replace("-", ""), "N", out _))
        {
            await bot.AnswerCallbackQueryAsync(callback.Id, "Кнопка недействительна.", ct);
            return;
        }
        var requestId = parts[2];

        if (parts[1] == "allow")
        {
            var confirmed = await _identity.ConfirmTelegramChallengeAsync(requestId, sender.Id, sender.Username, sender.FirstName, chatId, ct);
            if (!confirmed)
            {
                await bot.AnswerCallbackQueryAsync(callback.Id, "Запрос входа не найден или уже подтверждён.", ct);
                await bot.SendMessageAsync(chatId, "Запрос на вход устарел. Нажмите «Войти через Telegram» в Editor Helper ещё раз.", ct: ct);
                return;
            }
            await bot.AnswerCallbackQueryAsync(callback.Id, "Вход подтверждён.", ct);
            await bot.SendMessageAsync(chatId, "✅ Вход подтверждён. Editor Helper откроется автоматически — возвращаться в приложение не нужно.", ct: ct);
            return;
        }

        if (parts[1] == "deny")
        {
            var declined = await _identity.DeclineTelegramChallengeAsync(requestId, sender.Id, ct);
            await bot.AnswerCallbackQueryAsync(callback.Id, declined ? "Вход отменён." : "Кнопка устарела.", ct);
            if (declined) await bot.SendMessageAsync(chatId, "Вход отменён. Нажмите «Войти через Telegram» в Editor Helper, чтобы попробовать снова.", ct: ct);
        }
    }

    private async Task HandleCallbackAsync(TelegramClient bot, TgCallbackQuery callback, CancellationToken ct)
    {
        if (callback.From == null || callback.Message?.Chat == null || string.IsNullOrWhiteSpace(callback.Data)) return;
        var chatId = callback.Message.Chat.Id.ToString();
        var parts = callback.Data.Split(':');

        // Колбэки входа доступны до авторизации: пользователь жмёт кнопку,
        // ещё не имея сессии, поэтому фильтр роли для них не применяется
        if (parts[0] == "login")
        {
            await HandleLoginCallbackAsync(bot, callback, parts, ct);
            return;
        }

        // Свои устройства — операция владельца аккаунта, доступна любой роли.
        // Актор определяется по Telegram ID из проверенного сервером апдейта,
        // поэтому отвязать чужое устройство подменой ID нельзя.
        if (parts[0] is "mydev" or "mydevask" or "mydevunbind" or "mydevcancel")
        {
            await HandleMyDeviceCallbackAsync(bot, callback, parts, ct);
            return;
        }

        // Кнопка «назад» у пользователя без админ-меню: просто гасим колбэк
        if (parts[0] == "noop")
        {
            await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct);
            return;
        }

        var actor = await GetAuthorizedUserAsync(callback.From.Id, ct);
        if (actor == null) { await bot.AnswerCallbackQueryAsync(callback.Id, "Доступ запрещён.", ct); return; }
        switch (parts[0])
        {
            case "menu": await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct); await ShowMainMenuAsync(bot, chatId, callback.From.Id, ct); break;
            case "me": await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct); await bot.SendMessageAsync(chatId, $"ID: {actor.Id}\nTelegram ID: {callback.From.Id}\nРоль: {RoleLabel(actor.Role)}\nСтатус: активен", ct: ct); break;
            case "users": await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct); await ShowUsersAsync(bot, chatId, ct); break;
            case "find": await bot.AnswerCallbackQueryAsync(callback.Id, "Отправьте /find <ID> или /find <Telegram ID>.", ct); break;
            case "audit": await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct); await ShowAuditAsync(bot, chatId, ct); break;
            case "refresh": await bot.AnswerCallbackQueryAsync(callback.Id, "Данные обновлены.", ct); await ShowMainMenuAsync(bot, chatId, callback.From.Id, ct); break;
            case "user" when parts.Length == 2 && long.TryParse(parts[1], out var targetId):
                await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct); await ShowUserAsync(bot, chatId, actor, targetId.ToString(), ct); break;
            case "role" when parts.Length == 3 && long.TryParse(parts[1], out var roleId) && Enum.TryParse<UserRole>(parts[2], true, out var role):
                await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct); await AskConfirmationAsync(bot, chatId, actor, callback.From.Id, "role", roleId, role.ToString(), ct); break;
            case "block" when parts.Length == 3 && long.TryParse(parts[1], out var blockId) && bool.TryParse(parts[2], out var blocked):
                await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct); await AskConfirmationAsync(bot, chatId, actor, callback.From.Id, "block", blockId, blocked.ToString(), ct); break;
            case "revoke" when parts.Length == 2 && long.TryParse(parts[1], out var revokeId):
                await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct); await AskConfirmationAsync(bot, chatId, actor, callback.From.Id, "revoke", revokeId, "", ct); break;
            case "device" when parts.Length == 2 && long.TryParse(parts[1], out var deviceUserId):
                await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct); await AskConfirmationAsync(bot, chatId, actor, callback.From.Id, "unbind", deviceUserId, "", ct); break;
            case "confirm" when parts.Length == 2 && _pending.TryConsume(parts[1], callback.From.Id, out var pending):
                await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct);
                if (pending == null || pending.ExpiresAt < DateTime.UtcNow) { await bot.SendMessageAsync(chatId, "Подтверждение истекло.", ct: ct); return; }
                var fresh = await GetAuthorizedUserAsync(callback.From.Id, ct);
                if (fresh == null || fresh.Id != pending.ActorUserId) { await bot.SendMessageAsync(chatId, "Права изменились, операция отменена.", ct: ct); return; }
                await ExecutePendingAsync(bot, chatId, fresh, pending, ct); break;
            case "cancel" when parts.Length == 2 && _pending.TryCancel(parts[1], callback.From.Id):
                await bot.AnswerCallbackQueryAsync(callback.Id, "Отменено.", ct); await bot.SendMessageAsync(chatId, "Операция отменена.", ct: ct); break;
            default: await bot.AnswerCallbackQueryAsync(callback.Id, "Кнопка устарела или не принадлежит вам.", ct); break;
        }
    }

    private async Task ShowMainMenuAsync(TelegramClient bot, string chatId, long telegramId, CancellationToken ct)
    {
        var user = await GetAuthorizedUserAsync(telegramId, ct);
        if (user == null) { await bot.SendMessageAsync(chatId, "Доступ запрещён. Для входа используйте код в Editor Helper.", ct: ct); return; }
        await bot.SendMessageAsync(chatId, $"Editor Helper\nРоль: {RoleLabel(user.Role)}", MainKeyboard(), ct);
    }

    private static object MainKeyboard() => new { inline_keyboard = new object[][] { new object[] { Button("👤 Мой аккаунт", "me") }, new object[] { Button("📱 Мои устройства", "mydev") }, new object[] { Button("👥 Пользователи", "users") }, new object[] { Button("🔎 Найти пользователя", "find") }, new object[] { Button("📋 Журнал действий", "audit") }, new object[] { Button("🔄 Обновить данные", "refresh") } } };

    // ---------------------------------------------------------------- Мои устройства (любая роль)

    /// <summary>Владелец аккаунта по своему Telegram ID — для операций над своими данными.</summary>
    private async Task<AuthUser?> GetSelfUserAsync(long telegramId, CancellationToken ct)
    {
        var user = await _identity.FindUserAsync(telegramId.ToString(), ct);
        return user is { IsBlocked: false } ? user : null;
    }

    /// <summary>Меню «только для себя» — доступно не-Owner, у которых есть аккаунт.</summary>
    private static object SelfKeyboard() => new { inline_keyboard = new object[][] { new object[] { Button("📱 Мои устройства", "mydev") } } };

    /// <summary>
    /// Колбэки раздела «мои устройства». Идут до Owner-гейта, потому что
    /// управление своей привязкой доступно любой роли. Владелец аккаунта
    /// определяется по Telegram ID из проверенного сервером апдейта
    /// (callback.From.Id) — подмена невозможна, а операция всегда адресует
    /// только собственный аккаунт.
    /// </summary>
    private async Task HandleMyDeviceCallbackAsync(TelegramClient bot, TgCallbackQuery callback, string[] parts, CancellationToken ct)
    {
        var chatId = callback.Message!.Chat!.Id.ToString();
        var telegramId = callback.From!.Id;

        // mydev — показать список; mydevask — запросить подтверждение отвязки;
        // mydevunbind:<ticket> / mydevcancel:<ticket> — подтвердить или отменить
        switch (parts[0])
        {
            case "mydev":
                await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct);
                await ShowMyDevicesAsync(bot, chatId, telegramId, ct);
                break;

            case "mydevask":
                await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct);
                var self = await GetSelfUserAsync(telegramId, ct);
                if (self == null) { await bot.SendMessageAsync(chatId, "Аккаунт не найден.", ct: ct); return; }
                var ticket = Guid.NewGuid().ToString("N");
                _pending.Add(ticket, new PendingAdminAction(self.Id, telegramId, self.Id, "unbind_self", "", DateTime.UtcNow.AddMinutes(2)));
                await bot.SendMessageAsync(chatId, "Отвязать устройство? Все активные сессии аккаунта будут закрыты. Подтверждение действует 2 минуты.",
                    new { inline_keyboard = new[] { new[] { Button("🔓 Да, отвязать", $"mydevunbind:{ticket}"), Button("Отмена", $"mydevcancel:{ticket}") } } }, ct);
                break;

            case "mydevunbind" when parts.Length == 2 && _pending.TryConsume(parts[1], telegramId, out var pending):
                await bot.AnswerCallbackQueryAsync(callback.Id, ct: ct);
                if (pending == null || pending.ExpiresAt < DateTime.UtcNow) { await bot.SendMessageAsync(chatId, "Подтверждение истекло.", ct: ct); return; }
                // Повторная серверная проверка: аккаунт существует, не заблокирован,
                // и Telegram ID тикета совпадает с отправителем колбэка
                if (pending.ActorTelegramId != telegramId) { await bot.SendMessageAsync(chatId, "Операция отклонена.", ct: ct); return; }
                var result = await _identity.UnbindDeviceAsync(telegramId, ct);
                await bot.SendMessageAsync(chatId, (result.Success ? "✅ " : "⛔ ") + result.Message, ct: ct);
                await ShowMyDevicesAsync(bot, chatId, telegramId, ct);
                break;

            case "mydevcancel" when parts.Length == 2 && _pending.TryCancel(parts[1], telegramId):
                await bot.AnswerCallbackQueryAsync(callback.Id, "Отменено.", ct);
                await bot.SendMessageAsync(chatId, "Отвязка отменена.", ct: ct);
                break;

            default:
                await bot.AnswerCallbackQueryAsync(callback.Id, "Кнопка устарела или не принадлежит вам.", ct);
                break;
        }
    }

    /// <summary>Показывает текущую привязку аккаунта и кнопку отвязки.</summary>
    private async Task ShowMyDevicesAsync(TelegramClient bot, string chatId, long telegramId, CancellationToken ct)
    {
        var self = await GetSelfUserAsync(telegramId, ct);
        if (self == null) { await bot.SendMessageAsync(chatId, "Аккаунт не найден. Сначала войдите через Editor Helper.", ct: ct); return; }

        var binding = await _identity.GetDeviceAsync(self.Id, ct);
        if (binding == null)
        {
            await bot.SendMessageAsync(chatId, "К вашему аккаунту не привязано ни одно устройство.\nВойдите через Editor Helper — устройство привяжется автоматически при первом входе.",
                new { inline_keyboard = new[] { new[] { Button("← Меню", self.Role == UserRole.Owner ? "menu" : "noop") } } }, ct);
            return;
        }

        var label = string.IsNullOrWhiteSpace(binding.DeviceLabel) ? "Устройство" : binding.DeviceLabel!;
        var text = $"📱 Привязанное устройство\n\n{label}\nПривязано: {binding.BoundAt:yyyy-MM-dd HH:mm} UTC\nПоследний вход: {binding.LastSeenAt:yyyy-MM-dd HH:mm} UTC\n\nК аккаунту привязано одно устройство. Чтобы войти с другого — отвязайте текущее.";
        await bot.SendMessageAsync(chatId, text,
            new { inline_keyboard = new object[][] { new object[] { Button("🔓 Отвязать устройство", "mydevask") }, new object[] { Button("← Меню", self.Role == UserRole.Owner ? "menu" : "noop") } } }, ct);
    }

    private async Task ShowUsersAsync(TelegramClient bot, string chatId, CancellationToken ct)
    {
        var users = await _identity.ListUsersAsync(ct: ct);
        var rows = users.Take(20).Select(u => (object)new[] { Button($"{u.DisplayName} · {RoleLabel(u.Role)}{(u.IsBlocked ? " · заблокирован" : "")}", $"user:{u.Id}") }).ToList();
        rows.Add(new[] { Button("← Назад", "menu") });
        await bot.SendMessageAsync(chatId, $"Пользователи: {Math.Min(users.Count, 20)} из {users.Count}. Поиск: /find <ID> или /find <Telegram ID>.", new { inline_keyboard = rows }, ct);
    }

    private async Task ShowUserAsync(TelegramClient bot, string chatId, AuthUser actor, string key, CancellationToken ct)
    {
        var target = await _identity.FindUserAsync(key, ct);
        if (target == null) { await bot.SendMessageAsync(chatId, "Пользователь не найден.", ct: ct); return; }
        var rows = new List<object[]>();
        if (actor.Role == UserRole.Owner && actor.Id != target.Id && target.Role != UserRole.Owner)
        {
            if (target.Role != UserRole.User) rows.Add(new[] { Button("👤 Снять VIP (User)", $"role:{target.Id}:User") });
            if (target.Role != UserRole.VIP) rows.Add(new[] { Button("💎 Выдать VIP", $"role:{target.Id}:VIP") });
            rows.Add(new[] { Button(target.IsBlocked ? "Разблокировать" : "Блокировать", $"block:{target.Id}:{!target.IsBlocked}"), Button("Отозвать сессии", $"revoke:{target.Id}") });
            if (await _identity.GetDeviceAsync(target.Id, ct) != null)
                rows.Add(new[] { Button("🔓 Отвязать устройство", $"device:{target.Id}") });
        }
        rows.Add(new[] { Button("← Назад", actor.Role == UserRole.Owner ? "users" : "menu") });
        await bot.SendMessageAsync(chatId, $"ID: {target.Id}\nTelegram ID: {target.TelegramId?.ToString() ?? "не привязан"}\nИмя: {target.DisplayName}\nРоль: {RoleLabel(target.Role)}\nСтатус: {(target.IsBlocked ? "заблокирован" : "активен")}\nРегистрация UTC: {target.CreatedAt:yyyy-MM-dd HH:mm}", new { inline_keyboard = rows }, ct);
    }

    private static string RoleLabel(UserRole role) => role switch
    {
        UserRole.Owner => "Owner ⭐",
        UserRole.VIP => "VIP 💎",
        _ => "User"
    };

    private async Task ShowAuditAsync(TelegramClient bot, string chatId, CancellationToken ct)
    {
        var events = await _identity.ListAuditAsync(ct);
        var text = events.Count == 0 ? "Журнал пуст." : string.Join("\n", events.Take(10).Select(e => $"{e.CreatedAt:MM-dd HH:mm} · {e.Action} · {e.Target}"));
        await bot.SendMessageAsync(chatId, "Последние действия:\n" + text, new { inline_keyboard = new[] { new[] { Button("← Назад", "menu") } } }, ct);
    }

    private async Task AskConfirmationAsync(TelegramClient bot, string chatId, AuthUser actor, long telegramId, string action, long targetId, string value, CancellationToken ct)
    {
        var ticket = Guid.NewGuid().ToString("N");
        _pending.Add(ticket, new PendingAdminAction(actor.Id, telegramId, targetId, action, value, DateTime.UtcNow.AddMinutes(2)));
        await bot.SendMessageAsync(chatId, "Подтвердите операцию (действует 2 минуты).", new { inline_keyboard = new[] { new[] { Button("✅ Подтвердить", $"confirm:{ticket}"), Button("Отмена", $"cancel:{ticket}") } } }, ct);
    }

    private async Task ExecutePendingAsync(TelegramClient bot, string chatId, AuthUser actor, PendingAdminAction action, CancellationToken ct)
    {
        (bool Success, string Message) result = action.Action switch
        {
            "role" when actor.Role == UserRole.Owner && Enum.TryParse<UserRole>(action.Value, out var role) => await _identity.ChangeRoleAsync(actor.Id, action.TargetId, role, ct),
            "block" when bool.TryParse(action.Value, out var blocked) => await _identity.SetBlockedAsync(actor.Id, action.TargetId, blocked, ct),
            "revoke" => await _identity.RevokeAllSessionsAsync(actor.Id, action.TargetId, ct),
            "unbind" when actor.Role == UserRole.Owner => await _identity.UnbindDeviceByOwnerAsync(actor.Id, action.TargetId, ct),
            _ => (false, "Операция не разрешена.")
        };
        await bot.SendMessageAsync(chatId, (result.Success ? "✅ " : "⛔ ") + result.Message, ct: ct);
        if (result.Success) await ShowUserAsync(bot, chatId, actor, action.TargetId.ToString(), ct);
    }

    /// <summary>
    /// Админ-меню бота доступно только владельцу: Owner определяется
    /// постоянным Telegram ID из конфигурации сервера, а не username.
    /// </summary>
    private async Task<AuthUser?> GetAuthorizedUserAsync(long telegramId, CancellationToken ct)
    {
        if (_config.OwnerTelegramId == telegramId)
        {
            await _identity.EnsureConfiguredOwnerAsync(telegramId, ct);
            var owner = await _identity.FindUserAsync(telegramId.ToString(), ct);
            if (owner is { IsBlocked: false, Role: UserRole.Owner }) return owner;
        }
        return null;
    }

    private async Task VerifyChannelAccessAsync(TelegramClient bot, TgUser me, CancellationToken ct)
    {
        var chat = await bot.GetChatAsync(_config.TelegramChannel!, ct);
        var member = await bot.GetChatMemberAsync(_config.TelegramChannel!, me.Id, ct);
        if (member is not { IsAdmin: true })
            throw new TelegramException(
                $"Bot @{me.Username} needs administrator rights in channel {chat.Title ?? _config.TelegramChannel}. " +
                "Open the channel in Telegram → Manage Channel → Administrators → add the bot as admin. " +
                "This cannot be granted through the Bot API.");
        _logger.LogInformation("Single Telegram bot is configured for channel discovery: {Channel}", _config.TelegramChannel);
    }

    private async Task HandleChannelPostAsync(TgUpdate update, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.TelegramChannel) || update.ChannelPost?.Document == null) return;
        var chat = update.ChannelPost.Chat;
        var chatId = chat?.Id.ToString() ?? "";
        var configured = _config.TelegramChannel.Trim();
        if (configured != chatId && (string.IsNullOrEmpty(chat?.Username) || !string.Equals(chat.Username, configured.TrimStart('@'), StringComparison.OrdinalIgnoreCase))) return;
        var document = update.ChannelPost.Document;
        var fileName = string.IsNullOrWhiteSpace(document.FileName) ? $"document-{document.FileUniqueId}" : document.FileName!;
        await _catalog.UpdateDiscoveredAsync<bool>(data =>
        {
            data.LastUpdateId = Math.Max(data.LastUpdateId, update.UpdateId);
            var existing = data.Documents.FirstOrDefault(d => d.FileUniqueId == document.FileUniqueId);
            var link = BuildMessageLink(chat?.Username, chatId, update.ChannelPost.MessageId);
            if (existing != null)
            {
                existing.MessageId = update.ChannelPost.MessageId;
                existing.ChatId = chatId;
                existing.MessageLink = link;
                existing.SeenAt = DateTime.UtcNow;
                return false;
            }
            data.Documents.Insert(0, new DiscoveredDocument { FileId = document.FileId, FileUniqueId = document.FileUniqueId, FileName = fileName, SizeBytes = document.FileSize ?? 0, MessageId = update.ChannelPost.MessageId, ChatId = chatId, MessageLink = link, SeenAt = DateTime.UtcNow });
            return true;
        });
        _logger.LogInformation("Discovered channel document: {FileName}", fileName);
    }

    private async Task<long> LoadOffsetAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.TelegramChannel)) return 0;
        var data = await _catalog.LoadDiscoveredAsync();
        return data.LastUpdateId > 0 ? data.LastUpdateId + 1 : 0;
    }

    private Task SaveOffsetAsync(long offset) => _catalog.UpdateDiscoveredAsync<object?>(data => { data.LastUpdateId = Math.Max(data.LastUpdateId, offset - 1); return null; });
    private static string? BuildMessageLink(string? username, string chatId, long messageId)
    {
        if (!string.IsNullOrEmpty(username)) return $"https://t.me/{username}/{messageId}";
        if (long.TryParse(chatId, out var numeric) && numeric < 0) return $"https://t.me/c/{-numeric - 1000000000000L}/{messageId}";
        return null;
    }

    private static object Button(string text, string data) => new { text, callback_data = data };
}

public sealed record PendingAdminAction(long ActorUserId, long ActorTelegramId, long TargetId, string Action, string Value, DateTime ExpiresAt);

public sealed class AdminActionTickets
{
    private readonly ConcurrentDictionary<string, PendingAdminAction> _tickets = new();
    public void Add(string ticket, PendingAdminAction action) => _tickets[ticket] = action;
    public bool TryConsume(string ticket, long telegramId, out PendingAdminAction? action)
    {
        action = null;
        if (!_tickets.TryGetValue(ticket, out var current) || current.ActorTelegramId != telegramId || current.ExpiresAt < DateTime.UtcNow) return false;
        if (!_tickets.TryRemove(new KeyValuePair<string, PendingAdminAction>(ticket, current))) return false;
        action = current;
        return true;
    }
    public bool TryCancel(string ticket, long telegramId)
    {
        if (!_tickets.TryGetValue(ticket, out var current) || current.ActorTelegramId != telegramId) return false;
        return _tickets.TryRemove(new KeyValuePair<string, PendingAdminAction>(ticket, current));
    }
}

internal static class PollingGate
{
    private static readonly ConcurrentDictionary<string, byte> Active = new();
    public static IDisposable? Acquire(string key) => Active.TryAdd(key, 0) ? new Releaser(key) : null;
    private sealed class Releaser(string key) : IDisposable { public void Dispose() => Active.TryRemove(key, out _); }
}
