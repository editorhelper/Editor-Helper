using System.Text.Json.Serialization;

namespace EditorHelper.Server.Auth;

/// <summary>
/// Ровно три роли: User (по умолчанию), VIP (статус и бейдж) и Owner
/// (полный доступ, назначается только по Telegram ID из конфигурации сервера).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserRole
{
    User,
    VIP,
    Owner
}

public sealed record AuthUser(long Id, long? TelegramId, string DisplayName, UserRole Role, bool IsBlocked, DateTime CreatedAt, DateTime? LastLoginAt)
{
    /// <summary>
    /// Разрешение на бета-канал обновлений. Выдаётся только Owner через
    /// ChangeBetaAccessAsync; сериализуется в JSON сессии, поэтому поле
    /// видит и клиент — но только как ответ сервера, сам себе его назначить
    /// он не может.
    /// </summary>
    public bool IsBetaTester { get; init; }
}
public sealed record AuthSession(string Token, AuthUser User, DateTime ExpiresAt);
public sealed record TelegramAuthChallenge(string RequestId, string? Code, DateTime ExpiresAt)
{
    public long TelegramId { get; init; }
    public string? BotChatId { get; init; }
    public bool Declined { get; init; }
}

/// <summary>
/// Привязка аккаунта к устройству. DeviceIdHash — SHA-256 идентификатора
/// устройства: даже при утечке базы сопоставить запись с конкретным
/// устройством нельзя.
/// </summary>
public sealed record DeviceBinding(long UserId, string DeviceIdHash, string? DeviceLabel, DateTime BoundAt, DateTime LastSeenAt);

/// <summary>
/// Итог завершения Telegram-входа: сессия, либо конфликт привязки
/// (аккаунт уже привязан к другому устройству), либо отказ.
/// </summary>
public sealed record TelegramAuthOutcome(LoginResult? Session, DeviceBinding? Conflict)
{
    public static TelegramAuthOutcome Failed { get; } = new(null, null);
}
public sealed record LoginStartResult(string RequestId, string Code, DateTime ExpiresAt, bool DevelopmentOnly);
public sealed record LoginResult(string Token, DateTime ExpiresAt, AuthUser User);
public sealed record AuditEvent(long Id, long? ActorUserId, string Action, string? Target, string? Details, DateTime CreatedAt);
