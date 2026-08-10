using System.Net;
using System.Runtime.CompilerServices;
using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveFeishuEventSourceTests
{
    [TestMethod]
    public void ProductionConstructionAndDisposalDoNotReadCredentials()
    {
        var credentials = new RecordingCredentialSource();

        using (var source = new ActiveFeishuEventSource(
            ActiveOptions(),
            credentials))
        {
            Assert.AreEqual(0, credentials.Reads);
        }

        Assert.AreEqual(0, credentials.Reads);
    }

    [TestMethod]
    public async Task DefersCredentialsAndTransportUntilFirstReadAndBuildsOnce()
    {
        var credentials = new RecordingCredentialSource();
        var inner = new RecordingEventSource();
        var factories = 0;
        using var source = new ActiveFeishuEventSource(
            ActiveOptions(),
            credentials,
            value =>
            {
                factories++;
                Assert.AreSame(credentials.Value, value);
                return inner;
            });

        Assert.AreEqual(0, credentials.Reads);
        Assert.AreEqual(0, factories);

        await DrainAsync(source.ReadAllAsync());
        await DrainAsync(source.ReadAllAsync());

        Assert.AreEqual(1, credentials.Reads);
        Assert.AreEqual(1, factories);
        Assert.AreEqual(2, inner.Reads);
    }

    [TestMethod]
    public async Task ForwardsCancellationTokenToOwnedEventStream()
    {
        var inner = new RecordingEventSource();
        using var source = new ActiveFeishuEventSource(
            ActiveOptions(),
            new RecordingCredentialSource(),
            _ => inner);
        using var cancellation = new CancellationTokenSource();

        await DrainAsync(source.ReadAllAsync(cancellation.Token));

        Assert.AreEqual(cancellation.Token, inner.LastCancellationToken);
    }

    [TestMethod]
    public void CancelledReadDoesNotLoadCredentialsOrTransport()
    {
        var credentials = new RecordingCredentialSource();
        var factories = 0;
        using var source = new ActiveFeishuEventSource(
            ActiveOptions(),
            credentials,
            _ =>
            {
                factories++;
                return new RecordingEventSource();
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => source.ReadAllAsync(cancellation.Token));

        Assert.AreEqual(0, credentials.Reads);
        Assert.AreEqual(0, factories);
    }

    [TestMethod]
    public void RejectsPassiveOptionsBeforeReadingCredentials()
    {
        var credentials = new RecordingCredentialSource();
        var factories = 0;
        using var source = new ActiveFeishuEventSource(
            BridgeHostOptions.Passive(Path.GetTempPath()),
            credentials,
            _ =>
            {
                factories++;
                return new RecordingEventSource();
            });

        var error = Assert.ThrowsException<InvalidOperationException>(
            () => source.ReadAllAsync());

        StringAssert.Contains(error.Message, "只能用于 Active Host");
        Assert.AreEqual(0, credentials.Reads);
        Assert.AreEqual(0, factories);
    }

    [TestMethod]
    public void DisposedSourceRejectsReadBeforeLoadingCredentials()
    {
        var credentials = new RecordingCredentialSource();
        var source = new ActiveFeishuEventSource(
            ActiveOptions(),
            credentials,
            _ => new RecordingEventSource());

        source.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => source.ReadAllAsync());
        Assert.AreEqual(0, credentials.Reads);
    }

    [TestMethod]
    public void CreatesCompatibleFeishuWebSocketOptionsWithoutRedactionLoss()
    {
        var credentials = new BridgeFeishuCredentials(
            "cli_event_stream",
            "event-stream-secret");

        var options = ActiveFeishuEventSource.CreateOptions(credentials);

        Assert.AreEqual(credentials.AppId, options.AppId);
        Assert.AreEqual(credentials.AppSecret, options.AppSecret);
        Assert.AreEqual(new Uri("https://open.feishu.cn/"), options.BaseUri);
        Assert.IsNull(options.ReconnectDelay);
        Assert.IsNull(options.DefaultPingInterval);
        Assert.IsFalse(options.ToString().Contains(
            credentials.AppId,
            StringComparison.Ordinal));
        Assert.IsFalse(options.ToString().Contains(
            credentials.AppSecret,
            StringComparison.Ordinal));
    }

    private static async Task DrainAsync(
        IAsyncEnumerable<FeishuInboundEnvelope> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private static BridgeHostOptions ActiveOptions() => new(
        Path.Combine(
            Path.GetTempPath(),
            $"bridge-active-feishu-events-{Guid.NewGuid():N}",
            "data"),
        IPAddress.Loopback,
        8765,
        BridgeOwnershipMode.Active,
        "feishu-events-test");

    private sealed class RecordingCredentialSource : IBridgeFeishuCredentialSource
    {
        public BridgeFeishuCredentials Value { get; } =
            new("cli_recording", "recording-secret");

        public int Reads { get; private set; }

        public BridgeFeishuCredentials Credentials
        {
            get
            {
                Reads++;
                return Value;
            }
        }
    }

    private sealed class RecordingEventSource : IFeishuEventSource
    {
        public int Reads { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public async IAsyncEnumerable<FeishuInboundEnvelope> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Reads++;
            LastCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
