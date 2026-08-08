using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class SessionGroupControlApiTests
{
    [TestMethod]
    public async Task ActiveRetryRouteValidatesAndReturnsNodeCompatibleShape()
    {
        var business = new RecordingBusinessState(initialized: true);
        var coordinator = new RecordingSessionGroupCoordinator
        {
            NextResult = new(
                Succeeded: true,
                AlreadyConnected: false,
                ChatId: "chat-created",
                ChatName: "Codex｜project",
                Error: null),
        };
        await using var app = await StartAsync(business, coordinator);
        using var client = Client(app);

        using var unauthorized = await client.PostAsJsonAsync(
            "/sessions/feishu-group/retry",
            new { sessionId = "session-retry" });
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var invalid = Request(JsonContent.Create(new { sessionId = 42 }));
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.AreEqual(0, coordinator.RetryRequests.Count);

        using var request = Request(JsonContent.Create(new
        {
            sessionId = "  session-retry  ",
        }));
        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.IsFalse(
            body.RootElement.GetProperty("alreadyConnected").GetBoolean());
        Assert.AreEqual(
            "chat-created",
            body.RootElement.GetProperty("chatId").GetString());
        Assert.AreEqual(
            "Codex｜project",
            body.RootElement.GetProperty("chatName").GetString());
        CollectionAssert.AreEqual(
            new[] { "session-retry" },
            coordinator.RetryRequests.ToArray());

        coordinator.NextResult = new(
            Succeeded: false,
            AlreadyConnected: false,
            ChatId: null,
            ChatName: null,
            Error: "missing create chat permission");
        using var failed = Request(JsonContent.Create(new
        {
            sessionId = "session-retry",
        }));
        using var failedResponse = await client.SendAsync(failed);
        using var failedBody = JsonDocument.Parse(
            await failedResponse.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.BadRequest, failedResponse.StatusCode);
        Assert.IsFalse(failedBody.RootElement.GetProperty("ok").GetBoolean());
        StringAssert.Contains(
            failedBody.RootElement.GetProperty("error").GetString()!,
            "permission");

        await app.StopAsync();
    }

    [TestMethod]
    public async Task ActiveRetryRouteWaitsForBusinessStateInitialization()
    {
        var business = new RecordingBusinessState(initialized: false);
        var coordinator = new RecordingSessionGroupCoordinator();
        await using var app = await StartAsync(business, coordinator);
        using var client = Client(app);
        using var request = Request(JsonContent.Create(new
        {
            sessionId = "session-retry",
        }));

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(0, coordinator.RetryRequests.Count);
        await app.StopAsync();
    }

    private static async Task<WebApplication> StartAsync(
        RecordingBusinessState business,
        RecordingSessionGroupCoordinator coordinator)
    {
        var options = new BridgeHostOptions(
            Path.GetTempPath(),
            IPAddress.Loopback,
            0,
            BridgeOwnershipMode.Active,
            "session-group-control-api-test");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IBridgeControlTokenProvider>(
            new FixedTokenProvider("secret-token"));
        builder.Services.AddSingleton<IBridgeControlBusinessStateSource>(business);
        builder.Services.AddSingleton<IBridgeActiveSessionGroupCoordinator>(
            coordinator);
        var app = builder.Build();
        BridgeControlApi.MapSessionGroupControlApi(app);
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Request(HttpContent content)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/sessions/feishu-group/retry")
        {
            Content = content,
        };
        request.Headers.Add(
            BridgeControlApi.ControlTokenHeader,
            "secret-token");
        return request;
    }

    private static HttpClient Client(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private sealed class FixedTokenProvider(string token) :
        IBridgeControlTokenProvider
    {
        public ValueTask<string?> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(token);
    }

    private sealed class RecordingBusinessState(bool initialized) :
        IBridgeControlBusinessStateSource
    {
        public BridgeBusinessStateSnapshot Snapshot { get; } =
            BridgeBusinessStateSnapshot.NotInitialized with
            {
                Initialized = initialized,
                SourceStatus = initialized ? "production" : "not_loaded",
            };

        public BridgeComponentHealth ComponentHealth { get; } =
            new("recording-business", "ready");

        public Task RefreshAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingSessionGroupCoordinator :
        IBridgeActiveSessionGroupCoordinator
    {
        public BridgeSessionGroupRetryResult NextResult { get; set; } =
            new(
                Succeeded: false,
                AlreadyConnected: false,
                ChatId: null,
                ChatName: null,
                Error: "recording failure");

        public List<string> RetryRequests { get; } = [];

        public ValueTask<SessionStoreRecord?> EnsureAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SessionStoreRecord?>(null);

        public ValueTask<BridgeSessionGroupRetryResult> RetryAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetryRequests.Add(sessionId);
            return ValueTask.FromResult(NextResult);
        }

        public ValueTask<IReadOnlyList<string>> NotificationChatsAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public void ScheduleEnsure(string sessionId)
        {
        }
    }
}
