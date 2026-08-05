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
        Assert.AreEqual("passive", body.RootElement.GetProperty("ownershipMode").GetString());
        Assert.IsFalse(body.RootElement.GetProperty("activeOwner").GetBoolean());
        Assert.IsTrue(body.RootElement.GetProperty("processId").GetInt32() > 0);
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

    private sealed class FixedTokenProvider(string token) : IBridgeControlTokenProvider
    {
        public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(token);
    }
}
