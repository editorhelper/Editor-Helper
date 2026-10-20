using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EditorHelper.Services;

/// <summary>
/// Криптографический идентификатор устройства.
///
/// Генерируется один раз (256 бит криптослучайности, base64url) и хранится
/// в данных пользователя под Windows DPAPI (CurrentUser): файл, скопированный
/// на другую машину или другому пользователю системы, не расшифровывается.
/// IP, MAC и User-Agent не используются: они не уникальны, нестабильны
/// и подделываются.
///
/// Серверу отправляется только необратимый SHA-256 хеш идентификатора —
/// приватный ключ никогда не покидает устройство.
/// </summary>
public static class DeviceIdentity
{
    private static string KeyFile => Path.Combine(AppPaths.UserDataFolder, "device.key");

    private static string? _cachedId;

    /// <summary>Открытый идентификатор устройства: его хеш уходит на сервер.</summary>
    public static string GetDeviceId()
    {
        if (_cachedId != null) return _cachedId;

        _cachedId = TryLoad() ?? Create();
        return _cachedId;
    }

    /// <summary>Человекочитаемая метка устройства для отображения в боте.</summary>
    public static string GetDeviceLabel()
    {
        var machine = Environment.MachineName;
        var os = Environment.OSVersion.Version;
        return $"{machine} · Windows {os.Major}.{os.Minor}";
    }

    private static string? TryLoad()
    {
        try
        {
            if (!File.Exists(KeyFile)) return null;
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser);
            try
            {
                var id = Encoding.UTF8.GetString(clear).Trim();
                return id.Length is >= 16 and <= 128 ? id : null;
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch
        {
            // Файл повреждён или создан другой учётной записью Windows:
            // DPAPI его не расшифровал — создаём новый идентификатор
            return null;
        }
    }

    private static string Create()
    {
        var id = Base64Url(RandomNumberGenerator.GetBytes(32));
        try
        {
            Directory.CreateDirectory(AppPaths.UserDataFolder);
            var input = Encoding.UTF8.GetBytes(id);
            File.WriteAllBytes(KeyFile, ProtectedData.Protect(input, null, DataProtectionScope.CurrentUser));
            CryptographicOperations.ZeroMemory(input);
        }
        catch
        {
            // Если записать файл не удалось, идентификатор живёт только в памяти
            // процесса: привязка на сервере всё равно будет создана
        }
        return id;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
