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
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Listen(options.ListenAddress, options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
            });
        });

        AddInfrastructure(builder.Services, options);
        AddOwnershipAssembly(builder.Services, options);
        configureServices?.Invoke(builder.Services);
        _ = BridgeProductionAssemblyPreflight.Validate(options, builder.Services);

        var app = builder.Build();
        app.MapBridgeControlApi();
        return app;
    }

    private static void AddInfrastructure(
        IServiceCollection services,
        BridgeHostOptions options)
    {
        services.AddSingleton(options);
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
        services.AddSingleton<BridgeFeishuIntentIngress>();
        services.AddSingleton<IFeishuIntentSink>(services =>
            services.GetRequiredService<BridgeFeishuIntentIngress>());
        services.AddSingleton<IFeishuEventSource, PassiveFeishuEventSource>();
        services.AddSingleton<IFeishuGateway, PassiveFeishuGateway>();
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
        services.AddSingleton<RuntimeAdapterRegistry>(services =>
            services.GetRequiredService<BridgeBoundaryCatalog>().BuildRuntimeRegistry());
        services.AddSingleton<RuntimeCommandDispatcher>();
        services.AddSingleton<BridgeRuntimeCommandGateway>();
        services.AddSingleton<BridgeRuntimeCommandIngress>();
        services.AddSingleton<IBridgeRuntimeCommandGateway>(services =>
            services.GetRequiredService<BridgeRuntimeCommandIngress>());
        services.AddSingleton<IManagedTerminalDirectory, PassiveManagedTerminalDirectory>();
        services.AddSingleton<IManagedTerminalTransport, PassiveManagedTerminalTransport>();
        services.AddSingleton<IManagedRuntimeLifecycle, PassiveManagedRuntimeLifecycle>();
        services.AddSingleton<IManagedHookResponseSink, PassiveManagedHookResponseSink>();
        services.AddSingleton<IOpenCodeEndpointDirectory, PassiveOpenCodeEndpointDirectory>();
        services.AddSingleton<IOpenCodeTransport, PassiveOpenCodeTransport>();
        services.AddSingleton<IOpenCodeRuntimeLifecycle, PassiveOpenCodeRuntimeLifecycle>();
        services.AddSingleton<IRuntimeAdapter, CodexRuntimeAdapter>();
        services.AddSingleton<IRuntimeAdapter, ClaudeCodeRuntimeAdapter>();
        services.AddSingleton<IRuntimeAdapter, OpenCodeRuntimeAdapter>();
        services.AddSingleton<IBridgeStoreShadow, ReadOnlyNodeStoreShadow>();
        services.AddSingleton<BridgeControlStatusReader>();
        services.AddSingleton<IBridgeHostSubsystem, PassiveOwnerGuardSubsystem>();
        services.AddSingleton<IBridgeHostSubsystem, BridgeBoundarySubsystem>();
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            (IBridgeHostSubsystem)services.GetRequiredService<IBridgeStoreShadow>());
        services.AddSingleton<IBridgeHostSubsystem>(services =>
            services.GetRequiredService<BridgeBusinessStateOwner>());
        services.AddSingleton<IBridgeHostSubsystem, BridgeFeishuEventSubsystem>();
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
        services.AddSingleton<IBridgeRuntimeEventHandler>(services =>
            (IBridgeRuntimeEventHandler)services
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
        ]));
        // Production ports are registered only by their owning migration slices.
        // The incomplete manifest fails preflight and prevents Active mode from
        // building a provider or acquiring this lease until every owner exists.
    }
}
