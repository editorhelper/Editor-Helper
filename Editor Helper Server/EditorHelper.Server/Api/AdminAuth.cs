using System.Security.Cryptography;
using System.Text;

namespace EditorHelper.Server.Api;

/// <summary>
/// Проверка токена администратора. Сравнение с постоянным временем, чтобы
/// по длительности ответа нельзя было угадывать токен посимвольно.
/// </summary>
public static class AdminAuth
{
    public const string HeaderName = "X-Admin-Token";

    public static bool IsValid(HttpContext context, ServerConfig config)
    {
        if (string.IsNullOrEmpty(config.AdminToken)) return false;

        var provided = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(provided)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(config.AdminToken));
    }
}
