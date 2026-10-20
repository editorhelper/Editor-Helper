using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace EditorHelper.Services;

public class FavoritesService
{
    private readonly string? _favoritesPath;
    private readonly SessionService? _session;
    private readonly string _apiBaseUrl;
    private HashSet<string> _favoriteIds = new();

    public FavoritesService(string? favoritesPath = null)
    {
        _favoritesPath = favoritesPath ?? AppPaths.FavoritesFile;
        _apiBaseUrl = "";
        _favoriteIds = LoadLocal();
    }

    public FavoritesService(string apiBaseUrl, SessionService session)
    {
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        _session = session;
    }

    public event EventHandler? FavoritesChanged;
    public bool IsServerBacked => _session != null;
    public bool IsFavorite(string resourceId) => _favoriteIds.Contains(resourceId);
    public List<string> GetAllFavoriteIds() => _favoriteIds.ToList();
    public int Count => _favoriteIds.Count;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_session == null) return;
        using var request = new HttpRequestMessage(HttpMethod.Get, _apiBaseUrl + "/api/v1/me/favorites");
        _session.ApplyAuthorization(request);
        using var response = await SharedHttp.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Избранное недоступно: выполните вход заново.");
        var ids = await response.Content.ReadFromJsonAsync<List<string>>(cancellationToken: ct) ?? new();
        _favoriteIds = ids.ToHashSet(StringComparer.Ordinal);
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> ToggleFavoriteAsync(string resourceId, CancellationToken ct = default)
    {
        if (_session == null) throw new InvalidOperationException("Для сохранения избранного войдите в аккаунт.");
        var adding = !_favoriteIds.Contains(resourceId);
        using var request = new HttpRequestMessage(adding ? HttpMethod.Put : HttpMethod.Delete, _apiBaseUrl + "/api/v1/me/favorites/" + Uri.EscapeDataString(resourceId));
        _session.ApplyAuthorization(request);
        using var response = await SharedHttp.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Не удалось сохранить избранное.");
        if (adding) _favoriteIds.Add(resourceId); else _favoriteIds.Remove(resourceId);
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
        return adding;
    }

    public void ToggleFavorite(string resourceId)
    {
        if (_session != null) throw new InvalidOperationException("Используйте ToggleFavoriteAsync для серверного избранного.");
        if (!_favoriteIds.Remove(resourceId)) _favoriteIds.Add(resourceId);
        SaveLocal();
    }

    private HashSet<string> LoadLocal()
    {
        try
        {
            if (_favoritesPath != null && File.Exists(_favoritesPath))
            {
                var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_favoritesPath));
                return ids?.ToHashSet() ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveLocal()
    {
        if (_favoritesPath == null) return;
        try
        {
            var directory = Path.GetDirectoryName(_favoritesPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_favoritesPath, JsonSerializer.Serialize(_favoriteIds.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            FavoritesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
}
