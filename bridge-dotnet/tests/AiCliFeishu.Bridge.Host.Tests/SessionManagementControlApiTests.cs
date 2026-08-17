using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class SessionManagementControlApiTests
{
    [TestMethod]
    public async Task AliasRouteAuthenticatesPersistsAndSynchronizesGroupName()
    {
        var session = Session(
            "session-alias",
            new()
            {
                ["alias"] = JsonSerializer.SerializeToElement("新名称"),
                ["feishuChatId"] = JsonSerializer.SerializeToElement("chat-session"),
                ["feishuChatName"] = JsonSerializer.SerializeToElement("Codex｜旧名称"),
                ["feishuChatOrdinal"] = JsonSerializer.SerializeToElement(1),
            });
        var aliases = new RecordingAliasOwner
        {
            Result = new(session, null, null),
        };
        var history = new RecordingHistoryOwner();
        var groups = new RecordingGroupStateOwner(session);
        var gateway = new RecordingGateway();
        await using var app = await StartAsync(aliases, history, groups, gateway);
        using var client = Client(app);

        using var unauthorized = await client.PostAsJsonAsync(
            "/sessions/alias",
            new { sessionId = "session-alias", alias = "新名称" });
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var request = Request(
            "/sessions/alias",
            new { sessionId = "  session-alias  ", alias = "  新名称  " });
        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(
            "新名称",
            body.RootElement.GetProperty("session").GetProperty("alias").GetString());
        CollectionAssert.AreEqual(
            new[] { ("session-alias", "  新名称  ") },
            aliases.Calls.ToArray());
        CollectionAssert.AreEqual(
            new[] { ("chat-session", "Codex｜新名称") },
            gateway.Renames.ToArray());
        CollectionAssert.AreEqual(
            new[] { ("session-alias", "chat-session", "Codex｜新名称") },
            groups.NameUpdates.ToArray());

        await app.StopAsync();
    }

    [TestMethod]
    public async Task HistoryRouteValidatesAndReturnsCompatibleShape()
    {
        var aliases = new RecordingAliasOwner();
        var history = new RecordingHistoryOwner
        {
            Result = new(Session("session-history"), null),
        };
        var groups = new RecordingGroupStateOwner(Session("unused"));
        var gateway = new RecordingGateway();
        await using var app = await StartAsync(aliases, history, groups, gateway);
        using var client = Client(app);

        using var invalid = Request(
            "/sessions/history/hide",
            new { sessionId = 42 });
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        using var request = Request(
            "/sessions/history/hide",
            new { sessionId = " session-history " });
        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual("session-history", body.RootElement.GetProperty("sessionId").GetString());
        CollectionAssert.AreEqual(
            new[] { "session-history" },
            history.Calls.ToArray());

        await app.StopAsync();
    }

    private static async Task<WebApplication> StartAsync(
        RecordingAliasOwner aliases,
        RecordingHistoryOwner history,
        RecordingGroupStateOwner groups,
        RecordingGateway gateway)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(new BridgeHostOptions(
            Path.GetTempPath(),
            IPAddress.Loopback,
            0,
            BridgeOwnershipMode.Active,
            "session-management-api-test"));
        builder.Services.AddSingleton<IBridgeControlTokenProvider>(
            new FixedTokenProvider("secret-token"));
        builder.Services.AddSingleton<IBridgeControlBusinessStateSource>(
            new InitializedBusinessState());
        builder.Services.AddSingleton<IBridgeActiveSessionAliasStateOwner>(aliases);
        builder.Services.AddSingleton<IBridgeActiveSessionHistoryStateOwner>(history);
        builder.Services.AddSingleton<IBridgeActiveSessionGroupStateOwner>(groups);
        builder.Services.AddSingleton<IFeishuGateway>(gateway);
        var app = builder.Build();
        BridgeControlApi.MapSessionManagementControlApi(app);
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Request(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        return request;
    }

    private static HttpClient Client(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static SessionStoreRecord Session(
        string sessionId,
        Dictionary<string, JsonElement>? extensions = null) => new()
        {
            SessionId = sessionId,
            ShortId = sessionId[^Math.Min(8, sessionId.Length)..],
            Cwd = $"K:/workspace/{sessionId}",
            ProjectName = "project",
            Runtime = "codex",
            Status = SessionStatuses.Ended,
            OpenedAt = "2026-08-17T00:00:00.000Z",
            LastSeenAt = "2026-08-17T00:01:00.000Z",
            ExtensionData = extensions,
        };

    private sealed class FixedTokenProvider(string token) :
        IBridgeControlTokenProvider
    {
        public ValueTask<string?> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(token);
    }

    private sealed class InitializedBusinessState : IBridgeControlBusinessStateSource
    {
        public BridgeBusinessStateSnapshot Snapshot { get; } =
            BridgeBusinessStateSnapshot.NotInitialized with
            {
                Initialized = true,
                SourceStatus = "production",
            };

        public BridgeComponentHealth ComponentHealth { get; } =
            new("session-management-test", "ready");

        public Task RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingAliasOwner : IBridgeActiveSessionAliasStateOwner
    {
        public BridgeSessionAliasUpdateResult Result { get; set; } =
            new(null, null, "recording alias failure");
        public List<(string SessionId, string? Alias)> Calls { get; } = [];

        public ValueTask<BridgeSessionAliasUpdateResult> UpdateSessionAliasAsync(
            string sessionId,
            string? alias,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionId, alias));
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class RecordingHistoryOwner : IBridgeActiveSessionHistoryStateOwner
    {
        public BridgeSessionHistoryHideResult Result { get; set; } =
            new(null, "recording history failure");
        public List<string> Calls { get; } = [];

        public ValueTask<BridgeSessionHistoryHideResult> HideSessionFromHistoryAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(sessionId);
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class RecordingGroupStateOwner(SessionStoreRecord session) :
        IBridgeActiveSessionGroupStateOwner
    {
        public List<(string SessionId, string ChatId, string Name)> NameUpdates
        { get; } = [];

        public ValueTask<BridgeSessionGroupNameUpdateResult> UpdateSessionGroupNameAsync(
            string sessionId,
            string expectedChatId,
            string name,
            CancellationToken cancellationToken = default)
        {
            NameUpdates.Add((sessionId, expectedChatId, name));
            return ValueTask.FromResult(new BridgeSessionGroupNameUpdateResult(session, null));
        }

        public ValueTask<BridgeSessionGroupNameUpdateResult> EnsureSessionGroupOrdinalAsync(
            string sessionId,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<BridgeSessionGroupNameUpdateResult> BindSessionGroupAsync(
            string sessionId,
            int expectedOrdinal,
            string expectedOwnerOpenId,
            string chatId,
            string name,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<BridgeSessionGroupNameUpdateResult> RecordSessionGroupErrorAsync(
            string sessionId,
            int expectedOrdinal,
            string expectedOwnerOpenId,
            string error,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<BridgeSessionGroupNameUpdateResult> ClearSessionGroupErrorAsync(
            string sessionId,
            int expectedOrdinal,
            string expectedOwnerOpenId,
            CancellationToken cancellationToken = default) => throw Unused();

        public ValueTask<BridgeSessionGroupNameUpdateResult> ClearSessionGroupAsync(
            string sessionId,
            string expectedChatId,
            CancellationToken cancellationToken = default) => throw Unused();

        private static AssertFailedException Unused() =>
            new("会话管理 API 测试不应调用其他群状态方法。");
    }

    private sealed class RecordingGateway : IFeishuGateway
    {
        public List<(string ChatId, string Name)> Renames { get; } = [];

        public Task UpdateSessionGroupNameAsync(
            string chatId,
            string name,
            CancellationToken cancellationToken = default)
        {
            Renames.Add((chatId, name));
            return Task.CompletedTask;
        }

        public Task<string> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default) => throw Unused();
        public Task<string> ReplyTextAsync(string messageId, string text, CancellationToken cancellationToken = default) => throw Unused();
        public Task<string> SendCardAsync(string chatId, FeishuCardView card, string? idempotencyKey = null, CancellationToken cancellationToken = default) => throw Unused();
        public Task PatchCardAsync(string messageId, FeishuCardView card, CancellationToken cancellationToken = default) => throw Unused();
        public Task<FeishuSessionGroup> CreateSessionGroupAsync(string ownerOpenId, string name, string description, CancellationToken cancellationToken = default) => throw Unused();
        public Task DeleteSessionGroupAsync(string chatId, CancellationToken cancellationToken = default) => throw Unused();
        public Task<long> DownloadMessageResourceAsync(string messageId, string fileKey, string resourceType, string destinationPath, long maxBytes, CancellationToken cancellationToken = default) => throw Unused();
        public Task<string> SendLocalFileAsync(string chatId, string filePath, CancellationToken cancellationToken = default) => throw Unused();

        private static AssertFailedException Unused() =>
            new("会话管理 API 测试不应调用其他飞书方法。");
    }
}
