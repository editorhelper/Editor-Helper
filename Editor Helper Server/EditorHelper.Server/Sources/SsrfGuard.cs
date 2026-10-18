using System.Net;
using System.Net.Sockets;

namespace EditorHelper.Server.Sources;

/// <summary>
/// Проверка внешних ссылок на SSRF: сервер не должен ходить на localhost,
/// во внутренние сети и на служебные адреса — ни напрямую, ни через
/// перенаправления. Проверяем и литерал, и все адреса, в которые разрешается
/// доменное имя.
/// </summary>
public static class SsrfGuard
{
    /// <summary>Быстрая проверка URL без сети — для валидации ввода в Admin.</summary>
    public static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Пустая ссылка.";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "Ссылка не разбирается как абсолютный URL.";

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return $"Схема {uri.Scheme} не поддерживается — только http и https.";

        if (uri.UserInfo.Length > 0)
            return "Ссылки с логином/паролем в адресе (user@host) запрещены.";

        // localhost — то же самое, что 127.0.0.1, только буквами
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "Адрес localhost относится к запрещённым диапазонам.";

        if (IPAddress.TryParse(uri.Host, out var literal) && IsBlockedAddress(literal))
            return $"Адрес {uri.Host} относится к запрещённым диапазонам.";

        return null;
    }

    /// <summary>
    /// Только синтаксис: абсолютный URL, http/https, без user@host. Диапазоны
    /// адресов не проверяются. Нужен тестовому режиму, где «внешний» источник
    /// поднят на localhost и блокировать его нельзя (см. ServerConfig.AllowLocalExternalUrls).
    /// </summary>
    public static string? ValidateUrlSyntaxOnly(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Пустая ссылка.";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "Ссылка не разбирается как абсолютный URL.";

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return $"Схема {uri.Scheme} не поддерживается — только http и https.";

        if (uri.UserInfo.Length > 0)
            return "Ссылки с логином/паролем в адресе (user@host) запрещены.";

        return null;
    }

    /// <summary>
    /// Полная проверка с разрешением DNS: домен может указывать на внутренний
    /// адрес (DNS rebinding), поэтому литерала в URL недостаточно.
    /// </summary>
    public static async Task<string?> ValidateResolvedAsync(string url, CancellationToken ct)
    {
        var syntaxError = ValidateUrl(url);
        if (syntaxError != null) return syntaxError;

        var uri = new Uri(url);
        if (IPAddress.TryParse(uri.Host, out _)) return null;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            if (addresses.Length == 0)
                return $"Домен {uri.Host} не разрешается ни в один адрес.";

            foreach (var address in addresses)
            {
                if (IsBlockedAddress(address))
                    return $"Домен {uri.Host} разрешается в запрещённый адрес {address}.";
            }
        }
        catch (SocketException)
        {
            return $"Домен {uri.Host} не разрешается.";
        }

        return null;
    }

    /// <summary>Проверка адреса после каждого перенаправления.</summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,                                   // 10.0.0.0/8
                127 => true,                                    // 127.0.0.0/8
                0 => true,                                      // 0.0.0.0/8
                169 when bytes[1] == 254 => true,               // link-local 169.254.0.0/16
                172 when bytes[1] is >= 16 and <= 31 => true,   // 172.16.0.0/12
                192 when bytes[1] == 168 => true,               // 192.168.0.0/16
                100 when (bytes[1] & 0xC0) == 64 => true,       // CGNAT 100.64.0.0/10
                >= 224 => true,                                 // multicast и зарезервированные
                _ => false
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal) return true;
            if (address.IsIPv6SiteLocal) return true;

            var bytes = address.GetAddressBytes();
            // Уникальные локальные fc00::/7
            if ((bytes[0] & 0xFE) == 0xFC) return true;

            return false;
        }

        return true;
    }
}
