using System.Runtime.CompilerServices;
using AiCliFeishu.Bridge.Adapters.Feishu;
using Microsoft.Extensions.DependencyInjection;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeFeishuEventSubsystemTests
{
    [TestMethod]
    public void HostStartsEventPumpAfterBusinessStateOwner()
    {
        var options = BridgeHostOptions.Passive(
            Path.Combine(Path.GetTempPath(), $"bridge-feishu-worker-{Guid.NewGuid():N}"));
        using var app = BridgeHostApplication.Build(options);

        var subsystems = app.Services.GetServices<IBridgeHostSubsystem>()
            .Select(subsystem => subsystem.Name)
            .ToArray();

        var businessStateIndex = Array.IndexOf(subsystems, "business-state-owner");
        var eventPumpIndex = Array.IndexOf(subsystems, "feishu-event-pump");
        Assert.IsTrue(businessStateIndex >= 0);
        Assert.AreEqual(businessStateIndex + 1, eventPumpIndex);
    }

    [TestMethod]
    public async Task PassiveSubsystemRunsEmptyEventSourceWithoutPublishingIntent()
    {
        var source = new RecordingEmptyFeishuEventSource();
        var sink = new RecordingFeishuIntentSink();
        var subsystem = new BridgeFeishuEventSubsystem(
            new FeishuEventPump(
                source,
                new FeishuEventNormalizer(new InMemoryFeishuInboundDeduplicator()),
                sink),
            BridgeHostOptions.Passive(Path.GetTempPath()));

        await subsystem.StartAsync(CancellationToken.None);

        Assert.AreEqual(1, source.Subscriptions);
        Assert.AreEqual(0, sink.Published);
        Assert.AreEqual("passive", subsystem.ComponentHealth.Status);
        Assert.AreEqual("event-source-disabled", subsystem.ComponentHealth.Detail);

        await subsystem.StopAsync(CancellationToken.None);
        Assert.AreEqual("starting", subsystem.ComponentHealth.Status);
    }

    private sealed class RecordingEmptyFeishuEventSource : IFeishuEventSource
    {
        public int Subscriptions { get; private set; }

        public async IAsyncEnumerable<FeishuInboundEnvelope> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Subscriptions++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingFeishuIntentSink : IFeishuIntentSink
    {
        public int Published { get; private set; }

        public Task<FeishuCallbackResult?> PublishAsync(
            FeishuIntent intent,
            CancellationToken cancellationToken = default)
        {
            Published++;
            return Task.FromResult<FeishuCallbackResult?>(null);
        }
    }
}
