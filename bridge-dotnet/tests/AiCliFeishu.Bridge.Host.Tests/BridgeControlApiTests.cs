using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCliFeishu.Bridge.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeControlApiTests
{
    private string? directory;
    private WebApplication? app;
    private HttpClient? client;

    [TestInitialize]
    public async Task Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), $"ai-cli-feishu-host-api-{Guid.NewGuid():N}");
        var options = BridgeHostOptions.Passive(directory, port: 0) with { InstanceName = "api-test" };
        app = BridgeHostApplication.Build(options, configureServices: services =>
        {
            services.RemoveAll<IBridgeControlTokenProvider>();
            services.AddSingleton<IBridgeControlTokenProvider>(new FixedTokenProvider("secret-token"));
        });
        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        client = new HttpClient { BaseAddress = new Uri(address) };
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublicHealthExposesOnlyLiveness()
    {
        using var response = await client!.GetAsync("/health");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(1, body.RootElement.EnumerateObject().Count());
    }

    [TestMethod]
    public async Task AuthenticatedHealthExposesPassiveOwnershipAndProcessIdentity()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var response = await client!.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("dotnet", body.RootElement.GetProperty("hostKind").GetString());
        Assert.AreEqual(1, body.RootElement.GetProperty("managementApiVersion").GetInt32());
        Assert.AreEqual("api-test", body.RootElement.GetProperty("instanceName").GetString());
        Assert.AreEqual("passive", body.RootElement.GetProperty("ownershipMode").GetString());
        Assert.IsFalse(body.RootElement.GetProperty("activeOwner").GetBoolean());
        Assert.IsTrue(body.RootElement.GetProperty("processId").GetInt32() > 0);
    }

    [TestMethod]
    public async Task StatusRejectsMissingTokenAndCrossSiteRequests()
    {
        using var missingToken = await client!.GetAsync("/control/status");
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingToken.StatusCode);

        using var request = StatusRequest();
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Forbidden, crossSite.StatusCode);
    }

    [TestMethod]
    public async Task StatusRefreshesAndReturnsOnlyRedactedAggregateState()
    {
        await WriteStoreAsync();

        using var staleRequest = StatusRequest();
        using var staleResponse = await client!.SendAsync(staleRequest);
        using var staleBody = JsonDocument.Parse(await staleResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("missing", staleBody.RootElement.GetProperty("store").GetProperty("status").GetString());

        using var refreshRequest = StatusRequest(refresh: true);
        using var response = await client.SendAsync(refreshRequest);
        var json = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(json);
        var root = body.RootElement;
        var store = root.GetProperty("store");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("dotnet", root.GetProperty("hostKind").GetString());
        Assert.AreEqual("api-test", root.GetProperty("instanceName").GetString());
        Assert.AreEqual("passive", root.GetProperty("ownershipMode").GetString());
        Assert.IsFalse(root.GetProperty("activeOwner").GetBoolean());
        Assert.AreEqual("loaded", store.GetProperty("status").GetString());
        Assert.AreEqual(4, store.GetProperty("files").GetInt32());
        Assert.AreEqual(2, store.GetProperty("bindings").GetInt32());
        Assert.AreEqual(2, store.GetProperty("sessions").GetInt32());
        Assert.AreEqual(1, store.GetProperty("activeSessions").GetInt32());
        Assert.AreEqual(1, store.GetProperty("endedSessions").GetInt32());
        Assert.AreEqual(1, store.GetProperty("routes").GetInt32());
        Assert.AreEqual(2, store.GetProperty("processedInbound").GetInt32());
        Assert.AreEqual(2, store.GetProperty("approvals").GetInt32());
        Assert.AreEqual(1, store.GetProperty("pendingApprovals").GetInt32());
        Assert.AreEqual(10, store.EnumerateObject().Count());
        var business = root.GetProperty("businessState");
        Assert.IsTrue(business.GetProperty("initialized").GetBoolean());
        Assert.AreEqual("loaded", business.GetProperty("sourceStatus").GetString());
        Assert.AreEqual(0, business.GetProperty("revision").GetInt64());
        Assert.AreEqual(0, business.GetProperty("rejectedFeishuIntents").GetInt64());
        Assert.AreEqual(2, business.GetProperty("sessions").GetInt32());
        Assert.AreEqual(1, business.GetProperty("activeSessions").GetInt32());
        Assert.AreEqual(1, business.GetProperty("endedSessions").GetInt32());
        Assert.AreEqual(2, business.GetProperty("approvals").GetInt32());
        Assert.AreEqual(1, business.GetProperty("pendingApprovals").GetInt32());
        Assert.AreEqual(0, business.GetProperty("inputs").GetInt32());
        Assert.AreEqual(0, business.GetProperty("pendingInputs").GetInt32());
        Assert.AreEqual(11, business.EnumerateObject().Count());
        var boundaries = root.GetProperty("boundaries");
        Assert.AreEqual(1, boundaries.GetProperty("runtimeEventHandlers").GetInt32());
        Assert.AreEqual(1, boundaries.GetProperty("feishuIntentHandlers").GetInt32());
        Assert.IsFalse(boundaries.GetProperty("runtimeCommandsEnabled").GetBoolean());
        Assert.AreEqual(
            "blocked_passive_owner",
            boundaries.GetProperty("runtimeCommandStatus").GetString());
        Assert.AreEqual(0, boundaries.GetProperty("runtimeAdapters").GetArrayLength());
        Assert.AreEqual(12, root.EnumerateObject().Count());
        Assert.IsFalse(json.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains(directory!, StringComparison.OrdinalIgnoreCase));

        await File.WriteAllTextAsync(Path.Combine(directory!, "sessions.json"), "{invalid");
        using var incompatibleRequest = StatusRequest(refresh: true);
        using var incompatibleResponse = await client.SendAsync(incompatibleRequest);
        using var incompatibleBody = JsonDocument.Parse(
            await incompatibleResponse.Content.ReadAsStringAsync());
        Assert.IsFalse(incompatibleBody.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(
            "incompatible",
            incompatibleBody.RootElement.GetProperty("store").GetProperty("status").GetString());

        await WriteStoreAsync();
        using var repairedRequest = StatusRequest(refresh: true);
        using var repairedResponse = await client.SendAsync(repairedRequest);
        using var repairedBody = JsonDocument.Parse(
            await repairedResponse.Content.ReadAsStringAsync());
        Assert.IsTrue(repairedBody.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(
            "loaded",
            repairedBody.RootElement.GetProperty("store").GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task RuntimeEventEndpointAuthenticatesValidatesAndPublishesThroughIngress()
    {
        await WriteStoreAsync();

        using var missingToken = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/runtime-events")
        {
            Content = EventContent("event-1", "turn.started", "secret-active"),
        };
        using var missingResponse = await client!.SendAsync(missingToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingResponse.StatusCode);

        using var refresh = StatusRequest(refresh: true);
        using var refreshResponse = await client.SendAsync(refresh);
        Assert.AreEqual(HttpStatusCode.OK, refreshResponse.StatusCode);

        using var invalid = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/runtime-events")
        {
            Content = JsonContent.Create(new { eventType = "turn.started" }),
        };
        invalid.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/runtime-events")
        {
            Content = EventContent("event-1", "turn.started", "secret-active"),
        };
        request.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

        using var duplicate = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/runtime-events")
        {
            Content = EventContent("event-1", "turn.started", "secret-active"),
        };
        duplicate.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var duplicateResponse = await client.SendAsync(duplicate);
        Assert.AreEqual(HttpStatusCode.Accepted, duplicateResponse.StatusCode);

        using var statusRequest = StatusRequest();
        using var statusResponse = await client.SendAsync(statusRequest);
        using var statusBody = JsonDocument.Parse(
            await statusResponse.Content.ReadAsStringAsync());
        var business = statusBody.RootElement.GetProperty("businessState");
        Assert.AreEqual(1, business.GetProperty("revision").GetInt64());
        Assert.AreEqual(
            "loaded",
            statusBody.RootElement.GetProperty("store").GetProperty("status").GetString());
        Assert.AreEqual(1, business.GetProperty("activeSessions").GetInt32());
    }

    [TestMethod]
    public async Task FeishuIntentEndpointAuthenticatesValidatesAndReturnsShadowDecision()
    {
        using var missingToken = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/feishu-intents")
        {
            Content = IntentContent("intent-1", "message.prompt"),
        };
        using var missingResponse = await client!.SendAsync(missingToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingResponse.StatusCode);

        using var invalid = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/feishu-intents")
        {
            Content = JsonContent.Create(new { intentType = "message.prompt" }),
        };
        invalid.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/feishu-intents")
        {
            Content = IntentContent("intent-1", "message.prompt"),
        };
        request.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("warning", body.RootElement.GetProperty("toastType").GetString());
        StringAssert.Contains(
            body.RootElement.GetProperty("toastContent").GetString()!,
            "只读观测");
    }

    [TestMethod]
    public async Task RuntimeCommandEndpointRejectsUnauthenticatedInvalidAndPassiveCommands()
    {
        await WriteStoreAsync();
        using var refresh = StatusRequest(refresh: true);
        using var refreshResponse = await client!.SendAsync(refresh);
        Assert.AreEqual(HttpStatusCode.OK, refreshResponse.StatusCode);

        using var missingToken = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/runtime-commands")
        {
            Content = CommandContent("command-1"),
        };
        using var missingResponse = await client!.SendAsync(missingToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingResponse.StatusCode);

        using var invalid = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/runtime-commands")
        {
            Content = JsonContent.Create(new { commandType = RuntimeCommandTypes.PromptSend }),
        };
        invalid.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/runtime-commands")
        {
            Content = CommandContent("command-1"),
        };
        request.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [TestMethod]
    public async Task RuntimeCommandEndpointReturnsUnavailableBeforeStoreProjectionIsReady()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/runtime-commands")
        {
            Content = CommandContent("command-unavailable"),
        };
        request.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var response = await client!.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [TestMethod]
    public async Task StandardIngressReturnsUnavailableWhenStoreProjectionIsIncompatible()
    {
        await File.WriteAllTextAsync(Path.Combine(directory!, "sessions.json"), "{invalid");
        using var refresh = StatusRequest(refresh: true);
        using var refreshResponse = await client!.SendAsync(refresh);
        Assert.AreEqual(HttpStatusCode.OK, refreshResponse.StatusCode);

        using var runtimeRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/runtime-events")
        {
            Content = EventContent("event-unavailable", "turn.started", "session-1"),
        };
        runtimeRequest.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var runtimeResponse = await client.SendAsync(runtimeRequest);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, runtimeResponse.StatusCode);

        using var intentRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/control/feishu-intents")
        {
            Content = IntentContent("intent-unavailable", "message.prompt"),
        };
        intentRequest.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        using var intentResponse = await client.SendAsync(intentRequest);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, intentResponse.StatusCode);
    }

    [TestMethod]
    public async Task ShutdownRejectsMissingTokenAndCrossSiteRequests()
    {
        using var missingToken = await client!.PostAsJsonAsync("/control/shutdown", new { });
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingToken.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/control/shutdown")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await client!.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Forbidden, crossSite.StatusCode);
    }

    [TestMethod]
    public async Task ShutdownRequiresMatchingHostApiAndProcessIdentity()
    {
        using var wrongHost = ShutdownRequest("node", Environment.ProcessId);
        using var wrongHostResponse = await client!.SendAsync(wrongHost);
        Assert.AreEqual(HttpStatusCode.Conflict, wrongHostResponse.StatusCode);

        using var wrongProcess = ShutdownRequest("dotnet", Environment.ProcessId + 1);
        using var wrongProcessResponse = await client.SendAsync(wrongProcess);
        Assert.AreEqual(HttpStatusCode.Conflict, wrongProcessResponse.StatusCode);
    }

    [TestMethod]
    public async Task ShutdownAcceptsOnlyTheCurrentAuthenticatedHostIdentity()
    {
        using var request = ShutdownRequest("dotnet", Environment.ProcessId);
        using var response = await client!.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static HttpRequestMessage ShutdownRequest(string hostKind, int processId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/control/shutdown")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        request.Headers.Add(BridgeControlApi.ExpectedHostKindHeader, hostKind);
        request.Headers.Add(
            BridgeControlApi.ManagementApiVersionHeader,
            BridgeHostManagementContract.ApiVersion.ToString());
        request.Headers.Add(BridgeControlApi.ExpectedProcessIdHeader, processId.ToString());
        return request;
    }

    private static HttpRequestMessage StatusRequest(bool refresh = false)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            refresh ? "/control/status?refresh=1" : "/control/status");
        request.Headers.Add(BridgeControlApi.ControlTokenHeader, "secret-token");
        return request;
    }

    private static JsonContent EventContent(
        string eventId,
        string eventType,
        string sessionId) => JsonContent.Create(new
        {
            protocolVersion = 1,
            runtime = "codex",
            session = new { externalId = sessionId, cwd = "K:\\secret-active" },
            traceId = $"trace-{eventId}",
            eventId,
            eventType,
            occurredAt = "2026-08-06T00:02:00Z",
            payload = new { turnId = "turn-1" },
        });

    private static JsonContent IntentContent(
        string eventId,
        string intentType) => JsonContent.Create(new
        {
            eventId,
            intentType,
            operatorOpenId = "operator-1",
            chatId = "chat-1",
            messageId = "message-1",
            chatType = "group",
            traceId = $"trace-{eventId}",
            text = "继续",
        });

    private static JsonContent CommandContent(string commandId) => JsonContent.Create(new
    {
        protocolVersion = 1,
        runtime = "codex",
        session = new { externalId = "secret-active", cwd = "K:\\secret-active" },
        traceId = $"trace-{commandId}",
        commandId,
        commandType = "prompt.send",
        createdAt = "2026-08-06T00:02:00Z",
        payload = new { prompt = "继续", mode = "steer" },
    });

    private async Task WriteStoreAsync()
    {
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "bindings.json"),
            """
            {"users":{"secret-owner":{"openId":"secret-owner","chatId":"secret-chat-1","chatType":"p2p","boundAt":"2026-08-06T00:00:00Z"},"secret-user":{"openId":"secret-user","chatId":"secret-chat-2","chatType":"group","boundAt":"2026-08-06T00:00:00Z"}},"ownerOpenId":"secret-owner"}
            """);
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "sessions.json"),
            """
            {"sessions":{"secret-active":{"sessionId":"secret-active","cwd":"K:\\secret-active","status":"waiting","runtime":"codex","openedAt":"2026-08-06T00:00:00Z","lastSeenAt":"2026-08-06T00:01:00Z"},"secret-ended":{"sessionId":"secret-ended","cwd":"K:\\secret-ended","status":"ended","runtime":"claudecode","openedAt":"2026-08-05T00:00:00Z","lastSeenAt":"2026-08-06T00:00:00Z","endedAt":"2026-08-06T00:00:00Z"}}}
            """);
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "message-routes.json"),
            """
            {"messages":{"secret-message":{"messageId":"secret-message","sessionId":"secret-active","chatId":"secret-chat-1","kind":"approval","createdAt":"2026-08-06T00:00:00Z","requestId":"secret-request-pending"}},"processedInbound":{"secret-inbound-1":"2026-08-06T00:00:00Z","secret-inbound-2":"2026-08-06T00:00:01Z"}}
            """);
        await File.WriteAllTextAsync(
            Path.Combine(directory!, "approvals.json"),
            """
            {"requests":{"secret-request-pending":{"requestId":"secret-request-pending","sessionId":"secret-active","turnId":"secret-turn-1","cwd":"K:\\secret-active","toolName":"secret-tool","toolPreview":"secret-preview","createdAt":"2026-08-06T00:00:00Z","expiresAt":"2026-08-06T00:05:00Z","status":"pending","messageIds":["secret-message"]},"secret-request-resolved":{"requestId":"secret-request-resolved","sessionId":"secret-ended","turnId":"secret-turn-2","cwd":"K:\\secret-ended","toolName":"secret-tool","toolPreview":"secret-preview","createdAt":"2026-08-05T00:00:00Z","expiresAt":"2026-08-05T00:05:00Z","status":"resolved","messageIds":[],"resolution":"allow","resolvedAt":"2026-08-05T00:01:00Z"}}}
            """);
    }

    private sealed class FixedTokenProvider(string token) : IBridgeControlTokenProvider
    {
        public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(token);
    }
}
