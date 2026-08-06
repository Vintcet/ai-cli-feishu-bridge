using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeBoundaryTests
{
    [TestMethod]
    public async Task RuntimeIngressValidatesSerializesAndDeduplicatesCompletedEvents()
    {
        var handler = new RecordingRuntimeEventHandler();
        using var ingress = new BridgeRuntimeEventIngress([handler]);
        var runtimeEvent = Event("event-1");

        await Task.WhenAll(
            ingress.PublishAsync(runtimeEvent),
            ingress.PublishAsync(runtimeEvent));

        Assert.AreEqual(1, handler.Events.Count);
        Assert.AreEqual(1, handler.MaximumConcurrency);
    }

    [TestMethod]
    public async Task RuntimeIngressReleasesFailedEventForRetry()
    {
        var handler = new RecordingRuntimeEventHandler { FailuresRemaining = 1 };
        using var ingress = new BridgeRuntimeEventIngress([handler]);
        var runtimeEvent = Event("retry-event");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            ingress.PublishAsync(runtimeEvent));
        await ingress.PublishAsync(runtimeEvent);

        Assert.AreEqual(2, handler.Attempts);
        Assert.AreEqual(1, handler.Events.Count);
    }

    [TestMethod]
    public async Task RuntimeIngressRejectsInvalidProtocolBeforeBusinessHandler()
    {
        var handler = new RecordingRuntimeEventHandler();
        using var ingress = new BridgeRuntimeEventIngress([handler]);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            ingress.PublishAsync(Event("invalid") with { TraceId = "" }));

        Assert.AreEqual(0, handler.Attempts);
    }

    [TestMethod]
    public async Task FeishuIngressHasExactlyOneBusinessDecisionHandler()
    {
        var handler = new RecordingFeishuIntentHandler();
        var ingress = new BridgeFeishuIntentIngress([handler]);
        var intent = Intent();

        var result = await ingress.PublishAsync(intent);

        Assert.AreSame(intent, handler.Intents.Single());
        Assert.AreEqual("已处理", result!.ToastContent);
    }

    [TestMethod]
    public async Task UnknownFeishuIntentNeverReachesBusinessHandler()
    {
        var handler = new RecordingFeishuIntentHandler();
        var ingress = new BridgeFeishuIntentIngress([handler]);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            ingress.PublishAsync(Intent() with { IntentType = "unknown.intent" }));

        Assert.AreEqual(0, handler.Intents.Count);
    }

    [TestMethod]
    public void DuplicateBusinessHandlersAreRejectedAtStartup()
    {
        using var runtimeIngress = new BridgeRuntimeEventIngress(
            [new RecordingRuntimeEventHandler(), new RecordingRuntimeEventHandler()]);
        var feishuIngress = new BridgeFeishuIntentIngress(
            [new RecordingFeishuIntentHandler(), new RecordingFeishuIntentHandler()]);

        Assert.ThrowsException<InvalidOperationException>(runtimeIngress.ValidateConfiguration);
        Assert.ThrowsException<InvalidOperationException>(feishuIngress.ValidateConfiguration);
    }

    [TestMethod]
    public async Task StandardCommandGatewayDispatchesOnlyToMatchingRuntimeAdapter()
    {
        var codex = new RecordingRuntimeAdapter(RuntimeNames.Codex);
        var openCode = new RecordingRuntimeAdapter(RuntimeNames.OpenCode);
        var registry = new RuntimeAdapterRegistry();
        registry.Register(codex);
        registry.Register(openCode);
        IBridgeRuntimeCommandGateway gateway = new BridgeRuntimeCommandGateway(
            new RuntimeCommandDispatcher(registry));

        await gateway.DispatchAsync(Command(RuntimeNames.OpenCode));

        Assert.IsNull(codex.LastCommand);
        Assert.AreEqual(RuntimeNames.OpenCode, openCode.LastCommand!.Runtime);
    }

    [TestMethod]
    public async Task PassiveCommandGatewayNeverExecutesRuntimeAdapter()
    {
        var adapter = new RecordingRuntimeAdapter(RuntimeNames.Codex);
        var registry = new RuntimeAdapterRegistry();
        registry.Register(adapter);
        var adapterGateway = new BridgeRuntimeCommandGateway(
            new RuntimeCommandDispatcher(registry));
        using var gateway = new BridgeRuntimeCommandIngress(
            adapterGateway,
            BridgeHostOptions.Passive(Path.Combine(Path.GetTempPath(), "bridge-passive-test")));

        Assert.IsFalse(gateway.IsReady(RuntimeNames.Codex, new("session-1")));
        await Assert.ThrowsExceptionAsync<BridgeRuntimeCommandUnavailableException>(() =>
            gateway.DispatchAsync(Command(RuntimeNames.Codex)));
        Assert.IsNull(adapter.LastCommand);
    }

    [TestMethod]
    public void PassiveHostRuntimeAdaptersAreRegisteredButNeverReady()
    {
        var options = BridgeHostOptions.Passive(
            Path.Combine(Path.GetTempPath(), $"bridge-passive-adapters-{Guid.NewGuid():N}"));
        using var app = BridgeHostApplication.Build(options);
        var registry = app.Services.GetRequiredService<RuntimeAdapterRegistry>();

        foreach (var runtime in RuntimeNames.All)
        {
            var adapter = registry.ForRuntime(runtime);
            Assert.AreEqual(runtime, adapter.Runtime);
            Assert.IsFalse(adapter.IsReady(new RuntimeSession("session-1", "C:/repo")));
        }
    }

    [TestMethod]
    public async Task PassiveHostAssemblesFeishuAdapterWithoutLiveTransport()
    {
        var options = BridgeHostOptions.Passive(
            Path.Combine(Path.GetTempPath(), $"bridge-passive-feishu-{Guid.NewGuid():N}"));
        using var app = BridgeHostApplication.Build(options);
        var assembly = app.Services.GetRequiredService<IBridgeFeishuAdapterAssembly>();
        var snapshot = assembly.Validate();

        Assert.AreEqual("passive", snapshot.Mode);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "card-renderer",
                "event-normalizer",
                "event-pump",
                "event-source",
                "gateway",
                "intent-sink",
                "interaction-coordinator",
            },
            snapshot.Components.ToArray());
        Assert.IsFalse(snapshot.LiveEventStreamEnabled);
        Assert.IsFalse(snapshot.OutboundMessagingEnabled);
        Assert.IsInstanceOfType<PassiveFeishuEventSource>(
            app.Services.GetRequiredService<IFeishuEventSource>());
        Assert.IsInstanceOfType<PassiveFeishuGateway>(
            app.Services.GetRequiredService<IFeishuGateway>());
        Assert.IsNotNull(app.Services.GetRequiredService<IFeishuCardRenderer>()
            .CommandMenu());

        var received = 0;
        await foreach (var _ in app.Services.GetRequiredService<IFeishuEventSource>()
            .ReadAllAsync())
        {
            received++;
        }
        Assert.AreEqual(0, received);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            app.Services.GetRequiredService<IFeishuGateway>()
                .SendTextAsync("chat-1", "must-not-send"));
    }

    [TestMethod]
    public async Task PassiveHostAssemblesRuntimeIngressWithoutOpeningIngressTransport()
    {
        var options = BridgeHostOptions.Passive(
            Path.Combine(Path.GetTempPath(), $"bridge-passive-ingress-{Guid.NewGuid():N}"));
        using var app = BridgeHostApplication.Build(options);
        var assembly = app.Services.GetRequiredService<IBridgeRuntimeIngressAssembly>();
        var snapshot = assembly.Validate();

        Assert.AreEqual("passive", snapshot.Mode);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "managed-hook-bridge",
                "managed-hook-normalizer",
                "opencode-event-normalizer",
                "opencode-event-pump",
                "opencode-event-source",
                "runtime-event-sink",
            },
            snapshot.Components.ToArray());
        Assert.IsFalse(snapshot.ManagedHookHttpEnabled);
        Assert.IsFalse(snapshot.OpenCodeEventStreamEnabled);
        Assert.IsNotNull(app.Services.GetRequiredService<ManagedRuntimeHookBridge>());
        Assert.IsNotNull(app.Services.GetRequiredService<OpenCodeRuntimeEventPump>());
        Assert.IsInstanceOfType<PassiveOpenCodeEventSource>(
            app.Services.GetRequiredService<IOpenCodeEventSource>());

        var received = 0;
        var endpoint = new OpenCodeEndpoint(new Uri("http://127.0.0.1:1"), null);
        await foreach (var _ in app.Services.GetRequiredService<IOpenCodeEventSource>()
            .ReadAllAsync(endpoint))
        {
            received++;
        }
        Assert.AreEqual(0, received);
    }

    [TestMethod]
    public async Task FeishuIntentCanOnlyReachCliThroughStandardCommandGateway()
    {
        var codex = new RecordingRuntimeAdapter(RuntimeNames.Codex);
        var openCode = new RecordingRuntimeAdapter(RuntimeNames.OpenCode);
        var registry = new RuntimeAdapterRegistry();
        registry.Register(codex);
        registry.Register(openCode);
        var gateway = new BridgeRuntimeCommandGateway(new RuntimeCommandDispatcher(registry));
        var businessHandler = new PromptIntentHandler(gateway, RuntimeNames.OpenCode);
        var ingress = new BridgeFeishuIntentIngress([businessHandler]);

        await ingress.PublishAsync(Intent() with
        {
            IntentType = FeishuIntentTypes.MessagePrompt,
            Text = "继续",
        });

        Assert.IsNull(codex.LastCommand);
        Assert.AreEqual(RuntimeCommandTypes.PromptSend, openCode.LastCommand!.CommandType);
        Assert.AreEqual("trace-feishu", openCode.LastCommand.TraceId);
        Assert.AreEqual("继续", openCode.LastCommand.Payload.GetProperty("prompt").GetString());
    }

    [TestMethod]
    public void BoundaryCatalogRequiresOneOwnerAndRejectsDuplicateRuntimeAdapters()
    {
        using var runtimeIngress = new BridgeRuntimeEventIngress([]);
        var feishuIngress = new BridgeFeishuIntentIngress([]);
        var passive = BridgeHostOptions.Passive(Path.GetTempPath());
        var catalog = new BridgeBoundaryCatalog(
            [],
            runtimeIngress,
            feishuIngress,
            passive);

        Assert.ThrowsException<InvalidOperationException>(catalog.Validate);

        using var configuredRuntimeIngress = new BridgeRuntimeEventIngress(
            [new RecordingRuntimeEventHandler()]);
        var configuredFeishuIngress = new BridgeFeishuIntentIngress(
            [new RecordingFeishuIntentHandler()]);
        var configured = new BridgeBoundaryCatalog(
            [],
            configuredRuntimeIngress,
            configuredFeishuIngress,
            passive,
            [new RecordingFeishuAdapterAssembly()],
            [new RecordingRuntimeIngressAssembly()]).Validate();

        Assert.IsTrue(configured.Passive);
        Assert.AreEqual(0, configured.RegisteredRuntimes.Count);
        Assert.AreEqual(0, configured.RuntimeAdapters.Count);
        Assert.AreEqual(1, configured.RuntimeEventHandlers);
        Assert.AreEqual(1, configured.FeishuIntentHandlers);
        Assert.AreEqual("passive", configured.FeishuAdapter.Mode);
        Assert.IsFalse(configured.FeishuAdapter.LiveEventStreamEnabled);
        Assert.IsFalse(configured.FeishuAdapter.OutboundMessagingEnabled);
        Assert.AreEqual("passive", configured.RuntimeIngress.Mode);
        Assert.IsFalse(configured.RuntimeIngress.ManagedHookHttpEnabled);
        Assert.IsFalse(configured.RuntimeIngress.OpenCodeEventStreamEnabled);

        var duplicates = new BridgeBoundaryCatalog(
            [
                new RecordingRuntimeAdapter(RuntimeNames.Codex),
                new RecordingRuntimeAdapter(RuntimeNames.Codex),
            ],
            configuredRuntimeIngress,
            configuredFeishuIngress,
            passive,
            [new RecordingFeishuAdapterAssembly()],
            [new RecordingRuntimeIngressAssembly()]);
        Assert.ThrowsException<InvalidOperationException>(duplicates.Validate);

        var duplicateFeishuAdapters = new BridgeBoundaryCatalog(
            [],
            configuredRuntimeIngress,
            configuredFeishuIngress,
            passive,
            [new RecordingFeishuAdapterAssembly(), new RecordingFeishuAdapterAssembly()],
            [new RecordingRuntimeIngressAssembly()]);
        Assert.ThrowsException<InvalidOperationException>(duplicateFeishuAdapters.Validate);

        var duplicateRuntimeIngressAdapters = new BridgeBoundaryCatalog(
            [],
            configuredRuntimeIngress,
            configuredFeishuIngress,
            passive,
            [new RecordingFeishuAdapterAssembly()],
            [new RecordingRuntimeIngressAssembly(), new RecordingRuntimeIngressAssembly()]);
        Assert.ThrowsException<InvalidOperationException>(duplicateRuntimeIngressAdapters.Validate);
    }

    private static RuntimeEventEnvelope Event(string eventId) => new()
    {
        ProtocolVersion = BridgeProtocolVersion.Current,
        Runtime = RuntimeNames.Codex,
        Session = new RuntimeSessionReference { ExternalId = "session-1", Cwd = "C:/repo" },
        TraceId = "trace-1",
        EventId = eventId,
        EventType = RuntimeEventTypes.RuntimeConnected,
        OccurredAt = "2026-08-06T10:00:00.000Z",
        Payload = JsonSerializer.SerializeToElement(new { }),
    };

    private static RuntimeCommandEnvelope Command(string runtime) => new()
    {
        ProtocolVersion = BridgeProtocolVersion.Current,
        Runtime = runtime,
        Session = new RuntimeSessionReference { ExternalId = "session-1", Cwd = "C:/repo" },
        TraceId = "trace-command",
        CommandId = "command-1",
        CommandType = RuntimeCommandTypes.PromptSend,
        CreatedAt = "2026-08-06T10:00:00.000Z",
        Payload = JsonSerializer.SerializeToElement(new { prompt = "继续", mode = "steer" }),
    };

    private static FeishuIntent Intent() => new(
        "feishu-event-1",
        FeishuIntentTypes.CommandMenu,
        "owner-open-id",
        "chat-1",
        "message-1",
        "group",
        "trace-feishu");

    private sealed class RecordingRuntimeEventHandler : IBridgeRuntimeEventHandler
    {
        private int concurrency;

        public List<RuntimeEventEnvelope> Events { get; } = [];

        public int Attempts { get; private set; }

        public int FailuresRemaining { get; set; }

        public int MaximumConcurrency { get; private set; }

        public async Task HandleAsync(
            RuntimeEventEnvelope runtimeEvent,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            var current = Interlocked.Increment(ref concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, current);
            try
            {
                await Task.Yield();
                if (FailuresRemaining > 0)
                {
                    FailuresRemaining--;
                    throw new InvalidOperationException("synthetic failure");
                }
                Events.Add(runtimeEvent);
            }
            finally
            {
                Interlocked.Decrement(ref concurrency);
            }
        }
    }

    private sealed class RecordingFeishuIntentHandler : IBridgeFeishuIntentHandler
    {
        public List<FeishuIntent> Intents { get; } = [];

        public Task<FeishuCallbackResult?> HandleAsync(
            FeishuIntent intent,
            CancellationToken cancellationToken = default)
        {
            Intents.Add(intent);
            return Task.FromResult<FeishuCallbackResult?>(new("success", "已处理"));
        }
    }

    private sealed class RecordingRuntimeAdapter(string runtime) : IRuntimeAdapter
    {
        public string Runtime => runtime;

        public IReadOnlySet<RuntimeCapability> Capabilities { get; } =
            new HashSet<RuntimeCapability>
            {
                RuntimeCapability.PromptSend,
            };

        public RuntimeCommandEnvelope? LastCommand { get; private set; }

        public bool IsReady(RuntimeSession session) => true;

        public Task ExecuteAsync(
            RuntimeCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFeishuAdapterAssembly : IBridgeFeishuAdapterAssembly
    {
        private static readonly BridgeFeishuAdapterSnapshot snapshot =
            new("passive", ["test-component"], false, false);

        public BridgeFeishuAdapterSnapshot Validate() => snapshot;

        public BridgeFeishuAdapterSnapshot Snapshot() => snapshot;
    }

    private sealed class RecordingRuntimeIngressAssembly : IBridgeRuntimeIngressAssembly
    {
        private static readonly BridgeRuntimeIngressSnapshot snapshot =
            new("passive", ["test-component"], false, false);

        public BridgeRuntimeIngressSnapshot Validate() => snapshot;

        public BridgeRuntimeIngressSnapshot Snapshot() => snapshot;
    }

    private sealed class PromptIntentHandler(
        IBridgeRuntimeCommandGateway commands,
        string runtime) : IBridgeFeishuIntentHandler
    {
        public async Task<FeishuCallbackResult?> HandleAsync(
            FeishuIntent intent,
            CancellationToken cancellationToken = default)
        {
            var command = Command(runtime) with
            {
                TraceId = intent.TraceId,
                CommandId = $"feishu:{intent.EventId}",
                CorrelationId = intent.MessageId,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    prompt = intent.Text,
                    mode = "steer",
                }),
            };
            await commands.DispatchAsync(command, cancellationToken);
            return new("success", "已发送");
        }
    }
}
