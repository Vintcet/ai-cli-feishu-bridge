using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
