using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class BridgeProductionAssemblyTests
{
    [TestMethod]
    public void PassiveAssemblyUsesOnlyReadOnlyAndNoIoOwnershipPorts()
    {
        var options = BridgeHostOptions.Passive(
            Path.Combine(Path.GetTempPath(), $"bridge-passive-assembly-{Guid.NewGuid():N}"),
            port: 0);
        using var app = BridgeHostApplication.Build(options);

        Assert.IsInstanceOfType<ReadOnlyNodeStoreShadow>(
            app.Services.GetRequiredService<IBridgeStoreShadow>());
        Assert.IsInstanceOfType<PassiveFeishuEventSource>(
            app.Services.GetRequiredService<IFeishuEventSource>());
        Assert.IsInstanceOfType<PassiveFeishuGateway>(
            app.Services.GetRequiredService<IFeishuGateway>());
        Assert.IsInstanceOfType<PassiveManagedTerminalDirectory>(
            app.Services.GetRequiredService<IManagedTerminalDirectory>());
        Assert.IsInstanceOfType<PassiveManagedTerminalTransport>(
            app.Services.GetRequiredService<IManagedTerminalTransport>());
        Assert.IsInstanceOfType<PassiveManagedRuntimeLifecycle>(
            app.Services.GetRequiredService<IManagedRuntimeLifecycle>());
        Assert.IsInstanceOfType<PassiveManagedHookResponseSink>(
            app.Services.GetRequiredService<IManagedHookResponseSink>());
        Assert.IsInstanceOfType<PassiveOpenCodeEndpointDirectory>(
            app.Services.GetRequiredService<IOpenCodeEndpointDirectory>());
        Assert.IsInstanceOfType<PassiveOpenCodeEventSource>(
            app.Services.GetRequiredService<IOpenCodeEventSource>());
        Assert.IsInstanceOfType<PassiveOpenCodeTransport>(
            app.Services.GetRequiredService<IOpenCodeTransport>());
        Assert.IsInstanceOfType<PassiveOpenCodeRuntimeLifecycle>(
            app.Services.GetRequiredService<IOpenCodeRuntimeLifecycle>());
        Assert.IsNull(app.Services.GetService<BridgeProductionAssemblyManifest>());
        Assert.IsNull(app.Services.GetService<ActiveOwnerLeaseAcquirer>());
    }

    [TestMethod]
    public void PassivePreflightRejectsActiveLeaseLifecycleOverride()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
            {
                services.AddSingleton<IBridgeActiveOwnerLeaseLifecycle,
                    RecordingActiveOwnerLeaseLifecycle>();
                services.AddHostedService<ActiveOwnerLeaseHostedService>();
            }));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsActiveStoreOwnerBeforeResolvingIt()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);
        var constructed = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<IBridgeProductionStoreOwner>(_ =>
                {
                    constructed = true;
                    return new RecordingProductionStoreOwner();
                })));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
        Assert.IsFalse(constructed);
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveStoreOwner()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveProductionStoreOwner>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsPersistentStateOwnerBeforeResolvingIt()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);
        var constructed = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<IBridgePersistentBusinessStateOwner>(_ =>
                {
                    constructed = true;
                    return new RecordingPersistentBusinessStateOwner();
                })));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
        Assert.IsFalse(constructed);
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActivePersistentStateOwner()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActivePersistentBusinessStateOwner>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsFeishuCredentialSourceBeforeResolvingIt()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);
        var constructed = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<IBridgeFeishuCredentialSource>(_ =>
                {
                    constructed = true;
                    return new RecordingFeishuCredentialSource();
                })));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
        Assert.IsFalse(constructed);
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveFeishuCredentialSource()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveFeishuCredentialSource>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsActiveFeishuEventSourceBeforeResolvingIt()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);
        var constructed = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
            {
                services.RemoveAll<IFeishuEventSource>();
                services.AddSingleton<IFeishuEventSource>(_ =>
                {
                    constructed = true;
                    return new RecordingFeishuEventSource();
                });
            }));

        StringAssert.Contains(error.Message, nameof(IFeishuEventSource));
        Assert.IsFalse(constructed);
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveFeishuEventSource()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveFeishuEventSource>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveFeishuGateway()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveFeishuGateway>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsUnknownHostedLifecycle()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddHostedService<UnknownHostedService>()));

        StringAssert.Contains(error.Message, "后台生命周期注册缺失、重复、越序或包含未知实现");
    }

    [TestMethod]
    public void PassivePreflightRejectsProductionPortOverrideBeforeResolvingIt()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);
        var constructed = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
            {
                services.RemoveAll<IFeishuGateway>();
                services.AddSingleton<IFeishuGateway>(_ =>
                {
                    constructed = true;
                    return new RecordingFeishuGateway();
                });
            }));

        StringAssert.Contains(error.Message, nameof(IFeishuGateway));
        Assert.IsFalse(constructed);
    }

    [TestMethod]
    public void ActiveAssemblyIsIsolatedAndFailsClosedWhileCapabilitiesAreMissing()
    {
        var options = ActiveOptions();
        var services = new ServiceCollection();

        BridgeHostApplication.AddOwnershipAssembly(services, options);
        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, "Active Host 生产装配不完整");
        Assert.IsFalse(error.Message.Contains(
            nameof(BridgeProductionCapability.ActiveOwnerLease),
            StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(
            nameof(BridgeProductionCapability.ProductionStoreOwner),
            StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(
            nameof(BridgeProductionCapability.PersistentBusinessState),
            StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(
            nameof(BridgeProductionCapability.FeishuCredentials),
            StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(
            nameof(BridgeProductionCapability.FeishuEventStream),
            StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(
            nameof(BridgeProductionCapability.FeishuOutboundMessaging),
            StringComparison.Ordinal));
        Assert.IsFalse(services.Any(descriptor =>
            descriptor.ImplementationType?.Name.StartsWith("Passive", StringComparison.Ordinal) == true));
        Assert.IsFalse(services.Any(descriptor =>
            descriptor.ServiceType == typeof(IBridgeStoreShadow)));
        var storeOwner = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeProductionStoreOwner));
        Assert.AreEqual(typeof(ActiveProductionStoreOwner), storeOwner.ImplementationType);
        var subsystems = services.Where(descriptor =>
            descriptor.ServiceType == typeof(IBridgeHostSubsystem)).ToArray();
        Assert.AreEqual(3, subsystems.Length);
        Assert.IsTrue(subsystems.All(descriptor =>
            descriptor.ImplementationFactory is not null));
        var hostedServices = services.Where(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                typeof(BridgeInstanceLeaseService),
                typeof(ActiveOwnerLeaseHostedService),
                typeof(BridgeRuntimeWorker),
            },
            hostedServices.Select(descriptor => descriptor.ImplementationType).ToArray());
        var owner = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeActiveOwnerLeaseLifecycle));
        Assert.AreEqual(typeof(ActiveOwnerLeaseAcquirer), owner.ImplementationType);
        var manifest = (BridgeProductionAssemblyManifest)services.Single(descriptor =>
            descriptor.ServiceType == typeof(BridgeProductionAssemblyManifest))
            .ImplementationInstance!;
        Assert.AreEqual(6, manifest.Owners.Count);
        Assert.AreEqual(
            BridgeProductionCapability.ActiveOwnerLease,
            manifest.Owners[0].Capability);
        Assert.AreEqual(typeof(ActiveOwnerLeaseAcquirer), manifest.Owners[0].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.ProductionStoreOwner,
            manifest.Owners[1].Capability);
        Assert.AreEqual(typeof(ActiveProductionStoreOwner), manifest.Owners[1].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.PersistentBusinessState,
            manifest.Owners[2].Capability);
        Assert.AreEqual(
            typeof(ActivePersistentBusinessStateOwner),
            manifest.Owners[2].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.FeishuCredentials,
            manifest.Owners[3].Capability);
        Assert.AreEqual(
            typeof(ActiveFeishuCredentialSource),
            manifest.Owners[3].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.FeishuEventStream,
            manifest.Owners[4].Capability);
        Assert.AreEqual(
            typeof(ActiveFeishuEventSource),
            manifest.Owners[4].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.FeishuOutboundMessaging,
            manifest.Owners[5].Capability);
        Assert.AreEqual(
            typeof(ActiveFeishuGateway),
            manifest.Owners[5].OwnerType);
        var businessOwner = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgePersistentBusinessStateOwner));
        Assert.AreEqual(
            typeof(ActivePersistentBusinessStateOwner),
            businessOwner.ImplementationType);
        var credentials = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeFeishuCredentialSource));
        Assert.AreEqual(
            typeof(ActiveFeishuCredentialSource),
            credentials.ImplementationType);
        var eventSource = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IFeishuEventSource));
        Assert.AreEqual(
            typeof(ActiveFeishuEventSource),
            eventSource.ImplementationType);
        var gateway = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IFeishuGateway));
        Assert.AreEqual(
            typeof(ActiveFeishuGateway),
            gateway.ImplementationType);
        Assert.IsTrue(services.Any(descriptor =>
            descriptor.ServiceType == typeof(IBridgeRuntimeEventHandler) &&
            descriptor.ImplementationFactory is not null));
        Assert.IsFalse(services.Any(descriptor =>
            descriptor.ServiceType == typeof(IBridgeFeishuIntentHandler)));
        Assert.IsFalse(Directory.Exists(options.DataDirectory));
    }

    [TestMethod]
    public void ActivePreflightRejectsPassiveFallbackBeforeAnyFactoryRuns()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        var constructed = false;
        services.AddSingleton<SideEffectProbe>(_ =>
        {
            constructed = true;
            return new SideEffectProbe();
        });
        services.RemoveAll<IFeishuGateway>();
        services.AddSingleton<IFeishuGateway, PassiveFeishuGateway>();
        ReplaceManifestOwner<RecordingFeishuGateway>(
            services,
            BridgeProductionCapability.FeishuOutboundMessaging,
            typeof(PassiveFeishuGateway));

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, nameof(PassiveFeishuGateway));
        Assert.IsFalse(constructed);

    }

    [TestMethod]
    public void CompleteActiveManifestRequiresExactlyOneMatchingOwnerPerCapability()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();

        var snapshot = BridgeProductionAssemblyPreflight.Validate(options, services);

        Assert.AreEqual("active", snapshot.Mode);
        Assert.IsTrue(snapshot.Complete);
        CollectionAssert.AreEquivalent(
            Enum.GetValues<BridgeProductionCapability>(),
            snapshot.Capabilities.ToArray());

    }

    [TestMethod]
    public void ActivePreflightRejectsDuplicateCapabilityOwner()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        var manifest = (BridgeProductionAssemblyManifest)services.Single(descriptor =>
            descriptor.ServiceType == typeof(BridgeProductionAssemblyManifest))
            .ImplementationInstance!;
        services.RemoveAll<BridgeProductionAssemblyManifest>();
        services.AddSingleton(new BridgeProductionAssemblyManifest(
            manifest.Owners.Append(manifest.Owners[0])));

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, "所有者不唯一");

    }

    [TestMethod]
    public void ActivePreflightRejectsOwnerLeaseAfterRuntimeWorker()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        services.RemoveAll<IHostedService>();
        services.AddSingleton<IHostedService, BridgeInstanceLeaseService>();
        services.AddSingleton<IHostedService, BridgeRuntimeWorker>();
        services.AddSingleton<IHostedService, ActiveOwnerLeaseHostedService>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, "后台生命周期注册缺失、重复、越序或包含未知实现");
    }

    private static BridgeHostOptions ActiveOptions() => new(
        Path.Combine(Path.GetTempPath(), $"bridge-active-assembly-{Guid.NewGuid():N}"),
        IPAddress.Loopback,
        8765,
        BridgeOwnershipMode.Active,
        "preflight-test");

    private static ServiceCollection CompleteActiveServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostedService, BridgeInstanceLeaseService>();
        services.AddSingleton<IHostedService, ActiveOwnerLeaseHostedService>();
        services.AddSingleton<IHostedService, BridgeRuntimeWorker>();
        var owners = new[]
        {
            Owner<IBridgeActiveOwnerLeaseLifecycle, RecordingActiveOwnerLeaseLifecycle>(
                services, BridgeProductionCapability.ActiveOwnerLease),
            Owner<IBridgeProductionStoreOwner, RecordingProductionStoreOwner>(
                services, BridgeProductionCapability.ProductionStoreOwner),
            Owner<IBridgePersistentBusinessStateOwner, RecordingPersistentBusinessStateOwner>(
                services, BridgeProductionCapability.PersistentBusinessState),
            Owner<IBridgeFeishuCredentialSource, RecordingFeishuCredentialSource>(
                services, BridgeProductionCapability.FeishuCredentials),
            Owner<IFeishuEventSource, RecordingFeishuEventSource>(
                services, BridgeProductionCapability.FeishuEventStream),
            Owner<IFeishuGateway, RecordingFeishuGateway>(
                services, BridgeProductionCapability.FeishuOutboundMessaging),
            Owner<IManagedTerminalDirectory, RecordingManagedTerminalDirectory>(
                services, BridgeProductionCapability.ManagedTerminalDirectory),
            Owner<IManagedTerminalTransport, RecordingManagedTerminalTransport>(
                services, BridgeProductionCapability.ManagedTerminalTransport),
            Owner<IManagedRuntimeLifecycle, RecordingManagedRuntimeLifecycle>(
                services, BridgeProductionCapability.ManagedRuntimeLifecycle),
            Owner<IBridgeManagedHookIngress, RecordingManagedHookIngress>(
                services, BridgeProductionCapability.ManagedHookIngress),
            Owner<IManagedHookResponseSink, RecordingManagedHookResponseSink>(
                services, BridgeProductionCapability.ManagedHookResponses),
            Owner<IOpenCodeEndpointDirectory, RecordingOpenCodeEndpointDirectory>(
                services, BridgeProductionCapability.OpenCodeEndpointDirectory),
            Owner<IOpenCodeEventSource, RecordingOpenCodeEventSource>(
                services, BridgeProductionCapability.OpenCodeEventStream),
            Owner<IOpenCodeTransport, RecordingOpenCodeTransport>(
                services, BridgeProductionCapability.OpenCodeTransport),
            Owner<IOpenCodeRuntimeLifecycle, RecordingOpenCodeRuntimeLifecycle>(
                services, BridgeProductionCapability.OpenCodeRuntimeLifecycle),
        };
        services.AddSingleton(new BridgeProductionAssemblyManifest(owners));
        return services;
    }

    private static BridgeProductionCapabilityOwner Owner<TContract, TImplementation>(
        IServiceCollection services,
        BridgeProductionCapability capability)
        where TContract : class
        where TImplementation : class, TContract, new()
    {
        services.AddSingleton<TContract, TImplementation>();
        return new(capability, typeof(TImplementation));
    }

    private static void ReplaceManifestOwner<TExpected>(
        IServiceCollection services,
        BridgeProductionCapability capability,
        Type replacement)
    {
        var manifest = (BridgeProductionAssemblyManifest)services.Single(descriptor =>
            descriptor.ServiceType == typeof(BridgeProductionAssemblyManifest))
            .ImplementationInstance!;
        Assert.AreEqual(typeof(TExpected), manifest.Owners.Single(owner =>
            owner.Capability == capability).OwnerType);
        services.RemoveAll<BridgeProductionAssemblyManifest>();
        services.AddSingleton(new BridgeProductionAssemblyManifest(
            manifest.Owners.Select(owner => owner.Capability == capability
                ? owner with { OwnerType = replacement }
                : owner)));
    }


    private sealed class UnknownHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SideEffectProbe;

    private sealed class RecordingActiveOwnerLeaseLifecycle : IBridgeActiveOwnerLeaseLifecycle
    {
        public bool IsHeld => false;
        public AiCliFeishu.Bridge.Adapters.Storage.ActiveOwnerLeaseRecord? HeldLease => null;

        public ValueTask<AiCliFeishu.Bridge.Adapters.Storage.ActiveOwnerLeaseRecord> AcquireAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask ReleaseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RecordingProductionStoreOwner : IBridgeProductionStoreOwner
    {
        public BridgeProductionStoreSnapshot Snapshot { get; } = new(
            BridgeProductionStoreState.Open,
            null,
            0);

        public ValueTask OpenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<NodeStoreSnapshot> ReadAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            Func<NodeStoreSnapshot, NodeStoreSnapshot> update,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
    private sealed class RecordingPersistentBusinessStateOwner
        : IBridgePersistentBusinessStateOwner
    {
        public BridgeBusinessStateSnapshot Snapshot { get; } =
            BridgeBusinessStateSnapshot.NotInitialized;
    }
    private sealed class RecordingFeishuCredentialSource : IBridgeFeishuCredentialSource
    {
        public BridgeFeishuCredentials Credentials { get; } =
            new("cli_recording", "recording-secret");
    }
    private sealed class RecordingManagedHookIngress : IBridgeManagedHookIngress;

    private sealed class RecordingFeishuEventSource : IFeishuEventSource
    {
        public async IAsyncEnumerable<FeishuInboundEnvelope> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingFeishuGateway : IFeishuGateway
    {
        public Task<string> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default) => Task.FromResult("message");
        public Task<string> ReplyTextAsync(string messageId, string text, CancellationToken cancellationToken = default) => Task.FromResult("message");
        public Task<string> SendCardAsync(string chatId, FeishuCardView card, string? idempotencyKey = null, CancellationToken cancellationToken = default) => Task.FromResult("message");
        public Task PatchCardAsync(string messageId, FeishuCardView card, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<FeishuSessionGroup> CreateSessionGroupAsync(string ownerOpenId, string name, string description, CancellationToken cancellationToken = default) => Task.FromResult(new FeishuSessionGroup("chat", "name"));
        public Task UpdateSessionGroupNameAsync(string chatId, string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSessionGroupAsync(string chatId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<long> DownloadMessageResourceAsync(string messageId, string fileKey, string resourceType, string destinationPath, long maxBytes, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<string> SendLocalFileAsync(string chatId, string filePath, CancellationToken cancellationToken = default) => Task.FromResult("message");
    }

    private sealed class RecordingManagedTerminalDirectory : IManagedTerminalDirectory
    {
        public ManagedTerminalTarget? FindBySession(string sessionExternalId) => null;
    }

    private sealed class RecordingManagedTerminalTransport : IManagedTerminalTransport
    {
        public Task SendAsync(RuntimeCommandContext context, ManagedTerminalTarget target, string prompt, ManagedTerminalSubmitMode submitMode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingManagedRuntimeLifecycle : IManagedRuntimeLifecycle
    {
        public Task LaunchAsync(RuntimeCommandContext context, string runtime, string sessionExternalId, string cwd, string? prompt, bool elevated, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(RuntimeCommandContext context, string runtime, string sessionExternalId, string? cwd, string? prompt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(RuntimeCommandContext context, string runtime, string sessionExternalId, string? reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingManagedHookResponseSink : IManagedHookResponseSink
    {
        public Task ResolveApprovalAsync(RuntimeCommandContext context, string runtime, string sessionExternalId, string requestId, string decision, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveInputAsync(RuntimeCommandContext context, string runtime, string sessionExternalId, string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingOpenCodeEndpointDirectory : IOpenCodeEndpointDirectory
    {
        public OpenCodeEndpoint? FindBySession(string sessionExternalId) => null;
        public IReadOnlyList<OpenCodeEndpoint> ListReady() => [];
    }

    private sealed class RecordingOpenCodeEventSource : IOpenCodeEventSource
    {
        public async IAsyncEnumerable<OpenCodeRawEvent> ReadAllAsync(
            OpenCodeEndpoint endpoint,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingOpenCodeTransport : IOpenCodeTransport
    {
        public bool IsReady(string sessionExternalId) => true;
        public Task SendPromptAsync(RuntimeCommandContext context, string sessionExternalId, string prompt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveApprovalAsync(RuntimeCommandContext context, string sessionExternalId, string requestId, string decision, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveInputAsync(RuntimeCommandContext context, string sessionExternalId, string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LaunchAsync(RuntimeCommandContext context, string requestedExternalId, string cwd, string? prompt, bool elevated, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(RuntimeCommandContext context, string sessionExternalId, string? prompt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(RuntimeCommandContext context, string sessionExternalId, string? reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingOpenCodeRuntimeLifecycle : IOpenCodeRuntimeLifecycle
    {
        public Task LaunchAsync(RuntimeCommandContext context, string requestedExternalId, string cwd, bool elevated, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(RuntimeCommandContext context, string sessionExternalId, string? cwd, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitUntilReadyAsync(RuntimeCommandContext context, string sessionExternalId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(RuntimeCommandContext context, string sessionExternalId, string? reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
