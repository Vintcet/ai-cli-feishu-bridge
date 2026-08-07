using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
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

    [TestMethod]
    public async Task ActiveSubsystemRunsInBackgroundAndStopsItsSingleSubscription()
    {
        var source = new ControllableFeishuEventSource();
        var sink = new RecordingFeishuIntentSink();
        var subsystem = new BridgeFeishuEventSubsystem(
            new FeishuEventPump(
                source,
                new FeishuEventNormalizer(new InMemoryFeishuInboundDeduplicator()),
                sink),
            ActiveOptions());
        var acknowledged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await subsystem.StartAsync(CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await source.Events.Writer.WriteAsync(new(
            "event-active",
            "trace-active",
            "im.message.receive_v1",
            JsonSerializer.SerializeToElement(new
            {
                sender = new { sender_id = new { open_id = "owner-1" } },
                message = new
                {
                    message_id = "message-1",
                    chat_id = "chat-1",
                    chat_type = "p2p",
                    message_type = "text",
                    content = "{\"text\":\"/\"}",
                },
            }),
            (_, statusCode, _) =>
            {
                Assert.AreEqual(200, statusCode);
                acknowledged.TrySetResult();
                return Task.CompletedTask;
            }));

        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, sink.Published);
        Assert.AreEqual("ready", subsystem.ComponentHealth.Status);
        Assert.IsNotNull(subsystem.Completion);

        await subsystem.StopAsync(CancellationToken.None);

        await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("starting", subsystem.ComponentHealth.Status);
        Assert.IsNull(subsystem.Completion);
    }

    [TestMethod]
    public async Task ActiveSubsystemPublishesEventSourceFailureToHostLifecycle()
    {
        var source = new ControllableFeishuEventSource();
        var subsystem = new BridgeFeishuEventSubsystem(
            new FeishuEventPump(
                source,
                new FeishuEventNormalizer(new InMemoryFeishuInboundDeduplicator()),
                new RecordingFeishuIntentSink()),
            ActiveOptions());

        await subsystem.StartAsync(CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Events.Writer.TryComplete(new InvalidOperationException("synthetic failure"));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await subsystem.Completion!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual("failed", subsystem.ComponentHealth.Status);

        await subsystem.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task SynchronousEventSourceFailureCannotDeadlockStartupLock()
    {
        var subsystem = new BridgeFeishuEventSubsystem(
            new FeishuEventPump(
                new SynchronouslyFailingFeishuEventSource(),
                new FeishuEventNormalizer(new InMemoryFeishuInboundDeduplicator()),
                new RecordingFeishuIntentSink()),
            ActiveOptions());

        await subsystem.StartAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await subsystem.Completion!.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual("failed", subsystem.ComponentHealth.Status);

        await subsystem.StopAsync(CancellationToken.None);
    }

    private static BridgeHostOptions ActiveOptions() => new(
        Path.GetTempPath(),
        System.Net.IPAddress.Loopback,
        0,
        BridgeOwnershipMode.Active,
        "feishu-event-subsystem-test");

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

    private sealed class ControllableFeishuEventSource : IFeishuEventSource
    {
        public Channel<FeishuInboundEnvelope> Events { get; } =
            Channel.CreateUnbounded<FeishuInboundEnvelope>();
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<FeishuInboundEnvelope> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await foreach (var envelope in Events.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return envelope;
                }
            }
            finally
            {
                Completed.TrySetResult();
            }
        }
    }

    private sealed class SynchronouslyFailingFeishuEventSource : IFeishuEventSource
    {
        public async IAsyncEnumerable<FeishuInboundEnvelope> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            throw new InvalidOperationException("synthetic synchronous failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
