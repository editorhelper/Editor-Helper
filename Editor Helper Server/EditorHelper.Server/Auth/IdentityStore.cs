using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace EditorHelper.Server.Auth;

public sealed class IdentityStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public IdentityStore(ServerConfig config)
    {
        Directory.CreateDirectory(config.DataDir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(config.DataDir, "identity.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    telegram_id INTEGER NULL UNIQUE,
                    display_name TEXT NOT NULL,
                    role TEXT NOT NULL CHECK(role IN ('User','VIP','Owner')),
                    is_blocked INTEGER NOT NULL DEFAULT 0,
                    is_beta_tester INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    last_login_at TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS login_challenges (
                    request_id TEXT PRIMARY KEY,
                    code_hash TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    consumed_at TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS telegram_auth_challenges (
                    request_id TEXT PRIMARY KEY,
                    code_hash TEXT NOT NULL,
                    telegram_id INTEGER NULL,
                    username TEXT NULL,
                    first_name TEXT NULL,
                    bot_chat_id TEXT NULL,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    expires_at TEXT NOT NULL,
                    consumed_at TEXT NULL,
                    confirmed_at TEXT NULL,
                    declined_at TEXT NULL,
                    subscription_checked INTEGER NULL
                );
                CREATE INDEX IF NOT EXISTS ix_telegram_auth_challenges_expiry ON telegram_auth_challenges(expires_at);
                CREATE TABLE IF NOT EXISTS sessions (
                    token_hash TEXT PRIMARY KEY,
                    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    created_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    revoked_at TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_sessions_user_id ON sessions(user_id);
                CREATE TABLE IF NOT EXISTS favorites (
                    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    resource_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY(user_id, resource_id)
                );
                CREATE INDEX IF NOT EXISTS ix_favorites_user_id ON favorites(user_id);
                CREATE TABLE IF NOT EXISTS audit_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    actor_user_id INTEGER NULL,
                    action TEXT NOT NULL,
                    target TEXT NULL,
                    details TEXT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_audit_created_at ON audit_events(created_at DESC);
                CREATE TABLE IF NOT EXISTS devices (
                    user_id INTEGER PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
                    device_id_hash TEXT NOT NULL,
                    device_label TEXT NULL,
                    bound_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS device_unbind_attempts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    telegram_id INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_device_unbind_attempts ON device_unbind_attempts(telegram_id,created_at);
                """;
            await command.ExecuteNonQueryAsync(ct);
            await AddMissingColumnsAsync(connection, "telegram_auth_challenges", new[]
            {
                ("bot_chat_id", "TEXT NULL"),
                ("declined_at", "TEXT NULL")
            }, ct);
            // Бета-доступ: отдельный флаг, а не роль — ортогонален VIP/Owner
            // и не участвует в CHECK(role ...), поэтому старая база обновляется
            // простым ALTER без пересборки таблицы
            await AddMissingColumnsAsync(connection, "users", new[]
            {
                ("is_beta_tester", "INTEGER NOT NULL DEFAULT 0")
            }, ct);
            await UpgradeRoleSchemaAsync(connection, ct);
            _initialized = true;
        }
        finally { _initializeGate.Release(); }
    }

    /// <summary>
    /// Дописывает колонки, которых ещё нет в таблице: старые identity.db,
    /// созданные до появления bot_chat_id/declined_at, обновляются на месте,
    /// свежие — уже содержат их в CREATE TABLE, и ALTER пропускается.
    /// </summary>
    private static async Task AddMissingColumnsAsync(SqliteConnection connection, string table, (string Name, string Type)[] columns, CancellationToken ct)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await pragma.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) existing.Add(reader.GetString(1));
        }

        foreach (var (name, type) in columns)
        {
            if (existing.Contains(name)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {type}";
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Исторические схемы знали роли User/Admin/Owner и User/Admin/BetaTester/
    /// Creator. Текущая — ровно User/VIP/Owner: BetaTester становится VIP,
    /// Creator — Owner, упразднённый Admin понижается до User. Аккаунты,
    /// избранные и сессии сохраняются: SQLite позволяет table rename + copy
    /// внутри транзакции, данные не теряются.
    /// </summary>
    private static async Task UpgradeRoleSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        var needsUpgrade = false;
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='users'";
            var sql = await check.ExecuteScalarAsync(ct) as string;
            if (sql != null && !sql.Contains("CHECK(role IN ('User','VIP','Owner'))", StringComparison.OrdinalIgnoreCase))
                needsUpgrade = true;
        }
        if (!needsUpgrade) return;

        // favorites и sessions ссылаются на users c ON DELETE CASCADE, а
        // Microsoft.Data.Sqlite открывает соединение с foreign_keys=ON:
        // без этого PRAGMA DROP TABLE users унёс бы избранное и сессии.
        // Внутри транзакции PRAGMA не действует, поэтому ставим её до BEGIN.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=OFF;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using (var migrate = connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                migrate.CommandText = """
                    CREATE TABLE users_new (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        telegram_id INTEGER NULL UNIQUE,
                        display_name TEXT NOT NULL,
                        role TEXT NOT NULL CHECK(role IN ('User','VIP','Owner')),
                        is_blocked INTEGER NOT NULL DEFAULT 0,
                        is_beta_tester INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL,
                        last_login_at TEXT NULL
                    );
                    INSERT INTO users_new(id,telegram_id,display_name,role,is_blocked,is_beta_tester,created_at,last_login_at)
                        SELECT id,telegram_id,display_name,
                               CASE role WHEN 'BetaTester' THEN 'VIP'
                                         WHEN 'Creator' THEN 'Owner'
                                         WHEN 'Owner' THEN 'Owner'
                                         WHEN 'VIP' THEN 'VIP'
                                         ELSE 'User' END,
                               is_blocked,
                               CASE WHEN is_beta_tester IS NULL THEN 0 ELSE is_beta_tester END,
                               created_at,last_login_at FROM users;
                    DROP TABLE users;
                    ALTER TABLE users_new RENAME TO users;
                    """;
                await migrate.ExecuteNonQueryAsync(ct);
            }
            await using (var sessions = connection.CreateCommand())
            {
                // sessions держит FK на users: после пересоздания таблицы
                // пересоздаём индекс, foreign_keys в WAL-режиме не мешает
                sessions.Transaction = transaction;
                sessions.CommandText = "CREATE INDEX IF NOT EXISTS ix_sessions_user_id ON sessions(user_id);";
                await sessions.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<(long Id, UserRole Role)?> FindUserByTelegramIdAsync(long telegramId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, role FROM users WHERE telegram_id=$id";
        command.Parameters.AddWithValue("$id", telegramId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetInt64(0), Enum.Parse<UserRole>(reader.GetString(1)));
    }

    public async Task<long> CreateUserAsync(long? telegramId, string displayName, UserRole role, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO users(telegram_id,display_name,role,created_at) VALUES($telegram,$name,$role,$now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$telegram", (object?)telegramId ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", CleanDisplayName(displayName));
        command.Parameters.AddWithValue("$role", role.ToString());
        command.Parameters.AddWithValue("$now", Now());
        return (long)(await command.ExecuteScalarAsync(ct) ?? 0L);
    }

    /// <summary>
    /// Приводит configured Telegram ID к роли Owner и гарантирует, что других
    /// Owner в базе нет: роль владельца определяется конфигурацией сервера,
    /// а не выдаётся через API, поэтому посторонние Owner понижаются до User.
    /// </summary>
    public async Task<long?> EnsureConfiguredOwnerAsync(long telegramId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var demote = connection.CreateCommand())
        {
            demote.Transaction = (SqliteTransaction)transaction;
            demote.CommandText = "UPDATE users SET role='User' WHERE role='Owner' AND (telegram_id IS NULL OR telegram_id!=$telegram)";
            demote.Parameters.AddWithValue("$telegram", telegramId);
            var demoted = await demote.ExecuteNonQueryAsync(ct);
            if (demoted > 0)
                await AddAuditAsync(connection, (SqliteTransaction)transaction, null, "owner.others.demoted", telegramId.ToString(), demoted.ToString(), ct);
        }

        await using (var existingOwner = connection.CreateCommand())
        {
            existingOwner.Transaction = (SqliteTransaction)transaction;
            existingOwner.CommandText = "SELECT id FROM users WHERE role='Owner' AND telegram_id=$telegram LIMIT 1";
            existingOwner.Parameters.AddWithValue("$telegram", telegramId);
            var idValue = await existingOwner.ExecuteScalarAsync(ct);
            if (idValue != null)
            {
                await transaction.CommitAsync(ct);
                return (long)idValue;
            }
        }

        await using var bound = connection.CreateCommand();
        bound.Transaction = (SqliteTransaction)transaction;
        bound.CommandText = "SELECT id,role FROM users WHERE telegram_id=$telegram LIMIT 1";
        bound.Parameters.AddWithValue("$telegram", telegramId);
        await using var reader = await bound.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            reader.Close();
            await using var promote = connection.CreateCommand();
            promote.Transaction = (SqliteTransaction)transaction;
            promote.CommandText = "UPDATE users SET role='Owner',is_blocked=0 WHERE id=$id";
            promote.Parameters.AddWithValue("$id", id);
            await promote.ExecuteNonQueryAsync(ct);
            await AddAuditAsync(connection, (SqliteTransaction)transaction, id, "owner.configured", id.ToString(), null, ct);
            await transaction.CommitAsync(ct);
            return id;
        }
        reader.Close();
        await using var create = connection.CreateCommand();
        create.Transaction = (SqliteTransaction)transaction;
        create.CommandText = "INSERT INTO users(telegram_id,display_name,role,created_at) VALUES($telegram,$name,'Owner',$now); SELECT last_insert_rowid();";
        create.Parameters.AddWithValue("$telegram", telegramId);
        create.Parameters.AddWithValue("$name", $"Owner {telegramId}");
        create.Parameters.AddWithValue("$now", Now());
        var newId = (long)(await create.ExecuteScalarAsync(ct) ?? 0L);
        await AddAuditAsync(connection, (SqliteTransaction)transaction, newId, "owner.configured", newId.ToString(), null, ct);
        await transaction.CommitAsync(ct);
        return newId;
    }

    public async Task<(bool Created, long? UserId)> BootstrapOwnerAsync(string displayName, long? telegramId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var count = connection.CreateCommand();
        count.Transaction = (SqliteTransaction)transaction;
        count.CommandText = "SELECT COUNT(*) FROM users WHERE role='Owner'";
        if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) > 0)
            return (false, null);

        await using var create = connection.CreateCommand();
        create.Transaction = (SqliteTransaction)transaction;
        create.CommandText = "INSERT INTO users(telegram_id,display_name,role,created_at) VALUES($telegram,$name,'Owner',$now); SELECT last_insert_rowid();";
        create.Parameters.AddWithValue("$telegram", (object?)telegramId ?? DBNull.Value);
        create.Parameters.AddWithValue("$name", CleanDisplayName(displayName));
        create.Parameters.AddWithValue("$now", Now());
        var id = (long)(await create.ExecuteScalarAsync(ct) ?? 0L);
        await AddAuditAsync(connection, (SqliteTransaction)transaction, id, "owner.bootstrap", id.ToString(), "Development bootstrap", ct);
        await transaction.CommitAsync(ct);
        return (true, id);
    }

    public async Task<TelegramAuthChallenge> StartTelegramAuthAsync(TimeSpan lifetime, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var expires = DateTime.UtcNow.Add(lifetime);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO telegram_auth_challenges(request_id,code_hash,expires_at) VALUES($id,$hash,$expires)";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$hash", Hash("EH-" + code));
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        return new TelegramAuthChallenge(requestId, "EH-" + code, expires);
    }

    /// <summary>
    /// Бот подтвердил попытку входа кнопкой «Да, войти»: привязываем Telegram ID
    /// пользователя к этой попытке. Ключ — request_id из deep-link (?start=...),
    /// поэтому подтвердить можно только ту сессию, которую инициировал клиент,
    /// и только пока она не израсходована и не истекла.
    /// </summary>
    public async Task<bool> ConfirmTelegramChallengeAsync(string requestId, long telegramId, string? username, string? firstName, string? chatId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE telegram_auth_challenges SET telegram_id=$telegram,username=$username,first_name=$firstName,confirmed_at=$now,subscription_checked=$subscribed,bot_chat_id=$chat WHERE request_id=$id AND consumed_at IS NULL AND confirmed_at IS NULL AND declined_at IS NULL AND expires_at>$now";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$telegram", telegramId);
        command.Parameters.AddWithValue("$username", (object?)username ?? DBNull.Value);
        command.Parameters.AddWithValue("$firstName", (object?)firstName ?? DBNull.Value);
        command.Parameters.AddWithValue("$chat", (object?)chatId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$subscribed", 0);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> MarkTelegramChallengeSubscribedAsync(string requestId, long telegramId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE telegram_auth_challenges SET subscription_checked=1 WHERE request_id=$id AND telegram_id=$telegram AND consumed_at IS NULL AND confirmed_at IS NOT NULL AND expires_at>$now";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$telegram", telegramId);
        command.Parameters.AddWithValue("$now", Now());
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> IsTelegramChallengeConfirmedAsync(string requestId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT confirmed_at IS NOT NULL AND consumed_at IS NULL AND expires_at>$now FROM telegram_auth_challenges WHERE request_id=$id";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$now", Now());
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct) ?? 0) == 1;
    }

    public async Task<TelegramAuthChallenge?> GetConfirmedTelegramChallengeAsync(string requestId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_id,telegram_id,expires_at,bot_chat_id FROM telegram_auth_challenges WHERE request_id=$id AND consumed_at IS NULL AND confirmed_at IS NOT NULL AND expires_at>$now AND declined_at IS NULL";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$now", Now());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(1)) return null;
        return new TelegramAuthChallenge(reader.GetString(0), null, DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind))
        {
            TelegramId = reader.GetInt64(1),
            BotChatId = reader.IsDBNull(3) ? null : reader.GetString(3)
        };
    }

    /// <summary>
    /// Завершает Telegram-вход и проверяет привязку устройства:
    ///  — deviceHash пуст (клиент без device identity, например старые сборки
    ///    или dev-вход): привязка не проверяется и не создаётся, вход как раньше;
    ///  — аккаунт не привязан: первое устройство привязывается автоматически;
    ///  — привязан к этому же устройству: обновляется last_seen, вход разрешён;
    ///  — привязан к другому устройству: вход отклоняется, возвращается
    ///    текущая привязка (Conflict) — сессия не создаётся.
    /// </summary>
    public async Task<TelegramAuthOutcome> CompleteTelegramAuthAsync(string requestId, long? configuredOwnerTelegramId = null, string? deviceHash = null, string? deviceLabel = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        long telegramId;
        string? username;
        string? firstName;
        await using (var challenge = connection.CreateCommand())
        {
            challenge.Transaction = (SqliteTransaction)transaction;
            challenge.CommandText = "SELECT telegram_id,username,first_name FROM telegram_auth_challenges WHERE request_id=$id AND consumed_at IS NULL AND confirmed_at IS NOT NULL AND declined_at IS NULL AND expires_at>$now";
            challenge.Parameters.AddWithValue("$id", requestId);
            challenge.Parameters.AddWithValue("$now", Now());
            await using var reader = await challenge.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return TelegramAuthOutcome.Failed;
            telegramId = reader.GetInt64(0);
            username = reader.IsDBNull(1) ? null : reader.GetString(1);
            firstName = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = (SqliteTransaction)transaction;
            consume.CommandText = "UPDATE telegram_auth_challenges SET consumed_at=$now WHERE request_id=$id AND consumed_at IS NULL";
            consume.Parameters.AddWithValue("$now", Now());
            consume.Parameters.AddWithValue("$id", requestId);
            if (await consume.ExecuteNonQueryAsync(ct) != 1) return TelegramAuthOutcome.Failed;
        }

        var user = await FindUserByTelegramIdAsync(telegramId, ct);
        AuthUser authUser;
        if (user == null)
        {
            var displayName = !string.IsNullOrWhiteSpace(username) ? $"@{username}" : (!string.IsNullOrWhiteSpace(firstName) ? firstName : $"Telegram {telegramId}");
            var assignedRole = configuredOwnerTelegramId == telegramId ? UserRole.Owner : UserRole.User;
            var userId = await FindOrCreateTelegramUserAsync(connection, (SqliteTransaction)transaction, telegramId, displayName, assignedRole, ct);
            authUser = (await GetUserAsync(connection, (SqliteTransaction)transaction, userId, ct))!;
        }
        else
        {
            authUser = (await GetUserAsync(connection, (SqliteTransaction)transaction, user.Value.Id, ct))!;
        }
        if (authUser.IsBlocked) return TelegramAuthOutcome.Failed;

        // --- Проверка привязки устройства (внутри той же транзакции, что и сессия)
        if (!string.IsNullOrWhiteSpace(deviceHash))
        {
            var binding = await GetDeviceBindingAsync(connection, (SqliteTransaction)transaction, authUser.Id, ct);
            if (binding == null)
            {
                await BindDeviceAsync(connection, (SqliteTransaction)transaction, authUser.Id, deviceHash!, CleanDeviceLabel(deviceLabel), ct);
                await AddAuditAsync(connection, (SqliteTransaction)transaction, authUser.Id, "device.bound", telegramId.ToString(), null, ct);
            }
            else if (!string.Equals(binding.DeviceIdHash, deviceHash, StringComparison.Ordinal))
            {
                await AddAuditAsync(connection, (SqliteTransaction)transaction, authUser.Id, "device.conflict", telegramId.ToString(), binding.DeviceLabel, ct);
                await transaction.CommitAsync(ct);
                return new TelegramAuthOutcome(null, binding);
            }
            else
            {
                await using var seen = connection.CreateCommand();
                seen.Transaction = (SqliteTransaction)transaction;
                seen.CommandText = "UPDATE devices SET last_seen_at=$now, device_label=COALESCE($label, device_label) WHERE user_id=$user";
                seen.Parameters.AddWithValue("$now", Now());
                seen.Parameters.AddWithValue("$label", (object?)CleanDeviceLabel(deviceLabel) ?? DBNull.Value);
                seen.Parameters.AddWithValue("$user", authUser.Id);
                await seen.ExecuteNonQueryAsync(ct);
            }
        }

        var session = await CreateSessionAsync(connection, (SqliteTransaction)transaction, authUser, ct);
        await AddAuditAsync(connection, (SqliteTransaction)transaction, authUser.Id, "telegram.auth.success", telegramId.ToString(), null, ct);
        await transaction.CommitAsync(ct);
        return new TelegramAuthOutcome(session, null);
    }

    // ---------------------------------------------------------------- Привязка устройств

    public async Task<DeviceBinding?> GetDeviceAsync(long userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        return await GetDeviceBindingAsync(connection, null, userId, ct);
    }

    /// <summary>
    /// Отвязка устройства по серверно-подтверждённому Telegram ID. Актор —
    /// всегда сам владелец аккаунта: telegramId берётся из апдейта Telegram,
    /// проверенного сервером, а не из данных клиента. Все сессии аккаунта
    /// инвалидируются: после отвязки вход возможен с нового устройства.
    /// Rate limiting: не чаще трёх отвязок в час на один Telegram ID.
    /// </summary>
    public async Task<(bool Success, string Message)> UnbindDeviceAsync(long telegramId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Rate limiting по количеству недавних попыток отвязки
        await using (var trim = connection.CreateCommand())
        {
            trim.Transaction = (SqliteTransaction)transaction;
            trim.CommandText = "DELETE FROM device_unbind_attempts WHERE created_at<$cutoff";
            trim.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddHours(-24).ToString("O"));
            await trim.ExecuteNonQueryAsync(ct);
        }
        await using (var recent = connection.CreateCommand())
        {
            recent.Transaction = (SqliteTransaction)transaction;
            recent.CommandText = "SELECT COUNT(*) FROM device_unbind_attempts WHERE telegram_id=$tg AND created_at>$since";
            recent.Parameters.AddWithValue("$tg", telegramId);
            recent.Parameters.AddWithValue("$since", DateTime.UtcNow.AddHours(-1).ToString("O"));
            if (Convert.ToInt32(await recent.ExecuteScalarAsync(ct)) >= 3)
            {
                await transaction.CommitAsync(ct);
                return (false, "Слишком много попыток отвязки. Повторите через час.");
            }
        }
        await using (var attempt = connection.CreateCommand())
        {
            attempt.Transaction = (SqliteTransaction)transaction;
            attempt.CommandText = "INSERT INTO device_unbind_attempts(telegram_id,created_at) VALUES($tg,$now)";
            attempt.Parameters.AddWithValue("$tg", telegramId);
            attempt.Parameters.AddWithValue("$now", Now());
            await attempt.ExecuteNonQueryAsync(ct);
        }

        await using var find = connection.CreateCommand();
        find.Transaction = (SqliteTransaction)transaction;
        find.CommandText = "SELECT id FROM users WHERE telegram_id=$tg";
        find.Parameters.AddWithValue("$tg", telegramId);
        if (await find.ExecuteScalarAsync(ct) is not long userId)
        {
            await transaction.CommitAsync(ct);
            return (false, "Аккаунт не найден.");
        }

        var binding = await GetDeviceBindingAsync(connection, (SqliteTransaction)transaction, userId, ct);
        if (binding == null)
        {
            await transaction.CommitAsync(ct);
            return (false, "К аккаунту не привязано ни одно устройство.");
        }

        await using (var unbind = connection.CreateCommand())
        {
            unbind.Transaction = (SqliteTransaction)transaction;
            unbind.CommandText = "DELETE FROM devices WHERE user_id=$user";
            unbind.Parameters.AddWithValue("$user", userId);
            await unbind.ExecuteNonQueryAsync(ct);
        }
        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = (SqliteTransaction)transaction;
            revoke.CommandText = "UPDATE sessions SET revoked_at=$now WHERE user_id=$user AND revoked_at IS NULL";
            revoke.Parameters.AddWithValue("$now", Now());
            revoke.Parameters.AddWithValue("$user", userId);
            await revoke.ExecuteNonQueryAsync(ct);
        }
        await AddAuditAsync(connection, (SqliteTransaction)transaction, userId, "device.unbound", telegramId.ToString(), binding.DeviceLabel, ct);
        await transaction.CommitAsync(ct);
        return (true, "Устройство отвязано. Сессии закрыты — теперь можно войти с другого устройства.");
    }

    /// <summary>Служебная отвязка без сессий владельца: для админ-операций Owner.</summary>
    public async Task<(bool Success, string Message)> UnbindDeviceByOwnerAsync(long actorId, long targetId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var actor = await GetUserAsync(connection, (SqliteTransaction)transaction, actorId, ct);
        if (actor?.Role != UserRole.Owner)
        {
            await transaction.CommitAsync(ct);
            return (false, "Недостаточно прав.");
        }
        var binding = await GetDeviceBindingAsync(connection, (SqliteTransaction)transaction, targetId, ct);
        if (binding == null)
        {
            await transaction.CommitAsync(ct);
            return (false, "У пользователя нет привязанного устройства.");
        }
        await using (var unbind = connection.CreateCommand())
        {
            unbind.Transaction = (SqliteTransaction)transaction;
            unbind.CommandText = "DELETE FROM devices WHERE user_id=$user";
            unbind.Parameters.AddWithValue("$user", targetId);
            await unbind.ExecuteNonQueryAsync(ct);
        }
        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = (SqliteTransaction)transaction;
            revoke.CommandText = "UPDATE sessions SET revoked_at=$now WHERE user_id=$user AND revoked_at IS NULL";
            revoke.Parameters.AddWithValue("$now", Now());
            revoke.Parameters.AddWithValue("$user", targetId);
            await revoke.ExecuteNonQueryAsync(ct);
        }
        await AddAuditAsync(connection, (SqliteTransaction)transaction, actorId, "device.unbound.by_owner", targetId.ToString(), binding.DeviceLabel, ct);
        await transaction.CommitAsync(ct);
        return (true, "Устройство пользователя отвязано, сессии закрыты.");
    }

    private static async Task BindDeviceAsync(SqliteConnection connection, SqliteTransaction? transaction, long userId, string deviceHash, string? label, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO devices(user_id,device_id_hash,device_label,bound_at,last_seen_at)
            VALUES($user,$hash,$label,$now,$now)
            ON CONFLICT(user_id) DO UPDATE SET device_id_hash=$hash, device_label=$label, bound_at=$now, last_seen_at=$now
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$hash", deviceHash);
        command.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DeviceBinding?> GetDeviceBindingAsync(SqliteConnection connection, SqliteTransaction? transaction, long userId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT user_id,device_id_hash,device_label,bound_at,last_seen_at FROM devices WHERE user_id=$user";
        command.Parameters.AddWithValue("$user", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new DeviceBinding(
            reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    /// <summary>Метка устройства приходит из клиента: режем длину и управляющие символы.</summary>
    private static string? CleanDeviceLabel(string? value)
    {
        var label = new string((value ?? "").Where(c => !char.IsControl(c)).Take(120).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(label) ? null : label;
    }

    public async Task CancelTelegramChallengeAsync(string requestId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE telegram_auth_challenges SET consumed_at=$now WHERE request_id=$id AND consumed_at IS NULL";
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$id", requestId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Пользователь нажал «Нет, отмена» в боте. Отменить можно только свою
    /// попытку: telegram_id уже привязан нажатием «Да» либо ещё не задан
    /// (до подтверждения кнопкой), чужую попытку UPDATE не тронет.
    /// declined_at оставляет попытку видимой для status-эндпоинта, но
    /// блокирует подтверждение и завершение.
    /// </summary>
    public async Task<bool> DeclineTelegramChallengeAsync(string requestId, long telegramId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE telegram_auth_challenges SET declined_at=$now WHERE request_id=$id AND consumed_at IS NULL AND declined_at IS NULL AND (telegram_id=$telegram OR telegram_id IS NULL) AND expires_at>$now";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$telegram", telegramId);
        command.Parameters.AddWithValue("$now", Now());
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<TelegramAuthChallenge?> GetTelegramChallengeAsync(string requestId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT request_id,code_hash,telegram_id,expires_at,bot_chat_id,declined_at FROM telegram_auth_challenges WHERE request_id=$id AND consumed_at IS NULL AND expires_at>$now";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$now", Now());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new TelegramAuthChallenge(reader.GetString(0), null, DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind))
        {
            TelegramId = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
            BotChatId = reader.IsDBNull(4) ? null : reader.GetString(4),
            Declined = !reader.IsDBNull(5)
        };
    }

    private static async Task<long> FindOrCreateTelegramUserAsync(SqliteConnection connection, SqliteTransaction transaction, long telegramId, string displayName, UserRole role, CancellationToken ct)
    {
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT id FROM users WHERE telegram_id=$id";
        find.Parameters.AddWithValue("$id", telegramId);
        var existing = await find.ExecuteScalarAsync(ct);
        if (existing != null) return (long)existing;
        await using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = "INSERT INTO users(telegram_id,display_name,role,created_at) VALUES($telegram,$name,$role,$now); SELECT last_insert_rowid();";
        create.Parameters.AddWithValue("$telegram", telegramId);
        create.Parameters.AddWithValue("$role", role.ToString());
        create.Parameters.AddWithValue("$name", CleanDisplayName(displayName));
        create.Parameters.AddWithValue("$now", Now());
        return (long)(await create.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<LoginStartResult> StartDevelopmentLoginAsync(string displayName, TimeSpan lifetime, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var expires = DateTime.UtcNow.Add(lifetime);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO login_challenges(request_id,code_hash,display_name,expires_at) VALUES($id,$hash,$name,$expires)";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$hash", Hash(code));
        command.Parameters.AddWithValue("$name", CleanDisplayName(displayName));
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        return new LoginStartResult(requestId, code, expires, true);
    }

    public async Task<LoginResult?> CompleteDevelopmentLoginAsync(string requestId, string code, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        string name;
        await using (var challenge = connection.CreateCommand())
        {
            challenge.Transaction = (SqliteTransaction)transaction;
            challenge.CommandText = "SELECT display_name FROM login_challenges WHERE request_id=$id AND code_hash=$hash AND consumed_at IS NULL AND expires_at>$now";
            challenge.Parameters.AddWithValue("$id", requestId);
            challenge.Parameters.AddWithValue("$hash", Hash(code));
            challenge.Parameters.AddWithValue("$now", Now());
            var value = await challenge.ExecuteScalarAsync(ct);
            if (value is not string foundName) return null;
            name = foundName;
        }

        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = (SqliteTransaction)transaction;
            consume.CommandText = "UPDATE login_challenges SET consumed_at=$now WHERE request_id=$id AND consumed_at IS NULL";
            consume.Parameters.AddWithValue("$now", Now());
            consume.Parameters.AddWithValue("$id", requestId);
            if (await consume.ExecuteNonQueryAsync(ct) != 1) return null;
        }

        var userId = await FindOrCreateLocalUserAsync(connection, (SqliteTransaction)transaction, name, ct);
        var user = await GetUserAsync(connection, (SqliteTransaction)transaction, userId, ct);
        if (user == null || user.IsBlocked) return null;
        var session = await CreateSessionAsync(connection, (SqliteTransaction)transaction, user, ct);
        await transaction.CommitAsync(ct);
        return session;
    }

    private static async Task<long> FindOrCreateLocalUserAsync(SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken ct)
    {
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT id FROM users WHERE telegram_id IS NULL AND display_name=$name LIMIT 1";
        find.Parameters.AddWithValue("$name", name);
        var existing = await find.ExecuteScalarAsync(ct);
        if (existing != null) return (long)existing;
        await using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = "INSERT INTO users(display_name,role,created_at) VALUES($name,'User',$now); SELECT last_insert_rowid();";
        create.Parameters.AddWithValue("$name", name);
        create.Parameters.AddWithValue("$now", Now());
        return (long)(await create.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<AuthSession?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT u.id,u.telegram_id,u.display_name,u.role,u.is_blocked,u.is_beta_tester,u.created_at,u.last_login_at,s.expires_at FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=$hash AND s.revoked_at IS NULL AND s.expires_at>$now";
        command.Parameters.AddWithValue("$hash", Hash(token));
        command.Parameters.AddWithValue("$now", Now());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetInt32(4) != 0) return null;
        var user = ReadUser(reader);
        return new AuthSession(token, user, DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public async Task<LoginResult> CreateLocalDevelopmentSessionAsync(string displayName, TimeSpan lifetime, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var id = await FindOrCreateLocalUserAsync(connection, (SqliteTransaction)transaction, CleanDisplayName(displayName), ct);
        var user = await GetUserAsync(connection, (SqliteTransaction)transaction, id, ct) ?? throw new InvalidOperationException("User creation failed.");
        var result = await CreateSessionAsync(connection, (SqliteTransaction)transaction, user, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static async Task<LoginResult> CreateSessionAsync(SqliteConnection connection, SqliteTransaction transaction, AuthUser user, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expires = DateTime.UtcNow.AddHours(12);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO sessions(token_hash,user_id,created_at,expires_at) VALUES($hash,$user,$now,$expires); UPDATE users SET last_login_at=$now WHERE id=$user;";
        command.Parameters.AddWithValue("$hash", Hash(token));
        command.Parameters.AddWithValue("$user", user.Id);
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        user = user with { LastLoginAt = DateTime.UtcNow };
        return new LoginResult(token, expires, user);
    }

    public async Task RevokeSessionAsync(string token, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET revoked_at=$now WHERE token_hash=$hash AND revoked_at IS NULL";
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$hash", Hash(token));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetFavoritesAsync(long userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT resource_id FROM favorites WHERE user_id=$user ORDER BY created_at DESC";
        command.Parameters.AddWithValue("$user", userId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<bool> AddFavoriteAsync(long userId, string resourceId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO favorites(user_id,resource_id,created_at) VALUES($user,$resource,$now)";
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$resource", resourceId);
        command.Parameters.AddWithValue("$now", Now());
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> RemoveFavoriteAsync(long userId, string resourceId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM favorites WHERE user_id=$user AND resource_id=$resource";
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$resource", resourceId);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<AuthUser>> ListUsersAsync(string? query = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,telegram_id,display_name,role,is_blocked,is_beta_tester,created_at,last_login_at FROM users WHERE ($query IS NULL OR CAST(telegram_id AS TEXT)=$query OR display_name LIKE '%' || $query || '%') ORDER BY id LIMIT 200";
        command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);
        var result = new List<AuthUser>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadUser(reader));
        return result;
    }

    /// <summary>
    /// Смена роли. Доступна только Owner и только для назначения User или VIP:
    /// роль Owner выдаётся конфигурацией сервера по Telegram ID и через этот
    /// метод не присваивается. Целью не может быть Owner (роль владельца
    /// неприкосновенна), а также собственный аккаунт.
    /// </summary>
    public async Task<(bool Success, string Message)> ChangeRoleAsync(long actorId, long targetId, UserRole role, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var actor = await GetUserAsync(connection, (SqliteTransaction)transaction, actorId, ct);
        var target = await GetUserAsync(connection, (SqliteTransaction)transaction, targetId, ct);
        if (actor?.Role != UserRole.Owner) return (false, "Только Owner может менять роли.");
        if (target == null) return (false, "Пользователь не найден.");
        if (role is not (UserRole.User or UserRole.VIP)) return (false, "Можно назначить только User или VIP.");
        if (actorId == targetId) return (false, "Нельзя менять собственную роль.");
        if (target.Role == UserRole.Owner) return (false, "Роль Owner не изменяется.");
        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = "UPDATE users SET role=$role WHERE id=$id";
        update.Parameters.AddWithValue("$role", role.ToString());
        update.Parameters.AddWithValue("$id", targetId);
        await update.ExecuteNonQueryAsync(ct);
        await AddAuditAsync(connection, (SqliteTransaction)transaction, actorId, "user.role.changed", targetId.ToString(), role.ToString(), ct);
        await transaction.CommitAsync(ct);
        return (true, "Роль обновлена.");
    }

    /// <summary>
    /// Выдача и отзыв доступа к бета-каналу обновлений. Только Owner; цель —
    /// любой пользователь, включая самого себя (владельцу тестировать бету
    /// можно). Отзыв не удаляет установленную бета-версию — клиент сам
    /// предложит вернуться на Stable, — но новые бета-обновления выдаваться
    /// перестанут.
    /// </summary>
    public async Task<(bool Success, string Message)> ChangeBetaAccessAsync(long actorId, long targetId, bool allowed, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var actor = await GetUserAsync(connection, (SqliteTransaction)transaction, actorId, ct);
        var target = await GetUserAsync(connection, (SqliteTransaction)transaction, targetId, ct);
        if (actor?.Role != UserRole.Owner) return (false, "Только Owner может управлять бета-доступом.");
        if (target == null) return (false, "Пользователь не найден.");

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = "UPDATE users SET is_beta_tester=$beta WHERE id=$id";
        update.Parameters.AddWithValue("$beta", allowed ? 1 : 0);
        update.Parameters.AddWithValue("$id", targetId);
        await update.ExecuteNonQueryAsync(ct);
        await AddAuditAsync(connection, (SqliteTransaction)transaction, actorId,
            allowed ? "beta.access.granted" : "beta.access.revoked",
            targetId.ToString(), null, ct);
        await transaction.CommitAsync(ct);
        return (true, allowed ? "Доступ к бета-каналу выдан." : "Доступ к бета-каналу отозван.");
    }

    public async Task<(bool Success, string Message)> SetBlockedAsync(long actorId, long targetId, bool blocked, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var actor = await GetUserAsync(connection, (SqliteTransaction)transaction, actorId, ct);
        var target = await GetUserAsync(connection, (SqliteTransaction)transaction, targetId, ct);
        if (actor?.Role != UserRole.Owner) return (false, "Недостаточно прав.");
        if (target == null) return (false, "Пользователь не найден.");
        if (target.Id == actorId) return (false, "Нельзя блокировать собственный аккаунт.");
        if (target.Role == UserRole.Owner) return (false, "Недостаточно прав для этого пользователя.");
        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = "UPDATE users SET is_blocked=$blocked WHERE id=$id; UPDATE sessions SET revoked_at=$now WHERE user_id=$id AND revoked_at IS NULL";
        update.Parameters.AddWithValue("$blocked", blocked ? 1 : 0);
        update.Parameters.AddWithValue("$id", targetId);
        update.Parameters.AddWithValue("$now", Now());
        await update.ExecuteNonQueryAsync(ct);
        await AddAuditAsync(connection, (SqliteTransaction)transaction, actorId, blocked ? "user.blocked" : "user.unblocked", targetId.ToString(), null, ct);
        await transaction.CommitAsync(ct);
        return (true, blocked ? "Пользователь заблокирован." : "Пользователь разблокирован.");
    }

    public async Task<(bool Success, string Message)> RevokeAllSessionsAsync(long actorId, long targetId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var actor = await GetUserAsync(connection, (SqliteTransaction)transaction, actorId, ct);
        var target = await GetUserAsync(connection, (SqliteTransaction)transaction, targetId, ct);
        if (actor?.Role != UserRole.Owner) return (false, "Недостаточно прав.");
        if (target == null) return (false, "Пользователь не найден.");
        await using var revoke = connection.CreateCommand();
        revoke.Transaction = (SqliteTransaction)transaction;
        revoke.CommandText = "UPDATE sessions SET revoked_at=$now WHERE user_id=$target AND revoked_at IS NULL";
        revoke.Parameters.AddWithValue("$now", Now());
        revoke.Parameters.AddWithValue("$target", targetId);
        var count = await revoke.ExecuteNonQueryAsync(ct);
        await AddAuditAsync(connection, (SqliteTransaction)transaction, actorId, "user.sessions.revoked", targetId.ToString(), count.ToString(), ct);
        await transaction.CommitAsync(ct);
        return (true, $"Отозвано сессий: {count}.");
    }

    public async Task<AuthUser?> FindUserAsync(string key, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,telegram_id,display_name,role,is_blocked,is_beta_tester,created_at,last_login_at FROM users WHERE CAST(id AS TEXT)=$key OR CAST(telegram_id AS TEXT)=$key LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAuditAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,actor_user_id,action,target,details,created_at FROM audit_events ORDER BY id DESC LIMIT 200";
        var result = new List<AuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new AuditEvent(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return result;
    }

    public async Task AddAuditAsync(long? actorId, string action, string? target = null, string? details = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var connection = await OpenAsync(ct);
        await AddAuditAsync(connection, null, actorId, action, target, details, ct);
    }

    private static async Task AddAuditAsync(SqliteConnection connection, SqliteTransaction? transaction, long? actorId, string action, string? target, string? details, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO audit_events(actor_user_id,action,target,details,created_at) VALUES($actor,$action,$target,$details,$now)";
        command.Parameters.AddWithValue("$actor", (object?)actorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$target", (object?)target ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<AuthUser?> GetUserAsync(SqliteConnection connection, SqliteTransaction transaction, long id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,telegram_id,display_name,role,is_blocked,is_beta_tester,created_at,last_login_at FROM users WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUser(reader) : null;
    }

    private static AuthUser ReadUser(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2),
        Enum.Parse<UserRole>(reader.GetString(3)), reader.GetInt32(4) != 0,
        DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind))
    {
        IsBetaTester = BetaFlag(reader, 5)
    };

    /// <summary>
    /// is_beta_tester появился позже остальных колонок: старая база после
    /// ALTER содержит его, но при расширении запросов читатель может попасть
    /// на SELECT без колонки. NULL трактуется как «нет доступа» — так миграция
    /// не даёт никому бета-доступ по умолчанию.
    /// </summary>
    private static bool BetaFlag(SqliteDataReader reader, int ordinal) =>
        reader.FieldCount > ordinal && !reader.IsDBNull(ordinal) && reader.GetInt32(ordinal) != 0;

    private static string CleanDisplayName(string? value)
    {
        var name = new string((value ?? "Local user").Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(name) ? "Local user" : name;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Now() => DateTime.UtcNow.ToString("O");
}
