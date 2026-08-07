using System.Runtime.CompilerServices;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeOpenCodeEventSubsystemTests
{
    [TestMethod]
    public void HostStartsOpenCodeEventPumpAfterFeishuEventPump()
    {
        var options = BridgeHostOptions.Passive(
            Path.Combine(Path.GetTempPath(), $"bridge-opencode-worker-{Guid.NewGuid():N}"));
        using var app = BridgeHostApplication.Build(options);

        var subsystems = app.Services.GetServices<IBridgeHostSubsystem>()
            .Select(subsystem => subsystem.Name)
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "production-owner",
                "standard-boundaries",
                "node-store-shadow",
                "business-state-owner",
                "feishu-event-pump",
                "opencode-event-pump",
            },
            subsystems);
    }

    [TestMethod]
    public async Task PassiveSubsystemEnumeratesNoEndpointsAndDoesNotOpenEventSource()
    {
        var endpoints = new RecordingEndpointDirectory();
        var source = new RecordingOpenCodeEventSource();
        var subsystem = new BridgeOpenCodeEventSubsystem(
            endpoints,
            new OpenCodeRuntimeEventPump(
                source,
                new OpenCodeEventNormalizer(),
                new RecordingRuntimeEventSink()),
            source,
            BridgeHostOptions.Passive(Path.GetTempPath()));

        await subsystem.StartAsync(CancellationToken.None);

        Assert.AreEqual(1, endpoints.ListCalls);
        Assert.AreEqual(0, source.Subscriptions);
        Assert.AreEqual("passive", subsystem.ComponentHealth.Status);
        Assert.AreEqual("event-endpoints-disabled", subsystem.ComponentHealth.Detail);

        await subsystem.StopAsync(CancellationToken.None);
        Assert.AreEqual("starting", subsystem.ComponentHealth.Status);
    }

    [TestMethod]
    public async Task PassiveSubsystemRejectsAnyReadyEndpointBeforeOpeningEventSource()
    {
        var endpoints = new RecordingEndpointDirectory(
            new OpenCodeEndpoint(new Uri("http://127.0.0.1:1"), null));
        var source = new RecordingOpenCodeEventSource();
        var subsystem = new BridgeOpenCodeEventSubsystem(
            endpoints,
            new OpenCodeRuntimeEventPump(
                source,
                new OpenCodeEventNormalizer(),
                new RecordingRuntimeEventSink()),
            source,
            BridgeHostOptions.Passive(Path.GetTempPath()));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            subsystem.StartAsync(CancellationToken.None));

        Assert.AreEqual(0, source.Subscriptions);
    }

    private sealed class RecordingEndpointDirectory(
        params OpenCodeEndpoint[] readyEndpoints) : IOpenCodeEndpointDirectory
    {
        public int ListCalls { get; private set; }

        public OpenCodeEndpoint? FindBySession(string sessionExternalId) => null;

        public IReadOnlyList<OpenCodeEndpoint> ListReady()
        {
            ListCalls++;
            return readyEndpoints;
        }
    }

    private sealed class RecordingOpenCodeEventSource : IOpenCodeEventSource
    {
        public int Subscriptions { get; private set; }

        public async IAsyncEnumerable<OpenCodeRawEvent> ReadAllAsync(
            OpenCodeEndpoint endpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Subscriptions++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingRuntimeEventSink : IRuntimeEventSink
    {
        public Task PublishAsync(
            RuntimeEventEnvelope runtimeEvent,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
