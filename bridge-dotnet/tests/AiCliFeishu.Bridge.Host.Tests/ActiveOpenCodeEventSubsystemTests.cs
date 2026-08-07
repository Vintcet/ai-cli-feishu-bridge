using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ActiveOpenCodeEventSubsystemTests
{
    private static readonly string Cwd = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "opencode-event-subsystem-project"));

    [TestMethod]
    public async Task RawEventsUpdateSessionDirectoryBeforeCorePublication()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.Source.HealthResults.Enqueue(true);
        fixture.Directory.Register(5_101, Cwd);

        await fixture.Subsystem.StartAsync(CancellationToken.None);
        var stream = await fixture.Source.NextSubscriptionAsync();
        await WaitUntilAsync(() => fixture.Directory.ListReady().Count == 1);

        await stream.Events.Writer.WriteAsync(new(
            "session.created",
            JsonSerializer.SerializeToElement(new
            {
                info = new { id = "session-mapped", directory = Cwd },
            })));
        await fixture.Sink.WaitForCountAsync(1);

        Assert.IsTrue(fixture.Sink.MappedAtPublish[0]);
        Assert.IsNotNull(fixture.Directory.FindBySession("session-mapped"));

        await stream.Events.Writer.WriteAsync(new(
            "session.deleted",
            JsonSerializer.SerializeToElement(new
            {
                info = new { id = "session-mapped" },
            })));
        await fixture.Sink.WaitForCountAsync(2);

        Assert.IsFalse(fixture.Sink.MappedAtPublish[1]);
        Assert.IsNull(fixture.Directory.FindBySession("session-mapped"));

        await fixture.Subsystem.StopAsync(CancellationToken.None);
        await stream.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task ReplacementCancelsOldGenerationBeforeStartingNewSubscription()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.Source.HealthResults.Enqueue(true);
        fixture.Source.HealthResults.Enqueue(true);
        var firstIdentity = fixture.Directory.Register(5_102, Cwd);
        await fixture.Subsystem.StartAsync(CancellationToken.None);
        var first = await fixture.Source.NextSubscriptionAsync();
        await WaitUntilAsync(() => fixture.Directory.ListReady().Count == 1);

        var replacement = fixture.Directory.Register(
            5_102,
            Path.Combine(Cwd, "replacement"));
        var second = await fixture.Source.NextSubscriptionAsync();

        await first.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(first.Completed.Task.IsCompletedSuccessfully);
        Assert.IsTrue(replacement.Generation > firstIdentity.Generation);
        await WaitUntilAsync(() => fixture.Directory.ListReady().Single()
            .Directory!.EndsWith("replacement", StringComparison.Ordinal));
        Assert.IsFalse(fixture.Directory.RememberSession(
            5_102,
            firstIdentity.Generation,
            "session-stale"));

        await second.Events.Writer.WriteAsync(new(
            "session.status",
            JsonSerializer.SerializeToElement(new
            {
                sessionID = "session-current",
                status = new { type = "running" },
            })));
        await fixture.Sink.WaitForCountAsync(1);
        Assert.AreEqual(
            5_102,
            fixture.Directory.FindBySession("session-current")!.BaseUri.Port);

        await fixture.Subsystem.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task UnhealthyProbeAndClosedStreamReconnectWithBoundedBackoff()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.Source.HealthResults.Enqueue(false);
        fixture.Source.HealthResults.Enqueue(false);
        fixture.Source.HealthResults.Enqueue(true);
        fixture.Source.HealthResults.Enqueue(true);
        fixture.Directory.Register(5_103, Cwd);

        await fixture.Subsystem.StartAsync(CancellationToken.None);
        var first = await fixture.Source.NextSubscriptionAsync();

        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
            fixture.Delays.Take(2).ToArray());
        Assert.AreEqual(3, fixture.Source.ProbeCalls);
        first.Events.Writer.TryComplete();

        var second = await fixture.Source.NextSubscriptionAsync();
        Assert.AreEqual(4, fixture.Source.ProbeCalls);
        Assert.IsTrue(fixture.Delays.Any(value => value == TimeSpan.FromSeconds(2)));
        await WaitUntilAsync(() => fixture.Directory.ListReady().Count == 1);

        await fixture.Subsystem.StopAsync(CancellationToken.None);
        await second.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("starting", fixture.Subsystem.ComponentHealth.Status);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
            {
                throw new AssertFailedException("等待 OpenCode 事件条件超时。");
            }
            await Task.Delay(10);
        }
    }

    private sealed class Fixture(
        ActiveOpenCodeEndpointDirectory directory,
        ControllableEventSource source,
        RecordingRuntimeEventSink sink,
        BridgeOpenCodeEventSubsystem subsystem,
        ConcurrentQueue<TimeSpan> delays)
    {
        public ActiveOpenCodeEndpointDirectory Directory { get; } = directory;
        public ControllableEventSource Source { get; } = source;
        public RecordingRuntimeEventSink Sink { get; } = sink;
        public BridgeOpenCodeEventSubsystem Subsystem { get; } = subsystem;
        public ConcurrentQueue<TimeSpan> Delays { get; } = delays;

        public static async Task<Fixture> CreateAsync()
        {
            var options = new BridgeHostOptions(
                Path.GetTempPath(),
                IPAddress.Loopback,
                0,
                BridgeOwnershipMode.Active,
                "opencode-event-subsystem-test");
            var directory = new ActiveOpenCodeEndpointDirectory(options);
            await directory.StartAsync(CancellationToken.None);
            var source = new ControllableEventSource();
            var sink = new RecordingRuntimeEventSink(directory);
            var pump = new OpenCodeRuntimeEventPump(
                source,
                new OpenCodeEventNormalizer(),
                sink);
            var delays = new ConcurrentQueue<TimeSpan>();
            var subsystem = new BridgeOpenCodeEventSubsystem(
                directory,
                pump,
                source,
                options,
                (duration, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    delays.Enqueue(duration);
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30));
            return new(directory, source, sink, subsystem, delays);
        }
    }

    private sealed class ControllableEventSource : IBridgeOpenCodeEventStreamOwner
    {
        private readonly Channel<StreamSubscription> started =
            Channel.CreateUnbounded<StreamSubscription>();
        public ConcurrentQueue<bool> HealthResults { get; } = [];
        public int ProbeCalls => Volatile.Read(ref probeCalls);
        private int probeCalls;

        public ValueTask<bool> ProbeHealthAsync(
            OpenCodeEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref probeCalls);
            return ValueTask.FromResult(
                HealthResults.TryDequeue(out var result) ? result : true);
        }

        public async IAsyncEnumerable<OpenCodeRawEvent> ReadAllAsync(
            OpenCodeEndpoint endpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var subscription = new StreamSubscription(endpoint);
            await started.Writer.WriteAsync(subscription, cancellationToken);
            try
            {
                await foreach (var rawEvent in subscription.Events.Reader
                                   .ReadAllAsync(cancellationToken))
                {
                    yield return rawEvent;
                }
            }
            finally
            {
                subscription.Completed.TrySetResult();
            }
        }

        public async Task<StreamSubscription> NextSubscriptionAsync() =>
            await started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class StreamSubscription(OpenCodeEndpoint endpoint)
    {
        public OpenCodeEndpoint Endpoint { get; } = endpoint;
        public Channel<OpenCodeRawEvent> Events { get; } =
            Channel.CreateUnbounded<OpenCodeRawEvent>();
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingRuntimeEventSink(
        ActiveOpenCodeEndpointDirectory directory) : IRuntimeEventSink
    {
        private readonly object sync = new();
        private TaskCompletionSource changed = NewSignal();
        public List<RuntimeEventEnvelope> Events { get; } = [];
        public List<bool> MappedAtPublish { get; } = [];

        public Task PublishAsync(
            RuntimeEventEnvelope runtimeEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                Events.Add(runtimeEvent);
                MappedAtPublish.Add(
                    directory.FindBySession(runtimeEvent.Session!.ExternalId) is not null);
                var previous = changed;
                changed = NewSignal();
                previous.TrySetResult();
            }
            return Task.CompletedTask;
        }

        public async Task WaitForCountAsync(int expected)
        {
            while (true)
            {
                Task wait;
                lock (sync)
                {
                    if (Events.Count >= expected)
                    {
                        return;
                    }
                    wait = changed.Task;
                }
                await wait.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
