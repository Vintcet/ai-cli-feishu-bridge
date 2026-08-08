using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

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
        Assert.IsTrue(ReferenceEquals(
            app.Services.GetRequiredService<IBridgeStoreShadow>(),
            app.Services.GetRequiredService<IBridgeControlStoreStatusSource>()));
        Assert.IsTrue(ReferenceEquals(
            app.Services.GetRequiredService<BridgeBusinessStateOwner>(),
            app.Services.GetRequiredService<IBridgeControlBusinessStateSource>()));
        Assert.IsNotNull(app.Services.GetService<BridgeControlStatusReader>());
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
    public void PassivePreflightRejectsManagedTerminalRegistrationDirectoryBeforeResolvingIt()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);
        var constructed = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<IBridgeManagedTerminalRegistrationDirectory>(_ =>
                {
                    constructed = true;
                    return new RecordingManagedTerminalRegistrationDirectory();
                })));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
        Assert.IsFalse(constructed);
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveManagedTerminalDirectory()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveManagedTerminalDirectory>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveManagedTerminalTransport()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveManagedTerminalTransport>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveManagedRuntimeLifecycle()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveManagedRuntimeLifecycle>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveManagedHookIngress()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveManagedHookIngress>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveManagedHookResponseSink()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveManagedHookResponseSink>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveOpenCodeEndpointDirectory()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveOpenCodeEndpointDirectory>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsOpenCodeRegistrationDirectoryBeforeResolvingIt()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);
        var constructed = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<IBridgeOpenCodeEndpointRegistrationDirectory>(_ =>
                {
                    constructed = true;
                    return new RecordingOpenCodeRegistrationDirectory();
                })));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
        Assert.IsFalse(constructed);
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveOpenCodeEventSource()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveOpenCodeEventSource>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveOpenCodeTransport()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveOpenCodeTransport>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsConcreteActiveOpenCodeRuntimeLifecycle()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<ActiveOpenCodeRuntimeLifecycle>()));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
    }

    [TestMethod]
    public void PassivePreflightRejectsOpenCodeRuntimeLifecycleOwnerBeforeResolvingIt()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);
        var constructed = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<IBridgeOpenCodeRuntimeLifecycleOwner>(_ =>
                {
                    constructed = true;
                    throw new InvalidOperationException("must not resolve");
                })));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
        Assert.IsFalse(constructed);
    }

    [TestMethod]
    public void PassivePreflightRejectsOpenCodeEventStreamOwnerBeforeResolvingIt()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);
        var constructed = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.AddSingleton<IBridgeOpenCodeEventStreamOwner>(_ =>
                {
                    constructed = true;
                    return new RecordingOpenCodeEventSource();
                })));

        StringAssert.Contains(error.Message, "Active 专用生产能力");
        Assert.IsFalse(constructed);
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
    public void PassivePreflightRejectsMissingControlStateAlias()
    {
        var options = BridgeHostOptions.Passive(Path.GetTempPath(), port: 0);

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeHostApplication.Build(options, configureServices: services =>
                services.RemoveAll<IBridgeControlStoreStatusSource>()));

        StringAssert.Contains(
            error.Message,
            nameof(IBridgeControlStoreStatusSource));
        StringAssert.Contains(error.Message, "控制 API");
    }

    [TestMethod]
    public void ActiveAssemblyIsCompleteAndIsolatedWhileCutoverGateRemainsClosed()
    {
        var options = ActiveOptions();
        var services = new ServiceCollection();

        BridgeHostApplication.AddOwnershipAssembly(services, options);
        var snapshot = BridgeProductionAssemblyPreflight.Validate(options, services);

        Assert.AreEqual("active", snapshot.Mode);
        Assert.IsTrue(snapshot.Complete);
        CollectionAssert.AreEqual(
            Enum.GetValues<BridgeProductionCapability>(),
            snapshot.Capabilities.ToArray());
        Assert.IsFalse(services.Any(descriptor =>
            descriptor.ImplementationType?.Name.StartsWith("Passive", StringComparison.Ordinal) == true));
        Assert.IsFalse(services.Any(descriptor =>
            descriptor.ServiceType == typeof(IBridgeStoreShadow)));
        var storeOwner = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeProductionStoreOwner));
        Assert.AreEqual(typeof(ActiveProductionStoreOwner), storeOwner.ImplementationType);
        var subsystems = services.Where(descriptor =>
            descriptor.ServiceType == typeof(IBridgeHostSubsystem)).ToArray();
        Assert.AreEqual(10, subsystems.Length);
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
        Assert.AreEqual(15, manifest.Owners.Count);
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
        Assert.AreEqual(
            BridgeProductionCapability.ManagedTerminalDirectory,
            manifest.Owners[6].Capability);
        Assert.AreEqual(
            typeof(ActiveManagedTerminalDirectory),
            manifest.Owners[6].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.ManagedTerminalTransport,
            manifest.Owners[7].Capability);
        Assert.AreEqual(
            typeof(ActiveManagedTerminalTransport),
            manifest.Owners[7].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.ManagedRuntimeLifecycle,
            manifest.Owners[8].Capability);
        Assert.AreEqual(
            typeof(ActiveManagedRuntimeLifecycle),
            manifest.Owners[8].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.ManagedHookIngress,
            manifest.Owners[9].Capability);
        Assert.AreEqual(
            typeof(ActiveManagedHookIngress),
            manifest.Owners[9].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.ManagedHookResponses,
            manifest.Owners[10].Capability);
        Assert.AreEqual(
            typeof(ActiveManagedHookResponseSink),
            manifest.Owners[10].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.OpenCodeEndpointDirectory,
            manifest.Owners[11].Capability);
        Assert.AreEqual(
            typeof(ActiveOpenCodeEndpointDirectory),
            manifest.Owners[11].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.OpenCodeEventStream,
            manifest.Owners[12].Capability);
        Assert.AreEqual(
            typeof(ActiveOpenCodeEventSource),
            manifest.Owners[12].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.OpenCodeTransport,
            manifest.Owners[13].Capability);
        Assert.AreEqual(
            typeof(ActiveOpenCodeTransport),
            manifest.Owners[13].OwnerType);
        Assert.AreEqual(
            BridgeProductionCapability.OpenCodeRuntimeLifecycle,
            manifest.Owners[14].Capability);
        Assert.AreEqual(
            typeof(ActiveOpenCodeRuntimeLifecycle),
            manifest.Owners[14].OwnerType);
        var businessOwner = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgePersistentBusinessStateOwner));
        Assert.AreEqual(
            typeof(ActivePersistentBusinessStateOwner),
            businessOwner.ImplementationType);
        var controlStore = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeControlStoreStatusSource));
        Assert.IsNotNull(controlStore.ImplementationFactory);
        var controlBusiness = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeControlBusinessStateSource));
        Assert.IsNotNull(controlBusiness.ImplementationFactory);
        var sessionAliasState = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeActiveSessionAliasStateOwner));
        Assert.IsNotNull(sessionAliasState.ImplementationFactory);
        Assert.AreEqual(
            typeof(BridgeControlStatusReader),
            services.Single(descriptor =>
                descriptor.ServiceType == typeof(BridgeControlStatusReader))
                .ImplementationType);
        var productionStore = new RecordingProductionStoreOwner();
        var persistentBusiness = new RecordingPersistentBusinessStateOwner();
        var controlServices = new ServiceCollection();
        controlServices.AddSingleton<IBridgeProductionStoreOwner>(productionStore);
        controlServices.AddSingleton<IBridgePersistentBusinessStateOwner>(
            persistentBusiness);
        using (var provider = controlServices.BuildServiceProvider())
        {
            Assert.AreSame(
                productionStore,
                controlStore.ImplementationFactory(provider));
            Assert.AreSame(
                persistentBusiness,
                controlBusiness.ImplementationFactory(provider));
            Assert.AreSame(
                persistentBusiness,
                sessionAliasState.ImplementationFactory(provider));
        }
        Assert.IsNotNull(services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeActiveApprovalStateOwner))
            .ImplementationFactory);
        Assert.IsNotNull(services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeActiveInputStateOwner))
            .ImplementationFactory);
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
        var terminalDirectory = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IManagedTerminalDirectory));
        Assert.AreEqual(
            typeof(ActiveManagedTerminalDirectory),
            terminalDirectory.ImplementationType);
        var terminalTransport = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IManagedTerminalTransport));
        Assert.AreEqual(
            typeof(ActiveManagedTerminalTransport),
            terminalTransport.ImplementationType);
        var runtimeLifecycle = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IManagedRuntimeLifecycle));
        Assert.AreEqual(
            typeof(ActiveManagedRuntimeLifecycle),
            runtimeLifecycle.ImplementationType);
        var launchCoordinator = services.Single(descriptor =>
            descriptor.ServiceType ==
                typeof(IBridgeManagedRuntimeLaunchCoordinator));
        Assert.IsNotNull(launchCoordinator.ImplementationFactory);
        var hookIngress = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeManagedHookIngress));
        Assert.AreEqual(typeof(ActiveManagedHookIngress), hookIngress.ImplementationType);
        Assert.IsTrue(services.Any(descriptor =>
            descriptor.ServiceType == typeof(ManagedRuntimeHookBridge)));
        var hookResponses = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IManagedHookResponseSink));
        Assert.AreEqual(
            typeof(ActiveManagedHookResponseSink),
            hookResponses.ImplementationType);
        var openCodeDirectory = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IOpenCodeEndpointDirectory));
        Assert.AreEqual(
            typeof(ActiveOpenCodeEndpointDirectory),
            openCodeDirectory.ImplementationType);
        var openCodeEventSource = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IOpenCodeEventSource));
        Assert.AreEqual(
            typeof(ActiveOpenCodeEventSource),
            openCodeEventSource.ImplementationType);
        var openCodeEventOwner = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeOpenCodeEventStreamOwner));
        Assert.IsNotNull(openCodeEventOwner.ImplementationFactory);
        var openCodeTransport = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IOpenCodeTransport));
        Assert.AreEqual(
            typeof(ActiveOpenCodeTransport),
            openCodeTransport.ImplementationType);
        var openCodeLifecycle = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IOpenCodeRuntimeLifecycle));
        Assert.AreEqual(
            typeof(ActiveOpenCodeRuntimeLifecycle),
            openCodeLifecycle.ImplementationType);
        var openCodeLifecycleAlias = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeOpenCodeRuntimeLifecycleOwner));
        Assert.IsNotNull(openCodeLifecycleAlias.ImplementationFactory);
        var openCodeRegistrations = services.Single(descriptor =>
            descriptor.ServiceType ==
                typeof(IBridgeOpenCodeEndpointRegistrationDirectory));
        Assert.IsNotNull(openCodeRegistrations.ImplementationFactory);
        var openCodeDirectoryOwner = new RecordingOpenCodeRegistrationDirectory();
        var openCodeServices = new ServiceCollection();
        openCodeServices.AddSingleton<IOpenCodeEndpointDirectory>(
            openCodeDirectoryOwner);
        using (var provider = openCodeServices.BuildServiceProvider())
        {
            Assert.AreSame(
                openCodeDirectoryOwner,
                openCodeRegistrations.ImplementationFactory(provider));
        }
        var eventStreamOwner = new RecordingOpenCodeEventSource();
        var eventStreamServices = new ServiceCollection();
        eventStreamServices.AddSingleton<IOpenCodeEventSource>(eventStreamOwner);
        using (var provider = eventStreamServices.BuildServiceProvider())
        {
            Assert.AreSame(
                eventStreamOwner,
                openCodeEventOwner.ImplementationFactory(provider));
        }
        var openCodeLifecycleOwner = new RecordingOpenCodeRuntimeLifecycle();
        var openCodeLifecycleServices = new ServiceCollection();
        openCodeLifecycleServices.AddSingleton<IOpenCodeRuntimeLifecycle>(
            openCodeLifecycleOwner);
        using (var provider = openCodeLifecycleServices.BuildServiceProvider())
        {
            Assert.AreSame(
                openCodeLifecycleOwner,
                openCodeLifecycleAlias.ImplementationFactory(provider));
        }
        var lifecycleOwner = new RecordingManagedRuntimeLifecycle();
        var lifecycleServices = new ServiceCollection();
        lifecycleServices.AddSingleton<IManagedRuntimeLifecycle>(lifecycleOwner);
        using (var provider = lifecycleServices.BuildServiceProvider())
        {
            Assert.AreSame(
                lifecycleOwner,
                launchCoordinator.ImplementationFactory(provider));
        }
        var registrationDirectory = services.Single(descriptor =>
            descriptor.ServiceType ==
                typeof(IBridgeManagedTerminalRegistrationDirectory));
        Assert.IsNotNull(registrationDirectory.ImplementationFactory);
        var directoryOwner = new RecordingManagedTerminalRegistrationDirectory();
        var factoryServices = new ServiceCollection();
        factoryServices.AddSingleton<IManagedTerminalDirectory>(directoryOwner);
        using (var provider = factoryServices.BuildServiceProvider())
        {
            Assert.AreSame(
                directoryOwner,
                registrationDirectory.ImplementationFactory(provider));
        }
        Assert.IsTrue(services.Any(descriptor =>
            descriptor.ServiceType == typeof(IBridgeRuntimeEventHandler) &&
            descriptor.ImplementationFactory is not null));
        Assert.IsNotNull(services.Single(descriptor => descriptor.ServiceType ==
            typeof(ActiveRuntimeRetryCoordinator)).ImplementationFactory);
        Assert.IsNotNull(services.Single(descriptor => descriptor.ServiceType ==
            typeof(ActiveRuntimeActivityCoordinator)).ImplementationFactory);
        Assert.IsNotNull(services.Single(descriptor => descriptor.ServiceType ==
            typeof(IBridgeActiveRuntimeStateSink)).ImplementationFactory);
        Assert.IsNotNull(services.Single(descriptor => descriptor.ServiceType ==
            typeof(IBridgeActiveRuntimeRetryCoordinator)).ImplementationFactory);
        var feishuHandler = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBridgeFeishuIntentHandler));
        Assert.IsNotNull(feishuHandler.ImplementationFactory);
        Assert.AreEqual(
            typeof(ActiveFeishuIntentHandler),
            services.Single(descriptor =>
                descriptor.ServiceType == typeof(ActiveFeishuIntentHandler))
                .ImplementationType);
        Assert.AreEqual(
            typeof(ActiveFeishuApprovalCoordinator),
            services.Single(descriptor =>
                descriptor.ServiceType == typeof(ActiveFeishuApprovalCoordinator))
                .ImplementationType);
        Assert.AreEqual(
            typeof(ActiveFeishuInputCoordinator),
            services.Single(descriptor =>
                descriptor.ServiceType == typeof(ActiveFeishuInputCoordinator))
                .ImplementationType);
        Assert.AreEqual(
            typeof(BridgeFeishuAdapterAssembly),
            services.Single(descriptor =>
                descriptor.ServiceType == typeof(IBridgeFeishuAdapterAssembly))
                .ImplementationType);
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
    public void ActivePreflightRejectsIncompleteRuntimeAdapterSetBeforeResolvingFactories()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        var constructed = false;
        services.AddSingleton<SideEffectProbe>(_ =>
        {
            constructed = true;
            return new SideEffectProbe();
        });
        services.RemoveAll<IRuntimeAdapter>();
        services.AddSingleton<IRuntimeAdapter, CodexRuntimeAdapter>();
        services.AddSingleton<IRuntimeAdapter, ClaudeCodeRuntimeAdapter>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, "Runtime Adapter");
        Assert.IsFalse(constructed);
    }

    [TestMethod]
    public void ActivePreflightRejectsRuntimeCommandAliasThatCanOwnAnotherInstance()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        services.RemoveAll<IBridgeRuntimeCommandGateway>();
        services.AddSingleton<IBridgeRuntimeCommandGateway,
            BridgeRuntimeCommandGateway>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, nameof(IBridgeRuntimeCommandGateway));
        StringAssert.Contains(error.Message, "组合根工厂");
    }

    [TestMethod]
    public void ActivePreflightRejectsMissingControlStatusReader()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        services.RemoveAll<BridgeControlStatusReader>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, nameof(BridgeControlStatusReader));
        StringAssert.Contains(error.Message, "控制 API");
    }

    [TestMethod]
    public void ActivePreflightRejectsControlStateAliasThatCanOwnAnotherInstance()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        services.RemoveAll<IBridgeControlBusinessStateSource>();
        services.AddSingleton<IBridgeControlBusinessStateSource,
            RecordingPersistentBusinessStateOwner>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(
            error.Message,
            nameof(IBridgeControlBusinessStateSource));
        StringAssert.Contains(error.Message, "组合根工厂");
    }

    [TestMethod]
    public void ActivePreflightRejectsMissingFeishuIntentHandlerBeforeResolvingFactories()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        var constructed = false;
        services.AddSingleton<SideEffectProbe>(_ =>
        {
            constructed = true;
            return new SideEffectProbe();
        });
        services.RemoveAll<IBridgeFeishuIntentHandler>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, nameof(IBridgeFeishuIntentHandler));
        Assert.IsFalse(constructed);
    }

    [TestMethod]
    public void ActivePreflightRejectsMissingApprovalCoordinator()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        services.RemoveAll<ActiveFeishuApprovalCoordinator>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, nameof(ActiveFeishuApprovalCoordinator));
    }

    [TestMethod]
    public void ActivePreflightRejectsMissingInputCoordinator()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        services.RemoveAll<ActiveFeishuInputCoordinator>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, nameof(ActiveFeishuInputCoordinator));
    }

    [TestMethod]
    public void ActivePreflightRejectsApprovalStateAliasThatCanOwnAnotherInstance()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        services.RemoveAll<IBridgeActiveApprovalStateOwner>();
        services.AddSingleton<IBridgeActiveApprovalStateOwner,
            RecordingPersistentBusinessStateOwner>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, nameof(IBridgeActiveApprovalStateOwner));
        StringAssert.Contains(error.Message, "组合根工厂");
    }

    [TestMethod]
    public void ActivePreflightRejectsSessionAliasStatePortThatCanOwnAnotherInstance()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        services.RemoveAll<IBridgeActiveSessionAliasStateOwner>();
        services.AddSingleton<IBridgeActiveSessionAliasStateOwner,
            RecordingPersistentBusinessStateOwner>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(
            error.Message,
            nameof(IBridgeActiveSessionAliasStateOwner));
        StringAssert.Contains(error.Message, "组合根工厂");
    }

    [TestMethod]
    public void ActivePreflightRejectsFeishuIntentSinkThatCanOwnAnotherInstance()
    {
        var options = ActiveOptions();
        var services = CompleteActiveServices();
        services.RemoveAll<IFeishuIntentSink>();
        services.AddSingleton<IFeishuIntentSink, BridgeFeishuIntentIngress>();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            BridgeProductionAssemblyPreflight.Validate(options, services));

        StringAssert.Contains(error.Message, nameof(IFeishuIntentSink));
        StringAssert.Contains(error.Message, "组合根工厂");
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
        services.AddSingleton(ActiveOptions());
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
        AddCompleteActiveRuntimeAssembly(services);
        AddCompleteActiveFeishuAssembly(services);
        services.AddSingleton<IBridgeControlStoreStatusSource>(provider =>
            (IBridgeControlStoreStatusSource)provider
                .GetRequiredService<IBridgeProductionStoreOwner>());
        services.AddSingleton<IBridgeControlBusinessStateSource>(provider =>
            (IBridgeControlBusinessStateSource)provider
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<BridgeControlStatusReader>();
        services.AddSingleton(new BridgeProductionAssemblyManifest(owners));
        return services;
    }

    private static void AddCompleteActiveRuntimeAssembly(IServiceCollection services)
    {
        services.AddSingleton<IBridgeActiveRuntimeStateSink>(provider =>
            (IBridgeActiveRuntimeStateSink)provider
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<ActiveRuntimeActivityCoordinator>(provider =>
            ActivatorUtilities.CreateInstance<ActiveRuntimeActivityCoordinator>(provider));
        services.AddSingleton<IBridgeHostSubsystem>(provider =>
            provider.GetRequiredService<ActiveRuntimeActivityCoordinator>());
        services.AddSingleton<ActiveRuntimeRetryCoordinator>(provider =>
            ActivatorUtilities.CreateInstance<ActiveRuntimeRetryCoordinator>(provider));
        services.AddSingleton<IBridgeActiveRuntimeRetryCoordinator>(provider =>
            provider.GetRequiredService<ActiveRuntimeRetryCoordinator>());
        services.AddSingleton<IBridgeRuntimeEventHandler>(provider =>
            provider.GetRequiredService<ActiveRuntimeRetryCoordinator>());
        services.AddSingleton<BridgeRuntimeEventIngress>();
        services.AddSingleton<IRuntimeEventSink>(provider =>
            provider.GetRequiredService<BridgeRuntimeEventIngress>());
        services.AddSingleton<ManagedRuntimeHookNormalizer>();
        services.AddSingleton<ManagedRuntimeHookBridge>();
        services.AddSingleton<OpenCodeEventNormalizer>();
        services.AddSingleton<OpenCodeRuntimeEventPump>();
        services.AddSingleton<IBridgeRuntimeIngressAssembly,
            BridgeRuntimeIngressAssembly>();
        services.AddSingleton<IRuntimeAdapter, CodexRuntimeAdapter>();
        services.AddSingleton<IRuntimeAdapter, ClaudeCodeRuntimeAdapter>();
        services.AddSingleton<IRuntimeAdapter, OpenCodeRuntimeAdapter>();
        services.AddSingleton<RuntimeAdapterRegistry>(_ => new());
        services.AddSingleton<RuntimeCommandDispatcher>();
        services.AddSingleton<BridgeRuntimeCommandGateway>();
        services.AddSingleton<BridgeRuntimeCommandIngress>();
        services.AddSingleton<IBridgeRuntimeCommandGateway>(provider =>
            provider.GetRequiredService<BridgeRuntimeCommandIngress>());
    }

    private static void AddCompleteActiveFeishuAssembly(IServiceCollection services)
    {
        services.AddSingleton<IBridgeActiveApprovalStateOwner>(provider =>
            (IBridgeActiveApprovalStateOwner)provider
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<IBridgeActiveInputStateOwner>(provider =>
            (IBridgeActiveInputStateOwner)provider
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<IBridgeActiveSessionAliasStateOwner>(provider =>
            (IBridgeActiveSessionAliasStateOwner)provider
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<ActiveFeishuFileTransferCoordinator>();
        services.AddSingleton<IBridgeActiveFileTransferCoordinator>(provider =>
            provider.GetRequiredService<ActiveFeishuFileTransferCoordinator>());
        services.AddSingleton<ActiveFeishuPromptCoordinator>();
        services.AddSingleton<ActiveFeishuApprovalCoordinator>();
        services.AddSingleton<ActiveFeishuInputCoordinator>();
        services.AddSingleton<ActiveFeishuIntentHandler>();
        services.AddSingleton<IBridgeFeishuIntentHandler>(provider =>
            provider.GetRequiredService<ActiveFeishuIntentHandler>());
        services.AddSingleton<BridgeFeishuIntentIngress>();
        services.AddSingleton<IFeishuIntentSink>(provider =>
            provider.GetRequiredService<BridgeFeishuIntentIngress>());
        services.AddSingleton<IFeishuCardRenderer, FeishuCardRenderer>();
        services.AddSingleton<IFeishuCardPatchLedger, InMemoryFeishuCardPatchLedger>();
        services.AddSingleton<IFeishuInboundDeduplicator,
            InMemoryFeishuInboundDeduplicator>();
        services.AddSingleton<FeishuEventNormalizer>();
        services.AddSingleton<FeishuInteractionCoordinator>();
        services.AddSingleton<FeishuEventPump>();
        services.AddSingleton<IBridgeFeishuAdapterAssembly,
            BridgeFeishuAdapterAssembly>();
        services.AddSingleton<BridgeBoundaryCatalog>();
        services.AddSingleton<BridgeBoundarySubsystem>();
        services.AddSingleton<BridgeFeishuEventSubsystem>();
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
    private sealed class RecordingProductionStoreOwner :
        IBridgeProductionStoreOwner,
        IBridgeControlStoreStatusSource
    {
        public BridgeProductionStoreSnapshot Snapshot { get; } = new(
            BridgeProductionStoreState.Open,
            null,
            0);

        public BridgeControlStoreStatus Status { get; } = new(
            BridgeStoreShadowStatuses.NotLoaded,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0);

        public BridgeComponentHealth ComponentHealth { get; } =
            new("production-store", "starting");

        public Task RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
        : IBridgePersistentBusinessStateOwner,
          IBridgeControlBusinessStateSource,
          IBridgeActiveRuntimeStateSink,
          IBridgeActiveApprovalStateOwner,
          IBridgeActiveInputStateOwner,
          IBridgeActiveSessionAliasStateOwner
    {
        public BridgeBusinessStateSnapshot Snapshot { get; } =
            BridgeBusinessStateSnapshot.NotInitialized;

        public BridgeComponentHealth ComponentHealth { get; } =
            new("persistent-business-state-owner", "starting");

        public Task RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task HandleAsync(
            RuntimeEventEnvelope runtimeEvent,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask<BridgeSessionAliasUpdateResult> UpdateSessionAliasAsync(
            string sessionId,
            string? alias,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BridgeSessionAliasUpdateResult(
                null,
                null,
                "recording owner"));

        public ValueTask<BridgeApprovalClaim?> TryClaimApprovalAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BridgeApprovalClaim?>(null);

        public ValueTask ReleaseApprovalClaimAsync(
            string requestId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<BridgeApprovalClaim?> ResolveClaimedApprovalAsync(
            string requestId,
            string sessionId,
            string resolution,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BridgeApprovalClaim?>(null);

        public ValueTask<BridgeApprovalClaim?> DeferClaimedApprovalAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BridgeApprovalClaim?>(null);

        public ValueTask<BridgeInputAnswerProgress?> TryRecordInputAnswerAsync(
            string requestId,
            string sessionId,
            string questionId,
            IReadOnlyList<string> answers,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BridgeInputAnswerProgress?>(null);

        public ValueTask<BridgeInputClaim?> TryClaimInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BridgeInputClaim?>(null);

        public ValueTask<BridgeInputClaim?> ResolveClaimedInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BridgeInputClaim?>(null);

        public ValueTask<BridgeInputClaim?> DeferClaimedInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BridgeInputClaim?>(null);

        public ValueTask ReleaseInputClaimAsync(
            string requestId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<BridgeInputClaim?> ResetClaimedInputAsync(
            string requestId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<BridgeInputClaim?>(null);
    }
    private sealed class RecordingFeishuCredentialSource : IBridgeFeishuCredentialSource
    {
        public BridgeFeishuCredentials Credentials { get; } =
            new("cli_recording", "recording-secret");
    }
    private sealed class RecordingManagedHookIngress : IBridgeManagedHookIngress
    {
        public Task<JsonElement> HandleAsync(
            BridgeManagedIngressKind kind,
            JsonElement payload,
            string traceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { }));
    }

    private sealed class RecordingManagedTerminalRegistrationDirectory
        : IManagedTerminalDirectory,
          IBridgeManagedTerminalRegistrationDirectory
    {
        public BridgeManagedTerminalDirectorySnapshot Snapshot { get; } =
            new(true, 0, 0, 0, 0);

        public ManagedTerminalTarget? FindBySession(string sessionExternalId) => null;
        public void Register(BridgeManagedTerminalRegistration registration) { }
        public bool Unregister(string terminalId) => false;
        public BridgeManagedTerminalClaim? Claim(
            string cwd,
            string runtime,
            string sessionExternalId) => null;
        public BridgeManagedTerminalClaim? ClaimById(
            string terminalId,
            string cwd,
            string runtime,
            string sessionExternalId,
            bool? elevated = null) => null;
        public BridgeManagedTerminalIdentity? FindClaimBySession(
            string sessionExternalId) => null;
        public BridgeManagedTerminalIdentity? FindClaimByTerminal(
            string terminalId) => null;
        public void Release(string sessionExternalId) { }
        public bool IsCurrent(ManagedTerminalTarget target) => false;
    }

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

    private sealed class RecordingManagedRuntimeLifecycle :
        IManagedRuntimeLifecycle,
        IBridgeManagedRuntimeLaunchCoordinator
    {
        public BridgeManagedRuntimeLifecycleSnapshot Snapshot { get; } = new(0, 0, 0, 0);
        public BridgeManagedRuntimeLaunchRequest? Claim() => null;
        public BridgeManagedRuntimeLaunchCompletionResult Complete(BridgeManagedRuntimeLaunchCompletion completion) => new(true);
        public Task DrainAsync(string sessionExternalId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LaunchAsync(RuntimeCommandContext context, string runtime, string sessionExternalId, string cwd, string? prompt, bool elevated, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(RuntimeCommandContext context, string runtime, string sessionExternalId, string? cwd, string? prompt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(RuntimeCommandContext context, string runtime, string sessionExternalId, string? reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingManagedHookResponseSink : IManagedHookResponseSink
    {
        public bool IsReady(string runtime, string sessionExternalId) => false;
        public Task ResolveApprovalAsync(RuntimeCommandContext context, string runtime, string sessionExternalId, string requestId, string decision, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveInputAsync(RuntimeCommandContext context, string runtime, string sessionExternalId, string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeferInputToLocalAsync(string runtime, string sessionExternalId, string requestId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingOpenCodeEndpointDirectory : IOpenCodeEndpointDirectory
    {
        public OpenCodeEndpoint? FindBySession(string sessionExternalId) => null;
        public IReadOnlyList<OpenCodeEndpoint> ListReady() => [];
    }

    private sealed class RecordingOpenCodeRegistrationDirectory :
        IOpenCodeEndpointDirectory,
        IBridgeOpenCodeEndpointRegistrationDirectory
    {
        public BridgeOpenCodeEndpointDirectorySnapshot Snapshot { get; } =
            new(true, 0, 0, 0, 0);

        public OpenCodeEndpoint? FindBySession(string sessionExternalId) => null;
        public IReadOnlyList<OpenCodeEndpoint> ListReady() => [];
        public BridgeOpenCodeEndpointIdentity Register(int port, string cwd) =>
            throw new NotSupportedException();
        public BridgeOpenCodeEndpointIdentity? TryRegisterAvailable(
            int port,
            string cwd) => throw new NotSupportedException();
        public bool Unregister(int port) => false;
        public bool Unregister(int port, long generation) => false;
        public bool SetReady(int port, long generation, bool ready) => false;
        public bool RememberSession(
            int port,
            long generation,
            string sessionExternalId) => false;
        public bool ForgetSession(
            int port,
            long generation,
            string sessionExternalId) => false;
        public BridgeOpenCodeEndpointIdentity? FindRegistrationBySession(
            string sessionExternalId) => null;
        public bool IsCurrent(
            BridgeOpenCodeEndpointIdentity identity,
            string sessionExternalId) => false;
        public IReadOnlyList<BridgeOpenCodeEndpointIdentity> ListRegistrations() => [];
        public ValueTask<long> WaitForChangeAsync(
            long observedRevision,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(observedRevision);
    }

    private sealed class RecordingOpenCodeEventSource :
        IBridgeOpenCodeEventStreamOwner
    {
        public ValueTask<bool> ProbeHealthAsync(
            OpenCodeEndpoint endpoint,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

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

    private sealed class RecordingOpenCodeRuntimeLifecycle :
        IBridgeOpenCodeRuntimeLifecycleOwner
    {
        public ValueTask<BridgeOpenCodeEndpointIdentity> ReserveAsync(
            string cwd,
            string? sessionExternalId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BridgeOpenCodeEndpointIdentity(
                5_100,
                cwd,
                1,
                Ready: false));
        public bool Release(int port) => true;
        public Task LaunchAsync(RuntimeCommandContext context, string requestedExternalId, string cwd, bool elevated, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(RuntimeCommandContext context, string sessionExternalId, string? cwd, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitUntilReadyAsync(RuntimeCommandContext context, string sessionExternalId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(RuntimeCommandContext context, string sessionExternalId, string? reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
