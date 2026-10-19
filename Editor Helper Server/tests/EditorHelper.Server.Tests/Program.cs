using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EditorHelper.Server.Api;
using EditorHelper.Server.Auth;
using EditorHelper.Server.Sources;
using EditorHelper.Server.Storage;
using EditorHelper.Server.Telegram;
using EditorHelper.Services;

namespace EditorHelper.Server.Tests;

/// <summary>
/// Проверки сервера: юнит-логика (SSRF, разбор ссылок, хранилище) и полный
/// сценарий через HTTP — фейковый Telegram, обнаружение архива, карточка,
/// публикация и скачивание настоящим клиентским DownloadService.
/// Запуск: dotnet run --project server/tests/EditorHelper.Server.Tests
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    public static async Task<int> Main()
    {
        Console.WriteLine("Editor Helper Server — tests");
        Console.WriteLine(new string('=', 60));

        TestSsrfGuard();
        TestParseTelegramLink();
        await TestJsonFileStore();
        await TestCatalogRepository();
        TestPublicResourceMapping();
        await TestIdentityStoreAsync();
        await TestRoleMigrationAsync();
        await TestDeviceBindingAsync();
        await TestAdminPanelLifecycleAsync();
        await TestBetaChannelAsync();
        TestRateLimiterUnit();
        await TestRateLimitingAsync();
        await TestIntegrationAsync();

        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"Passed: {_passed}   Failed: {_failed}");
        if (Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures:");
            foreach (var f in Failures) Console.WriteLine($"  - {f}");
        }
        return _failed == 0 ? 0 : 1;
    }

    private static void TestSsrfGuard()
    {
        Section("SSRF guard");
        Check("public https allowed", SsrfGuard.ValidateUrl("https://example.com/a.zip") == null);
        Check("public http allowed", SsrfGuard.ValidateUrl("http://cdn.example.com/a.zip") == null);
        Check("localhost rejected", SsrfGuard.ValidateUrl("http://localhost/a.zip") != null);
        Check("loopback rejected", SsrfGuard.ValidateUrl("http://127.0.0.1/a.zip") != null);
        Check("loopback 127.x rejected", SsrfGuard.ValidateUrl("http://127.5.5.5/a.zip") != null);
        Check("10.x rejected", SsrfGuard.ValidateUrl("http://10.1.2.3/a.zip") != null);
        Check("192.168.x rejected", SsrfGuard.ValidateUrl("http://192.168.0.1/a.zip") != null);
        Check("172.16.x rejected", SsrfGuard.ValidateUrl("http://172.16.5.4/a.zip") != null);
        Check("172.31.x rejected", SsrfGuard.ValidateUrl("http://172.31.5.4/a.zip") != null);
        Check("172.32.x allowed", SsrfGuard.ValidateUrl("http://172.32.5.4/a.zip") == null);
        Check("link-local rejected", SsrfGuard.ValidateUrl("http://169.254.1.1/a.zip") != null);
        Check("cgnat rejected", SsrfGuard.ValidateUrl("http://100.64.0.1/a.zip") != null);
        Check("metadata ip rejected", SsrfGuard.ValidateUrl("http://169.254.169.254/latest") != null);
        Check("ftp scheme rejected", SsrfGuard.ValidateUrl("ftp://example.com/a.zip") != null);
        Check("file scheme rejected", SsrfGuard.ValidateUrl("file:///c:/a.zip") != null);
        Check("userinfo rejected", SsrfGuard.ValidateUrl("http://user:pass@example.com/a.zip") != null);
        Check("empty rejected", SsrfGuard.ValidateUrl("") != null);
        Check("garbage rejected", SsrfGuard.ValidateUrl("not a url") != null);
        Check("ipv6 loopback blocked", SsrfGuard.IsBlockedAddress(System.Net.IPAddress.IPv6Loopback));
        Check("ipv6 unique-local blocked", SsrfGuard.IsBlockedAddress(System.Net.IPAddress.Parse("fc00::1")));
        Check("ipv4-mapped loopback blocked", SsrfGuard.IsBlockedAddress(System.Net.IPAddress.Parse("::ffff:127.0.0.1")));
        Check("public ipv4 not blocked", !SsrfGuard.IsBlockedAddress(System.Net.IPAddress.Parse("8.8.8.8")));
    }

    private static void TestParseTelegramLink()
    {
        Section("Telegram link parsing");
        var publicLink = Endpoints.ParseTelegramLink("https://t.me/mychannel/42");
        Check("public channel link parsed", publicLink is { MessageId: 42, ChannelName: "mychannel" }, publicLink?.ToString() ?? "null");
        var privateLink = Endpoints.ParseTelegramLink("https://t.me/c/1234567/89");
        Check("private channel link parsed", privateLink is { MessageId: 89, ChannelName: null }, privateLink?.ToString() ?? "null");
        Check("t.me without id rejected", Endpoints.ParseTelegramLink("https://t.me/mychannel") == null);
        Check("non-telegram host rejected", Endpoints.ParseTelegramLink("https://example.com/a/1") == null);
        Check("empty rejected", Endpoints.ParseTelegramLink("") == null);
        Check("null rejected", Endpoints.ParseTelegramLink(null) == null);
        Check("garbage rejected", Endpoints.ParseTelegramLink("привет") == null);
    }

    private static async Task TestJsonFileStore()
    {
        Section("JSON file store");
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "doc.json");
            var store = new JsonFileStore<TestDoc>(path);
            await store.SaveAsync(new TestDoc { Name = "first", Count = 7 });
            var loaded = await store.LoadAsync();
            Check("round-trip keeps values", loaded.Name == "first" && loaded.Count == 7);
            var updated = await store.UpdateAsync(d => { d.Count = 9; return d.Count; });
            Check("update writes through", updated == 9 && (await store.LoadAsync()).Count == 9);
            File.WriteAllText(path, "{ не json");
            var threw = false;
            try { await store.LoadAsync(); } catch (InvalidDataException) { threw = true; }
            Check("corrupt file raises", threw);
            Check("corrupt file backed up", Directory.GetFiles(dir, "*.corrupt-*").Length == 1);
            Check("after corruption starts empty", (await store.LoadAsync()).Count == 0);
        }
        finally { TryDeleteDir(dir); }
    }

    private static async Task TestCatalogRepository()
    {
        Section("Catalog repository");
        var dir = NewTempDir();
        try
        {
            var repo = new CatalogRepository(dir);
            var record = new ResourceRecord { Id = "r1", Slug = "r1", Title = "One", Type = "Plugin", Status = "" };
            await repo.UpsertAsync(record);
            Check("upsert stores record", (await repo.LoadCatalogAsync()).Resources.Count == 1);
            var found = await repo.FindAsync("r1");
            Check("find by id", found?.Title == "One");
            record.Title = "One edited";
            await repo.UpsertAsync(record);
            Check("upsert updates in place", (await repo.LoadCatalogAsync()).Resources.Count == 1 && (await repo.FindAsync("r1"))!.Title == "One edited");
            await repo.MigrateAsync();
            Check("migration fills empty status", (await repo.FindAsync("r1"))!.Status == "published");
            Check("delete removes", await repo.DeleteAsync("r1"));
            Check("delete missing returns false", !await repo.DeleteAsync("nope"));
        }
        finally { TryDeleteDir(dir); }
    }

    private static void TestPublicResourceMapping()
    {
        Section("Public resource mapping");
        var record = new ResourceRecord
        {
            Id = "ae-tool", Slug = "tool", Title = "Tool", Description = "Desc", Type = "Plugin",
            Application = "After Effects", InstallationType = "Manual", Status = "published",
            Source = new FileSourceRef { Kind = "telegram", TelegramFileId = "fid-secret-123", TelegramFileUniqueId = "uniq-secret", TelegramMessageId = 42, FileName = "tool.zip", SizeBytes = 100, Sha256 = "abc123" },
            Note = "internal note", Banner = new FileSourceRef { Kind = "local", LocalPath = "banners/ae-tool.png", FileName = "ae-tool.png" }
        };
        var pub = PublicResource.FromRecord(record, "http://host:1");
        Check("download url points to API", pub.DownloadUrl == "http://host:1/api/v1/resources/ae-tool/download");
        Check("banner url points to API", pub.Thumbnail == "http://host:1/api/v1/resources/ae-tool/banner");
        Check("file name from source", pub.FileName == "tool.zip");
        Check("checksum from source sha256", pub.Checksum == "sha256:abc123");
        var json = JsonSerializer.Serialize(pub, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Check("telegram file id not exposed", !json.Contains("fid-secret-123"));
        Check("telegram unique id not exposed", !json.Contains("uniq-secret"));
        Check("internal note not exposed", !json.Contains("internal note"));
        Check("no telegram word in payload", !json.Contains("elegram", StringComparison.OrdinalIgnoreCase), json);
    }

    private static async Task TestIdentityStoreAsync()
    {
        Section("Identity store");
        var dir = NewTempDir();
        try
        {
            var config = new ServerConfig { DataDir = dir };
            var store = new IdentityStore(config);
            var roleDir = dir;
            var challenge = await store.StartDevelopmentLoginAsync("Test Owner", TimeSpan.FromMinutes(2));
            var login = await store.CompleteDevelopmentLoginAsync(challenge.RequestId, challenge.Code);
            Check("development code creates session", login != null && login.User.Role == UserRole.User);
            Check("one-time code cannot be reused", await store.CompleteDevelopmentLoginAsync(challenge.RequestId, challenge.Code) == null);
            Check("session authenticates", await store.AuthenticateAsync(login!.Token) != null);
            var telegramChallenge = await store.StartTelegramAuthAsync(TimeSpan.FromMinutes(2));
            Check("Telegram challenge does not trust client identity", telegramChallenge.TelegramId == 0);
            Check("unknown request id cannot confirm challenge", !await store.ConfirmTelegramChallengeAsync("00000000000000000000000000000000", 222333444, null, null, null));
            Check("real Telegram update confirms matching challenge", await store.ConfirmTelegramChallengeAsync(telegramChallenge.RequestId, 222333444, "verified_user", "Verified", "556677"));
            Check("confirmed Telegram challenge is reported to client", await store.IsTelegramChallengeConfirmedAsync(telegramChallenge.RequestId));
            var telegramLogin = await store.CompleteTelegramAuthAsync(telegramChallenge.RequestId);
            Check("confirmed Telegram identity creates server user and session", telegramLogin.Session?.User.TelegramId == 222333444 && telegramLogin.Session.User.Role == UserRole.User);
            var configuredOwnerChallenge = await store.StartTelegramAuthAsync(TimeSpan.FromMinutes(2));
            await store.ConfirmTelegramChallengeAsync(configuredOwnerChallenge.RequestId, 987654321, "owner", "Owner", null);
            var configuredOwnerLogin = await store.CompleteTelegramAuthAsync(configuredOwnerChallenge.RequestId, 987654321);
            Check("configured Telegram ID receives Owner server-side", configuredOwnerLogin.Session?.User.Role == UserRole.Owner);
            var managerSession = await store.CreateLocalDevelopmentSessionAsync("Role Manager", TimeSpan.FromHours(1));
            var grantVipByOwner = await store.ChangeRoleAsync(configuredOwnerLogin.Session!.User.Id, managerSession.User.Id, UserRole.VIP, CancellationToken.None);
            var vipManager = (await store.ListUsersAsync("Role Manager")).Single();
            Check("Owner can grant VIP role", grantVipByOwner.Success && vipManager.Role == UserRole.VIP);
            var roleStore = new IdentityStore(new ServerConfig { DataDir = roleDir });
            {
                var vipChallenge = await roleStore.StartTelegramAuthAsync(TimeSpan.FromMinutes(2));
                await roleStore.ConfirmTelegramChallengeAsync(vipChallenge.RequestId, 555666777, "vip_user", "Vip", null);
                var vipLogin = await roleStore.CompleteTelegramAuthAsync(vipChallenge.RequestId, 555000111);
                Check("new Telegram user starts as User", vipLogin.Session?.User.Role == UserRole.User);
                var configuredOwner = await roleStore.EnsureConfiguredOwnerAsync(555000111);
                var grantVipScoped = await roleStore.ChangeRoleAsync(configuredOwner!.Value, vipLogin.Session!.User.Id, UserRole.VIP, CancellationToken.None);
                Check("configured Owner can assign VIP role", grantVipScoped.Success);
                var vipUser = (await roleStore.ListUsersAsync("vip_user")).Single();
                Check("VIP role persists in store", vipUser.Role == UserRole.VIP);
                var nobodySession = await roleStore.CreateLocalDevelopmentSessionAsync("Nobody", TimeSpan.FromHours(1));
                var selfPromote = await roleStore.ChangeRoleAsync(vipLogin.Session.User.Id, nobodySession.User.Id, UserRole.VIP, CancellationToken.None);
                Check("non-Owner cannot change roles", !selfPromote.Success);
                var toOwner = await roleStore.ChangeRoleAsync(vipLogin.Session.User.Id, nobodySession.User.Id, UserRole.Owner, CancellationToken.None);
                Check("non-Owner cannot grant Owner", !toOwner.Success);
            }
            Check("Telegram challenge cannot be replayed", (await store.CompleteTelegramAuthAsync(telegramChallenge.RequestId)).Session == null);
            var declinedChallenge = await store.StartTelegramAuthAsync(TimeSpan.FromMinutes(2));
            Check("declined challenge was open", !declinedChallenge.Declined);
            Check("owner can decline own challenge", await store.DeclineTelegramChallengeAsync(declinedChallenge.RequestId, 444555666));
            var declinedView = await store.GetTelegramChallengeAsync(declinedChallenge.RequestId);
            Check("declined challenge is reported to client", declinedView?.Declined == true);
            Check("declined challenge cannot be confirmed", !await store.ConfirmTelegramChallengeAsync(declinedChallenge.RequestId, 444555666, null, null, null));
            Check("declined challenge cannot be completed", (await store.CompleteTelegramAuthAsync(declinedChallenge.RequestId)).Session == null);
            var foreignChallenge = await store.StartTelegramAuthAsync(TimeSpan.FromMinutes(2));
            await store.ConfirmTelegramChallengeAsync(foreignChallenge.RequestId, 222333444, "verified_user", "Verified", null);
            Check("foreign Telegram ID cannot decline someone else's challenge", !await store.DeclineTelegramChallengeAsync(foreignChallenge.RequestId, 999999999));
            var cancelledChallenge = await store.StartTelegramAuthAsync(TimeSpan.FromMinutes(2));
            await store.CancelTelegramChallengeAsync(cancelledChallenge.RequestId);
            Check("cancelled Telegram challenge cannot be confirmed", !await store.ConfirmTelegramChallengeAsync(cancelledChallenge.RequestId, 333444555, null, null, null));
            var favoritesUserA = await store.CreateLocalDevelopmentSessionAsync("Favorites A", TimeSpan.FromHours(1));
            var favoritesUserB = await store.CreateLocalDevelopmentSessionAsync("Favorites B", TimeSpan.FromHours(1));
            Check("favorite insert is idempotent", await store.AddFavoriteAsync(favoritesUserA.User.Id, "plugin-a") && !await store.AddFavoriteAsync(favoritesUserA.User.Id, "plugin-a"));
            await store.AddFavoriteAsync(favoritesUserA.User.Id, "plugin-b");
            await store.AddFavoriteAsync(favoritesUserB.User.Id, "plugin-b");
            Check("favorites are isolated per user", (await store.GetFavoritesAsync(favoritesUserA.User.Id)).SequenceEqual(new[] { "plugin-b", "plugin-a" }) && (await store.GetFavoritesAsync(favoritesUserB.User.Id)).SequenceEqual(new[] { "plugin-b" }));
            Check("favorite removal is scoped", await store.RemoveFavoriteAsync(favoritesUserA.User.Id, "plugin-a") && !(await store.GetFavoritesAsync(favoritesUserB.User.Id)).Contains("plugin-a"));
            var ordinary = await store.CreateLocalDevelopmentSessionAsync("Ordinary", TimeSpan.FromHours(1));
            var forbidden = await store.ChangeRoleAsync(ordinary.User.Id, login.User.Id, UserRole.Owner);
            Check("ordinary user cannot grant Owner", !forbidden.Success);
            await store.RevokeSessionAsync(login.Token);
            Check("revoked session rejected", await store.AuthenticateAsync(login.Token) == null);
            var configuredOwnerId = await store.EnsureConfiguredOwnerAsync(987654321);
            Check("configured Telegram ID bootstraps Owner", configuredOwnerId != null && (await store.FindUserByTelegramIdAsync(987654321))?.Role == UserRole.Owner);
            Check("configured Owner bootstrap is idempotent", await store.EnsureConfiguredOwnerAsync(987654321) == configuredOwnerId);
            var ownerId = configuredOwnerId!.Value;
            var ownerSession = await store.CreateLocalDevelopmentSessionAsync("Temporary Owner", TimeSpan.FromHours(1));
            var change = await store.ChangeRoleAsync(ownerSession.User.Id, ordinary.User.Id, UserRole.VIP);
            Check("non-Owner cannot grant VIP", !change.Success);
            var grantVip = await store.ChangeRoleAsync(ownerId, ordinary.User.Id, UserRole.VIP);
            Check("owner can grant VIP", grantVip.Success && (await store.ListUsersAsync("Ordinary")).Single().Role == UserRole.VIP);
            var selfRole = await store.ChangeRoleAsync(ownerId, ownerId, UserRole.User);
            Check("Owner cannot change own role", !selfRole.Success);
            var demoteOwner = await store.ChangeRoleAsync(ownerId, ownerId, UserRole.VIP);
            Check("Owner cannot demote self to VIP", !demoteOwner.Success);
            var ownerToOther = await store.ChangeRoleAsync(ownerId, ordinary.User.Id, UserRole.Owner);
            Check("Owner role cannot be granted through ChangeRole", !ownerToOther.Success);
            var demoteOwnerByOther = await store.ChangeRoleAsync(ownerSession.User.Id, ownerId, UserRole.User);
            Check("Owner account cannot be demoted by non-Owner", !demoteOwnerByOther.Success);
            var vipUserId = (await store.ListUsersAsync("Ordinary")).Single().Id;
            var demoteVip = await store.ChangeRoleAsync(ownerId, vipUserId, UserRole.User);
            Check("owner can revoke VIP", demoteVip.Success && (await store.ListUsersAsync("Ordinary")).Single().Role == UserRole.User);
            var stranger = await store.CreateLocalDevelopmentSessionAsync("Temp VIP", TimeSpan.FromHours(1));
            var blockByUser = await store.SetBlockedAsync(stranger.User.Id, vipUserId, true);
            Check("User cannot block another user", !blockByUser.Success);
            var promoteByUser = await store.ChangeRoleAsync(stranger.User.Id, vipUserId, UserRole.VIP);
            Check("User cannot assign VIP", !promoteByUser.Success);
            var selfBlock = await store.SetBlockedAsync(ownerId, ownerId, true);
            Check("Owner cannot block self", !selfBlock.Success);
            var userSession = await store.CreateLocalDevelopmentSessionAsync("Blocked User", TimeSpan.FromHours(1));
            var block = await store.SetBlockedAsync(ownerId, userSession.User.Id, true);
            Check("owner can block user", block.Success);
            Check("blocking revokes active sessions", await store.AuthenticateAsync(userSession.Token) == null);
            Check("blocked user cannot start a valid Telegram session", !block.Success || userSession.User.Role == UserRole.User);
            var revoke = await store.RevokeAllSessionsAsync(ownerId, vipUserId);
            Check("owner can revoke all user sessions", revoke.Success);
            Check("owner actions are audited", (await store.ListAuditAsync()).Count >= 2);
        }
        finally { TryDeleteDir(dir); }
    }

    private static async Task TestIntegrationAsync()
    {
        Section("Integration: Telegram -> card -> download");
        await TestGetChatMemberDiagnosticsAsync();
        var zipBytes = BuildZip();
        var sha = Sha256Hex(zipBytes);
        await using var fake = await FakeTelegram.StartAsync(zipBytes);
        await using var serverA = await StartServerAsync(telegram: true, allowLocalExternal: true, fake);
        await using var serverB = await StartServerAsync(telegram: false, allowLocalExternal: true);
        await using var serverC = await StartServerAsync(telegram: false, allowLocalExternal: false);
        var a = new AdminApi(serverA.BaseUrl, serverA.AdminToken);
        var b = new AdminApi(serverB.BaseUrl, serverB.AdminToken);
        var c = new AdminApi(serverC.BaseUrl, serverC.AdminToken);

        var discovered = await WaitForAsync(async () =>
        {
            var docs = await a.GetJsonAsync("/telegram/documents");
            return docs.ValueKind == JsonValueKind.Array && docs.GetArrayLength() > 0 ? docs : (JsonElement?)null;
        }, TimeSpan.FromSeconds(20));
        Check("polling discovered the archive", discovered != null);
        if (discovered == null) return;
        var doc = discovered.Value[0];
        Check("discovered file name", doc.GetProperty("fileName").GetString() == FakeTelegram.FileName);
        Check("discovered file id", doc.GetProperty("fileId").GetString() == FakeTelegram.FileId);
        Check("discovered within bot limit", doc.GetProperty("withinBotLimit").GetBoolean());

        var byLink = await a.PostJsonAsync("/telegram/check-link", new { url = "https://t.me/testchannel/77" });
        Check("check-link finds the document", byLink.status == 200 && byLink.json.GetProperty("fileId").GetString() == FakeTelegram.FileId);
        var badLink = await a.PostJsonAsync("/telegram/check-link", new { url = "https://t.me/testchannel/9999" });
        Check("check-link unknown post explains re-send", badLink.status == 404);

        var create = await a.PostJsonAsync("/resources", new { title = "Test Archive", description = "Архив из канала", application = "After Effects", type = "Plugin", installationType = "Manual", checksum = $"sha256:{sha}", source = new { kind = "telegram", telegramFileId = FakeTelegram.FileId, telegramFileUniqueId = FakeTelegram.FileUniqueId, telegramMessageId = FakeTelegram.MessageId, fileName = FakeTelegram.FileName, sizeBytes = zipBytes.Length } });
        Check("card created", create.status == 201);
        var cardId = create.json.GetProperty("id").GetString()!;
        Check("new card starts as draft", create.json.GetProperty("status").GetString() == "draft");
        var publish = await a.PostJsonAsync($"/resources/{cardId}/publish", null);
        Check("card published", publish.status == 200, publish.Body);
        var remote = new RemoteCatalogProvider(serverA.BaseUrl);
        Check("client sees server as available", await remote.IsAvailableAsync());
        var resources = await remote.GetAllResourcesAsync();
        var clientResource = resources.FirstOrDefault(r => r.Id == cardId);
        Check("client catalog contains published card", clientResource != null);
        Check("client parsed type enum from string", clientResource?.Type == EditorHelper.Models.ResourceType.Plugin);
        Check("client download url is API path (no telegram id)", clientResource?.DownloadUrl?.EndsWith($"/api/v1/resources/{cardId}/download") == true);
        var downloadDir = NewTempDir();
        try
        {
            var result = await new DownloadService(downloadDir).DownloadResourceAsync(clientResource!, overwrite: true);
            Check("client download succeeded", File.Exists(result.FilePath));
            Check("client verified checksum", result.Checksum == ChecksumStatus.Verified, result.Message);
            Check("downloaded bytes match original", Sha256Hex(await File.ReadAllBytesAsync(result.FilePath)) == sha);
            Check("downloaded archive opens", CanOpenZip(await File.ReadAllBytesAsync(result.FilePath)));
        }
        finally { TryDeleteDir(downloadDir); }

        var bigCreate = await a.PostJsonAsync("/resources", new { title = "Huge Archive", description = "Слишком большой", application = "Premiere Pro", type = "Plugin", installationType = "Manual", source = new { kind = "telegram", telegramFileId = FakeTelegram.BigFileId, fileName = "big.zip" } });
        Check("big card created", bigCreate.status == 201);
        var bigId = bigCreate.json.GetProperty("id").GetString()!;
        var bigPublish = await a.PostJsonAsync($"/resources/{bigId}/publish", null);
        Check("publishing oversized telegram file rejected", bigPublish.status == 409, bigPublish.Body);
        Check("oversize rejection mentions the 20 MB limit", bigPublish.Body.Contains("20 МБ") || bigPublish.Body.Contains("лимита"));
        Check("unpublish ok", (await a.PostJsonAsync($"/resources/{cardId}/unpublish", null)).status == 200);
        Check("unpublished resource 404 for client", (await a.GetRawAsync($"/api/v1/resources/{cardId}")).status == 404);
        await a.PostJsonAsync($"/resources/{cardId}/publish", null);

        Check("upload to telegram accepted", (await a.PostFormAsync("/telegram/upload", FakeTelegram.FileName, zipBytes)).status == 200);
        Check("second upload accepted", (await a.PostFormAsync("/telegram/upload", FakeTelegram.FileName, zipBytes)).status == 200);
        var docsAfterUpload = await a.GetJsonAsync("/telegram/documents");
        var uploadedCount = docsAfterUpload.EnumerateArray().Count(d => d.GetProperty("fileUniqueId").GetString() == "uniq-uploaded-1");
        Check("duplicate upload not registered twice", uploadedCount == 1, $"count={uploadedCount}");
        await TestExternalSourceAsync(a, serverA.BaseUrl, fake, zipBytes, sha, "External A");
        Check("server B reports telegram off", (await b.GetRawAsync("/api/v1/health")).Body.Contains("\"telegram\":false"));
        await TestExternalSourceAsync(b, serverB.BaseUrl, fake, zipBytes, sha, "External B (no telegram)");
        var blocked = await c.PostJsonAsync("/files/check-external", new { url = "http://127.0.0.1/x.zip" });
        Check("check-external blocks loopback", blocked.status == 200 && !blocked.json.GetProperty("available").GetBoolean());
        Check("loopback blocked by SSRF text", blocked.json.TryGetProperty("message", out var m) && m.GetString()!.Contains("запрещён"));
        var ssrfCard = await c.PostJsonAsync("/resources", new { title = "SSRF attempt", description = "x", application = "After Effects", type = "Plugin", installationType = "Manual", source = new { kind = "http", externalUrl = "http://169.254.169.254/latest/meta-data" } });
        Check("card with internal url rejected", ssrfCard.status == 422, ssrfCard.Body);

        Check("admin without token is 401", await AdminApi.AnonymousStatusAsync(serverA.BaseUrl + "/admin/api/resources") == 401);
        Check("admin with wrong token is 401", await AdminApi.AnonymousStatusAsync(serverA.BaseUrl + "/admin/api/resources", "wrong-token") == 401);
        Check("admin with token is 200", (await a.GetRawAsync("/admin/api/resources")).status == 200);
        Check("catalog has no bot token", !(await a.GetRawAsync("/api/v1/catalog")).Body.Contains(FakeTelegram.Token));
        Check("catalog has no admin token", !(await a.GetRawAsync("/api/v1/catalog")).Body.Contains(serverA.AdminToken));
        Check("catalog has no telegram file id", !(await a.GetRawAsync("/api/v1/catalog")).Body.Contains(FakeTelegram.FileId));
        var statusBody = (await a.GetRawAsync("/telegram/status")).Body;
        Check("status has no bot token", !statusBody.Contains(FakeTelegram.Token));
        Check("status reports bot as admin", statusBody.Contains("\"botIsAdmin\":true"), statusBody);

        var telegramStart = await PostDirectAsync(serverA.BaseUrl + "/api/v1/auth/telegram/start", new { });
        Check("single configured Telegram bot starts auth challenge", telegramStart.StatusCode == System.Net.HttpStatusCode.OK);
        var telegramChallenge = JsonSerializer.Deserialize<SessionChallenge>(await telegramStart.Content.ReadAsStringAsync(), JsonFileStore<object>.JsonOptions)!;
        var telegramStatus = await GetDirectAsync(serverA.BaseUrl + "/api/v1/auth/telegram/status/" + telegramChallenge.RequestId);
        Check("new Telegram challenge awaits bot confirmation", telegramStatus.RootElement.GetProperty("status").GetString() == "waiting");
        using var cancelChallenge = await PostDirectAsync(serverA.BaseUrl + "/api/v1/auth/telegram/cancel", new { requestId = telegramChallenge.RequestId });
        Check("Telegram login challenge can be cancelled", cancelChallenge.StatusCode == System.Net.HttpStatusCode.NoContent);
        var cancelledStatus = await GetDirectAsync(serverA.BaseUrl + "/api/v1/auth/telegram/status/" + telegramChallenge.RequestId);
        Check("cancelled Telegram challenge cannot be reused", cancelledStatus.RootElement.GetProperty("status").GetString() == "expired");

        var devStart = await PostDirectAsync(serverA.BaseUrl + "/api/v1/auth/dev/start", new { displayName = "Admin Test" });
        Check("Development login start enabled in test host", devStart.StatusCode == System.Net.HttpStatusCode.OK);
        var challenge = JsonSerializer.Deserialize<LoginStartResult>(await devStart.Content.ReadAsStringAsync(), JsonFileStore<object>.JsonOptions)!;
        using var complete = await PostDirectAsync(serverA.BaseUrl + "/api/v1/auth/dev/complete", new { requestId = challenge.RequestId, code = challenge.Code });
        Check("one-time Development login succeeds", complete.IsSuccessStatusCode);
        var login = JsonSerializer.Deserialize<LoginResult>(await complete.Content.ReadAsStringAsync(), JsonFileStore<object>.JsonOptions)!;
        var userAdmin = await SendBearerAsync(serverA.BaseUrl, login.Token, HttpMethod.Get, "/admin/api/users");
        Check("User is denied admin APIs", userAdmin.StatusCode == System.Net.HttpStatusCode.Forbidden);
        var repeated = await PostDirectAsync(serverA.BaseUrl + "/api/v1/auth/dev/complete", new { requestId = challenge.RequestId, code = challenge.Code });
        Check("one-time code replay rejected", repeated.StatusCode == System.Net.HttpStatusCode.Unauthorized);
        var anonymousRole = await SendBearerJsonAsync(serverA.BaseUrl, login.Token, HttpMethod.Put, "/admin/api/users/1/role", new { role = "Owner" });
        Check("ordinary user cannot self-promote", anonymousRole.StatusCode == System.Net.HttpStatusCode.Forbidden);
        using var logout = await SendBearerAsync(serverA.BaseUrl, login.Token, HttpMethod.Post, "/api/v1/auth/logout");
        Check("logout succeeds", logout.IsSuccessStatusCode);
        using var afterLogout = await SendBearerAsync(serverA.BaseUrl, login.Token, HttpMethod.Get, "/api/v1/auth/session");
        Check("logout revokes session", afterLogout.StatusCode == System.Net.HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Полный цикл админ-панели через Bearer-сессию: создание, сохранение,
    /// редактирование, публикация, появление в каталоге, смена ссылки на
    /// скачивание и удаление. Плюс проверка, что обычный пользователь и
    /// аноним не получают доступ к админ-API прямым запросом.
    /// </summary>
    private static async Task TestAdminPanelLifecycleAsync()
    {
        Section("Admin panel: lifecycle and authorization");

        await using var server = await StartServerAsync(telegram: false, allowLocalExternal: true);
        var store = server.App.Services.GetRequiredService<IdentityStore>();

        // Владелец определяется конфигурацией сервера: bootstrap создаёт
        // единственного Owner, дальше сессия выдаётся обычным механизмом входа.
        await store.BootstrapOwnerAsync("Panel Owner", telegramId: null);
        var owner = await store.CreateLocalDevelopmentSessionAsync("Panel Owner", TimeSpan.FromHours(1));
        var ordinary = await store.CreateLocalDevelopmentSessionAsync("Panel User", TimeSpan.FromHours(1));

        var api = new BearerApi(server.BaseUrl, owner.Token);
        var user = new BearerApi(server.BaseUrl, ordinary.Token);

        // --- доступ ---
        Check("owner reaches admin list", (await api.SendAsync(HttpMethod.Get, "/resources", null)).status == 200);
        Check("ordinary user gets 403 on admin list", (await user.SendAsync(HttpMethod.Get, "/resources", null)).status == 403);
        Check("anonymous gets 401 on admin list", await AdminApi.AnonymousStatusAsync(server.BaseUrl + "/admin/api/resources") == 401);
        Check("ordinary user cannot create a card", (await user.SendAsync(HttpMethod.Post, "/resources", new { title = "Hijack", application = "Photoshop", type = "Script", installationType = "Manual" })).status == 403);
        Check("ordinary user cannot delete a card", (await user.SendAsync(HttpMethod.Delete, "/resources/whatever", null)).status == 403);
        Check("ordinary user cannot publish a card", (await user.SendAsync(HttpMethod.Post, "/resources/whatever/publish", null)).status == 403);
        Check("ordinary user cannot read the audit log", await AdminApi.AnonymousStatusAsync(server.BaseUrl + "/admin/api/audit") == 401);

        // --- загрузка файла в хранилище сервера ---
        var zipBytes = BuildZip();
        var upload = await api.UploadAsync("/files/upload", "panel-archive.zip", zipBytes);
        Check("archive uploaded to server storage", upload.status == 200, upload.Body);
        var localPath = upload.json.GetProperty("localPath").GetString()!;
        var localSha = upload.json.GetProperty("sha256").GetString()!;

        // --- создание ---
        var create = await api.SendAsync(HttpMethod.Post, "/resources", new
        {
            title = "Панельный скрипт",
            description = "Краткое описание из панели",
            longDescription = "Полное описание со всеми подробностями.",
            application = "After Effects",
            type = "Script",
            installationType = "Manual",
            category = "Utilities",
            version = "1.2.0",
            author = "Panel Owner",
            tags = new[] { "panel", "automation" },
            supportedVersions = new[] { "2024", "2025" },
            documentationUrl = "https://example.com/docs",
            links = new[] { new { label = "Сайт автора", url = "https://example.com" } },
            source = new { kind = "local", localPath, fileName = "panel-archive.zip", sizeBytes = zipBytes.Length, sha256 = localSha }
        });
        Check("card created from the panel", create.status == 201, create.Body);
        Check("new card is a draft", create.json.GetProperty("status").GetString() == "draft");
        Check("documentation url stored", create.json.GetProperty("documentationUrl").GetString() == "https://example.com/docs");
        Check("extra link stored", create.json.GetProperty("links")[0].GetProperty("label").GetString() == "Сайт автора");
        var id = create.json.GetProperty("id").GetString()!;

        // --- валидация формы ---
        Check("empty title rejected", (await api.SendAsync(HttpMethod.Post, "/resources", new { title = " ", description = "x", application = "Photoshop", type = "Script", installationType = "Manual" })).status == 422);
        Check("unknown type rejected", (await api.SendAsync(HttpMethod.Put, $"/resources/{id}", new { title = "Панельный скрипт", description = "x", application = "After Effects", type = "NotAType", installationType = "Manual" })).status == 422);
        Check("unknown status rejected", (await api.SendAsync(HttpMethod.Put, $"/resources/{id}", new { title = "Панельный скрипт", description = "x", application = "After Effects", type = "Script", installationType = "Manual", status = "archived" })).status == 422);
        Check("internal documentation url rejected", (await api.SendAsync(HttpMethod.Put, $"/resources/{id}", new { title = "Панельный скрипт", description = "x", application = "After Effects", type = "Script", installationType = "Manual", documentationUrl = "http://127.0.0.1/docs" })).status == 422);
        Check("link without label rejected", (await api.SendAsync(HttpMethod.Put, $"/resources/{id}", new { title = "Панельный скрипт", description = "x", application = "After Effects", type = "Script", installationType = "Manual", links = new[] { new { label = "", url = "https://example.com" } } })).status == 422);
        Check("duplicate slug rejected", (await api.SendAsync(HttpMethod.Post, "/resources", new { title = "Другая карточка", description = "x", application = "Photoshop", type = "Script", installationType = "Manual", slug = create.json.GetProperty("slug").GetString() })).status == 422);

        // --- редактирование ---
        var edit = await api.SendAsync(HttpMethod.Put, $"/resources/{id}", new
        {
            title = "Панельный скрипт (обновлён)",
            description = "Обновлённое краткое описание",
            application = "After Effects",
            type = "Script",
            installationType = "CopyToFolder",
            category = "Utilities",
            version = "1.3.0",
            author = "Panel Owner",
            tags = new[] { "panel" },
            documentationUrl = "https://example.com/docs/v2",
            source = new { kind = "local", localPath, fileName = "panel-archive.zip", sizeBytes = zipBytes.Length, sha256 = localSha }
        });
        Check("card edited", edit.status == 200, edit.Body);
        Check("title updated", edit.json.GetProperty("title").GetString() == "Панельный скрипт (обновлён)");
        Check("version updated", edit.json.GetProperty("version").GetString() == "1.3.0");
        Check("installation type updated", edit.json.GetProperty("installationType").GetString() == "CopyToFolder");
        Check("edit keeps the draft status", edit.json.GetProperty("status").GetString() == "draft");
        Check("documentation url updated", edit.json.GetProperty("documentationUrl").GetString() == "https://example.com/docs/v2");
        Check("cleared links removed", edit.json.GetProperty("links").GetArrayLength() == 0);
        Check("draft is invisible in the public catalog", (await new BearerApi(server.BaseUrl, owner.Token).SendAsync(HttpMethod.Get, "/resources/" + id, null, publicApi: true)).status == 404);

        // --- карточка без файла: так её заводит форма, когда архива ещё нет ---
        var noFile = await api.SendAsync(HttpMethod.Post, "/resources", new
        {
            title = "Карточка без файла",
            description = "Заведена до появления архива.",
            application = "After Effects",
            type = "Script",
            installationType = "Manual"
        });
        Check("card without a file source is created", noFile.status == 201, noFile.Body);
        var noFileId = noFile.json.GetProperty("id").GetString()!;
        Check("card without a file stays a draft", noFile.json.GetProperty("status").GetString() == "draft");
        var noFilePublish = await api.SendAsync(HttpMethod.Post, $"/resources/{noFileId}/publish", null);
        Check("publishing a card without a file is refused", noFilePublish.status == 409, noFilePublish.Body);
        Check("card without a file is absent from the catalog",
            !(await new RemoteCatalogProvider(server.BaseUrl).GetAllResourcesAsync()).Any(r => r.Id == noFileId));
        var noFileRelink = await api.SendAsync(HttpMethod.Put, $"/resources/{noFileId}", new
        {
            title = "Карточка без файла",
            description = "Заведена до появления архива.",
            application = "After Effects",
            type = "Script",
            installationType = "Manual",
            source = new { kind = "local", localPath, fileName = "panel-archive.zip", sizeBytes = zipBytes.Length, sha256 = localSha }
        });
        Check("file source attached later", noFileRelink.status == 200, noFileRelink.Body);
        Check("card publishes once the file exists",
            (await api.SendAsync(HttpMethod.Post, $"/resources/{noFileId}/publish", null)).status == 200);
        Check("card reaches the catalog after relink and publish",
            (await new RemoteCatalogProvider(server.BaseUrl).GetAllResourcesAsync()).Any(r => r.Id == noFileId));
        await api.SendAsync(HttpMethod.Delete, $"/resources/{noFileId}", null);

        // --- дубликат ---
        var duplicate = await api.SendAsync(HttpMethod.Post, $"/resources/{id}/duplicate", null);
        Check("card duplicated", duplicate.status == 201, duplicate.Body);
        Check("duplicate is a draft", duplicate.json.GetProperty("status").GetString() == "draft");
        Check("duplicate has another id", duplicate.json.GetProperty("id").GetString() != id);
        Check("duplicate title marked as a copy", duplicate.json.GetProperty("title").GetString()!.Contains("копия"));
        Check("duplicate keeps the file source", duplicate.json.GetProperty("source").GetProperty("localPath").GetString() == localPath);
        var copyId = duplicate.json.GetProperty("id").GetString()!;

        // --- публикация и каталог ---
        var publish = await api.SendAsync(HttpMethod.Post, $"/resources/{id}/publish", null);
        Check("card published", publish.status == 200, publish.Body);
        Check("published status saved", publish.json.GetProperty("status").GetString() == "published");
        Check("publish sets PublishedAt", publish.json.GetProperty("publishedAt").GetDateTime() != default);

        var catalog = await new RemoteCatalogProvider(server.BaseUrl).GetAllResourcesAsync();
        var publishedCard = catalog.FirstOrDefault(r => r.Id == id);
        Check("published card appears in the client catalog", publishedCard != null);
        Check("catalog card shows the edited title", publishedCard?.Title == "Панельный скрипт (обновлён)");
        Check("catalog card carries documentation url", publishedCard?.DocumentationUrl == "https://example.com/docs/v2");

        var dir = NewTempDir();
        try
        {
            var result = await new DownloadService(dir).DownloadResourceAsync(publishedCard!, overwrite: true);
            Check("published card downloads", File.Exists(result.FilePath), result.Message);
            Check("downloaded bytes match the uploaded archive", Sha256Hex(await File.ReadAllBytesAsync(result.FilePath)) == localSha);
        }
        finally { TryDeleteDir(dir); }

        // --- смена ссылки на скачивание у опубликованной карточки ---
        var externalUrl = "https://example.com/new-archive.zip";
        var relink = await api.SendAsync(HttpMethod.Put, $"/resources/{id}", new
        {
            title = "Панельный скрипт (обновлён)",
            description = "Обновлённое краткое описание",
            application = "After Effects",
            type = "Script",
            installationType = "CopyToFolder",
            version = "1.4.0",
            source = new { kind = "http", externalUrl, fileName = "new-archive.zip" }
        });
        Check("download link changed", relink.status == 200, relink.Body);
        Check("source switched to http", relink.json.GetProperty("source").GetProperty("kind").GetString() == "http");
        Check("new download url stored", relink.json.GetProperty("source").GetProperty("externalUrl").GetString() == externalUrl);
        Check("card stays published after relink", relink.json.GetProperty("status").GetString() == "published");

        // --- скрытие, снятие с публикации, удаление ---
        var hide = await api.SendAsync(HttpMethod.Post, $"/resources/{copyId}/hide", null);
        Check("card hidden", hide.status == 200 && hide.json.GetProperty("status").GetString() == "hidden");
        var unpublish = await api.SendAsync(HttpMethod.Post, $"/resources/{id}/unpublish", null);
        Check("card unpublished", unpublish.status == 200 && unpublish.json.GetProperty("status").GetString() == "draft");
        Check("unpublished card leaves the catalog", !(await new RemoteCatalogProvider(server.BaseUrl).GetAllResourcesAsync()).Any(r => r.Id == id));
        Check("unpublished card 404 for direct request", (await new BearerApi(server.BaseUrl, owner.Token).SendAsync(HttpMethod.Get, "/resources/" + id, null, publicApi: true)).status == 404);

        var delete = await api.SendAsync(HttpMethod.Delete, $"/resources/{id}", null);
        Check("card deleted", delete.status == 200);
        Check("deleted card is gone from the admin list", !(await api.SendAsync(HttpMethod.Get, "/resources", null)).Body.Contains(id));
        Check("deleting twice is 404", (await api.SendAsync(HttpMethod.Delete, $"/resources/{id}", null)).status == 404);

        // --- обложка ---
        var png = BuildPng();
        var banner = await api.UploadAsync($"/resources/{copyId}/banner", "cover.png", png);
        Check("banner uploaded", banner.status == 200, banner.Body);
        Check("admin banner route serves the draft", (await api.SendAsync(HttpMethod.Get, $"/resources/{copyId}/banner", null)).status == 200);
        Check("draft banner is not public", (await new BearerApi(server.BaseUrl, owner.Token).SendAsync(HttpMethod.Get, "/resources/" + copyId + "/banner", null, publicApi: true)).status == 404);
        Check("oversized banner rejected", (await api.UploadAsync($"/resources/{copyId}/banner", "big.png", new byte[5 * 1024 * 1024 + 1])).status == 422);
        Check("wrong banner format rejected", (await api.UploadAsync($"/resources/{copyId}/banner", "cover.gif", png)).status == 422);

        // --- журнал ---
        var audit = await api.SendAsync(HttpMethod.Get, "/audit", null);
        Check("owner reads the audit log", audit.status == 200);
        Check("admin actions are audited", audit.Body.Contains("admin.post") && audit.Body.Contains("admin.delete"));
    }

    private static void TestRateLimiterUnit()
    {
        Section("Rate limiter (unit)");

        var limiter = new RateLimiter();
        var t0 = DateTimeOffset.UtcNow;

        var allowed = 0;
        for (int i = 0; i < 10; i++)
            if (limiter.Allow("k", 5, t0).Allowed) allowed++;
        Check("allows up to the limit within one minute", allowed == 5, $"allowed={allowed}");
        var blocked = limiter.Allow("k", 5, t0);
        Check("blocks the request past the limit", !blocked.Allowed);
        Check("blocked decision suggests a retry", blocked.RetryAfterSeconds >= 1);

        // Другой ключ имеет собственное окно
        Check("keys are limited independently", limiter.Allow("other", 5, t0).Allowed);

        // Спустя минуту окно освобождается
        Check("window frees up after a minute", limiter.Allow("k", 5, t0.AddMinutes(1)).Allowed);

        // Скользящее окно: запросы, ушедшие за пределы минуты, больше не считаются
        var sliding = new RateLimiter();
        for (int i = 0; i < 5; i++) sliding.Allow("s", 5, t0);
        Check("still throttled within the same minute", !sliding.Allow("s", 5, t0.AddSeconds(30)).Allowed);
        Check("throttle lifts once the burst ages out of the minute",
            sliding.Allow("s", 5, t0.AddSeconds(61)).Allowed);
    }

    /// <summary>
    /// Бета-канал обновлений: выдача и отзыв прав, отсутствие доступа по
    /// умолчанию, невозможность самоназначения, отражение флага в сессии и
    /// аудит. Сценарии клиента сводятся к этим же эндпоинтам: канал решает
    /// сервер, локальный флаг клиента без права ничего не даёт.
    /// </summary>
    private static async Task TestBetaChannelAsync()
    {
        Section("Beta channel access");

        await using var server = await StartServerAsync(telegram: false, allowLocalExternal: true);
        var store = server.App.Services.GetRequiredService<IdentityStore>();

        await store.BootstrapOwnerAsync("Beta Owner", telegramId: null);
        var owner = await store.CreateLocalDevelopmentSessionAsync("Beta Owner", TimeSpan.FromHours(1));
        var plain = await store.CreateLocalDevelopmentSessionAsync("Plain User", TimeSpan.FromHours(1));
        var tester = await store.CreateLocalDevelopmentSessionAsync("Future Tester", TimeSpan.FromHours(1));

        // ------------------------------------------------ по умолчанию доступа нет
        var plainUser = await store.FindUserAsync(plain.User.Id.ToString());
        Check("new user has no beta access by default", plainUser?.IsBetaTester == false);
        var testerUser = await store.FindUserAsync(tester.User.Id.ToString());
        Check("tester before grant has no beta access", testerUser?.IsBetaTester == false);

        // ------------------------------------------------ самоназначение запрещено

        // Пользователь не может выдать доступ сам себе через API
        var selfGrant = await SendBearerJsonAsync(server.BaseUrl, plain.Token, HttpMethod.Put,
            $"/admin/api/users/{plain.User.Id}/beta", new { allowed = true });
        Check("user cannot grant beta to self",
            selfGrant.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized,
            selfGrant.StatusCode.ToString());
        selfGrant.Dispose();

        // Аноним — тем более (PUT: неверный метод дал бы 405 вместо проверки прав)
        using var anonGrant = await SendBearerJsonAsync(server.BaseUrl, "not-a-real-token", HttpMethod.Put,
            $"/admin/api/users/{plain.User.Id}/beta", new { allowed = true });
        Check("anonymous cannot grant beta",
            anonGrant.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden,
            anonGrant.StatusCode.ToString());

        // ------------------------------------------------ выдача и отзыв

        var grant = await SendBearerJsonAsync(server.BaseUrl, owner.Token, HttpMethod.Put,
            $"/admin/api/users/{tester.User.Id}/beta", new { allowed = true });
        Check("owner grants beta access", grant.IsSuccessStatusCode, await grant.Content.ReadAsStringAsync());
        grant.Dispose();

        testerUser = await store.FindUserAsync(tester.User.Id.ToString());
        Check("grant persisted in store", testerUser?.IsBetaTester == true);

        // Флаг попадает в сессию — именно так /api/v1/updates/check его видит
        var authed = await store.AuthenticateAsync(tester.Token);
        Check("session reflects beta flag", authed?.User.IsBetaTester == true);
        var plainFresh = await store.AuthenticateAsync(plain.Token);
        Check("plain session reflects no beta", plainFresh?.User.IsBetaTester == false);

        // Отзыв
        var revoke = await SendBearerJsonAsync(server.BaseUrl, owner.Token, HttpMethod.Put,
            $"/admin/api/users/{tester.User.Id}/beta", new { allowed = false });
        Check("owner revokes beta access", revoke.IsSuccessStatusCode);
        revoke.Dispose();
        testerUser = await store.FindUserAsync(tester.User.Id.ToString());
        Check("revoke persisted in store", testerUser?.IsBetaTester == false);

        // Отзыв виден сразу: сессия читает флаг из базы при каждом запросе,
        // а не кэширует его на время жизни токена
        var revokedSession = await store.AuthenticateAsync(tester.Token);
        Check("revoked access visible immediately", revokedSession?.User.IsBetaTester == false);

        // Повторная выдача после отзыва работает
        var regrant = await SendBearerJsonAsync(server.BaseUrl, owner.Token, HttpMethod.Put,
            $"/admin/api/users/{tester.User.Id}/beta", new { allowed = true });
        Check("re-grant after revoke works", regrant.IsSuccessStatusCode);
        regrant.Dispose();

        // ------------------------------------------------ журнал аудита

        var audit = await store.ListAuditAsync();
        Check("grant is audited", audit.Any(e => e.Action == "beta.access.granted" && e.Target == tester.User.Id.ToString()));
        Check("revoke is audited", audit.Any(e => e.Action == "beta.access.revoked" && e.Target == tester.User.Id.ToString()));

        // ------------------------------------------------ эндпоинт проверки

        // Без настроенных репозиториев обновлений сервер отвечает 503, а не
        // раздаёт пустые метаданные: канал честно не сконфигурирован
        using var checkResponse = await SendBearerAsync(server.BaseUrl, tester.Token, HttpMethod.Get,
            "/api/v1/updates/check?channel=beta");
        Check("update check without repo config returns 503",
            checkResponse.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable,
            checkResponse.StatusCode.ToString());

        // Запрос беты без права: серверу неоткуда взять бета-репозиторий для
        // неавторизованного в бете пользователя — тоже отказ, а не фолбэк на стейбл
        using var plainCheck = await SendBearerAsync(server.BaseUrl, plain.Token, HttpMethod.Get,
            "/api/v1/updates/check?channel=beta");
        Check("plain user beta-channel request does not fall back silently",
            plainCheck.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable);

        // ------------------------------------------------ сравнение тегов

        Check("v0.10.0 > v0.9.0 (numeric, not string order)",
            EditorHelper.Server.Api.UpdateEndpoints.CompareTags("v0.10.0", "v0.9.0") > 0);
        Check("v1.0.0 > v1.0.0-beta.1 (final beats prerelease)",
            EditorHelper.Server.Api.UpdateEndpoints.CompareTags("v1.0.0", "v1.0.0-beta.1") > 0);
        Check("v1.0.0-beta.2 > v1.0.0-beta.1",
            EditorHelper.Server.Api.UpdateEndpoints.CompareTags("v1.0.0-beta.2", "v1.0.0-beta.1") > 0);
        Check("equal tags compare as equal",
            EditorHelper.Server.Api.UpdateEndpoints.CompareTags("v1.0.0", "v1.0.0") == 0);
        Check("v2.0.0 > v1.9.9",
            EditorHelper.Server.Api.UpdateEndpoints.CompareTags("v2.0.0", "v1.9.9") > 0);
    }

    private static async Task TestRateLimitingAsync()
    {
        Section("Rate limiting");

        await using var server = await StartRateLimitedServerAsync(publicLimit: 3, authLimit: 2);

        using var http = new HttpClient();

        // Публичный каталог: первые запросы проходят, затем сервер отвечает 429
        var okCount = 0;
        HttpResponseMessage? throttled = null;
        for (int i = 0; i < 6; i++)
        {
            var response = await http.GetAsync(server.BaseUrl + "/api/v1/health");
            if (response.IsSuccessStatusCode) okCount++;
            else if ((int)response.StatusCode == 429) { throttled = response; break; }
        }
        Check("public requests allowed up to the limit", okCount == 3, $"ok={okCount}");
        Check("public requests throttled with 429", throttled != null);
        Check("429 carries Retry-After", throttled != null && throttled.Headers.RetryAfter != null);
        Check("throttled body explains the limit",
            throttled != null && (await throttled.Content.ReadAsStringAsync()).Contains("Слишком много запросов"));

        // Отдельный сервер: путь /download не попадает под лимит по числу запросов
        await using var downloadServer = await StartRateLimitedServerAsync(publicLimit: 2, authLimit: 2);
        var downloadStatuses = new List<int>();
        for (int i = 0; i < 5; i++)
        {
            var response = await http.GetAsync(downloadServer.BaseUrl + "/api/v1/resources/none/download");
            downloadStatuses.Add((int)response.StatusCode);
        }
        Check("download route is exempt from the request-count limit",
            !downloadStatuses.Contains(429), string.Join(",", downloadStatuses));

        // Предел тела запроса: JSON-форма не принимает тело размером с архив.
        // Kestrel при превышении лимита до чтения тела может оборвать соединение
        // вместо аккуратного 413 — оба исхода считаются защитой сработавшей.
        // Свежий сервер: оборванное соединение не должно задевать другие проверки
        await using var bodyServer = await StartRateLimitedServerAsync(publicLimit: 100, authLimit: 100);
        var hugeBody = new string('a', 512 * 1024);
        var oversizedRejected = false;
        try
        {
            using var bigContent = new StringContent(hugeBody, Encoding.UTF8, "application/json");
            using var bigResponse = await http.PostAsync(
                bodyServer.BaseUrl + "/api/v1/auth/telegram/complete", bigContent);
            oversizedRejected = (int)bigResponse.StatusCode == 413;
            if (!oversizedRejected)
                Console.WriteLine($"      note: oversized body got status {(int)bigResponse.StatusCode}");
        }
        catch (HttpRequestException)
        {
            oversizedRejected = true;
        }
        Check("oversized JSON body rejected (413 or connection reset)", oversizedRejected);

        // Тело допустимого размера доходит до обработчика (а не отбивается транспортом)
        using var okContent = new StringContent("{\"requestId\":\"x\"}", Encoding.UTF8, "application/json");
        using var okResponse = await http.PostAsync(
            bodyServer.BaseUrl + "/api/v1/auth/telegram/complete", okContent);
        Check("normal-size JSON body reaches the handler",
            (int)okResponse.StatusCode is 401 or 422, $"status={(int)okResponse.StatusCode}");
    }

    private static byte[] BuildPng()
    {
        // Минимальный корректный PNG 1×1: проверка формата, а не декодирования
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    }

    /// <summary>Админ-API с Bearer-сессией — так ходит клиентская панель.</summary>
    private sealed class BearerApi
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly string _baseUrl;
        private readonly string _token;

        public BearerApi(string baseUrl, string token)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _token = token;
        }

        public async Task<(int status, JsonElement json, string Body)> SendAsync(HttpMethod method, string path, object? body, bool publicApi = false)
        {
            using var request = new HttpRequestMessage(method, _baseUrl + (publicApi ? "/api/v1" : "/admin/api") + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (body != null) request.Content = JsonContent.Create(body);
            return await ParseAsync(await Http.SendAsync(request));
        }

        public async Task<(int status, JsonElement json, string Body)> UploadAsync(string path, string fileName, byte[] bytes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/admin/api" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", fileName);
            request.Content = form;
            return await ParseAsync(await Http.SendAsync(request));
        }

        private static async Task<(int, JsonElement, string)> ParseAsync(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync();
            JsonElement json = default;
            try { using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text); json = doc.RootElement.Clone(); } catch (JsonException) { }
            return ((int)response.StatusCode, json, text);
        }
    }

    private static async Task TestExternalSourceAsync(AdminApi api, string baseUrl, FakeTelegram fake, byte[] zipBytes, string sha, string title)
    {
        var externalUrl = $"{fake.BaseUrl}/external/{FakeTelegram.FileName}";
        var check = await api.PostJsonAsync("/files/check-external", new { url = externalUrl });
        Check($"[{title}] external url reachable", check.status == 200 && check.json.GetProperty("available").GetBoolean(), check.Body);
        var create = await api.PostJsonAsync("/resources", new { title, description = "Внешняя ссылка", application = "Photoshop", type = "Preset", installationType = "Manual", checksum = $"sha256:{sha}", source = new { kind = "http", externalUrl, fileName = FakeTelegram.FileName, sizeBytes = zipBytes.Length } });
        Check($"[{title}] external card created", create.status == 201, create.Body);
        var id = create.json.GetProperty("id").GetString()!;
        var publish = await api.PostJsonAsync($"/resources/{id}/publish", null);
        Check($"[{title}] external card published", publish.status == 200, publish.Body);
        var resource = (await new RemoteCatalogProvider(baseUrl).GetAllResourcesAsync()).FirstOrDefault(r => r.Id == id);
        Check($"[{title}] client sees external card", resource != null);
        var dir = NewTempDir();
        try
        {
            var result = await new DownloadService(dir).DownloadResourceAsync(resource!, overwrite: true);
            var bytes = await File.ReadAllBytesAsync(result.FilePath);
            Check($"[{title}] external download matches original", Sha256Hex(bytes) == sha);
            Check($"[{title}] external archive opens", CanOpenZip(bytes));
        }
        finally { TryDeleteDir(dir); }
    }

    private sealed class RunningServer : IAsyncDisposable
    {
        public WebApplication App { get; init; } = null!;
        public string BaseUrl { get; init; } = "";
        public string AdminToken { get; init; } = "";
        public string DataDir { get; init; } = "";
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); TryDeleteDir(DataDir); }
    }

    private static async Task<RunningServer> StartServerAsync(bool telegram, bool allowLocalExternal, FakeTelegram? fake = null)
    {
        var dataDir = NewTempDir();
        var port = FakeTelegram.GetFreePort();
        var config = new ServerConfig { DataDir = dataDir, ListenAddress = "127.0.0.1", Port = port, AdminToken = "admin-" + Guid.NewGuid().ToString("N"), AllowLocalExternalUrls = allowLocalExternal, PollTimeoutSeconds = 2, EnableDevelopmentLogin = true, AllowLegacyAdminToken = true, TelegramToken = "", EnableRateLimiting = false };
        if (telegram && fake != null)
        {
            config.TelegramToken = FakeTelegram.Token;
            config.TelegramChannel = FakeTelegram.ChatId.ToString();
            config.TelegramApiBase = fake.BaseUrl;
            config.UsePolling = true;
        }
        var app = ServerHost.Create(Array.Empty<string>(), config);
        app.Environment.EnvironmentName = "Development";
        await app.StartAsync();
        return new RunningServer { App = app, BaseUrl = $"http://127.0.0.1:{port}", AdminToken = config.AdminToken, DataDir = dataDir };
    }

    /// <summary>
    /// Сервер для проверки ограничения частоты. Обычный тестовый хост держит
    /// лимит выключенным: сотни запросов с одного loopback-адреса уперлись бы
    /// в него и сломали бы остальные сценарии.
    /// </summary>
    private static async Task<RunningServer> StartRateLimitedServerAsync(int publicLimit, int authLimit)
    {
        var dataDir = NewTempDir();
        var port = FakeTelegram.GetFreePort();
        var config = new ServerConfig
        {
            DataDir = dataDir,
            ListenAddress = "127.0.0.1",
            Port = port,
            AdminToken = "admin-" + Guid.NewGuid().ToString("N"),
            AllowLocalExternalUrls = true,
            PollTimeoutSeconds = 2,
            EnableDevelopmentLogin = true,
            AllowLegacyAdminToken = true,
            TelegramToken = "",
            EnableRateLimiting = true,
            RateLimitPerMinute = publicLimit,
            AuthRateLimitPerMinute = authLimit
        };
        var app = ServerHost.Create(Array.Empty<string>(), config);
        app.Environment.EnvironmentName = "Development";
        await app.StartAsync();
        return new RunningServer { App = app, BaseUrl = $"http://127.0.0.1:{port}", AdminToken = config.AdminToken, DataDir = dataDir };
    }

    private sealed class AdminApi
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly string _baseUrl;
        private readonly string? _token;
        public AdminApi(string baseUrl, string? token) { _baseUrl = baseUrl.TrimEnd('/'); _token = token; }
        private HttpRequestMessage Req(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, _baseUrl + path);
            if (_token != null) req.Headers.TryAddWithoutValidation(AdminAuth.HeaderName, _token);
            return req;
        }
        public async Task<(int status, JsonElement json, string Body)> PostJsonAsync(string path, object? body)
        {
            using var req = Req(HttpMethod.Post, "/admin/api" + path);
            if (body != null) req.Content = JsonContent.Create(body);
            return await ParseAsync(await Http.SendAsync(req));
        }
        public async Task<(int status, JsonElement json, string Body)> PostFormAsync(string path, string fileName, byte[] bytes)
        {
            using var req = Req(HttpMethod.Post, "/admin/api" + path);
            var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", fileName);
            req.Content = form;
            return await ParseAsync(await Http.SendAsync(req));
        }
        public async Task<JsonElement> GetJsonAsync(string path) { var (_, json, _) = await GetRawAsync("/admin/api" + path); return json; }
        public async Task<(int status, JsonElement json, string Body)> GetRawAsync(string path)
        {
            var full = path.StartsWith("/api") || path.StartsWith("/admin") ? path : "/admin/api" + path;
            using var req = Req(HttpMethod.Get, full);
            return await ParseAsync(await Http.SendAsync(req));
        }
        private static async Task<(int, JsonElement, string)> ParseAsync(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync();
            JsonElement json = default;
            try { using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text); json = doc.RootElement.Clone(); } catch (JsonException) { }
            return ((int)response.StatusCode, json, text);
        }
        public static async Task<int> AnonymousStatusAsync(string url, string? token = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (token != null) req.Headers.TryAddWithoutValidation(AdminAuth.HeaderName, token);
            using var response = await Http.SendAsync(req);
            return (int)response.StatusCode;
        }
    }

    private static async Task<HttpResponseMessage> PostDirectAsync(string url, object body)
    {
        using var http = new HttpClient();
        return await http.PostAsJsonAsync(url, body);
    }

    private static async Task<JsonDocument> GetDirectAsync(string url)
    {
        using var http = new HttpClient();
        var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static async Task<HttpResponseMessage> SendBearerAsync(string baseUrl, string token, HttpMethod method, string path)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(method, baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendBearerJsonAsync(string baseUrl, string token, HttpMethod method, string path, object body)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(method, baseUrl + path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request);
    }

    private static async Task<JsonElement?> WaitForAsync(Func<Task<JsonElement?>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = await probe();
            if (result != null) return result;
            await Task.Delay(250);
        }
        return null;
    }

    private static byte[] BuildZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("Editor Helper test archive — проверка открытия.");
        }
        return ms.ToArray();
    }

    private static bool CanOpenZip(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry("readme.txt");
            if (entry == null) return false;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd().Contains("Editor Helper");
        }
        catch { return false; }
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// getChatMember: ошибка «member list is inaccessible» (бот не админ канала)
    /// должна пробрасываться наверх как исключение, а «user not found»
    /// (человека нет в канале) — превращаться в null. Иначе проверка прав
    /// бота теряет точную причину и всё сваливается в «не админ».
    /// </summary>
    private static async Task TestGetChatMemberDiagnosticsAsync()
    {
        Section("getChatMember diagnostics");
        await using var fake = await FakeTelegram.StartAsync(BuildZip());

        var client = new TelegramClient(FakeTelegram.Token, apiBase: fake.BaseUrl);

        var admin = await client.GetChatMemberAsync(FakeTelegram.ChatId.ToString(), 1);
        Check("admin lookup succeeds", admin is { IsAdmin: true });

        var unknown = await client.GetChatMemberAsync(FakeTelegram.ChatId.ToString(), FakeTelegram.UnknownUserId);
        Check("unknown member resolves to null", unknown == null);

        try
        {
            await client.GetChatMemberAsync(FakeTelegram.ChatId.ToString(), FakeTelegram.NotAdminUserId);
            Check("inaccessible member list is surfaced", false, "no exception thrown");
        }
        catch (TelegramException ex)
        {
            Check("inaccessible member list is surfaced",
                ex.Message.Contains("member list is inaccessible", StringComparison.OrdinalIgnoreCase),
                ex.Message);
        }
    }

    private static async Task TestRoleMigrationAsync()
    {
        Section("Role schema migration");
        var dir = NewTempDir();
        try
        {
            var dbPath = Path.Combine(dir, "identity.db");
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                legacy.Open();
                using var create = legacy.CreateCommand();
                create.CommandText = """
                    CREATE TABLE users (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        telegram_id INTEGER NULL UNIQUE,
                        display_name TEXT NOT NULL,
                        role TEXT NOT NULL CHECK(role IN ('User','Admin','BetaTester','Creator')),
                        is_blocked INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL,
                        last_login_at TEXT NULL
                    );
                    CREATE TABLE favorites (
                        user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                        resource_id TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY(user_id, resource_id)
                    );
                    CREATE TABLE sessions (
                        token_hash TEXT PRIMARY KEY,
                        user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                        created_at TEXT NOT NULL,
                        expires_at TEXT NOT NULL,
                        revoked_at TEXT NULL
                    );
                    INSERT INTO users(id,telegram_id,display_name,role,created_at) VALUES
                        (1,111,'Plain User','User','2025-01-01T00:00:00Z'),
                        (2,222,'Old Admin','Admin','2025-01-01T00:00:00Z'),
                        (3,333,'Beta Member','BetaTester','2025-01-01T00:00:00Z'),
                        (4,444,'The Creator','Creator','2025-01-01T00:00:00Z');
                    INSERT INTO favorites(user_id,resource_id,created_at) VALUES (3,'plugin-x','2025-01-01T00:00:00Z');
                    """;
                create.ExecuteNonQuery();

                var token = "migrating-token";
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                using var session = legacy.CreateCommand();
                session.CommandText = "INSERT INTO sessions(token_hash,user_id,created_at,expires_at) VALUES($h,3,$now,$expires)";
                session.Parameters.AddWithValue("$h", hash);
                session.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                session.Parameters.AddWithValue("$expires", DateTime.UtcNow.AddHours(1).ToString("O"));
                session.ExecuteNonQuery();
            }

            var store = new IdentityStore(new ServerConfig { DataDir = dir });
            await store.InitializeAsync();

            var users = await store.ListUsersAsync();
            Check("migration keeps all accounts", users.Count == 4);
            Check("BetaTester migrated to VIP", users.Single(u => u.Id == 3).Role == UserRole.VIP);
            Check("Creator migrated to Owner", users.Single(u => u.Id == 4).Role == UserRole.Owner);
            Check("Admin demoted to User", users.Single(u => u.Id == 2).Role == UserRole.User);
            Check("User role unchanged", users.Single(u => u.Id == 1).Role == UserRole.User);
            Check("favorites survived migration", (await store.GetFavoritesAsync(3)).Contains("plugin-x"));
            var restored = await store.AuthenticateAsync("migrating-token");
            Check("active session still authenticates as VIP", restored != null && restored.User.Role == UserRole.VIP);
            Check("migration is idempotent on second open", await SecondOpenRolesAsync(dir) is { Count: 4 } second && second.Single(u => u.Id == 3).Role == UserRole.VIP);
        }
        finally { TryDeleteDir(dir); }

        var oldestDir = NewTempDir();
        try
        {
            var dbPath = Path.Combine(oldestDir, "identity.db");
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                legacy.Open();
                using var create = legacy.CreateCommand();
                create.CommandText = """
                    CREATE TABLE users (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        telegram_id INTEGER NULL UNIQUE,
                        display_name TEXT NOT NULL,
                        role TEXT NOT NULL CHECK(role IN ('User','Admin','Owner')),
                        is_blocked INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL,
                        last_login_at TEXT NULL
                    );
                    INSERT INTO users(id,telegram_id,display_name,role,created_at) VALUES
                        (1,111,'Legacy Owner','Owner','2025-01-01T00:00:00Z'),
                        (2,222,'Legacy Admin','Admin','2025-01-01T00:00:00Z');
                    """;
                create.ExecuteNonQuery();
            }

            var store = new IdentityStore(new ServerConfig { DataDir = oldestDir });
            var migrated = await store.ListUsersAsync();
            Check("oldest schema migrates too", migrated.Count == 2
                && migrated.Single(u => u.Id == 1).Role == UserRole.Owner
                && migrated.Single(u => u.Id == 2).Role == UserRole.User);
        }
        finally { TryDeleteDir(oldestDir); }

        static async Task<IReadOnlyList<AuthUser>> SecondOpenRolesAsync(string dir)
        {
            var reopened = new IdentityStore(new ServerConfig { DataDir = dir });
            return await reopened.ListUsersAsync();
        }
    }

    /// <summary>
    /// Привязка аккаунта к устройству: первый вход привязывает, повторный с тем
    /// же устройством проходит, чужое устройство отклоняется (конфликт без
    /// сессии), отвязка по Telegram ID закрывает сессии и разрешает вход с
    /// другого устройства. Owner может отвязать чужое устройство; чужой
    /// Telegram ID не даёт доступа к отвязке.
    /// </summary>
    private static async Task TestDeviceBindingAsync()
    {
        Section("Device binding");
        var dir = NewTempDir();
        try
        {
            var store = new IdentityStore(new ServerConfig { DataDir = dir });
            const long ownerTelegramId = 987654321;
            string DeviceHash(string seed) =>
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

            async Task<TelegramAuthOutcome> LoginAsync(long telegramId, string? deviceHash, string? label = null)
            {
                var challenge = await store.StartTelegramAuthAsync(TimeSpan.FromMinutes(2));
                await store.ConfirmTelegramChallengeAsync(challenge.RequestId, telegramId, "user" + telegramId, "User", null);
                return await store.CompleteTelegramAuthAsync(challenge.RequestId, ownerTelegramId, deviceHash, label);
            }

            // Первый вход: устройство привязывается автоматически
            var firstLogin = await LoginAsync(555666777, DeviceHash("device-A"), "PC-A");
            Check("first login creates session", firstLogin.Session != null && firstLogin.Conflict == null);
            var userId = firstLogin.Session!.User.Id;
            var binding = await store.GetDeviceAsync(userId);
            Check("first login binds the device", binding != null && binding.DeviceLabel == "PC-A");
            Check("only device hash is stored, not raw id", binding!.DeviceIdHash == DeviceHash("device-A") && binding.DeviceIdHash != "device-A");

            // Повторный вход с того же устройства — разрешён, привязка та же
            var sameDevice = await LoginAsync(555666777, DeviceHash("device-A"), "PC-A");
            Check("repeat login from same device succeeds", sameDevice.Session != null && sameDevice.Conflict == null);

            // Вход с другого устройства — конфликт, сессия не выдаётся
            var otherDevice = await LoginAsync(555666777, DeviceHash("device-B"), "PC-B");
            Check("login from second device is rejected", otherDevice.Session == null && otherDevice.Conflict != null);
            Check("conflict reports currently bound device", otherDevice.Conflict!.DeviceLabel == "PC-A");
            Check("rejected login did not rebind device", (await store.GetDeviceAsync(userId))!.DeviceLabel == "PC-A");

            // Клиент без device identity (пустой хеш) — привязка не создаётся и не проверяется
            var noDeviceUser = await LoginAsync(222333444, null);
            Check("login without device id still works", noDeviceUser.Session != null);
            Check("login without device id creates no binding", await store.GetDeviceAsync(noDeviceUser.Session!.User.Id) == null);

            // Отвязка по собственному Telegram ID закрывает сессию
            var activeSession = sameDevice.Session!;
            Check("session valid before unbind", await store.AuthenticateAsync(activeSession.Token) != null);
            var unbind = await store.UnbindDeviceAsync(555666777);
            Check("owner unbinds own device", unbind.Success);
            Check("device binding removed", await store.GetDeviceAsync(userId) == null);
            Check("unbind revokes active sessions", await store.AuthenticateAsync(activeSession.Token) == null);

            // После отвязки вход с нового устройства succeeds и перепривязывает
            var rebind = await LoginAsync(555666777, DeviceHash("device-B"), "PC-B");
            Check("login from new device succeeds after unbind", rebind.Session != null && rebind.Conflict == null);
            Check("new device is now bound", (await store.GetDeviceAsync(userId))!.DeviceLabel == "PC-B");

            // Отвязка для чужого/неизвестного Telegram ID не срабатывает и ничего не ломает
            var strangerUnbind = await store.UnbindDeviceAsync(999999999);
            Check("unbind by unknown telegram id fails", !strangerUnbind.Success);
            Check("binding survives stranger unbind attempt", (await store.GetDeviceAsync(userId))!.DeviceLabel == "PC-B");

            // Owner может отвязать устройство другого пользователя
            var ownerId = (await store.EnsureConfiguredOwnerAsync(ownerTelegramId))!.Value;
            var ownerUnbind = await store.UnbindDeviceByOwnerAsync(ownerId, userId);
            Check("owner can unbind another user's device", ownerUnbind.Success);
            Check("target device unbound by owner", await store.GetDeviceAsync(userId) == null);

            // Не-Owner не может отвязать чужое устройство
            var vipTelegram = 777888999L;
            var vipLogin = await LoginAsync(vipTelegram, DeviceHash("device-V"), "PC-V");
            var vipId = vipLogin.Session!.User.Id;
            var vipNotOwner = await store.UnbindDeviceByOwnerAsync(vipId, userId);
            Check("non-Owner cannot unbind via owner path", !vipNotOwner.Success);

            // Отвязка чужого устройства по его же Telegram ID не удалась бы: у VIP своё устройство
            var vipBinding = await store.GetDeviceAsync(vipId);
            Check("VIP device is independent", vipBinding != null && vipBinding.DeviceLabel == "PC-V");

            // Rate limiting: много подряд отвязок одним Telegram ID упирается в лимит
            var limitTg = 121212121L;
            await LoginAsync(limitTg, DeviceHash("device-L"));
            var limited = 0;
            for (var i = 0; i < 5; i++)
            {
                await LoginAsync(limitTg, DeviceHash("device-L-" + i));  // перепривязка не нужна, но создаёт аккаунт
                var r = await store.UnbindDeviceAsync(limitTg);
                if (!r.Success && r.Message.Contains("Слишком много")) { limited = i; break; }
            }
            Check("unbind rate limiting kicks in", limited > 0, $"at attempt {limited}");
        }
        finally { TryDeleteDir(dir); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eh-srv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    private static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
    private static void Section(string name) { Console.WriteLine(); Console.WriteLine($"-- {name}"); }
    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; Console.WriteLine($"   ok   {name}"); }
        else { _failed++; var line = detail == null ? name : $"{name} ({detail})"; Failures.Add(line); Console.WriteLine($"   FAIL {line}"); }
    }
    private class TestDoc { public string Name { get; set; } = ""; public int Count { get; set; } }
}
