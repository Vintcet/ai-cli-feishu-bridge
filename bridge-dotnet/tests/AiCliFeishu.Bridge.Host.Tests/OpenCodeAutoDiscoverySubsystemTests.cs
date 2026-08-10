using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using AiCliFeishu.Bridge.Adapters.OpenCode;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class OpenCodeAutoDiscoverySubsystemTests
{
    [TestMethod]
    public async Task DiscoversHealthyLoopbackEndpointAndItsWorkingDirectory()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var response = ServePathOnceAsync(listener, cwd);
        var options = Options();
        var directory = new ActiveOpenCodeEndpointDirectory(options);
        await directory.StartAsync(CancellationToken.None);
        var subsystem = new OpenCodeAutoDiscoverySubsystem(
            options,
            directory,
            new RecordingEventSource(port));
        try
        {
            await subsystem.RunPassAsync();
            await response.WaitAsync(TimeSpan.FromSeconds(5));

            var registration = directory.ListRegistrations().Single(item => item.Port == port);
            Assert.AreEqual(Path.TrimEndingDirectorySeparator(cwd), registration.Cwd);
        }
        finally
        {
            listener.Stop();
            await subsystem.StopAsync(CancellationToken.None);
            await directory.StopAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task UnregistersKnownEndpointOnlyAfterThreeFailedPasses()
    {
        var options = Options();
        var directory = new ActiveOpenCodeEndpointDirectory(options);
        await directory.StartAsync(CancellationToken.None);
        var identity = directory.Register(65_000, Path.GetFullPath(Path.GetTempPath()));
        var subsystem = new OpenCodeAutoDiscoverySubsystem(
            options,
            directory,
            new RecordingEventSource(healthyPort: null));
        try
        {
            await subsystem.RunPassAsync();
            await subsystem.RunPassAsync();
            Assert.IsTrue(directory.ListRegistrations().Any(item =>
                item.Port == identity.Port && item.Generation == identity.Generation));

            await subsystem.RunPassAsync();
            Assert.IsFalse(directory.ListRegistrations().Any(item => item.Port == identity.Port));
        }
        finally
        {
            await subsystem.StopAsync(CancellationToken.None);
            await directory.StopAsync(CancellationToken.None);
        }
    }

    private static BridgeHostOptions Options() => new(
        Path.Combine(Path.GetTempPath(), $"opencode-discovery-{Guid.NewGuid():N}"),
        IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "opencode-discovery-test");

    private static async Task ServePathOnceAsync(TcpListener listener, string cwd)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var request = new byte[8 * 1024];
        _ = await stream.ReadAsync(request);
        var body = Encoding.UTF8.GetBytes(
            $"{{\"directory\":{System.Text.Json.JsonSerializer.Serialize(cwd)}}}");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private sealed class RecordingEventSource(int? healthyPort) :
        IBridgeOpenCodeEventStreamOwner
    {
        public ValueTask<bool> ProbeHealthAsync(
            OpenCodeEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(endpoint.BaseUri.Port == healthyPort);
        }

        public async IAsyncEnumerable<OpenCodeRawEvent> ReadAllAsync(
            OpenCodeEndpoint endpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
