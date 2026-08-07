using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class OpenCodeControlApiTests
{
    private static readonly string Cwd = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "opencode-control-api-project"));

    [TestMethod]
    public async Task PassiveRoutesFailBeforeResolvingActiveDirectory()
    {
        var options = BridgeHostOptions.Passive(
            Path.Combine(Path.GetTempPath(), $"opencode-passive-api-{Guid.NewGuid():N}"),
            port: 0);
        await using var app = BridgeHostApplication.Build(
            options,
            configureServices: services =>
            {
                services.AddSingleton<IBridgeControlTokenProvider>(
                    new FixedTokenProvider("secret-token"));
            });
        await app.StartAsync();
        using var client = Client(app);
        using var request = Request(
            "/opencode/register",
            JsonContent.Create(new { port = 5_201, cwd = Cwd }));

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await app.StopAsync();
    }

    [TestMethod]
    public async Task PassiveLaunchFailsBeforeResolvingActiveLifecycleOwner()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);
        var constructed = false;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IBridgeControlTokenProvider>(
            new FixedTokenProvider("secret-token"));
        builder.Services.AddSingleton<IBridgeOpenCodeRuntimeLifecycleOwner>(_ =>
        {
            constructed = true;
            throw new InvalidOperationException("must not resolve");
        });
        await using var app = builder.Build();
        BridgeControlApi.MapOpenCodeEndpointApi(app);
        await app.StartAsync();
        using var client = Client(app);
        using var request = Request(
            "/opencode/launch",
            JsonContent.Create(new { cwd = Cwd, sessionId = "session-passive" }));

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.IsFalse(constructed);
        await app.StopAsync();
    }

    [TestMethod]
    public async Task ActiveRoutesEnforceBoundaryAndRegisterCompatiblePayloads()
    {
        var options = new BridgeHostOptions(
            Path.GetTempPath(),
            IPAddress.Loopback,
            0,
            BridgeOwnershipMode.Active,
            "opencode-control-api-test");
        var directory = new ActiveOpenCodeEndpointDirectory(options);
        await directory.StartAsync(CancellationToken.None);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IBridgeControlTokenProvider>(
            new FixedTokenProvider("secret-token"));
        builder.Services.AddSingleton<IBridgeOpenCodeEndpointRegistrationDirectory>(
            directory);
        var lifecycle = new RecordingOpenCodeLifecycleOwner(directory);
        builder.Services.AddSingleton<IBridgeOpenCodeRuntimeLifecycleOwner>(lifecycle);
        await using var app = builder.Build();
        BridgeControlApi.MapOpenCodeEndpointApi(app);
        await app.StartAsync();
        using var client = Client(app);

        using var unauthorized = await client.PostAsJsonAsync(
            "/opencode/register",
            new { port = 5_202, cwd = Cwd });
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var crossSite = Request(
            "/opencode/register",
            JsonContent.Create(new { port = 5_202, cwd = Cwd }));
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSiteResponse = await client.SendAsync(crossSite);
        Assert.AreEqual(HttpStatusCode.Forbidden, crossSiteResponse.StatusCode);

        using var wrongMedia = Request(
            "/opencode/register",
            new StringContent("{}", Encoding.UTF8, "text/plain"));
        using var wrongMediaResponse = await client.SendAsync(wrongMedia);
        Assert.AreEqual(
            HttpStatusCode.UnsupportedMediaType,
            wrongMediaResponse.StatusCode);

        var oversizedJson = JsonSerializer.Serialize(new
        {
            port = 5_202,
            cwd = Cwd,
            padding = new string('x', 1024 * 1024),
        });
        using var oversized = Request(
            "/opencode/register",
            new StringContent(oversizedJson, Encoding.UTF8, "application/json"));
        using var oversizedResponse = await client.SendAsync(oversized);
        Assert.AreEqual(
            HttpStatusCode.RequestEntityTooLarge,
            oversizedResponse.StatusCode);

        using var invalidLaunch = Request(
            "/opencode/launch",
            JsonContent.Create(new { cwd = Cwd, sessionId = 42 }));
        using var invalidLaunchResponse = await client.SendAsync(invalidLaunch);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidLaunchResponse.StatusCode);
        Assert.AreEqual(0, lifecycle.Reservations.Count);

        using var launch = Request(
            "/opencode/launch",
            JsonContent.Create(new { cwd = Cwd, sessionId = "session-resume" }));
        using var launchResponse = await client.SendAsync(launch);
        using var launchBody = JsonDocument.Parse(
            await launchResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, launchResponse.StatusCode);
        Assert.IsTrue(launchBody.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(5_203, launchBody.RootElement.GetProperty("port").GetInt32());
        Assert.AreEqual(Cwd, launchBody.RootElement.GetProperty("cwd").GetString());
        Assert.AreEqual((Cwd, "session-resume"), lifecycle.Reservations.Single());
        Assert.AreEqual(
            5_203,
            directory.FindRegistrationBySession("session-resume")?.Port);

        using var releaseLaunch = Request(
            "/opencode/unregister",
            JsonContent.Create(new { port = 5_203 }));
        using var releaseLaunchResponse = await client.SendAsync(releaseLaunch);
        Assert.AreEqual(HttpStatusCode.OK, releaseLaunchResponse.StatusCode);
        CollectionAssert.Contains(lifecycle.ReleasedPorts.ToArray(), 5_203);
        Assert.IsNull(directory.FindRegistrationBySession("session-resume"));

        using var register = Request(
            "/opencode/register",
            JsonContent.Create(new { port = "5202", cwd = Cwd }));
        using var registerResponse = await client.SendAsync(register);
        using var registerBody = JsonDocument.Parse(
            await registerResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.IsTrue(registerBody.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(5_202, registerBody.RootElement.GetProperty("port").GetInt32());
        Assert.AreEqual(Cwd, registerBody.RootElement.GetProperty("cwd").GetString());
        Assert.AreEqual(1, directory.ListRegistrations().Count);

        using var unregister = Request(
            "/opencode/unregister",
            JsonContent.Create(new { port = 5_202 }));
        using var unregisterResponse = await client.SendAsync(unregister);
        using var unregisterBody = JsonDocument.Parse(
            await unregisterResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, unregisterResponse.StatusCode);
        Assert.AreEqual(2, unregisterBody.RootElement.EnumerateObject().Count());
        Assert.AreEqual(0, directory.ListRegistrations().Count);
        CollectionAssert.Contains(lifecycle.ReleasedPorts.ToArray(), 5_202);

        await app.StopAsync();
        await directory.StopAsync(CancellationToken.None);
    }

    private static HttpRequestMessage Request(string path, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content,
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

    private sealed class FixedTokenProvider(string token) : IBridgeControlTokenProvider
    {
        public ValueTask<string?> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(token);
    }

    private sealed class RecordingOpenCodeLifecycleOwner(
        IBridgeOpenCodeEndpointRegistrationDirectory directory) :
        IBridgeOpenCodeRuntimeLifecycleOwner
    {
        public List<(string Cwd, string? SessionId)> Reservations { get; } = [];
        public List<int> ReleasedPorts { get; } = [];

        public ValueTask<BridgeOpenCodeEndpointIdentity> ReserveAsync(
            string cwd,
            string? sessionExternalId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reservations.Add((cwd, sessionExternalId));
            var identity = directory.TryRegisterAvailable(5_203, cwd) ??
                throw new InvalidOperationException("test port unavailable");
            if (sessionExternalId is not null &&
                !directory.RememberSession(
                    identity.Port,
                    identity.Generation,
                    sessionExternalId))
            {
                throw new InvalidOperationException("test session unavailable");
            }
            return ValueTask.FromResult(identity);
        }

        public bool Release(int port)
        {
            ReleasedPorts.Add(port);
            return directory.Unregister(port);
        }

        public Task LaunchAsync(
            RuntimeCommandContext context,
            string requestedExternalId,
            string cwd,
            bool elevated,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResumeAsync(
            RuntimeCommandContext context,
            string sessionExternalId,
            string? cwd,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WaitUntilReadyAsync(
            RuntimeCommandContext context,
            string sessionExternalId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(
            RuntimeCommandContext context,
            string sessionExternalId,
            string? reason,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
