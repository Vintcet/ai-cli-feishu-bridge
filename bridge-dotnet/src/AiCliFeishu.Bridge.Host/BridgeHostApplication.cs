using Microsoft.AspNetCore.Server.Kestrel.Core;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

public static class BridgeHostApplication
{
    public static WebApplication Build(
        BridgeHostOptions options,
        string[]? args = null,
        Action<IServiceCollection>? configureServices = null)
    {
        options = options.Validate();
        var activeAuthorization = options.OwnershipMode is BridgeOwnershipMode.Active
            ? BridgeActiveStartupGate.Authorize(options)
            : null;
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Listen(options.ListenAddress, options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
            });
        });

        AddInfrastructure(builder.Services, options, activeAuthorization);
        AddOwnershipAssembly(builder.Services, options);
        configureServices?.Invoke(builder.Services);
        _ = BridgeProductionAssemblyPreflight.Validate(options, builder.Services);

        var app = builder.Build();
        app.MapBridgeControlApi();
        return app;
    }

    private static void AddInfrastructure(
        IServiceCollection services,
        BridgeHostOptions options,
        BridgeActiveStartupAuthorization? activeAuthorization)
    {
        services.AddSingleton(options);
        if (activeAuthorization is not null)
        {
            services.AddSingleton(activeAuthorization);
        }
        services.AddSingleton<BridgeHealthRegistry>();
        services.AddSingleton<IBridgeInstanceLease, FileBridgeInstanceLease>();
        services.AddSingleton(services =>
            new ActiveOwnerLeaseObserver(
                services.GetRequiredService<BridgeHostOptions>().DataDirectory));
        services.AddSingleton<IBridgeControlTokenProvider, FileBridgeControlTokenProvider>();
    }

    internal static void AddOwnershipAssembly(
        IServiceCollection services,
        BridgeHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        switch (options.OwnershipMode)
        {
            case BridgeOwnershipMode.Passive:
                AddPassiveAssembly(services);
                return;
            case BridgeOwnershipMode.Active:
                AddActiveAssembly(services);
                return;
            default:
                throw new InvalidOperationException(
                    $"未知的 Bridge Host 所有权模式 {options.OwnershipMode}。");
        }
    }

    private static void AddPassiveAssembly(IServiceCollection services)
    {
        services.AddSingleton<BridgeBusinessStateOwner>();
        services.AddSingleton<IBridgeRuntimeEventHandler>(services =>
            services.GetRequiredService<BridgeBusinessStateOwner>());
        services.AddSingleton<IBridgeFeishuIntentHandler>(services =>
            services.GetRequiredService<BridgeBusinessStateOwner>());
        services.AddSingleton<BridgeRuntimeEventIngress>();
        services.AddSingleton<IRuntimeEventSink>(services =>
            services.GetRequiredService<BridgeRuntimeEventIngress>());
        services.AddSingleton<ManagedRuntimeHookNormalizer>();
        services.AddSingleton<ManagedRuntimeHookBridge>();
        services.AddSingleton<IOpenCodeEventSource, PassiveOpenCodeEventSource>();
        services.AddSingleton<OpenCodeEventNormalizer>();
        services.AddSingleton<OpenCodeRuntimeEventPump>();
        services.AddSingleton<IBridgeRuntimeIngressAssembly,
            BridgeRuntimeIngressAssembly>();
        services.AddSingleton<IFeishuEventSource, PassiveFeishuEventSource>();
        services.AddSingleton<IFeishuGateway, PassiveFeishuGateway>();
        AddFeishuAdapterAssembly(services);
        services.AddSingleton<BridgeBoundaryCatalog>();
        services.AddSingleton<BridgeBoundarySubsystem>();
        services.AddSingleton<BridgeFeishuEventSubsystem>();
        services.AddSingleton<IManagedTerminalDirectory, PassiveManagedTerminalDirectory>();
        services.AddSingleton<IManagedTerminalTransport, PassiveManagedTerminalTransport>();
        services.AddSingleton<IManagedRuntimeLifecycle, PassiveManagedRuntimeLifecycle>();
        services.AddSingleton<IManagedHookResponseSink, PassiveManagedHookResponseSink>();
        services.AddSingleton<IOpenCodeEndpointDirectory, PassiveOpenCodeEndpointDirectory>();
        services.AddSingleton<IOpenCodeTransport, PassiveOpenCodeTransport>();
        services.AddSingleton<IOpenCodeRuntimeLifecycle, PassiveOpenCodeRuntimeLifecycle>();
        AddRuntimeCommandAssembly(services);
        services.AddSingleton<IBridgeStoreShadow, ReadOnlyNodeStoreShadow>();
        services.AddSingleton<IBridgeControlStoreStatusSource>(services =>
            (IBridgeControlStoreStatusSource)services
                .GetRequiredService<IBridgeStoreShadow>());
        services.AddSingleton<IBridgeControlBusinessStateSource>(services =>
            (IBridgeControlBusinessStateSource)services
                .GetRequiredService<BridgeBusinessStateOwner>());
        services.AddSingleton<BridgeControlStatusReader>();
        services.AddSingleton<IBridgeHostSubsystem, PassiveOwnerGuardSubsystem>();
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<BridgeBoundarySubsystem>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            (IBridgeHostSubsystem)services.GetRequiredService<IBridgeStoreShadow>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<BridgeBusinessStateOwner>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<BridgeFeishuEventSubsystem>());
        services.AddSingleton<IBridgeHostSubsystem, BridgeOpenCodeEventSubsystem>();
        services.AddHostedService<BridgeInstanceLeaseService>();
        services.AddHostedService<BridgeRuntimeWorker>();
    }

    private static void AddActiveAssembly(IServiceCollection services)
    {
        services.AddSingleton<IBridgeActiveOwnerLeaseLifecycle,
            ActiveOwnerLeaseAcquirer>();
        services.AddSingleton<IBridgeProductionStoreOwner,
            ActiveProductionStoreOwner>();
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            (IBridgeHostSubsystem)services.GetRequiredService<IBridgeProductionStoreOwner>());
        services.AddSingleton<IBridgePersistentBusinessStateOwner,
            ActivePersistentBusinessStateOwner>();
        services.AddSingleton<IBridgeActiveSessionAliasStateOwner>(services =>
            (IBridgeActiveSessionAliasStateOwner)services
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<IBridgeActiveSessionGroupStateOwner>(services =>
            (IBridgeActiveSessionGroupStateOwner)services
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<IBridgeControlBusinessStateSource>(services =>
            (IBridgeControlBusinessStateSource)services
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<IBridgeControlStoreStatusSource>(services =>
            (IBridgeControlStoreStatusSource)services
                .GetRequiredService<IBridgeProductionStoreOwner>());
        services.AddSingleton<BridgeControlStatusReader>();
        services.AddSingleton<IBridgeActiveRuntimeStateSink>(services =>
            (IBridgeActiveRuntimeStateSink)services
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<IBridgeActiveApprovalStateOwner>(services =>
            (IBridgeActiveApprovalStateOwner)services
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<IBridgeActiveInputStateOwner>(services =>
            (IBridgeActiveInputStateOwner)services
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<BridgeRuntimeEventIngress>();
        services.AddSingleton<IRuntimeEventSink>(services =>
            services.GetRequiredService<BridgeRuntimeEventIngress>());
        services.AddSingleton<ManagedRuntimeHookNormalizer>();
        services.AddSingleton<ManagedRuntimeHookBridge>();
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            (IBridgeHostSubsystem)services
                .GetRequiredService<IBridgePersistentBusinessStateOwner>());
        services.AddSingleton<IBridgeFeishuCredentialSource,
            ActiveFeishuCredentialSource>();
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            (IBridgeHostSubsystem)services
                .GetRequiredService<IBridgeFeishuCredentialSource>());
        services.AddSingleton<IFeishuEventSource, ActiveFeishuEventSource>();
        services.AddSingleton<IFeishuGateway, ActiveFeishuGateway>();
        services.AddSingleton<ActiveSessionGroupCoordinator>();
        services.AddSingleton<IBridgeActiveSessionGroupCoordinator>(services =>
            services.GetRequiredService<ActiveSessionGroupCoordinator>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<ActiveSessionGroupCoordinator>());
        services.AddSingleton<ActiveFeishuFileTransferCoordinator>();
        services.AddSingleton<IBridgeActiveFileTransferCoordinator>(services =>
            services.GetRequiredService<ActiveFeishuFileTransferCoordinator>());
        services.AddSingleton<ActiveFeishuPromptCoordinator>();
        services.AddSingleton<ActiveFeishuApprovalCoordinator>();
        services.AddSingleton<ActiveFeishuApprovalNotificationCoordinator>();
        services.AddSingleton<IBridgeActiveApprovalNotifier>(services =>
            services.GetRequiredService<ActiveFeishuApprovalNotificationCoordinator>());
        services.AddSingleton<ActiveFeishuInputCoordinator>();
        services.AddSingleton<ActiveFeishuIntentHandler>();
        services.AddSingleton<IBridgeFeishuIntentHandler>(services =>
            services.GetRequiredService<ActiveFeishuIntentHandler>());
        AddFeishuAdapterAssembly(services);
        services.AddSingleton<IManagedTerminalDirectory,
            ActiveManagedTerminalDirectory>();
        services.AddSingleton<IBridgeManagedTerminalRegistrationDirectory>(services =>
            (IBridgeManagedTerminalRegistrationDirectory)services
                .GetRequiredService<IManagedTerminalDirectory>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            (IBridgeHostSubsystem)services
                .GetRequiredService<IManagedTerminalDirectory>());
        services.AddSingleton<IManagedTerminalTransport,
            ActiveManagedTerminalTransport>();
        services.AddSingleton<IManagedRuntimeLifecycle,
            ActiveManagedRuntimeLifecycle>();
        services.AddSingleton<IBridgeManagedRuntimeLaunchCoordinator>(services =>
            (IBridgeManagedRuntimeLaunchCoordinator)services
                .GetRequiredService<IManagedRuntimeLifecycle>());
        services.AddSingleton<IBridgeManagedHookIngress,
            ActiveManagedHookIngress>();
        services.AddSingleton<IManagedHookResponseSink,
            ActiveManagedHookResponseSink>();
        services.AddSingleton<IOpenCodeEventSource,
            ActiveOpenCodeEventSource>();
        services.AddSingleton<IBridgeOpenCodeEventStreamOwner>(services =>
            (IBridgeOpenCodeEventStreamOwner)services
                .GetRequiredService<IOpenCodeEventSource>());
        services.AddSingleton<OpenCodeEventNormalizer>();
        services.AddSingleton<OpenCodeRuntimeEventPump>();
        services.AddSingleton<IOpenCodeEndpointDirectory,
            ActiveOpenCodeEndpointDirectory>();
        services.AddSingleton<IBridgeOpenCodeEndpointRegistrationDirectory>(services =>
            (IBridgeOpenCodeEndpointRegistrationDirectory)services
                .GetRequiredService<IOpenCodeEndpointDirectory>());
        services.AddSingleton<IOpenCodeRuntimeLifecycle,
            ActiveOpenCodeRuntimeLifecycle>();
        services.AddSingleton<IBridgeOpenCodeRuntimeLifecycleOwner>(services =>
            (IBridgeOpenCodeRuntimeLifecycleOwner)services
                .GetRequiredService<IOpenCodeRuntimeLifecycle>());
        services.AddSingleton<IOpenCodeTransport,
            ActiveOpenCodeTransport>();
        services.AddSingleton<IBridgeRuntimeIngressAssembly,
            BridgeRuntimeIngressAssembly>();
        AddRuntimeCommandAssembly(services);
        services.AddSingleton<ActiveRuntimeActivityCoordinator>(services => new(
            services.GetRequiredService<BridgeHostOptions>(),
            services.GetRequiredService<IBridgeProductionStoreOwner>(),
            services.GetRequiredService<IFeishuGateway>(),
            services.GetRequiredService<IFeishuCardRenderer>(),
            sessionGroups:
                services.GetRequiredService<IBridgeActiveSessionGroupCoordinator>()));
        services.AddSingleton<ActiveRuntimeRetryCoordinator>(services => new(
            services.GetRequiredService<BridgeHostOptions>(),
            services.GetRequiredService<IBridgeActiveRuntimeStateSink>(),
            services.GetRequiredService<IBridgeProductionStoreOwner>(),
            () => services.GetRequiredService<IBridgeRuntimeCommandGateway>(),
            services.GetRequiredService<IFeishuGateway>(),
            services.GetRequiredService<IFeishuCardRenderer>(),
            activity: services.GetRequiredService<ActiveRuntimeActivityCoordinator>(),
            fileTransfers: services.GetRequiredService<IBridgeActiveFileTransferCoordinator>(),
            sessionGroups:
                services.GetRequiredService<IBridgeActiveSessionGroupCoordinator>(),
            approvalNotifications:
                services.GetRequiredService<IBridgeActiveApprovalNotifier>()));
        services.AddSingleton<IBridgeActiveRuntimeRetryCoordinator>(services =>
            services.GetRequiredService<ActiveRuntimeRetryCoordinator>());
        services.AddSingleton<IBridgeRuntimeEventHandler>(services =>
            services.GetRequiredService<ActiveRuntimeRetryCoordinator>());
        services.AddSingleton<BridgeBoundaryCatalog>();
        services.AddSingleton<BridgeBoundarySubsystem>();
        services.AddSingleton<BridgeFeishuEventSubsystem>();
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            (IBridgeHostSubsystem)services
                .GetRequiredService<IOpenCodeEndpointDirectory>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<ActiveRuntimeActivityCoordinator>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<ActiveRuntimeRetryCoordinator>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<BridgeBoundarySubsystem>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<BridgeFeishuEventSubsystem>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            ActivatorUtilities.CreateInstance<BridgeOpenCodeEventSubsystem>(services));
        services.AddHostedService<BridgeInstanceLeaseService>();
        services.AddHostedService<ActiveOwnerLeaseHostedService>();
        services.AddHostedService<BridgeRuntimeWorker>();
        services.AddSingleton(new BridgeProductionAssemblyManifest(
        [
            new(
                BridgeProductionCapability.ActiveOwnerLease,
                typeof(ActiveOwnerLeaseAcquirer)),
            new(
                BridgeProductionCapability.ProductionStoreOwner,
                typeof(ActiveProductionStoreOwner)),
            new(
                BridgeProductionCapability.PersistentBusinessState,
                typeof(ActivePersistentBusinessStateOwner)),
            new(
                BridgeProductionCapability.FeishuCredentials,
                typeof(ActiveFeishuCredentialSource)),
            new(
                BridgeProductionCapability.FeishuEventStream,
                typeof(ActiveFeishuEventSource)),
            new(
                BridgeProductionCapability.FeishuOutboundMessaging,
                typeof(ActiveFeishuGateway)),
            new(
                BridgeProductionCapability.ManagedTerminalDirectory,
                typeof(ActiveManagedTerminalDirectory)),
            new(
                BridgeProductionCapability.ManagedTerminalTransport,
                typeof(ActiveManagedTerminalTransport)),
            new(
                BridgeProductionCapability.ManagedRuntimeLifecycle,
                typeof(ActiveManagedRuntimeLifecycle)),
            new(
                BridgeProductionCapability.ManagedHookIngress,
                typeof(ActiveManagedHookIngress)),
            new(
                BridgeProductionCapability.ManagedHookResponses,
                typeof(ActiveManagedHookResponseSink)),
            new(
                BridgeProductionCapability.OpenCodeEndpointDirectory,
                typeof(ActiveOpenCodeEndpointDirectory)),
            new(
                BridgeProductionCapability.OpenCodeEventStream,
                typeof(ActiveOpenCodeEventSource)),
            new(
                BridgeProductionCapability.OpenCodeTransport,
                typeof(ActiveOpenCodeTransport)),
            new(
                BridgeProductionCapability.OpenCodeRuntimeLifecycle,
                typeof(ActiveOpenCodeRuntimeLifecycle)),
        ]));
        // Production owners and the audited adapter roots are assembled here, but
        // the Active cutover gate stays closed until full behavior parity is proven.
    }

    private static void AddRuntimeCommandAssembly(IServiceCollection services)
    {
        services.AddSingleton<IRuntimeAdapter, CodexRuntimeAdapter>();
        services.AddSingleton<IRuntimeAdapter, ClaudeCodeRuntimeAdapter>();
        services.AddSingleton<IRuntimeAdapter, OpenCodeRuntimeAdapter>();
        services.AddSingleton<RuntimeAdapterRegistry>(services =>
        {
            var registry = new RuntimeAdapterRegistry();
            foreach (var adapter in services.GetServices<IRuntimeAdapter>())
            {
                registry.Register(adapter);
            }
            return registry;
        });
        services.AddSingleton<RuntimeCommandDispatcher>();
        services.AddSingleton<BridgeRuntimeCommandGateway>();
        services.AddSingleton<BridgeRuntimeCommandIngress>();
        services.AddSingleton<IBridgeRuntimeCommandGateway>(services =>
            services.GetRequiredService<BridgeRuntimeCommandIngress>());
    }

    private static void AddFeishuAdapterAssembly(IServiceCollection services)
    {
        services.AddSingleton<BridgeFeishuIntentIngress>();
        services.AddSingleton<IFeishuIntentSink>(services =>
            services.GetRequiredService<BridgeFeishuIntentIngress>());
        services.AddSingleton<IFeishuCardRenderer, FeishuCardRenderer>();
        services.AddSingleton<IFeishuCardPatchLedger, InMemoryFeishuCardPatchLedger>();
        services.AddSingleton<IFeishuInboundDeduplicator,
            InMemoryFeishuInboundDeduplicator>();
        services.AddSingleton<FeishuEventNormalizer>();
        services.AddSingleton<FeishuInteractionCoordinator>();
        services.AddSingleton<FeishuEventPump>();
        services.AddSingleton<IBridgeFeishuAdapterAssembly,
            BridgeFeishuAdapterAssembly>();
    }
}
